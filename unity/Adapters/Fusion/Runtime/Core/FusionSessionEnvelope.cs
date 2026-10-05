#nullable enable
namespace SignalFish.Client.Adapters.Fusion
{
    using System;
    using System.Text;

    /// <summary>
    /// The JSON envelope that carries the Fusion session name across the
    /// room's game-data lane:
    /// <c>{ "signal_fish_fusion_session": "&lt;name&gt;" }</c>. The
    /// room's game-data lane is shared with the game's own payloads, so
    /// the envelope is recognized by its exact quoted property name and
    /// everything else is ignored — a foreign or malformed payload
    /// decodes as absent, never as an error. Session names are
    /// restricted to a conservative charset ([A-Za-z0-9_-], at most 128
    /// chars — Fusion's own default names are GUIDs, which fit), which
    /// the host validates before it publishes, so the codec scans bytes
    /// and needs no JSON parser.
    /// </summary>
    public static class FusionSessionEnvelope
    {
        /// <summary>The envelope's property name, matched verbatim.</summary>
        public const string PropertyName = "signal_fish_fusion_session";

        /// <summary>The longest session name the envelope accepts.</summary>
        public const int MaxSessionNameLength = 128;

        /// <summary>The longest envelope the codec writes (a name at the length bound).</summary>
        public const int MaxEnvelopeLength = FixedLength + MaxSessionNameLength;

        private const int FixedLength = 33;

        /// <summary>Gets whether the session name fits the envelope's charset and length bound.</summary>
        public static bool IsValidSessionName(string? sessionName)
        {
            if (string.IsNullOrEmpty(sessionName) || sessionName.Length > MaxSessionNameLength)
            {
                return false;
            }

            foreach (char c in sessionName)
            {
                if (!IsSessionNameChar(c))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Writes the envelope as UTF-8; <see langword="false"/> when the
        /// session name is invalid or the buffer is too small.
        /// </summary>
        public static bool TryWrite(string sessionName, Span<byte> destination, out int written)
        {
            written = 0;
            if (!IsValidSessionName(sessionName))
            {
                return false;
            }

            if (destination.Length < FixedLength + sessionName.Length)
            {
                return false;
            }

            int cursor = 0;
            destination[cursor++] = (byte)'{';
            destination[cursor++] = (byte)'"';
            foreach (char c in PropertyName)
            {
                destination[cursor++] = (byte)c;
            }

            destination[cursor++] = (byte)'"';
            destination[cursor++] = (byte)':';
            destination[cursor++] = (byte)'"';
            foreach (char c in sessionName)
            {
                destination[cursor++] = (byte)c;
            }

            destination[cursor++] = (byte)'"';
            destination[cursor++] = (byte)'}';
            written = cursor;
            return true;
        }

        /// <summary>
        /// Reads the session name out of a JSON object payload;
        /// <see langword="false"/> when the payload carries no envelope.
        /// Unknown fields and surrounding content are ignored.
        /// </summary>
        public static bool TryRead(ReadOnlySpan<byte> payload, out string sessionName)
        {
            sessionName = string.Empty;
            Span<byte> key = stackalloc byte[PropertyName.Length + 2];
            WriteKeyLiteral(key);

            int keyStart = payload.IndexOf(key);
            while (keyStart >= 0)
            {
                int scanner = keyStart + key.Length;
                if (TryReadValue(payload, ref scanner, out string candidate))
                {
                    sessionName = candidate;
                    return true;
                }

                int next = payload.Slice(scanner).IndexOf(key);
                keyStart = next < 0 ? -1 : scanner + next;
            }

            return false;
        }

        private static bool TryReadValue(
            ReadOnlySpan<byte> payload,
            ref int scanner,
            out string sessionName
        )
        {
            sessionName = string.Empty;
            if (!SkipToByte(payload, ref scanner, (byte)':'))
            {
                return false;
            }

            while (scanner < payload.Length && IsSpace(payload[scanner]))
            {
                scanner++;
            }

            if (scanner >= payload.Length || payload[scanner] != (byte)'"')
            {
                return false;
            }

            scanner++;
            int start = scanner;
            while (scanner < payload.Length && payload[scanner] != (byte)'"')
            {
                if (
                    !IsSessionNameChar((char)payload[scanner])
                    || scanner - start >= MaxSessionNameLength
                )
                {
                    return false;
                }

                scanner++;
            }

            if (scanner >= payload.Length || scanner == start)
            {
                return false;
            }

            int length = scanner - start;
            sessionName = Encoding.ASCII.GetString(payload.Slice(start, length));
            scanner++;
            return true;
        }

        private static bool SkipToByte(ReadOnlySpan<byte> payload, ref int scanner, byte expected)
        {
            while (scanner < payload.Length)
            {
                byte current = payload[scanner];
                if (current == expected)
                {
                    scanner++;
                    return true;
                }

                if (!IsSpace(current))
                {
                    return false;
                }

                scanner++;
            }

            return false;
        }

        private static void WriteKeyLiteral(Span<byte> destination)
        {
            destination[0] = (byte)'"';
            for (int i = 0; i < PropertyName.Length; i++)
            {
                destination[i + 1] = (byte)PropertyName[i];
            }

            destination[PropertyName.Length + 1] = (byte)'"';
        }

        private static bool IsSessionNameChar(char c)
        {
            return (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9')
                || c == '-'
                || c == '_';
        }

        private static bool IsSpace(byte b)
        {
            return b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n';
        }
    }
}
