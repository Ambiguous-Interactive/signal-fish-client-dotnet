#nullable enable
namespace SignalFish.Client.Adapters.Ngo
{
    using System;
    using SignalFish.Client.Adapters;

    /// <summary>
    /// The connection-approval payload an NGO client presents when it
    /// connects: exactly 16 bytes — the client's Signal Fish player id in
    /// the relay's RFC-4122 network-order UUID spelling (shared
    /// <see cref="AdapterWire"/>), so the host's approval check is a
    /// roster lookup and the id round-trips byte-for-byte with the
    /// relay's own <c>from_player</c> stamps.
    /// </summary>
    public static class ConnectionApprovalPayload
    {
        /// <summary>Gets the payload length in bytes.</summary>
        public const int Length = 16;

        /// <summary>Allocates and writes the payload for the player id.</summary>
        public static byte[] Write(Guid playerId)
        {
            byte[] payload = new byte[Length];
            AdapterWire.WriteNetworkUuid(payload, playerId);
            return payload;
        }

        /// <summary>
        /// Reads the player id out of an approval payload;
        /// <see langword="false"/> when the payload is any other length
        /// or carries the empty id.
        /// </summary>
        public static bool TryRead(ReadOnlySpan<byte> payload, out Guid playerId)
        {
            if (payload.Length != Length)
            {
                playerId = Guid.Empty;
                return false;
            }

            playerId = AdapterWire.ReadNetworkUuid(payload);
            if (playerId == Guid.Empty)
            {
                return false;
            }

            return true;
        }
    }
}
