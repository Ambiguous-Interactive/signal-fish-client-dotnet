namespace SignalFish.Client.FuzzTests
{
    using System;
    using System.Buffers;
    using System.IO;
    using SharpFuzz;
    using SignalFish.Client.Protocol;

    /// <summary>
    /// Coverage-guided codec fuzz host (PLAN M1.5). Select a target with the
    /// SIGNALFISH_FUZZ_TARGET environment variable: "reader" (envelope decode
    /// totality) or "writer" (encode validity + roundtrip identity). Under
    /// the libfuzzer-dotnet driver each target loops forever; standalone
    /// (no driver) the first argument is executed once, which keeps local
    /// red-green checks bounded.
    /// </summary>
    internal static class Program
    {
        private delegate bool PayloadTryDecode<T>(ReadOnlyMemory<byte> data, out T message);

        private static int Main(string[] args)
        {
            if (args.Length == 0 && !RunningUnderLibFuzzer())
            {
                Console.Error.WriteLine(
                    "Usage: SIGNALFISH_FUZZ_TARGET=reader|writer dotnet SignalFish.Client.FuzzTests.dll [input-file]"
                );
                return 2;
            }

            switch (Environment.GetEnvironmentVariable("SIGNALFISH_FUZZ_TARGET"))
            {
                case "reader":
                    Fuzzer.LibFuzzer.Run(WithCrashDump(FuzzReader));
                    return 0;
                case "writer":
                    Fuzzer.LibFuzzer.Run(WithCrashDump(FuzzWriter));
                    return 0;
                default:
                    Console.Error.WriteLine("Set SIGNALFISH_FUZZ_TARGET to 'reader' or 'writer'.");
                    return 2;
            }
        }

        /// <summary>SharpFuzz's driver mode announces itself via env vars.</summary>
        private static bool RunningUnderLibFuzzer()
        {
            foreach (object? key in Environment.GetEnvironmentVariables().Keys)
            {
                if (key is string name && name.StartsWith("__LIBFUZZER", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Persists a crashing input before it propagates to the driver: the
        /// libfuzzer-dotnet parent exits on a dead child without writing the
        /// crash artifact itself. Best-effort: a dump failure must not mask
        /// the original exception.
        /// </summary>
        private static ReadOnlySpanAction WithCrashDump(ReadOnlySpanAction target)
        {
            return input =>
            {
                try
                {
                    target(input);
                }
                catch (Exception)
                {
                    try
                    {
                        string dir =
                            Environment.GetEnvironmentVariable("SIGNALFISH_FUZZ_CRASH_DIR")
                            ?? Directory.GetCurrentDirectory();
                        Directory.CreateDirectory(dir);
                        string path = Path.Combine(
                            dir,
                            "crash-"
                                + Convert.ToHexString(
                                    System.Security.Cryptography.SHA256.HashData(input.ToArray())
                                )[..16]
                                + ".bin"
                        );
                        File.WriteAllBytes(path, input.ToArray());
                        Console.Error.WriteLine($"Crashing input saved to {path}");
                    }
                    catch (Exception dumpFailure)
                    {
                        Console.Error.WriteLine($"Crash dump failed: {dumpFailure.Message}");
                    }

                    throw;
                }
            };
        }

        /// <summary>
        /// The decoder is total: arbitrary bytes never throw, failures are
        /// bounded and carry an in-range byte offset, and event routing stays
        /// coherent (Message events name a real kind; UnknownMessage events
        /// carry their type text).
        /// </summary>
        private static void FuzzReader(ReadOnlySpan<byte> input)
        {
            byte[] frame = input.ToArray();
            EnvelopeEvent decoded = EnvelopeReader.Decode(frame);

            if (decoded.Kind == EnvelopeEventKind.DecodeFailed)
            {
                if (decoded.Error == default(DecodeError))
                {
                    throw new InvalidOperationException(
                        "DecodeFailed event without a decode error."
                    );
                }

                if (decoded.ErrorOffset < 0 || decoded.ErrorOffset > frame.Length)
                {
                    throw new InvalidOperationException(
                        $"DecodeFailed offset {decoded.ErrorOffset} outside [0, {frame.Length}]."
                    );
                }

                return;
            }

            if (decoded.Error != default(DecodeError))
            {
                throw new InvalidOperationException("Non-failed event carries a decode error.");
            }

            if (
                decoded.Kind == EnvelopeEventKind.Message
                && decoded.Message == default(MessageKind)
            )
            {
                throw new InvalidOperationException(
                    "Message event routed to default(MessageKind)."
                );
            }

            if (decoded.Kind == EnvelopeEventKind.Message && decoded.TypeText is not null)
            {
                throw new InvalidOperationException("Message event carries a type text.");
            }

            if (
                decoded.Kind == EnvelopeEventKind.UnknownMessage
                && string.IsNullOrEmpty(decoded.TypeText)
            )
            {
                throw new InvalidOperationException("UnknownMessage event lost its type text.");
            }
        }

        /// <summary>
        /// The encoder only ever refuses documented misuse (missing required
        /// fields, non-JSON verbatim payloads) via ArgumentException; every
        /// accepted encode emits valid UTF-8 that the decoder routes back to
        /// the same message kind with a payload struct that roundtrips
        /// byte-for-byte.
        /// </summary>
        private static void FuzzWriter(ReadOnlySpan<byte> input)
        {
            FuzzCursor cursor = new FuzzCursor(input);
            ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>(256);

            switch (cursor.Byte() % 4u)
            {
                case 0:
                {
                    AuthenticateMessage message = BuildAuthenticate(ref cursor);
                    if (!TryEncode(buffer, () => EnvelopeWriter.WriteAuthenticate(buffer, message)))
                    {
                        return;
                    }

                    AssertRoundtrip(
                        buffer,
                        MessageKind.Authenticate,
                        message,
                        AuthenticateMessage.TryDecode
                    );
                    break;
                }

                case 1:
                {
                    JoinRoomMessage message = BuildJoinRoom(ref cursor);
                    if (!TryEncode(buffer, () => EnvelopeWriter.WriteJoinRoom(buffer, message)))
                    {
                        return;
                    }

                    AssertRoundtrip(
                        buffer,
                        MessageKind.JoinRoom,
                        message,
                        JoinRoomMessage.TryDecode
                    );
                    break;
                }

                case 2:
                {
                    GameDataMessage message = BuildGameData(ref cursor, out bool seededPayload);
                    if (!TryEncode(buffer, () => EnvelopeWriter.WriteGameData(buffer, message)))
                    {
                        if (seededPayload)
                        {
                            // Seed payloads are valid JSON by construction;
                            // refusing one is a writer regression, not a
                            // documented misuse.
                            throw new InvalidOperationException(
                                "Writer refused a valid JSON seed payload."
                            );
                        }

                        return;
                    }

                    AssertRoundtrip(
                        buffer,
                        MessageKind.GameData,
                        message,
                        GameDataMessage.TryDecode
                    );
                    break;
                }

                default:
                {
                    EnvelopeWriter.WritePing(buffer);
                    EnvelopeEvent decoded = EnvelopeReader.Decode(buffer.WrittenMemory);
                    if (
                        decoded.Kind != EnvelopeEventKind.Message
                        || decoded.Message != MessageKind.Ping
                        || decoded.Error != default(DecodeError)
                        || !decoded.Data.IsEmpty
                    )
                    {
                        throw new InvalidOperationException(
                            "Encoded Ping frame did not decode back as Ping."
                        );
                    }

                    break;
                }
            }
        }

        private static AuthenticateMessage BuildAuthenticate(ref FuzzCursor cursor) =>
            new AuthenticateMessage(
                appId: cursor.OptionalString(),
                sdkVersion: cursor.OptionalString(),
                platform: cursor.OptionalString(),
                gameDataFormat: cursor.OptionalString(),
                protocolVersion: cursor.OptionalUint(),
                supportedTransports: cursor.OptionalStringList(),
                supportedTopologies: cursor.OptionalStringList(),
                requestedCapabilities: cursor.OptionalStringList(),
                connectToken: cursor.OptionalString()
            );

        private static JoinRoomMessage BuildJoinRoom(ref FuzzCursor cursor) =>
            new JoinRoomMessage(
                gameName: cursor.String(),
                playerName: cursor.String(),
                roomCode: cursor.OptionalString(),
                maxPlayers: cursor.OptionalUint(),
                supportsAuthority: cursor.OptionalBool(),
                relayTransport: cursor.OptionalString(),
                password: cursor.OptionalString()
            );

        private static GameDataMessage BuildGameData(ref FuzzCursor cursor, out bool seededPayload)
        {
            // Half the inputs start from a valid JSON seed so the fuzzer can
            // explore the classified-delivery paths without first having to
            // synthesize valid JSON; the rest mutate raw bytes (the writer
            // must refuse those with its documented ArgumentException).
            // Whitespace is trimmed: the writer emits the payload verbatim
            // while the reader slices the bare value token (JSON whitespace
            // is insignificant), so padded payloads cannot roundtrip bytes.
            seededPayload = (cursor.Byte() & 1) == 0;
            ReadOnlyMemory<byte> payload = seededPayload
                ? cursor.JsonSeed()
                : TrimJsonWhitespace(cursor.RawSlice());

            // Sample the valid classes only (1..3): the default (0) is the
            // non-valid None sentinel and the writer refuses it, so mapping
            // the raw byte modulo the valid range keeps the classified-
            // delivery paths (reliable/latest/volatile) uniformly reachable.
            GameDataClass classification = (GameDataClass)(1u + (cursor.Byte() % 3u));
            uint key = cursor.OptionalUint() ?? 0u;
            return new GameDataMessage(payload, classification, key);
        }

        /// <summary>Strips JSON whitespace from both ends of a raw payload.</summary>
        private static ReadOnlyMemory<byte> TrimJsonWhitespace(ReadOnlyMemory<byte> payload)
        {
            int start = 0;
            int end = payload.Length;
            while (start < end && IsJsonWhitespace(payload.Span[start]))
            {
                start++;
            }

            while (end > start && IsJsonWhitespace(payload.Span[end - 1]))
            {
                end--;
            }

            return payload.Slice(start, end - start);
        }

        private static bool IsJsonWhitespace(byte b) => b is 0x20 or 0x09 or 0x0A or 0x0D;

        private static bool TryEncode(ArrayBufferWriter<byte> buffer, Action encode)
        {
            try
            {
                encode();
                return true;
            }
            catch (ArgumentException)
            {
                // Documented misuse: missing required fields or a verbatim
                // payload that is not valid JSON. A refusal is correct.
                return false;
            }
        }

        private static void AssertRoundtrip<T>(
            ArrayBufferWriter<byte> buffer,
            MessageKind expectedKind,
            T expected,
            PayloadTryDecode<T> tryDecode
        )
            where T : struct, IEquatable<T>
        {
            EnvelopeEvent decoded = EnvelopeReader.Decode(buffer.WrittenMemory);

            if (
                decoded.Kind != EnvelopeEventKind.Message
                || decoded.Message != expectedKind
                || decoded.Error != default(DecodeError)
            )
            {
                throw new InvalidOperationException(
                    $"Encoded {expectedKind} frame did not decode back as {expectedKind}. Frame: {FrameText(buffer)}"
                );
            }

            // A message whose payload is entirely optional encodes with no
            // data member at all; TryDecode's contract covers data objects,
            // so an empty slice leaves nothing to roundtrip.
            if (decoded.Data.IsEmpty)
            {
                return;
            }

            if (!tryDecode(decoded.Data, out T actual))
            {
                throw new InvalidOperationException(
                    $"Encoded {expectedKind} payload did not decode back. Frame: {FrameText(buffer)}"
                );
            }

            if (!actual.Equals(expected))
            {
                throw new InvalidOperationException(
                    $"Encoded {expectedKind} frame changed the payload. Frame: {FrameText(buffer)}"
                );
            }
        }

        /// <summary>Lossy text rendering of a frame for failure diagnostics.</summary>
        private static string FrameText(ArrayBufferWriter<byte> buffer) =>
            System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);

        /// <summary>
        /// Deterministic consumption of fuzz input: every choice comes from
        /// the next unconsumed byte, and exhausted input stays deterministic
        /// so standalone replays of a crashing artifact reproduce exactly.
        /// </summary>
        private ref struct FuzzCursor
        {
            private ReadOnlySpan<byte> _input;
            private int _position;

            public FuzzCursor(ReadOnlySpan<byte> input)
            {
                _input = input;
                _position = 0;
            }

            public uint Byte()
            {
                uint value =
                    _position < _input.Length ? _input[_position] : (byte)((13 * _position) + 7);
                _position++;
                return value;
            }

            public uint? OptionalUint() => (Byte() & 3u) == 0 ? Byte() : null;

            public bool? OptionalBool()
            {
                uint pick = Byte();
                return (pick & 3u) == 0 ? (pick & 4u) != 0 : null;
            }

            /// <summary>A required string: 0-48 chars over U+0000-U+00FF.</summary>
            public string String() => String(Byte() % 49u);

            /// <summary>
            /// A string or null (null ~25% of the time). Chars map 1:1 from
            /// input bytes so escapes, control characters, and two-byte UTF-8
            /// sequences all appear on the wire.
            /// </summary>
            public string? OptionalString()
            {
                uint pick = Byte();
                return (pick & 3u) == 0 ? null : String((pick >> 2) % 49u);
            }

            /// <summary>A list of 0-3 strings, or null ~25% of the time.</summary>
            public string[]? OptionalStringList()
            {
                uint pick = Byte();
                if ((pick & 3u) == 0)
                {
                    return null;
                }

                uint count = (pick >> 2) % 4u;
                string[] items = new string[count];
                for (uint i = 0; i < count; i++)
                {
                    items[i] = String();
                }

                return items;
            }

            public ReadOnlyMemory<byte> RawSlice()
            {
                uint length = Byte() % 96u;
                byte[] bytes = new byte[length];
                for (uint i = 0; i < length; i++)
                {
                    bytes[i] = (byte)Byte();
                }

                return bytes;
            }

            public ReadOnlyMemory<byte> JsonSeed()
            {
                return (Byte() % 5u) switch
                {
                    0 => new byte[] { 123, 34, 110, 34, 58, 49, 125 }, // {"n":1}
                    1 => new byte[] { 91, 49, 44, 50, 93 }, // [1,2]
                    2 => new byte[] { 34, 115, 116, 114, 34 }, // "str"
                    3 => new byte[] { 116, 114, 117, 101 }, // true
                    _ => new byte[] { 110, 117, 108, 108 }, // null
                };
            }

            private string String(uint length)
            {
                char[] chars = new char[length];
                for (uint i = 0; i < length; i++)
                {
                    chars[i] = (char)Byte();
                }

                return new string(chars);
            }
        }
    }
}
