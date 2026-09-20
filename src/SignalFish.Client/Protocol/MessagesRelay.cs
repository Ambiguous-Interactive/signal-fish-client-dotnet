namespace SignalFish.Client.Protocol
{
    using System;

    /// <summary>
    /// The v3 delivery classification of a JSON <c>GameData</c> message.
    /// <see cref="Reliable"/> (the default) omits the metadata entirely and
    /// reproduces the v2 relay-floor wire form; <see cref="Latest"/> pairs
    /// with a sender-defined <c>key</c>; <see cref="Volatile"/> delivers
    /// opportunistically.
    /// </summary>
    public enum GameDataClass : byte
    {
        /// <summary>Sentinel for <c>default(GameDataClass)</c>; not a delivery class.</summary>
        [Obsolete(
            "This value only exists so the enum default (0) is not a delivery class. Construct GameDataMessage explicitly; the writer refuses an unset class."
        )]
        None = 0,

        /// <summary>Relay-floor delivery: preserve the message or close the slow recipient loudly. No wire metadata.</summary>
        Reliable = 1,

        /// <summary>Coalescing delivery: retain only the newest queued value for the message's key.</summary>
        Latest = 2,

        /// <summary>Opportunistic delivery: never paces the sender.</summary>
        Volatile = 3,
    }

    /// <summary>
    /// Payload of the outbound <c>GameData</c> message: the relay payload
    /// plus optional v3 delivery classification. <paramref name="payload"/>
    /// is a verbatim JSON value (object, array, scalar, or null) relayed
    /// without inspection; it must be valid UTF-8 JSON.
    /// </summary>
    public readonly struct GameDataMessage : IEquatable<GameDataMessage>
    {
        private readonly ReadOnlyMemory<byte> _payload;

        /// <summary>
        /// Initializes a new reliable (relay-floor, v2 wire form)
        /// <see cref="GameDataMessage"/>.
        /// </summary>
        /// <param name="payload">The game-data JSON value, as UTF-8 bytes.</param>
        public GameDataMessage(ReadOnlyMemory<byte> payload)
            : this(payload, GameDataClass.Reliable, key: 0) { }

        /// <summary>
        /// Initializes a new classified <see cref="GameDataMessage"/>. The
        /// classification and key always move together:
        /// <see cref="GameDataClass.Latest"/> emits <c>class</c> and
        /// <c>key</c>; every other class omits both, so illegal class/key
        /// pairings are unrepresentable.
        /// </summary>
        /// <param name="payload">The game-data JSON value, as UTF-8 bytes.</param>
        /// <param name="classification">The v3 delivery class.</param>
        /// <param name="key">The sender-defined coalescing key (used by <see cref="GameDataClass.Latest"/> only).</param>
        public GameDataMessage(
            ReadOnlyMemory<byte> payload,
            GameDataClass classification,
            uint key = 0
        )
        {
            _payload = payload;
            Class = classification;

            // The key is only meaningful alongside `class: "latest"`, so
            // illegal class/key pairings stay unrepresentable.
            Key = classification == GameDataClass.Latest ? key : 0;
        }

        /// <summary>Gets the game-data JSON value, as UTF-8 bytes (relayed verbatim).</summary>
        public ReadOnlyMemory<byte> Payload => _payload;

        /// <summary>Gets the v3 delivery classification.</summary>
        public GameDataClass Class { get; }

        /// <summary>Gets the coalescing key; meaningful only for <see cref="GameDataClass.Latest"/>.</summary>
        public uint Key { get; }

        /// <inheritdoc />
        public bool Equals(GameDataMessage other) =>
            _payload.Span.SequenceEqual(other._payload.Span)
            && Class == other.Class
            && Key == other.Key;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is GameDataMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(ProtocolHash.Of(_payload.Span));
            hash.Add(Class);
            hash.Add(Key);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(GameDataMessage left, GameDataMessage right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(GameDataMessage left, GameDataMessage right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>GameData</c> envelope (the
        /// <see cref="EnvelopeEvent.Data"/> slice) into the verbatim payload
        /// plus delivery classification. Unknown fields are skipped.
        /// Returns <see langword="false"/> for malformed input or a missing
        /// <c>data</c> field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out GameDataMessage message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            ReadOnlyMemory<byte> payload = default;
            bool payloadSeen = false;
            GameDataClass classification = GameDataClass.Reliable;
            uint key = 0;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "data"))
                {
                    (int Offset, int Length) slice = valueRaw.GetOffsetAndLength(data.Length);
                    payload = data.Slice(slice.Offset, slice.Length);
                    payloadSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "class"))
                {
                    if (!scanner.TryReadString(valueRaw, out string? classToken))
                    {
                        return false;
                    }

                    if (classToken == "reliable")
                    {
                        classification = GameDataClass.Reliable;
                    }
                    else if (classToken == "latest")
                    {
                        classification = GameDataClass.Latest;
                    }
                    else if (classToken == "volatile")
                    {
                        classification = GameDataClass.Volatile;
                    }
                    else
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "key"))
                {
                    if (!scanner.TryReadUInt32(valueRaw, out key))
                    {
                        return false;
                    }
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || !payloadSeen)
            {
                return false;
            }

            message = new GameDataMessage(payload, classification, key);
            return true;
        }
    }
}
