namespace SignalFish.Client.Tests
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using NUnit.Framework;
    using SignalFish.Client.Protocol;

    /// <summary>
    /// One parsed golden client fixture: the exact wire bytes, the expected
    /// routed kind, the payload struct it must encode/decode to, and the
    /// writer entry point under test.
    /// </summary>
    public sealed class FixtureMessage
    {
        internal string Wire { get; }

        internal MessageKind Kind { get; }

        internal object Payload { get; }

        internal Action<IBufferWriter<byte>> Write { get; }

        internal FixtureMessage(
            string wire,
            MessageKind kind,
            object payload,
            Action<IBufferWriter<byte>> write
        )
        {
            Wire = wire;
            Kind = kind;
            Payload = payload;
            Write = write;
        }
    }

    /// <summary>
    /// Red-green anchor for PLAN.md M1.3: <see cref="EnvelopeWriter"/> emits
    /// byte-identical frames for every outbound golden fixture, written
    /// frames round-trip through <see cref="EnvelopeReader"/> back to equal
    /// payload structs, and steady-state encoding allocates nothing.
    /// </summary>
    [TestFixture]
    public class EnvelopeWriterTests
    {
        /// <summary>
        /// An <see cref="IBufferWriter{T}"/> that hands out bounded spans so
        /// every multi-byte write must split across segments.
        /// </summary>
        private sealed class ChunkedBufferWriter : IBufferWriter<byte>
        {
            private readonly List<(byte[] Buffer, int Used)> _chunks = new List<(byte[], int)>();
            private readonly int _chunkSize;
            private byte[]? _current;

            internal ChunkedBufferWriter(int chunkSize)
            {
                _chunkSize = chunkSize;
            }

            public void Advance(int count)
            {
                if (_current is null)
                {
                    throw new InvalidOperationException("Advance without GetSpan.");
                }

                ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _current.Length);

                _chunks[^1] = (_current, count);
            }

            public Memory<byte> GetMemory(int sizeHint) => throw new NotSupportedException();

            public Span<byte> GetSpan(int sizeHint)
            {
                _current = new byte[Math.Max(sizeHint, _chunkSize)];
                _chunks.Add((_current, 0));
                return _current;
            }

            internal byte[] ToArray()
            {
                int total = _chunks.Sum(c => c.Used);
                byte[] result = new byte[total];
                int offset = 0;
                foreach ((byte[] buffer, int used) in _chunks)
                {
                    Buffer.BlockCopy(buffer, 0, result, offset, used);
                    offset += used;
                }

                return result;
            }
        }

        private static readonly string[] ClientFixtureFiles =
        {
            "v2-client-messages.jsonl",
            "v3-client-messages.jsonl",
        };

        private static readonly string[] RelayTransportNames = { "relay", "direct", "webrtc" };

        private static readonly string[] TopologyNames = { "relay", "host", "mesh" };

        private static readonly string[] CapabilityNames = { "room_operation_ids" };

        // --- Golden fixture corpus: byte-identical + roundtrip ----------------
        [Test, TestCaseSource(nameof(ClientFixtureMessages))]
        public void WriteFixtureLineReproducesByteIdenticalFrame(FixtureMessage fixture)
        {
            byte[] expected = Encoding.UTF8.GetBytes(fixture.Wire);
            ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>(expected.Length + 16);

            fixture.Write(buffer);

            Assert.That(
                buffer.WrittenSpan.ToArray(),
                Is.EqualTo(expected),
                $"{fixture.Kind} must reproduce the fixture bytes exactly."
            );
        }

        [Test, TestCaseSource(nameof(ClientFixtureMessages))]
        public void WriteFixtureLineRoundTripsThroughReaderToEqualPayload(FixtureMessage fixture)
        {
            ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>(64);
            fixture.Write(buffer);

            EnvelopeEvent ev = EnvelopeReader.Decode(buffer.WrittenSpan.ToArray());

            Assert.That(ev.Kind, Is.EqualTo(EnvelopeEventKind.Message), fixture.Wire);
            Assert.That(ev.Message, Is.EqualTo(fixture.Kind), fixture.Wire);
            Assert.That(
                PayloadDecodesTo(fixture.Kind, ev.Data, fixture.Payload),
                Is.True,
                $"Written frame must decode back to the same payload: {fixture.Wire}"
            );
        }

        [Test, TestCaseSource(nameof(ClientFixtureMessages))]
        public void WriteFixtureLineStaysWithinEnvelopeShape(FixtureMessage fixture)
        {
            ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>(64);
            fixture.Write(buffer);

            string json = Encoding.UTF8.GetString(buffer.WrittenSpan.ToArray());
            using JsonDocument doc = JsonDocument.Parse(json);

            Assert.That(doc.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Object));
            Assert.That(
                doc.RootElement.GetProperty("type").GetString(),
                Is.EqualTo(fixture.Kind.ToString())
            );
        }

        // --- Envelope shape ----------------------------------------------------
        [Test]
        public void WritePayloadlessCommandsOmitDataMemberEntirely()
        {
            foreach (
                (Action<IBufferWriter<byte>> write, string type) in new[]
                {
                    ((Action<IBufferWriter<byte>>)EnvelopeWriter.WritePing, "Ping"),
                    (EnvelopeWriter.WritePlayerReady, "PlayerReady"),
                    (EnvelopeWriter.WriteStartGame, "StartGame"),
                    (EnvelopeWriter.WriteLeaveRoom, "LeaveRoom"),
                    (EnvelopeWriter.WriteLeaveSpectator, "LeaveSpectator"),
                }
            )
            {
                ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>(64);
                write(buffer);

                Assert.That(
                    Encoding.UTF8.GetString(buffer.WrittenSpan.ToArray()),
                    Is.EqualTo($"{{\"type\": \"{type}\"}}"),
                    $"{type} must omit the data member."
                );
            }
        }

        [Test]
        public void WriteMinimalAuthenticateEmitsOnlyAppId()
        {
            ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>(64);
            EnvelopeWriter.WriteAuthenticate(buffer, new AuthenticateMessage(appId: "my-game"));

            Assert.That(
                Encoding.UTF8.GetString(buffer.WrittenSpan.ToArray()),
                Is.EqualTo("{\"type\": \"Authenticate\", \"data\": {\"app_id\": \"my-game\"}}")
            );
        }

        [Test]
        public void WriteEmptyCapabilityArraysEmitEmptyJsonArrays()
        {
            ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>(128);
            EnvelopeWriter.WriteAuthenticate(
                buffer,
                new AuthenticateMessage(
                    appId: "a",
                    protocolVersion: 3,
                    supportedTransports: Array.Empty<string>()
                )
            );

            Assert.That(
                Encoding.UTF8.GetString(buffer.WrittenSpan.ToArray()),
                Is.EqualTo(
                    "{\"type\": \"Authenticate\", \"data\": {\"app_id\": \"a\", \"protocol_version\": 3, \"supported_transports\": []}}"
                )
            );
        }

        [Test]
        public void GameDataPayloadAtDepthLimitIsAcceptedAndBeyondRefused()
        {
            const int limit = EnvelopeWriter.MaxVerbatimPayloadDepth;

            /*
                The bound is the deepest level a value may occupy (root is
                level 1), matching the envelope decode bound's semantics.
                Exactly at the bound is legal, one deeper is refused — for
                both container shapes, and a deep branch is refused even
                when a shallow sibling is fine. The bound keeps the walk
                (and the server's decode) stack-safe.
            */
            Assert.That(
                (Action)(
                    () => _ = new GameDataMessage(Encoding.UTF8.GetBytes(Nested("1", limit - 1)))
                ),
                Throws.Nothing
            );
            Assert.That(
                (Action)(
                    () => _ = new GameDataMessage(Encoding.UTF8.GetBytes(Nested("{}", limit - 1)))
                ),
                Throws.Nothing
            );
            Assert.That(
                (Action)(
                    () => _ = new GameDataMessage(Encoding.UTF8.GetBytes(Nested("[1]", limit - 2)))
                ),
                Throws.Nothing
            );

            Assert.That(
                (Action)(() => _ = new GameDataMessage(Encoding.UTF8.GetBytes(Nested("1", limit)))),
                Throws.ArgumentException
            );
            Assert.That(
                (Action)(
                    () => _ = new GameDataMessage(Encoding.UTF8.GetBytes(Nested("[1]", limit)))
                ),
                Throws.ArgumentException
            );
            Assert.That(
                (Action)(
                    () => _ = new GameDataMessage(Encoding.UTF8.GetBytes(Nested("{}", limit)))
                ),
                Throws.ArgumentException
            );
            Assert.That(
                (Action)(
                    () =>
                        _ = new GameDataMessage(
                            Encoding.UTF8.GetBytes(
                                "{\"flat\": true, \"deep\": " + Nested("1", limit - 1) + "}"
                            )
                        )
                ),
                Throws.ArgumentException,
                "a deep branch must not hide behind a shallow sibling"
            );

            /*
                A payload this SDK accepts must survive its own wire
                roundtrip. The envelope embeds a payload two levels deeper
                than its standalone form (root + the data member), so the
                deepest payload decodable on the wire is standalone depth
                125 — exactly at the shared 128 bound once embedded. This
                pins the send→decode boundary; the two-level gap between
                "ctor-accepted" (127) and "wire-decodable" (125) mirrors the
                server codec's own embedding overhead.
            */
            ArrayBufferWriter<byte> deepFrame = new ArrayBufferWriter<byte>(256);
            EnvelopeWriter.WriteGameData(
                deepFrame,
                new GameDataMessage(Encoding.UTF8.GetBytes(Nested("1", limit - 3)))
            );
            EnvelopeEvent ev = EnvelopeReader.Decode(deepFrame.WrittenSpan.ToArray());
            Assert.That(ev.Kind, Is.EqualTo(EnvelopeEventKind.Message));
            Assert.That(ev.Message, Is.EqualTo(MessageKind.GameData));
        }

        [Test]
        public void SignalAndConnectionInfoPayloadsShareTheVerbatimDepthBoundary()
        {
            const int limit = EnvelopeWriter.MaxVerbatimPayloadDepth;

            /*
                One depth constant governs every verbatim outbound payload.
                Signal takes any JSON value; connection info must be a JSON
                object. Both are validated at construction — exactly at the
                bound is legal, one deeper is refused — and a payload the
                constructor accepted must encode without re-validation.
            */
            Assert.That(
                (Action)(
                    () =>
                        _ = new SignalMessage(
                            "00000000-0000-0000-0000-000000000001",
                            "00000000-0000-0000-0000-000000000002",
                            Encoding.UTF8.GetBytes(Nested("1", limit - 1))
                        )
                ),
                Throws.Nothing
            );
            Assert.That(
                (Action)(
                    () =>
                        _ = new SignalMessage(
                            "00000000-0000-0000-0000-000000000001",
                            "00000000-0000-0000-0000-000000000002",
                            Encoding.UTF8.GetBytes(Nested("1", limit))
                        )
                ),
                Throws.ArgumentException
            );
            Assert.That(
                (Action)(
                    () =>
                        _ = new ProvideConnectionInfoMessage(
                            Encoding.UTF8.GetBytes(Nested("{}", limit - 1))
                        )
                ),
                Throws.Nothing
            );
            Assert.That(
                (Action)(
                    () =>
                        _ = new ProvideConnectionInfoMessage(
                            Encoding.UTF8.GetBytes(Nested("{}", limit))
                        )
                ),
                Throws.ArgumentException
            );
            Assert.That(
                (Action)(
                    () => _ = new ProvideConnectionInfoMessage(Encoding.UTF8.GetBytes("[1, 2]"))
                ),
                Throws.ArgumentException,
                "connection info must be a JSON object, not just any JSON value"
            );

            ArrayBufferWriter<byte> deepSignal = new ArrayBufferWriter<byte>(256);
            EnvelopeWriter.WriteSignal(
                deepSignal,
                new SignalMessage(
                    "00000000-0000-0000-0000-000000000001",
                    "00000000-0000-0000-0000-000000000002",
                    Encoding.UTF8.GetBytes(Nested("1", limit - 3))
                )
            );
            EnvelopeEvent decoded = EnvelopeReader.Decode(deepSignal.WrittenSpan.ToArray());
            Assert.That(decoded.Kind, Is.EqualTo(EnvelopeEventKind.Message));
            Assert.That(decoded.Message, Is.EqualTo(MessageKind.Signal));
        }

        private static string Nested(string inner, int levels)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder(inner);
            for (int i = 0; i < levels; i++)
            {
                builder.Insert(0, "{\"k\":");
                builder.Append('}');
            }

            return builder.ToString();
        }

        [Test]
        public void WriteReliableGameDataMatchesLegacyV2WireForm()
        {
            ArrayBufferWriter<byte> reliable = new ArrayBufferWriter<byte>(64);
            EnvelopeWriter.WriteGameData(
                reliable,
                new GameDataMessage(Encoding.UTF8.GetBytes("{\"tick\": 1}"))
            );
            ArrayBufferWriter<byte> explicitReliable = new ArrayBufferWriter<byte>(64);
            EnvelopeWriter.WriteGameData(
                explicitReliable,
                new GameDataMessage(Encoding.UTF8.GetBytes("{\"tick\": 1}"), GameDataClass.Reliable)
            );

            Assert.That(
                Encoding.UTF8.GetString(reliable.WrittenSpan.ToArray()),
                Is.EqualTo("{\"type\": \"GameData\", \"data\": {\"data\": {\"tick\": 1}}}")
            );
            Assert.That(
                reliable.WrittenSpan.ToArray(),
                Is.EqualTo(explicitReliable.WrittenSpan.ToArray())
            );
        }

        // --- Non-fixture field combinations (roundtrip-pinned) ------------------
        [Test]
        public void WriteJoinRoomCreationFormRoundTrips()
        {
            JoinRoomMessage message = new JoinRoomMessage(
                "my-game",
                "Alice",
                maxPlayers: 8,
                supportsAuthority: true
            );

            AssertRoundTrips(
                MessageKind.JoinRoom,
                message,
                static (w, m) => EnvelopeWriter.WriteJoinRoom(w, in m)
            );

            Assert.That(
                Encoding.UTF8.GetString(Written(MessageKind.JoinRoom, message)),
                Is.EqualTo(
                    "{\"type\": \"JoinRoom\", \"data\": {\"game_name\": \"my-game\", \"room_code\": null, \"player_name\": \"Alice\", \"max_players\": 8, \"supports_authority\": true, \"relay_transport\": null}}"
                )
            );
        }

        [Test]
        public void WriteJoinRoomAllOptionalFieldsByteExact()
        {
            ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>(128);
            EnvelopeWriter.WriteJoinRoom(
                buffer,
                new JoinRoomMessage(
                    "my-game",
                    "Alice",
                    roomCode: "ABC123",
                    maxPlayers: 4,
                    supportsAuthority: false,
                    relayTransport: "tcp",
                    password: "pw"
                )
            );

            Assert.That(
                Encoding.UTF8.GetString(buffer.WrittenSpan.ToArray()),
                Is.EqualTo(
                    "{\"type\": \"JoinRoom\", \"data\": {\"game_name\": \"my-game\", \"room_code\": \"ABC123\", \"player_name\": \"Alice\", \"max_players\": 4, \"supports_authority\": false, \"relay_transport\": \"tcp\", \"password\": \"pw\"}}"
                )
            );
        }

        [Test]
        public void WriteJoinRoomWithPasswordRoundTrips()
        {
            JoinRoomMessage message = new JoinRoomMessage(
                "my-game",
                "Alice",
                roomCode: "ABC123",
                password: "s3cret!"
            );

            AssertRoundTrips(
                MessageKind.JoinRoom,
                message,
                static (w, m) => EnvelopeWriter.WriteJoinRoom(w, in m)
            );
        }

        [Test]
        public void WriteJoinAsSpectatorWithPasswordRoundTrips()
        {
            JoinAsSpectatorMessage message = new JoinAsSpectatorMessage(
                "my-game",
                "ABC123",
                "Observer",
                password: "s3cret!"
            );

            AssertRoundTrips(
                MessageKind.JoinAsSpectator,
                message,
                static (w, m) => EnvelopeWriter.WriteJoinAsSpectator(w, in m)
            );

            Assert.That(
                Encoding.UTF8.GetString(Written(MessageKind.JoinAsSpectator, message)),
                Is.EqualTo(
                    "{\"type\": \"JoinAsSpectator\", \"data\": {\"game_name\": \"my-game\","
                        + " \"room_code\": \"ABC123\", \"spectator_name\": \"Observer\", \"password\": \"s3cret!\"}}"
                )
            );
        }

        [Test]
        public void WriteRoomOperationSetRoomAccessBothPolaritiesRoundTrip()
        {
            RoomOperationMessage seal = new RoomOperationMessage(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                RoomOperationCommand.SetRoomAccess("pw")
            );
            RoomOperationMessage reopen = new RoomOperationMessage(
                "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                RoomOperationCommand.SetRoomAccess(null)
            );

            AssertRoundTrips(
                MessageKind.RoomOperation,
                seal,
                static (w, m) => EnvelopeWriter.WriteRoomOperation(w, in m)
            );
            AssertRoundTrips(
                MessageKind.RoomOperation,
                reopen,
                static (w, m) => EnvelopeWriter.WriteRoomOperation(w, in m)
            );

            string reopenJson = Encoding.UTF8.GetString(Written(MessageKind.RoomOperation, reopen));
            Assert.That(
                reopenJson,
                Does.Contain("\"password\": null"),
                "SetRoomAccess reopen must present an explicit null password."
            );
        }

        [Test]
        public void WriteRoomOperationModerationCommandsByteExact()
        {
            /*
                The nested operation shapes have no golden fixtures upstream,
                so their canonical field order is pinned here byte-exactly.
            */
            foreach (
                (RoomOperationCommand command, string operationJson) in new[]
                {
                    (
                        RoomOperationCommand.KickPlayer("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                        "{\"type\": \"KickPlayer\", \"data\": {\"player_id\": \"cccccccc-cccc-cccc-cccc-cccccccccccc\"}}"
                    ),
                    (
                        RoomOperationCommand.RegenerateRoomCode(),
                        "{\"type\": \"RegenerateRoomCode\"}"
                    ),
                    (
                        RoomOperationCommand.SetRoomAccess("pw"),
                        "{\"type\": \"SetRoomAccess\", \"data\": {\"password\": \"pw\"}}"
                    ),
                    (
                        RoomOperationCommand.SetRoomAccess(null),
                        "{\"type\": \"SetRoomAccess\", \"data\": {\"password\": null}}"
                    ),
                    (
                        RoomOperationCommand.BanPlayer("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                        "{\"type\": \"BanPlayer\", \"data\": {\"player_id\": \"cccccccc-cccc-cccc-cccc-cccccccccccc\"}}"
                    ),
                    (
                        RoomOperationCommand.UnbanPlayer("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                        "{\"type\": \"UnbanPlayer\", \"data\": {\"player_id\": \"cccccccc-cccc-cccc-cccc-cccccccccccc\"}}"
                    ),
                    (
                        RoomOperationCommand.TransferAuthority(
                            "cccccccc-cccc-cccc-cccc-cccccccccccc"
                        ),
                        "{\"type\": \"TransferAuthority\", \"data\": {\"player_id\": \"cccccccc-cccc-cccc-cccc-cccccccccccc\"}}"
                    ),
                }
            )
            {
                RoomOperationMessage message = new RoomOperationMessage(
                    "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    command
                );
                ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>(256);
                EnvelopeWriter.WriteRoomOperation(buffer, in message);

                string expected =
                    "{\"type\": \"RoomOperation\", \"data\": {\"operation_id\":"
                    + " \"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\", \"operation\": "
                    + operationJson
                    + "}}";
                Assert.That(
                    Encoding.UTF8.GetString(buffer.WrittenSpan.ToArray()),
                    Is.EqualTo(expected),
                    $"{command.Kind} nested operation must match the canonical shape."
                );
            }
        }

        [Test]
        public void WriteRoomOperationLegacyLifecycleCommandsRoundTrip()
        {
            RoomOperationMessage join = new RoomOperationMessage(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                RoomOperationCommand.JoinRoom(
                    new JoinRoomMessage("my-game", "Alice", roomCode: "ABC123")
                )
            );
            RoomOperationMessage reconnect = new RoomOperationMessage(
                "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                RoomOperationCommand.Reconnect(new ReconnectMessage("p", "r", "t"))
            );
            RoomOperationMessage spectator = new RoomOperationMessage(
                "cccccccc-cccc-cccc-cccc-cccccccccccc",
                RoomOperationCommand.JoinAsSpectator(new JoinAsSpectatorMessage("g", "ABC123", "S"))
            );

            AssertRoundTrips(
                MessageKind.RoomOperation,
                join,
                static (w, m) => EnvelopeWriter.WriteRoomOperation(w, in m)
            );
            AssertRoundTrips(
                MessageKind.RoomOperation,
                reconnect,
                static (w, m) => EnvelopeWriter.WriteRoomOperation(w, in m)
            );
            AssertRoundTrips(
                MessageKind.RoomOperation,
                spectator,
                static (w, m) => EnvelopeWriter.WriteRoomOperation(w, in m)
            );

            Assert.That(
                Encoding.UTF8.GetString(Written(MessageKind.RoomOperation, join)),
                Is.EqualTo(
                    "{\"type\": \"RoomOperation\", \"data\": {\"operation_id\":"
                        + " \"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\", \"operation\": {\"type\": \"JoinRoom\","
                        + " \"data\": {\"game_name\": \"my-game\", \"room_code\": \"ABC123\", \"player_name\": \"Alice\", \"max_players\": null, \"supports_authority\": null, \"relay_transport\": null}}}}"
                ),
                "The nested JoinRoom payload must keep the canonical field order."
            );
        }

        // --- String escaping ----------------------------------------------------
        [TestCase("quote\"inside", "quote\\\"inside")]
        [TestCase("back\\slash", "back\\\\slash")]
        [TestCase("line\nbreak", "line\\nbreak")]
        [TestCase("tab\there", "tab\\there")]
        [TestCase("cr\rreturn", "cr\\rreturn")]
        [TestCase("back\bspace", "back\\bspace")]
        [TestCase("form\ffeed", "form\\ffeed")]
        [TestCase("ctrl\u0001char", "ctrl\\u0001char")]
        [TestCase("del\u007Fchar", "del\u007Fchar")]
        public void WriteStringEscapesControlAndQuoteCharacters(string value, string escapedJson)
        {
            ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>(128);
            EnvelopeWriter.WriteJoinRoom(buffer, new JoinRoomMessage(value, value));

            string expected =
                "{\"type\": \"JoinRoom\", \"data\": {\"game_name\": \""
                + escapedJson
                + "\", \"room_code\": null, \"player_name\": \""
                + escapedJson
                + "\", \"max_players\": null, \"supports_authority\": null, \"relay_transport\": null}}";
            Assert.That(
                Encoding.UTF8.GetString(buffer.WrittenSpan.ToArray()),
                Is.EqualTo(expected)
            );

            // The escaped frame must decode back to the original string.
            EnvelopeEvent ev = EnvelopeReader.Decode(buffer.WrittenSpan.ToArray());
            Assert.That(JoinRoomMessage.TryDecode(ev.Data, out JoinRoomMessage decoded), Is.True);
            Assert.That(decoded, Is.EqualTo(new JoinRoomMessage(value, value)));
        }

        [Test]
        public void WriteNonAsciiStringPassesUtf8ThroughAndRoundTrips()
        {
            const string value = "Åke ☃ 你好";
            ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>(128);
            EnvelopeWriter.WriteJoinRoom(buffer, new JoinRoomMessage(value, "p"));

            byte[] bytes = buffer.WrittenSpan.ToArray();
            Assert.That(
                IndexOf(bytes, Encoding.UTF8.GetBytes("☃")),
                Is.GreaterThanOrEqualTo(0),
                "Non-ASCII characters must pass through as UTF-8."
            );
            Assert.That(
                bytes,
                Is.EqualTo(
                    Encoding.UTF8.GetBytes(
                        "{\"type\": \"JoinRoom\", \"data\": {\"game_name\": \""
                            + value
                            + "\", \"room_code\": null, \"player_name\": \"p\", \"max_players\": null, \"supports_authority\": null, \"relay_transport\": null}}"
                    )
                )
            );

            EnvelopeEvent ev = EnvelopeReader.Decode(bytes);
            Assert.That(JoinRoomMessage.TryDecode(ev.Data, out JoinRoomMessage decoded), Is.True);
            Assert.That(decoded.GameName, Is.EqualTo(value));
        }

        // --- Buffer growth across segments ---------------------------------------
        [TestCase(1)]
        [TestCase(3)]
        [TestCase(17)]
        public void WriteSmallBufferSegmentsMatchesSingleSegmentOutput(int chunkSize)
        {
            AuthenticateMessage message = new AuthenticateMessage(
                appId: "mb_app_abc123",
                sdkVersion: "1.2.3",
                platform: "unity",
                protocolVersion: 3,
                supportedTransports: RelayTransportNames,
                supportedTopologies: TopologyNames,
                requestedCapabilities: CapabilityNames
            );

            ArrayBufferWriter<byte> single = new ArrayBufferWriter<byte>(64);
            EnvelopeWriter.WriteAuthenticate(single, in message);

            ChunkedBufferWriter chunked = new ChunkedBufferWriter(chunkSize);
            EnvelopeWriter.WriteAuthenticate(chunked, in message);

            Assert.That(
                chunked.ToArray(),
                Is.EqualTo(single.WrittenSpan.ToArray()),
                $"Chunked writes (chunk size {chunkSize}) must produce identical output."
            );
        }

        [Test]
        public void WriteLongNonAsciiStringDoesNotOverflowAndRoundTrips()
        {
            /*
                Stackalloc-per-iteration in the writer would overflow the
                stack (uncatchable) on inputs of this scale.
            */
            string value = new string('é', 100_000) + new string('☃', 25_000);
            ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>(64);
            EnvelopeWriter.WriteJoinRoom(buffer, new JoinRoomMessage(value, "p"));

            EnvelopeEvent ev = EnvelopeReader.Decode(buffer.WrittenSpan.ToArray());
            Assert.That(JoinRoomMessage.TryDecode(ev.Data, out JoinRoomMessage decoded), Is.True);
            Assert.That(decoded.GameName, Is.EqualTo(value));
        }

        [Test]
        public void GameDataNonLatestKeyIsNormalizedAway()
        {
            byte[] payloadBytes = Encoding.UTF8.GetBytes("{}");
            GameDataMessage payload = new GameDataMessage(
                payloadBytes,
                GameDataClass.Volatile,
                key: 5
            );

            Assert.That(payload.Key, Is.EqualTo(0));
            Assert.That(
                payload,
                Is.EqualTo(new GameDataMessage(payload.Payload, GameDataClass.Volatile)),
                "A key without class latest must not change equality."
            );
        }

        // --- Encode misuse is a programmer error ---------------------------------
        [Test]
        public void WriteMissingRequiredFieldsThrowsArgumentException()
        {
            Assert.That(
                (Action)(
                    () =>
                        EnvelopeWriter.WriteJoinRoom(
                            new ArrayBufferWriter<byte>(),
                            new JoinRoomMessage(null!, "Alice")
                        )
                ),
                Throws.ArgumentException
            );
            Assert.That(
                (Action)(
                    () =>
                        EnvelopeWriter.WriteReconnect(
                            new ArrayBufferWriter<byte>(),
                            new ReconnectMessage("p", "r", null!)
                        )
                ),
                Throws.ArgumentException
            );
            Assert.That(
                (Action)(
                    () =>
                        EnvelopeWriter.WriteJoinAsSpectator(
                            new ArrayBufferWriter<byte>(),
                            new JoinAsSpectatorMessage("g", "C", null!)
                        )
                ),
                Throws.ArgumentException
            );
            Assert.That(
                (Action)(
                    () =>
                        EnvelopeWriter.WriteTransportStatus(
                            new ArrayBufferWriter<byte>(),
                            new TransportStatusMessage(null!, true)
                        )
                ),
                Throws.ArgumentException
            );
            Assert.That(
                (Action)(
                    () =>
                        EnvelopeWriter.WriteRoomOperation(
                            new ArrayBufferWriter<byte>(),
                            new RoomOperationMessage("not-a-uuid", RoomOperationCommand.LeaveRoom())
                        )
                ),
                Throws.ArgumentException,
                "operation_id must be the canonical hyphenated lowercase UUID form."
            );

            RoomOperationCommand command = RoomOperationCommand.KickPlayer("target");
            Assert.That(
                (Action)(
                    () =>
                        EnvelopeWriter.WriteSignal(
                            new ArrayBufferWriter<byte>(),
                            new SignalMessage("peer", "gen", Encoding.UTF8.GetBytes("{}"))
                        )
                ),
                Throws.ArgumentException,
                "Signal target and generation must be canonical UUIDs."
            );
        }

        [Test]
        public void WriteInvalidVerbatimPayloadsThrowArgumentException()
        {
            Assert.That(
                (Action)(
                    () =>
                        EnvelopeWriter.WriteGameData(
                            new ArrayBufferWriter<byte>(),
                            new GameDataMessage(Array.Empty<byte>())
                        )
                ),
                Throws.ArgumentException
            );
            Assert.That(
                (Action)(
                    () =>
                        EnvelopeWriter.WriteProvideConnectionInfo(
                            new ArrayBufferWriter<byte>(),
                            new ProvideConnectionInfoMessage(Encoding.UTF8.GetBytes("[1, 2]"))
                        )
                ),
                Throws.ArgumentException
            );
            Assert.That(
                (Action)(
                    () =>
                        EnvelopeWriter.WriteSignal(
                            new ArrayBufferWriter<byte>(),
                            new SignalMessage("peer", "gen", Array.Empty<byte>())
                        )
                ),
                Throws.ArgumentException
            );
            Assert.That(
                (Action)(
                    () =>
                        EnvelopeWriter.WriteGameData(
                            new ArrayBufferWriter<byte>(),
                            new GameDataMessage(Encoding.UTF8.GetBytes("{oops"))
                        )
                ),
                Throws.ArgumentException,
                "A malformed verbatim payload must fail at construction, not corrupt the frame."
            );
            Assert.That(
                (Action)(
                    () =>
                        EnvelopeWriter.WriteProvideConnectionInfo(
                            new ArrayBufferWriter<byte>(),
                            new ProvideConnectionInfoMessage(Encoding.UTF8.GetBytes("{} trailing"))
                        )
                ),
                Throws.ArgumentException,
                "Verbatim payloads must carry exactly one JSON value."
            );
        }

        [Test]
        public void WriteNullDestinationThrowsArgumentNullException()
        {
            JoinRoomMessage message = new JoinRoomMessage("g", "p");
            Assert.That(
                (Action)(() => EnvelopeWriter.WriteJoinRoom(null!, in message)),
                Throws.ArgumentNullException
            );
            Assert.That(
                (Action)(() => EnvelopeWriter.WritePing(null!)),
                Throws.ArgumentNullException
            );
        }

        // --- Payload decode robustness --------------------------------------------
        [TestCase("not an object")]
        [TestCase("[\"array\"]")]
        [TestCase("{\"game_name\": 5, \"player_name\": \"p\"}")] // wrong-typed field
        [TestCase("{\"player_name\": \"p\"}")] // missing required field
        [TestCase("{\"game_name\": \"g\"")] // truncated
        [TestCase("{\"game_name\": \"g\", \"player_name\": \"p\",,}")] // malformed separator
        public void DecodeMalformedPayloadReturnsFalseWithoutThrowing(string payload)
        {
            Assert.That(
                JoinRoomMessage.TryDecode(Encoding.UTF8.GetBytes(payload), out _),
                Is.False
            );
        }

        [Test]
        public void DecodeEscapedMemberKeysMatchPlainWireNames()
        {
            // The envelope routes keys escape-aware; payload decode must agree.
            Assert.That(
                JoinRoomMessage.TryDecode(
                    Encoding.UTF8.GetBytes("{\"\\u0067ame_name\": \"g\", \"player_name\": \"p\"}"),
                    out JoinRoomMessage decoded
                ),
                Is.True
            );
            Assert.That(decoded.GameName, Is.EqualTo("g"));
            Assert.That(decoded.PlayerName, Is.EqualTo("p"));
        }

        [Test]
        public void DecodeExplicitNullOptionalsDecodeAsAbsent()
        {
            /*
                Canonical wire form: absent optionals are explicit nulls
                (upstream JoinRoom samples; password is [string, 'null']).
            */
            Assert.That(
                JoinRoomMessage.TryDecode(
                    Encoding.UTF8.GetBytes(
                        "{\"game_name\": \"g\", \"room_code\": null, \"player_name\": \"p\","
                            + " \"max_players\": null, \"supports_authority\": null,"
                            + " \"relay_transport\": null, \"password\": null}"
                    ),
                    out JoinRoomMessage join
                ),
                Is.True
            );
            Assert.That(join.RoomCode, Is.Null);
            Assert.That(join.MaxPlayers, Is.Null);
            Assert.That(join.SupportsAuthority, Is.Null);
            Assert.That(join.RelayTransport, Is.Null);
            Assert.That(join.Password, Is.Null);

            Assert.That(
                JoinAsSpectatorMessage.TryDecode(
                    Encoding.UTF8.GetBytes(
                        "{\"game_name\": \"g\", \"room_code\": \"C\", \"spectator_name\": \"s\","
                            + " \"password\": null}"
                    ),
                    out JoinAsSpectatorMessage spectator
                ),
                Is.True
            );
            Assert.That(spectator.Password, Is.Null);

            // Required fields stay required even when null-typed optionals exist.
            Assert.That(
                JoinAsSpectatorMessage.TryDecode(
                    Encoding.UTF8.GetBytes(
                        "{\"game_name\": \"g\", \"room_code\": null, \"spectator_name\": \"s\"}"
                    ),
                    out _
                ),
                Is.False
            );
        }

        [Test]
        public void DecodeUnknownPayloadFieldsAreSkipped()
        {
            Assert.That(
                ReconnectMessage.TryDecode(
                    Encoding.UTF8.GetBytes(
                        "{\"future\": {\"a\": [1, 2, {\"b\": null}]}, \"player_id\": \"p\", \"room_id\": \"r\", \"auth_token\": \"t\"}"
                    ),
                    out ReconnectMessage decoded
                ),
                Is.True
            );
            Assert.That(decoded, Is.EqualTo(new ReconnectMessage("p", "r", "t")));
        }

        [Test]
        public void DecodeGameDataExplicitReliableNormalizesKeyAway()
        {
            Assert.That(
                GameDataMessage.TryDecode(
                    Encoding.UTF8.GetBytes(
                        "{\"data\": {\"x\": 1}, \"class\": \"reliable\", \"key\": 9}"
                    ),
                    out GameDataMessage decoded
                ),
                Is.True
            );
            Assert.That(decoded.Class, Is.EqualTo(GameDataClass.Reliable));
            Assert.That(
                decoded.Key,
                Is.EqualTo(0),
                "The key is only meaningful alongside class latest."
            );
        }

        [Test]
        public void DecodeGameDataUnknownClassTokenReturnsFalse()
        {
            Assert.That(
                GameDataMessage.TryDecode(
                    Encoding.UTF8.GetBytes("{\"data\": {}, \"class\": \"banana\"}"),
                    out _
                ),
                Is.False
            );
        }

        [Test]
        public void DecodeRoomOperationUnknownCommandTypeReturnsFalse()
        {
            Assert.That(
                RoomOperationMessage.TryDecode(
                    Encoding.UTF8.GetBytes(
                        "{\"operation_id\": \"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\", \"operation\": {\"type\": \"NukeRoom\"}}"
                    ),
                    out _
                ),
                Is.False
            );
        }

        [Test]
        public void DecodeEmptyAuthenticateObjectDecodesToDefault()
        {
            Assert.That(
                AuthenticateMessage.TryDecode(
                    Encoding.UTF8.GetBytes("{}"),
                    out AuthenticateMessage decoded
                ),
                Is.True
            );
            Assert.That(decoded, Is.EqualTo(default(AuthenticateMessage)));
        }

        // --- Allocation gate --------------------------------------------------------
        [Test]
        public void WriteSteadyStateFullCorpusAllocatesNothing()
        {
            List<FixtureMessage> fixtures = BuildAllClientFixtureMessages();
            ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>(64 * 1024);

            foreach (FixtureMessage fixture in fixtures)
            {
                fixture.Write(buffer);
            }

            buffer.Clear();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            /*
                Minimum delta across passes: a real regression allocates on
                every pass, while one-time JIT/OSR bookkeeping (a few bytes,
                observed under code-coverage instrumentation) inflates only
                the first measured pass.
            */
            long minDelta = long.MaxValue;
            for (int pass = 0; pass < 4; pass++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                foreach (FixtureMessage fixture in fixtures)
                {
                    fixture.Write(buffer);
                }

                long after = GC.GetAllocatedBytesForCurrentThread();
                minDelta = Math.Min(minDelta, after - before);
            }

            Assert.That(
                minDelta,
                Is.EqualTo(0),
                "Steady-state encoding of the whole outbound corpus must not allocate."
            );
        }

        internal static IEnumerable<TestCaseData> ClientFixtureMessages()
        {
            foreach (string fileName in ClientFixtureFiles)
            {
                string[] lines = File.ReadAllLines(
                    Path.Combine(GoldenFixtures.GoldenDirectory, fileName)
                );
                for (int i = 0; i < lines.Length; i++)
                {
                    FixtureMessage message = BuildFromFixture(lines[i]);
                    yield return new TestCaseData(message).SetArgDisplayNames(
                        $"{fileName}:{i + 1}",
                        message.Kind.ToString()
                    );
                }
            }
        }

        internal static List<FixtureMessage> BuildAllClientFixtureMessages()
        {
            return ClientFixtureMessages().Select(c => (FixtureMessage)c.Arguments![0]!).ToList();
        }

        // --- Helpers -----------------------------------------------------------------
        private static byte[] Written(MessageKind kind, object payload)
        {
            ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>(64);
            WriteTo(buffer, kind, payload);
            return buffer.WrittenSpan.ToArray();
        }

        private static void AssertRoundTrips<TMessage>(
            MessageKind kind,
            TMessage message,
            Action<IBufferWriter<byte>, TMessage> write
        )
            where TMessage : struct
        {
            ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>(64);
            write(buffer, message);

            EnvelopeEvent ev = EnvelopeReader.Decode(buffer.WrittenSpan.ToArray());
            Assert.That(ev.Kind, Is.EqualTo(EnvelopeEventKind.Message));
            Assert.That(ev.Message, Is.EqualTo(kind));

            Assert.That(
                PayloadDecodesTo(kind, ev.Data, message),
                Is.True,
                "Written frame must decode back to an equal payload struct."
            );
        }

        private static bool PayloadDecodesTo(
            MessageKind kind,
            ReadOnlyMemory<byte> data,
            object expected
        )
        {
            switch (kind)
            {
                case MessageKind.Authenticate:
                    return AuthenticateMessage.TryDecode(data, out AuthenticateMessage a)
                        && a.Equals((AuthenticateMessage)expected);
                case MessageKind.JoinRoom:
                    return JoinRoomMessage.TryDecode(data, out JoinRoomMessage jr)
                        && jr.Equals((JoinRoomMessage)expected);
                case MessageKind.JoinAsSpectator:
                    return JoinAsSpectatorMessage.TryDecode(data, out JoinAsSpectatorMessage jas)
                        && jas.Equals((JoinAsSpectatorMessage)expected);
                case MessageKind.Reconnect:
                    return ReconnectMessage.TryDecode(data, out ReconnectMessage rc)
                        && rc.Equals((ReconnectMessage)expected);
                case MessageKind.AuthorityRequest:
                    return AuthorityRequestMessage.TryDecode(data, out AuthorityRequestMessage ar)
                        && ar.Equals((AuthorityRequestMessage)expected);
                case MessageKind.ProvideConnectionInfo:
                    return ProvideConnectionInfoMessage.TryDecode(
                            data,
                            out ProvideConnectionInfoMessage pci
                        ) && pci.Equals((ProvideConnectionInfoMessage)expected);
                case MessageKind.GameData:
                    return GameDataMessage.TryDecode(data, out GameDataMessage gd)
                        && gd.Equals((GameDataMessage)expected);
                case MessageKind.RoomOperation:
                    return RoomOperationMessage.TryDecode(data, out RoomOperationMessage ro)
                        && ro.Equals((RoomOperationMessage)expected);
                case MessageKind.Signal:
                    return SignalMessage.TryDecode(data, out SignalMessage sig)
                        && sig.Equals((SignalMessage)expected);
                case MessageKind.TransportStatus:
                    return TransportStatusMessage.TryDecode(data, out TransportStatusMessage ts)
                        && ts.Equals((TransportStatusMessage)expected);
                case MessageKind.PlayerReady:
                case MessageKind.StartGame:
                case MessageKind.LeaveRoom:
                case MessageKind.LeaveSpectator:
                case MessageKind.Ping:
                case MessageKind.Pong:
                    return data.Length == 0;
                default:
                    Assert.Fail($"Unexpected message kind {kind}.");
                    return false;
            }
        }

        private static void WriteTo(
            IBufferWriter<byte> destination,
            MessageKind kind,
            object payload
        )
        {
            switch (kind)
            {
                case MessageKind.Authenticate:
                    EnvelopeWriter.WriteAuthenticate(destination, (AuthenticateMessage)payload);
                    break;
                case MessageKind.JoinRoom:
                    EnvelopeWriter.WriteJoinRoom(destination, (JoinRoomMessage)payload);
                    break;
                case MessageKind.JoinAsSpectator:
                    EnvelopeWriter.WriteJoinAsSpectator(
                        destination,
                        (JoinAsSpectatorMessage)payload
                    );
                    break;
                case MessageKind.Reconnect:
                    EnvelopeWriter.WriteReconnect(destination, (ReconnectMessage)payload);
                    break;
                case MessageKind.AuthorityRequest:
                    EnvelopeWriter.WriteAuthorityRequest(
                        destination,
                        (AuthorityRequestMessage)payload
                    );
                    break;
                case MessageKind.ProvideConnectionInfo:
                    EnvelopeWriter.WriteProvideConnectionInfo(
                        destination,
                        (ProvideConnectionInfoMessage)payload
                    );
                    break;
                case MessageKind.GameData:
                    EnvelopeWriter.WriteGameData(destination, (GameDataMessage)payload);
                    break;
                case MessageKind.RoomOperation:
                    EnvelopeWriter.WriteRoomOperation(destination, (RoomOperationMessage)payload);
                    break;
                case MessageKind.Signal:
                    EnvelopeWriter.WriteSignal(destination, (SignalMessage)payload);
                    break;
                case MessageKind.TransportStatus:
                    EnvelopeWriter.WriteTransportStatus(
                        destination,
                        (TransportStatusMessage)payload
                    );
                    break;
                default:
                    Assert.Fail($"No writer for kind {kind}.");
                    break;
            }
        }

        private static FixtureMessage BuildFromFixture(string wire)
        {
            using JsonDocument doc = JsonDocument.Parse(wire);
            JsonElement root = doc.RootElement;
            string type = root.GetProperty("type").GetString()!;
            JsonElement data = root.TryGetProperty("data", out JsonElement d) ? d : default;

            switch (type)
            {
                case "Authenticate":
                {
                    AuthenticateMessage message = new AuthenticateMessage(
                        appId: OptionalString(data, "app_id"),
                        sdkVersion: OptionalString(data, "sdk_version"),
                        platform: OptionalString(data, "platform"),
                        protocolVersion: OptionalUInt32(data, "protocol_version"),
                        supportedTransports: OptionalArray(data, "supported_transports"),
                        supportedTopologies: OptionalArray(data, "supported_topologies"),
                        requestedCapabilities: OptionalArray(data, "requested_capabilities")
                    );
                    return new FixtureMessage(
                        wire,
                        MessageKind.Authenticate,
                        message,
                        w => EnvelopeWriter.WriteAuthenticate(w, in message)
                    );
                }

                case "JoinRoom":
                {
                    JoinRoomMessage message = new JoinRoomMessage(
                        data.GetProperty("game_name").GetString()!,
                        data.GetProperty("player_name").GetString()!,
                        roomCode: OptionalString(data, "room_code"),
                        maxPlayers: OptionalUInt32(data, "max_players"),
                        supportsAuthority: OptionalBool(data, "supports_authority"),
                        relayTransport: OptionalString(data, "relay_transport"),
                        password: OptionalString(data, "password")
                    );
                    return new FixtureMessage(
                        wire,
                        MessageKind.JoinRoom,
                        message,
                        w => EnvelopeWriter.WriteJoinRoom(w, in message)
                    );
                }

                case "GameData":
                {
                    uint? key = OptionalUInt32(data, "key");
                    GameDataMessage message = new GameDataMessage(
                        RawProperty(root, "data", "data"),
                        key is not null ? GameDataClass.Latest
                            : OptionalString(data, "class") == "volatile" ? GameDataClass.Volatile
                            : GameDataClass.Reliable,
                        key.GetValueOrDefault()
                    );
                    return new FixtureMessage(
                        wire,
                        MessageKind.GameData,
                        message,
                        w => EnvelopeWriter.WriteGameData(w, in message)
                    );
                }

                case "AuthorityRequest":
                {
                    AuthorityRequestMessage message = new AuthorityRequestMessage(
                        data.GetProperty("become_authority").GetBoolean()
                    );
                    return new FixtureMessage(
                        wire,
                        MessageKind.AuthorityRequest,
                        message,
                        w => EnvelopeWriter.WriteAuthorityRequest(w, in message)
                    );
                }

                case "ProvideConnectionInfo":
                {
                    ProvideConnectionInfoMessage message = new ProvideConnectionInfoMessage(
                        RawProperty(root, "data", "connection_info")
                    );
                    return new FixtureMessage(
                        wire,
                        MessageKind.ProvideConnectionInfo,
                        message,
                        w => EnvelopeWriter.WriteProvideConnectionInfo(w, in message)
                    );
                }

                case "Reconnect":
                {
                    ReconnectMessage message = new ReconnectMessage(
                        data.GetProperty("player_id").GetString()!,
                        data.GetProperty("room_id").GetString()!,
                        data.GetProperty("auth_token").GetString()!
                    );
                    return new FixtureMessage(
                        wire,
                        MessageKind.Reconnect,
                        message,
                        w => EnvelopeWriter.WriteReconnect(w, in message)
                    );
                }

                case "JoinAsSpectator":
                {
                    JoinAsSpectatorMessage message = new JoinAsSpectatorMessage(
                        data.GetProperty("game_name").GetString()!,
                        data.GetProperty("room_code").GetString()!,
                        data.GetProperty("spectator_name").GetString()!
                    );
                    return new FixtureMessage(
                        wire,
                        MessageKind.JoinAsSpectator,
                        message,
                        w => EnvelopeWriter.WriteJoinAsSpectator(w, in message)
                    );
                }

                case "RoomOperation":
                {
                    JsonElement operation = data.GetProperty("operation");
                    RoomOperationCommand command = operation.GetProperty("type").GetString() switch
                    {
                        "LeaveRoom" => RoomOperationCommand.LeaveRoom(),
                        "RegenerateRoomCode" => RoomOperationCommand.RegenerateRoomCode(),
                        "KickPlayer" => RoomOperationCommand.KickPlayer(
                            operation.GetProperty("data").GetProperty("player_id").GetString()!
                        ),
                        _ => throw new InvalidOperationException(
                            $"Unhandled fixture command: {operation.GetProperty("type").GetString() ?? "<missing>"}"
                        ),
                    };
                    RoomOperationMessage message = new RoomOperationMessage(
                        data.GetProperty("operation_id").GetString()!,
                        command
                    );
                    return new FixtureMessage(
                        wire,
                        MessageKind.RoomOperation,
                        message,
                        w => EnvelopeWriter.WriteRoomOperation(w, in message)
                    );
                }

                case "Signal":
                {
                    SignalMessage message = new SignalMessage(
                        data.GetProperty("to").GetString()!,
                        data.GetProperty("generation").GetString()!,
                        RawProperty(root, "data", "signal")
                    );
                    return new FixtureMessage(
                        wire,
                        MessageKind.Signal,
                        message,
                        w => EnvelopeWriter.WriteSignal(w, in message)
                    );
                }

                case "TransportStatus":
                {
                    TransportStatusMessage message = new TransportStatusMessage(
                        data.GetProperty("transport").GetString()!,
                        data.GetProperty("connected").GetBoolean()
                    );
                    return new FixtureMessage(
                        wire,
                        MessageKind.TransportStatus,
                        message,
                        w => EnvelopeWriter.WriteTransportStatus(w, in message)
                    );
                }

                case "PlayerReady":
                    return new FixtureMessage(
                        wire,
                        MessageKind.PlayerReady,
                        wire,
                        EnvelopeWriter.WritePlayerReady
                    );
                case "StartGame":
                    return new FixtureMessage(
                        wire,
                        MessageKind.StartGame,
                        wire,
                        EnvelopeWriter.WriteStartGame
                    );
                case "LeaveRoom":
                    return new FixtureMessage(
                        wire,
                        MessageKind.LeaveRoom,
                        wire,
                        EnvelopeWriter.WriteLeaveRoom
                    );
                case "LeaveSpectator":
                    return new FixtureMessage(
                        wire,
                        MessageKind.LeaveSpectator,
                        wire,
                        EnvelopeWriter.WriteLeaveSpectator
                    );
                case "Ping":
                    return new FixtureMessage(
                        wire,
                        MessageKind.Ping,
                        wire,
                        EnvelopeWriter.WritePing
                    );
                default:
                    throw new InvalidOperationException($"Unhandled client fixture type: {type}");
            }
        }

        /// <summary>The exact bytes of a property in the fixture line (raw slice, not re-serialized).</summary>
        private static byte[] RawProperty(JsonElement root, string container, string property)
        {
            string raw = root.GetProperty(container).GetProperty(property).GetRawText();
            return Encoding.UTF8.GetBytes(raw);
        }

        private static string? OptionalString(JsonElement data, string name) =>
            data.ValueKind != JsonValueKind.Object ? null
            : data.TryGetProperty(name, out JsonElement e) ? e.GetString()
            : null;

        private static uint? OptionalUInt32(JsonElement data, string name) =>
            data.ValueKind != JsonValueKind.Object ? null
            : data.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number
                ? e.GetUInt32()
            : null;

        private static bool? OptionalBool(JsonElement data, string name)
        {
            if (
                data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty(name, out JsonElement e)
            )
            {
                return null;
            }

            return e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null;
        }

        private static string[]? OptionalArray(JsonElement data, string name)
        {
            if (
                data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty(name, out JsonElement e)
            )
            {
                return null;
            }

            return e.EnumerateArray().Select(v => v.GetString()!).ToArray();
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length && match; j++)
                {
                    match = haystack[i + j] == needle[j];
                }

                if (match)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
