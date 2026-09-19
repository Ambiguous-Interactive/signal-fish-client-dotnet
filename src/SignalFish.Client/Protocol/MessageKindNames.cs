using System;

namespace SignalFish.Client.Protocol
{
    /// <summary>
    /// Wire-name table for <see cref="MessageKind"/>. Single source for both
    /// byte-level routing (decode) and human-readable names (diagnostics),
    /// so a routed kind always round-trips to the exact wire type name.
    /// </summary>
    public static class MessageKindNames
    {
        // Order must match the MessageKind enum (index = kind value - 1).
        private static readonly string[] Names =
        {
            "Authenticate",
            "Authenticated",
            "AuthorityRequest",
            "AuthorityResponse",
            "DeliveryReport",
            "Error",
            "GameData",
            "GameStarting",
            "JoinAsSpectator",
            "JoinRoom",
            "LeaveRoom",
            "LeaveSpectator",
            "LobbyStateChanged",
            "NewPeer",
            "Ping",
            "Pong",
            "PlayerJoined",
            "PlayerLeft",
            "PlayerReady",
            "ProtocolInfo",
            "ProvideConnectionInfo",
            "Reconnect",
            "Reconnected",
            "RoomJoined",
            "RoomLeft",
            "RoomOperation",
            "RoomOperationResult",
            "SessionPlan",
            "Signal",
            "StartGame",
            "TransportStatus",
            "PeerTransportStatus",
            "GoingAway",
        };

        private static readonly byte[][] NameBytes = BuildNameBytes();

        /// <summary>
        /// Returns the exact wire <c>type</c> name for a known message kind,
        /// or <see langword="null"/> for <see cref="MessageKind.None"/> or
        /// any value outside the defined kind range.
        /// </summary>
        public static string? ToWireName(MessageKind kind)
        {
            var index = (int)kind - 1;
            return (uint)index < (uint)Names.Length ? Names[index] : null;
        }

        /// <summary>
        /// Routes a wire <c>type</c> value (UTF-8 bytes, quotes excluded) to
        /// its known kind. Comparison is byte-exact; the protocol names are
        /// plain ASCII, so escaped JSON never routes to a known kind.
        /// </summary>
        internal static bool TryRoute(ReadOnlySpan<byte> utf8TypeName, out MessageKind kind)
        {
            for (var i = 0; i < NameBytes.Length; i++)
            {
                if (utf8TypeName.SequenceEqual(NameBytes[i]))
                {
                    kind = (MessageKind)(i + 1);
                    return true;
                }
            }

            kind = MessageKind.None;
            return false;
        }

        private static byte[][] BuildNameBytes()
        {
            var bytes = new byte[Names.Length][];
            for (var i = 0; i < Names.Length; i++)
            {
                bytes[i] = System.Text.Encoding.ASCII.GetBytes(Names[i]);
            }

            return bytes;
        }
    }
}
