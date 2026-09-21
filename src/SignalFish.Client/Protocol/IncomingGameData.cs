namespace SignalFish.Client.Protocol
{
    using System;

    /// <summary>
    /// The inbound (S→C) <c>GameData</c> relay frame: the sending player
    /// plus the verbatim JSON payload. The payload is relayed without
    /// inspection — the application owns its schema (the MessagePack codec
    /// surface lands with v3; this is the v2 relay floor). v3 delivery
    /// metadata (<c>class</c>/<c>key</c>) is tolerated as unknown fields
    /// until the delivery work.
    /// </summary>
    public readonly struct IncomingGameData : IEquatable<IncomingGameData>
    {
        /// <summary>Gets the player that sent the payload.</summary>
        public Guid FromPlayer { get; }

        /// <summary>Gets the verbatim JSON payload, as UTF-8 bytes.</summary>
        public ReadOnlyMemory<byte> Payload => _payload;

        private readonly ReadOnlyMemory<byte> _payload;

        /// <summary>Initializes a new inbound game-data frame.</summary>
        public IncomingGameData(Guid fromPlayer, ReadOnlyMemory<byte> payload)
        {
            FromPlayer = fromPlayer;
            _payload = payload;
        }

        /// <inheritdoc />
        public bool Equals(IncomingGameData other) =>
            FromPlayer == other.FromPlayer && _payload.Span.SequenceEqual(other._payload.Span);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is IncomingGameData other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(FromPlayer);
            hash.Add(ProtocolHash.Of(_payload.Span));
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(IncomingGameData left, IncomingGameData right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(IncomingGameData left, IncomingGameData right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>GameData</c> envelope (the
        /// <see cref="EnvelopeEvent.Data"/> slice) into the sender id and
        /// the verbatim payload slice. Unknown fields are skipped; a
        /// repeated known key is rejected. Returns <see langword="false"/>
        /// for malformed input or a missing <c>from_player</c>/<c>data</c>.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out IncomingGameData message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            Guid fromPlayer = default;
            ReadOnlyMemory<byte> payload = default;
            bool senderSeen = false;
            bool payloadSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "from_player"))
                {
                    if (senderSeen || !scanner.TryReadGuid(valueRaw, out fromPlayer))
                    {
                        return false;
                    }

                    senderSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "data"))
                {
                    if (payloadSeen)
                    {
                        return false;
                    }

                    (int Offset, int Length) slice = valueRaw.GetOffsetAndLength(data.Length);
                    payload = data.Slice(slice.Offset, slice.Length);
                    payloadSeen = true;
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || !senderSeen || !payloadSeen)
            {
                return false;
            }

            message = new IncomingGameData(fromPlayer, payload);
            return true;
        }
    }
}
