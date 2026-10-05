#nullable enable
namespace SignalFish.Client.Adapters.SteamworksNet
{
    using System;
    using System.Text;

    /// <summary>
    /// The JSON envelope that carries a SteamId64 across the room's
    /// game-data lane, under two role-scoped keys:
    /// <c>{ "signal_fish_steam_host": "&lt;id&gt;" }</c> is the host's
    /// id (the host publishes, clients consume — it is the address the
    /// client's Steam sockets connection dials), and
    /// <c>{ "signal_fish_steam_peer": "&lt;id&gt;" }</c> is a client's
    /// id (the client publishes, the host consumes it into the accept
    /// fence). The relay lane is a broadcast, so the role keys are what
    /// keep a client from reading another client's id as the host's.
    /// The lane is shared with the game's own payloads, so an envelope
    /// is recognized by its exact quoted property name and everything
    /// else is ignored — a foreign or malformed payload decodes as
    /// absent, never as an error. An id is a decimal string of 1-20
    /// digits with no leading zero, so it survives every JSON decoder
    /// losslessly (a raw 64-bit number would not); the codec scans
    /// bytes and needs no JSON parser.
    /// </summary>
    public static class SteamIdentityEnvelope
    {
        /// <summary>The host lane's property name, matched verbatim.</summary>
        public const string HostPropertyName = "signal_fish_steam_host";

        /// <summary>The peer lane's property name, matched verbatim.</summary>
        public const string PeerPropertyName = "signal_fish_steam_peer";

        /// <summary>The longest SteamId64 the envelope accepts (20 digits covers the full unsigned 64-bit range).</summary>
        public const int MaxSteamIdLength = 20;

        /// <summary>The longest envelope the codec writes (an id at the length bound).</summary>
        public const int MaxEnvelopeLength = FixedLength + MaxSteamIdLength;

        private const int FixedLength = 29;

        /// <summary>Gets whether the id fits the envelope's charset and length bound.</summary>
        public static bool IsValidSteamId(string? steamId)
        {
            if (string.IsNullOrEmpty(steamId) || steamId.Length > MaxSteamIdLength)
            {
                return false;
            }

            if (steamId[0] < '1' || steamId[0] > '9')
            {
                return false;
            }

            for (int i = 1; i < steamId.Length; i++)
            {
                if (steamId[i] < '0' || steamId[i] > '9')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Writes the envelope as UTF-8 under one of the two lane keys;
        /// <see langword="false"/> when the key or the id is invalid or
        /// the buffer is too small.
        /// </summary>
        public static bool TryWrite(
            string? propertyName,
            string? steamId,
            Span<byte> destination,
            out int written
        )
        {
            written = 0;
            if (!IsLaneKey(propertyName) || !IsValidSteamId(steamId))
            {
                return false;
            }

            if (destination.Length < FixedLength + steamId!.Length)
            {
                return false;
            }

            int cursor = 0;
            destination[cursor++] = (byte)'{';
            destination[cursor++] = (byte)'"';
            foreach (char c in propertyName!)
            {
                destination[cursor++] = (byte)c;
            }

            destination[cursor++] = (byte)'"';
            destination[cursor++] = (byte)':';
            destination[cursor++] = (byte)'"';
            foreach (char c in steamId)
            {
                destination[cursor++] = (byte)c;
            }

            destination[cursor++] = (byte)'"';
            destination[cursor++] = (byte)'}';
            written = cursor;
            return true;
        }

        /// <summary>
        /// Reads a SteamId64 out of a JSON object payload under one of
        /// the two lane keys; <see langword="false"/> when the payload
        /// carries no envelope. Unknown fields and surrounding content
        /// are ignored.
        /// </summary>
        public static bool TryRead(
            ReadOnlySpan<byte> payload,
            string propertyName,
            out string steamId
        )
        {
            steamId = string.Empty;
            if (!IsLaneKey(propertyName))
            {
                return false;
            }

            Span<byte> key = stackalloc byte[propertyName.Length + 2];
            WriteKeyLiteral(key, propertyName);

            int keyStart = payload.IndexOf(key);
            while (keyStart >= 0)
            {
                int scanner = keyStart + key.Length;
                if (TryReadValue(payload, ref scanner, out string candidate))
                {
                    steamId = candidate;
                    return true;
                }

                int next = payload.Slice(scanner).IndexOf(key);
                keyStart = next < 0 ? -1 : scanner + next;
            }

            return false;
        }

        private static bool IsLaneKey(string? propertyName)
        {
            return propertyName == HostPropertyName || propertyName == PeerPropertyName;
        }

        private static bool TryReadValue(
            ReadOnlySpan<byte> payload,
            ref int scanner,
            out string steamId
        )
        {
            steamId = string.Empty;
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
                if (!IsIdChar((char)payload[scanner]) || scanner - start >= MaxSteamIdLength)
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
            steamId = Encoding.ASCII.GetString(payload.Slice(start, length));
            scanner++;
            return IsValidSteamId(steamId);
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

        private static void WriteKeyLiteral(Span<byte> destination, string propertyName)
        {
            destination[0] = (byte)'"';
            for (int i = 0; i < propertyName.Length; i++)
            {
                destination[i + 1] = (byte)propertyName[i];
            }

            destination[propertyName.Length + 1] = (byte)'"';
        }

        private static bool IsIdChar(char c)
        {
            return c >= '0' && c <= '9';
        }

        private static bool IsSpace(byte b)
        {
            return b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n';
        }
    }
}
