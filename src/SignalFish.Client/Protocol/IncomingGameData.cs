namespace SignalFish.Client.Protocol
{
    using System;

    /// <summary>
    /// The inbound (S→C) <c>GameData</c> relay frame: the sending player,
    /// the verbatim JSON payload, and the sender's v3 delivery
    /// classification (omitted metadata means
    /// <see cref="GameDataClass.Reliable"/>). The payload is relayed
    /// without inspection — the application owns its schema. The
    /// server performs all latest-wins coalescing and volatile dropping;
    /// the client surfaces every delivered frame in arrival order and the
    /// sequence accounting arrives out-of-band via <c>DeliveryReport</c>
    /// (decoded with the delivery-accounting work).
    /// </summary>
    public readonly struct IncomingGameData : IEquatable<IncomingGameData>
    {
        /// <summary>Gets the player that sent the payload.</summary>
        public Guid FromPlayer { get; }

        /// <summary>Gets the verbatim JSON payload, as UTF-8 bytes.</summary>
        public ReadOnlyMemory<byte> Payload => _payload;

        /// <summary>
        /// Gets the sender's delivery class; omitted metadata means
        /// <see cref="GameDataClass.Reliable"/>.
        /// </summary>
        public GameDataClass Class { get; }

        /// <summary>
        /// Gets the sender's coalescing key; meaningful only for
        /// <see cref="GameDataClass.Latest"/>.
        /// </summary>
        public uint Key { get; }

        private readonly ReadOnlyMemory<byte> _payload;

        /// <summary>Initializes a new inbound game-data frame.</summary>
        public IncomingGameData(Guid fromPlayer, ReadOnlyMemory<byte> payload)
            : this(fromPlayer, payload, GameDataClass.Reliable, key: 0) { }

        /// <summary>Initializes a new inbound classified game-data frame.</summary>
        public IncomingGameData(
            Guid fromPlayer,
            ReadOnlyMemory<byte> payload,
            GameDataClass classification,
            uint key = 0
        )
        {
            FromPlayer = fromPlayer;
            _payload = payload;
            Class = classification;

            /*
                The key is only meaningful alongside `class: "latest"`, so
                illegal class/key pairings stay unrepresentable.
            */
            Key = classification == GameDataClass.Latest ? key : 0;
        }

        /// <inheritdoc />
        public bool Equals(IncomingGameData other) =>
            FromPlayer == other.FromPlayer
            && Class == other.Class
            && Key == other.Key
            && _payload.Span.SequenceEqual(other._payload.Span);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is IncomingGameData other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(FromPlayer);
            hash.Add(Class);
            hash.Add(Key);
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
        /// <see cref="EnvelopeEvent.Data"/> slice) into the sender id, the
        /// verbatim payload slice, and the delivery classification
        /// (<c>class</c>/<c>key</c>; omitted means reliable; an unknown
        /// class token or malformed key fails the frame). Unknown fields
        /// are skipped; a repeated known key is rejected. Returns
        /// <see langword="false"/> for malformed input or a missing
        /// <c>from_player</c>/<c>data</c>.
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
            bool classSeen = false;
            bool keySeen = false;
            GameDataClass classification = GameDataClass.Reliable;
            uint key = 0;

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
                else if (scanner.KeyIs(keyRaw, "class"))
                {
                    if (
                        classSeen
                        || !GameDataMessage.TryReadClassToken(scanner, valueRaw, out classification)
                    )
                    {
                        return false;
                    }

                    classSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "key"))
                {
                    if (keySeen || !scanner.TryReadUInt32(valueRaw, out key))
                    {
                        return false;
                    }

                    keySeen = true;
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || !senderSeen || !payloadSeen)
            {
                return false;
            }

            message = new IncomingGameData(fromPlayer, payload, classification, key);
            return true;
        }
    }
}
