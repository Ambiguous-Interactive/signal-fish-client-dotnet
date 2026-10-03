#nullable enable
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
    /// the client surfaces every delivered frame in arrival order. On a
    /// negotiated-v3 connection the server stamps every relayed frame
    /// with the sender's <see cref="Seq"/>/<see cref="Epoch"/>; the v2
    /// wire omits both (delivery accounting arrives out-of-band via
    /// <c>DeliveryReport</c> on v2).
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

        /// <summary>
        /// Gets the sender's per-room relay sequence stamp
        /// (negotiated-v3 connections); <see langword="null"/> when the
        /// v2 wire omitted it.
        /// </summary>
        public ulong? Seq { get; }

        /// <summary>
        /// Gets the sender's incarnation epoch stamp (negotiated-v3
        /// connections); <see langword="null"/> when the v2 wire omitted
        /// it.
        /// </summary>
        public uint? Epoch { get; }

        /// <summary>
        /// Gets the wire classification; <see langword="null"/> when the
        /// frame carried no metadata (the relay floor). Delivery
        /// accounting feeds this presence-aware form to the engine — the
        /// v2 floor requires absent metadata.
        /// </summary>
        internal GameDataClass? WireClass => _classPresent ? Class : null;

        /// <summary>
        /// Gets the wire coalescing key; <see langword="null"/> when the
        /// frame omitted it.
        /// </summary>
        internal uint? WireKey => _keyPresent ? Key : null;

        private readonly ReadOnlyMemory<byte> _payload;
        private readonly bool _classPresent;
        private readonly bool _keyPresent;

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
            : this(fromPlayer, payload, classification, key, seq: null, epoch: null) { }

        /// <summary>Initializes a new inbound stamped game-data frame.</summary>
        public IncomingGameData(
            Guid fromPlayer,
            ReadOnlyMemory<byte> payload,
            GameDataClass classification,
            uint key,
            ulong? seq,
            uint? epoch
        )
            : this(
                fromPlayer,
                payload,
                classification,
                classPresent: false,
                key,
                keyPresent: false,
                seq,
                epoch
            ) { }

        /// <summary>
        /// Initializes a new inbound game-data frame. The presence flags
        /// record what the wire carried; synthesized frames (public
        /// constructors) report no wire metadata.
        /// </summary>
        internal IncomingGameData(
            Guid fromPlayer,
            ReadOnlyMemory<byte> payload,
            GameDataClass classification,
            bool classPresent,
            uint key,
            bool keyPresent,
            ulong? seq,
            uint? epoch
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
            _classPresent = classPresent;
            _keyPresent = keyPresent;
            Seq = seq;
            Epoch = epoch;
        }

        /// <inheritdoc />
        public bool Equals(IncomingGameData other) =>
            FromPlayer == other.FromPlayer
            && Class == other.Class
            && Key == other.Key
            && Seq == other.Seq
            && Epoch == other.Epoch
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
            hash.Add(Seq);
            hash.Add(Epoch);
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
        /// verbatim payload slice, the delivery classification
        /// (<c>class</c>/<c>key</c>; omitted means reliable; an unknown
        /// class token or malformed key fails the frame), and the v3
        /// relay stamps (<c>seq</c>/<c>epoch</c>; omitted on the v2
        /// wire). Unknown fields are skipped; a repeated known key is
        /// rejected. Returns <see langword="false"/> for malformed input
        /// or a missing <c>from_player</c>/<c>data</c>.
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
            bool seqSeen = false;
            bool epochSeen = false;
            GameDataClass classification = GameDataClass.Reliable;
            uint key = 0;
            ulong seq = 0;
            uint epoch = 0;

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
                else if (scanner.KeyIs(keyRaw, "seq"))
                {
                    if (seqSeen || !scanner.TryReadUInt64(valueRaw, out seq))
                    {
                        return false;
                    }

                    seqSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "epoch"))
                {
                    if (epochSeen || !scanner.TryReadUInt32(valueRaw, out epoch))
                    {
                        return false;
                    }

                    epochSeen = true;
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || !senderSeen || !payloadSeen)
            {
                return false;
            }

            message = new IncomingGameData(
                fromPlayer,
                payload,
                classification,
                classSeen,
                key,
                keySeen,
                seqSeen ? seq : null,
                epochSeen ? epoch : null
            );
            return true;
        }
    }
}
