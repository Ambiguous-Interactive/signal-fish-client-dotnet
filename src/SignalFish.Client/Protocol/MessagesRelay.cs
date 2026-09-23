namespace SignalFish.Client.Protocol
{
    using System;
    using System.Collections.Generic;

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
    /// without inspection; it must be valid UTF-8 JSON no deeper than
    /// <see cref="EnvelopeWriter.MaxVerbatimPayloadDepth"/> containers,
    /// checked at construction so a refusal precedes every send.
    /// </summary>
    public readonly struct GameDataMessage : IEquatable<GameDataMessage>
    {
        /// <summary>Gets the game-data JSON value, as UTF-8 bytes (relayed verbatim).</summary>
        public ReadOnlyMemory<byte> Payload => _payload;

        /// <summary>Gets the v3 delivery classification.</summary>
        public GameDataClass Class { get; }

        /// <summary>Gets the coalescing key; meaningful only for <see cref="GameDataClass.Latest"/>.</summary>
        public uint Key { get; }

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
            EnvelopeWriter.RequireJsonValue(payload.Span, "data (payload)");
            _payload = payload;
            Class = classification;

            /*
                The key is only meaningful alongside `class: "latest"`, so
                illegal class/key pairings stay unrepresentable.
            */
            Key = classification == GameDataClass.Latest ? key : 0;
        }

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

                if (scanner.KeyIs(keyRaw, "data"))
                {
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

            if (state != JsonMemberState.EndObject || !payloadSeen)
            {
                return false;
            }

            message = new GameDataMessage(payload, classification, key);
            return true;
        }

        /// <summary>
        /// Reads a wire class token (<c>reliable</c>/<c>latest</c>/<c>volatile</c>)
        /// as its enum value without materializing the string. Non-string
        /// values fail; an escaped spelling matches only if it decodes to
        /// the exact token (any other spelling is a malformed delivery
        /// class — the server answers <c>INVALID_INPUT</c> for it).
        /// </summary>
        internal static bool TryReadClassToken(
            JsonScanner scanner,
            Range valueRaw,
            out GameDataClass classification
        )
        {
            if (!scanner.IsQuotedValue(valueRaw))
            {
                classification = default(GameDataClass);
                return false;
            }

            if (scanner.KeyIs(valueRaw, "reliable"))
            {
                classification = GameDataClass.Reliable;
                return true;
            }

            if (scanner.KeyIs(valueRaw, "latest"))
            {
                classification = GameDataClass.Latest;
                return true;
            }

            if (scanner.KeyIs(valueRaw, "volatile"))
            {
                classification = GameDataClass.Volatile;
                return true;
            }

            classification = default(GameDataClass);
            return false;
        }
    }

    /// <summary>
    /// Why one v3 <c>DeliveryReport</c> gap range exists: the class's
    /// retention policy dropped the range before it reached this client.
    /// </summary>
    public enum DeliveryGapReason : byte
    {
        /// <summary>Sentinel for <c>default(DeliveryGapReason)</c>; not a gap reason.</summary>
        [Obsolete(
            "This value only exists so the enum default (0) is not a gap reason. Compare against default(DeliveryGapReason) instead."
        )]
        None = 0,

        /// <summary>A newer value for the message's key superseded the range.</summary>
        LatestSuperseded = 1,

        /// <summary>The latest-value queue was full and the range was dropped.</summary>
        LatestDroppedFull = 2,

        /// <summary>Opportunistic volatile delivery dropped the range.</summary>
        VolatileDropped = 3,

        /// <summary>The recipient cannot parse the payload format.</summary>
        UnsupportedFormat = 4,
    }

    /// <summary>
    /// Delivery counters for the v3 reliable (relay-floor) class: frames
    /// delivered, ranges abandoned because a slow recipient would have
    /// closed, and frames skipped as an unsupported format. All fields are
    /// required on the wire.
    /// </summary>
    public readonly struct ReliableDeliveryCounters : IEquatable<ReliableDeliveryCounters>
    {
        /// <summary>Gets the delivered frame count (required).</summary>
        public ulong Delivered { get; }

        /// <summary>Gets the abandoned range count (required).</summary>
        public ulong Abandoned { get; }

        /// <summary>Gets the unsupported-format frame count (required).</summary>
        public ulong UnsupportedFormat { get; }

        /// <summary>Initializes a new <see cref="ReliableDeliveryCounters"/> value.</summary>
        public ReliableDeliveryCounters(ulong delivered, ulong abandoned, ulong unsupportedFormat)
        {
            Delivered = delivered;
            Abandoned = abandoned;
            UnsupportedFormat = unsupportedFormat;
        }

        /// <inheritdoc />
        public bool Equals(ReliableDeliveryCounters other) =>
            Delivered == other.Delivered
            && Abandoned == other.Abandoned
            && UnsupportedFormat == other.UnsupportedFormat;

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is ReliableDeliveryCounters other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(Delivered);
            hash.Add(Abandoned);
            hash.Add(UnsupportedFormat);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(
            ReliableDeliveryCounters left,
            ReliableDeliveryCounters right
        ) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(
            ReliableDeliveryCounters left,
            ReliableDeliveryCounters right
        ) => !left.Equals(right);

        /// <summary>
        /// Decodes one <c>reliable</c> counters object. Unknown fields are
        /// skipped; a repeated key, a wrong-typed value, or a missing
        /// required field is rejected. Returns
        /// <see langword="false"/> for malformed input.
        /// </summary>
        internal static bool TryDecode(
            ReadOnlyMemory<byte> data,
            out ReliableDeliveryCounters counters
        )
        {
            counters = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            ulong delivered = 0;
            ulong abandoned = 0;
            ulong unsupportedFormat = 0;
            bool deliveredSeen = false;
            bool abandonedSeen = false;
            bool unsupportedSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "delivered"))
                {
                    if (deliveredSeen || !scanner.TryReadUInt64(valueRaw, out delivered))
                    {
                        return false;
                    }

                    deliveredSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "abandoned"))
                {
                    if (abandonedSeen || !scanner.TryReadUInt64(valueRaw, out abandoned))
                    {
                        return false;
                    }

                    abandonedSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "unsupported_format"))
                {
                    if (unsupportedSeen || !scanner.TryReadUInt64(valueRaw, out unsupportedFormat))
                    {
                        return false;
                    }

                    unsupportedSeen = true;
                }

                state = scanner.EndMember();
            }

            if (
                state != JsonMemberState.EndObject
                || !deliveredSeen
                || !abandonedSeen
                || !unsupportedSeen
            )
            {
                return false;
            }

            counters = new ReliableDeliveryCounters(delivered, abandoned, unsupportedFormat);
            return true;
        }
    }

    /// <summary>
    /// Delivery counters for the v3 latest (coalescing) class: frames
    /// delivered, values superseded by a newer key match, ranges dropped
    /// because the queue was full, ranges abandoned for a slow recipient,
    /// and frames skipped as an unsupported format. All fields are
    /// required on the wire.
    /// </summary>
    public readonly struct LatestDeliveryCounters : IEquatable<LatestDeliveryCounters>
    {
        /// <summary>Gets the delivered frame count (required).</summary>
        public ulong Delivered { get; }

        /// <summary>Gets the superseded value count (required).</summary>
        public ulong Superseded { get; }

        /// <summary>Gets the dropped-full range count (required).</summary>
        public ulong DroppedFull { get; }

        /// <summary>Gets the abandoned range count (required).</summary>
        public ulong Abandoned { get; }

        /// <summary>Gets the unsupported-format frame count (required).</summary>
        public ulong UnsupportedFormat { get; }

        /// <summary>Initializes a new <see cref="LatestDeliveryCounters"/> value.</summary>
        public LatestDeliveryCounters(
            ulong delivered,
            ulong superseded,
            ulong droppedFull,
            ulong abandoned,
            ulong unsupportedFormat
        )
        {
            Delivered = delivered;
            Superseded = superseded;
            DroppedFull = droppedFull;
            Abandoned = abandoned;
            UnsupportedFormat = unsupportedFormat;
        }

        /// <inheritdoc />
        public bool Equals(LatestDeliveryCounters other) =>
            Delivered == other.Delivered
            && Superseded == other.Superseded
            && DroppedFull == other.DroppedFull
            && Abandoned == other.Abandoned
            && UnsupportedFormat == other.UnsupportedFormat;

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is LatestDeliveryCounters other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(Delivered);
            hash.Add(Superseded);
            hash.Add(DroppedFull);
            hash.Add(Abandoned);
            hash.Add(UnsupportedFormat);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(LatestDeliveryCounters left, LatestDeliveryCounters right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(LatestDeliveryCounters left, LatestDeliveryCounters right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes one <c>latest</c> counters object. Unknown fields are
        /// skipped; a repeated key, a wrong-typed value, or a missing
        /// required field is rejected. Returns
        /// <see langword="false"/> for malformed input.
        /// </summary>
        internal static bool TryDecode(
            ReadOnlyMemory<byte> data,
            out LatestDeliveryCounters counters
        )
        {
            counters = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            ulong delivered = 0;
            ulong superseded = 0;
            ulong droppedFull = 0;
            ulong abandoned = 0;
            ulong unsupportedFormat = 0;
            bool deliveredSeen = false;
            bool supersededSeen = false;
            bool droppedFullSeen = false;
            bool abandonedSeen = false;
            bool unsupportedSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "delivered"))
                {
                    if (deliveredSeen || !scanner.TryReadUInt64(valueRaw, out delivered))
                    {
                        return false;
                    }

                    deliveredSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "superseded"))
                {
                    if (supersededSeen || !scanner.TryReadUInt64(valueRaw, out superseded))
                    {
                        return false;
                    }

                    supersededSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "dropped_full"))
                {
                    if (droppedFullSeen || !scanner.TryReadUInt64(valueRaw, out droppedFull))
                    {
                        return false;
                    }

                    droppedFullSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "abandoned"))
                {
                    if (abandonedSeen || !scanner.TryReadUInt64(valueRaw, out abandoned))
                    {
                        return false;
                    }

                    abandonedSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "unsupported_format"))
                {
                    if (unsupportedSeen || !scanner.TryReadUInt64(valueRaw, out unsupportedFormat))
                    {
                        return false;
                    }

                    unsupportedSeen = true;
                }

                state = scanner.EndMember();
            }

            if (
                state != JsonMemberState.EndObject
                || !deliveredSeen
                || !supersededSeen
                || !droppedFullSeen
                || !abandonedSeen
                || !unsupportedSeen
            )
            {
                return false;
            }

            counters = new LatestDeliveryCounters(
                delivered,
                superseded,
                droppedFull,
                abandoned,
                unsupportedFormat
            );
            return true;
        }
    }

    /// <summary>
    /// Delivery counters for the v3 volatile (opportunistic) class: frames
    /// delivered, frames dropped under load, ranges abandoned for a slow
    /// recipient, and frames skipped as an unsupported format. All fields
    /// are required on the wire.
    /// </summary>
    public readonly struct VolatileDeliveryCounters : IEquatable<VolatileDeliveryCounters>
    {
        /// <summary>Gets the delivered frame count (required).</summary>
        public ulong Delivered { get; }

        /// <summary>Gets the dropped frame count (required).</summary>
        public ulong Dropped { get; }

        /// <summary>Gets the abandoned range count (required).</summary>
        public ulong Abandoned { get; }

        /// <summary>Gets the unsupported-format frame count (required).</summary>
        public ulong UnsupportedFormat { get; }

        /// <summary>Initializes a new <see cref="VolatileDeliveryCounters"/> value.</summary>
        public VolatileDeliveryCounters(
            ulong delivered,
            ulong dropped,
            ulong abandoned,
            ulong unsupportedFormat
        )
        {
            Delivered = delivered;
            Dropped = dropped;
            Abandoned = abandoned;
            UnsupportedFormat = unsupportedFormat;
        }

        /// <inheritdoc />
        public bool Equals(VolatileDeliveryCounters other) =>
            Delivered == other.Delivered
            && Dropped == other.Dropped
            && Abandoned == other.Abandoned
            && UnsupportedFormat == other.UnsupportedFormat;

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is VolatileDeliveryCounters other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(Delivered);
            hash.Add(Dropped);
            hash.Add(Abandoned);
            hash.Add(UnsupportedFormat);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(
            VolatileDeliveryCounters left,
            VolatileDeliveryCounters right
        ) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(
            VolatileDeliveryCounters left,
            VolatileDeliveryCounters right
        ) => !left.Equals(right);

        /// <summary>
        /// Decodes one <c>volatile</c> counters object. Unknown fields are
        /// skipped; a repeated key, a wrong-typed value, or a missing
        /// required field is rejected. Returns
        /// <see langword="false"/> for malformed input.
        /// </summary>
        internal static bool TryDecode(
            ReadOnlyMemory<byte> data,
            out VolatileDeliveryCounters counters
        )
        {
            counters = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            ulong delivered = 0;
            ulong dropped = 0;
            ulong abandoned = 0;
            ulong unsupportedFormat = 0;
            bool deliveredSeen = false;
            bool droppedSeen = false;
            bool abandonedSeen = false;
            bool unsupportedSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "delivered"))
                {
                    if (deliveredSeen || !scanner.TryReadUInt64(valueRaw, out delivered))
                    {
                        return false;
                    }

                    deliveredSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "dropped"))
                {
                    if (droppedSeen || !scanner.TryReadUInt64(valueRaw, out dropped))
                    {
                        return false;
                    }

                    droppedSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "abandoned"))
                {
                    if (abandonedSeen || !scanner.TryReadUInt64(valueRaw, out abandoned))
                    {
                        return false;
                    }

                    abandonedSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "unsupported_format"))
                {
                    if (unsupportedSeen || !scanner.TryReadUInt64(valueRaw, out unsupportedFormat))
                    {
                        return false;
                    }

                    unsupportedSeen = true;
                }

                state = scanner.EndMember();
            }

            if (
                state != JsonMemberState.EndObject
                || !deliveredSeen
                || !droppedSeen
                || !abandonedSeen
                || !unsupportedSeen
            )
            {
                return false;
            }

            counters = new VolatileDeliveryCounters(
                delivered,
                dropped,
                abandoned,
                unsupportedFormat
            );
            return true;
        }
    }

    /// <summary>
    /// The per-class delivery accounting block of a v3
    /// <c>DeliveryReport</c>: one required counters object for each of the
    /// reliable, latest, and volatile classes.
    /// </summary>
    public readonly struct DeliveryCountersByClass : IEquatable<DeliveryCountersByClass>
    {
        /// <summary>Gets the reliable-class counters (required).</summary>
        public ReliableDeliveryCounters Reliable { get; }

        /// <summary>Gets the latest-class counters (required).</summary>
        public LatestDeliveryCounters Latest { get; }

        /// <summary>Gets the volatile-class counters (required).</summary>
        public VolatileDeliveryCounters Volatile { get; }

        /// <summary>Initializes a new <see cref="DeliveryCountersByClass"/> value.</summary>
        public DeliveryCountersByClass(
            ReliableDeliveryCounters reliable,
            LatestDeliveryCounters latest,
            VolatileDeliveryCounters volatileCounters
        )
        {
            Reliable = reliable;
            Latest = latest;
            Volatile = volatileCounters;
        }

        /// <inheritdoc />
        public bool Equals(DeliveryCountersByClass other) =>
            Reliable == other.Reliable && Latest == other.Latest && Volatile == other.Volatile;

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is DeliveryCountersByClass other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(Reliable);
            hash.Add(Latest);
            hash.Add(Volatile);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(
            DeliveryCountersByClass left,
            DeliveryCountersByClass right
        ) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(
            DeliveryCountersByClass left,
            DeliveryCountersByClass right
        ) => !left.Equals(right);

        /// <summary>
        /// Decodes the <c>per_class</c> object. Unknown fields are skipped;
        /// a repeated key, a wrong-typed value, or a missing class
        /// sub-object is rejected. Returns
        /// <see langword="false"/> for malformed input.
        /// </summary>
        internal static bool TryDecode(
            ReadOnlyMemory<byte> data,
            out DeliveryCountersByClass counters
        )
        {
            counters = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            ReliableDeliveryCounters reliable = default;
            LatestDeliveryCounters latest = default;
            VolatileDeliveryCounters volatileCounters = default;
            bool reliableSeen = false;
            bool latestSeen = false;
            bool volatileSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "reliable"))
                {
                    if (
                        reliableSeen
                        || !JsonScanner.TryReadObjectSlice(
                            data,
                            valueRaw,
                            out ReadOnlyMemory<byte> slice
                        )
                        || !ReliableDeliveryCounters.TryDecode(slice, out reliable)
                    )
                    {
                        return false;
                    }

                    reliableSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "latest"))
                {
                    if (
                        latestSeen
                        || !JsonScanner.TryReadObjectSlice(
                            data,
                            valueRaw,
                            out ReadOnlyMemory<byte> slice
                        )
                        || !LatestDeliveryCounters.TryDecode(slice, out latest)
                    )
                    {
                        return false;
                    }

                    latestSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "volatile"))
                {
                    if (
                        volatileSeen
                        || !JsonScanner.TryReadObjectSlice(
                            data,
                            valueRaw,
                            out ReadOnlyMemory<byte> slice
                        )
                        || !VolatileDeliveryCounters.TryDecode(slice, out volatileCounters)
                    )
                    {
                        return false;
                    }

                    volatileSeen = true;
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || !reliableSeen || !latestSeen || !volatileSeen)
            {
                return false;
            }

            counters = new DeliveryCountersByClass(reliable, latest, volatileCounters);
            return true;
        }
    }

    /// <summary>
    /// One contiguous undelivered range from a v3 <c>DeliveryReport</c>:
    /// sequences <see cref="FromSeq"/>..<see cref="ToSeq"/> from a sender
    /// were withheld under the named policy. All fields are required.
    /// </summary>
    public readonly struct DeliveryGap : IEquatable<DeliveryGap>
    {
        /// <summary>Gets the sender whose range was withheld (required).</summary>
        public Guid FromPlayer { get; }

        /// <summary>Gets the delivery epoch the range belongs to (required).</summary>
        public uint Epoch { get; }

        /// <summary>Gets the first withheld sequence, inclusive (required).</summary>
        public ulong FromSeq { get; }

        /// <summary>Gets the last withheld sequence, inclusive (required).</summary>
        public ulong ToSeq { get; }

        /// <summary>Gets the retention policy that withheld the range (required).</summary>
        public DeliveryGapReason Reason { get; }

        /// <summary>Initializes a new <see cref="DeliveryGap"/> value.</summary>
        public DeliveryGap(
            Guid fromPlayer,
            uint epoch,
            ulong fromSeq,
            ulong toSeq,
            DeliveryGapReason reason
        )
        {
            FromPlayer = fromPlayer;
            Epoch = epoch;
            FromSeq = fromSeq;
            ToSeq = toSeq;
            Reason = reason;
        }

        /// <inheritdoc />
        public bool Equals(DeliveryGap other) =>
            FromPlayer == other.FromPlayer
            && Epoch == other.Epoch
            && FromSeq == other.FromSeq
            && ToSeq == other.ToSeq
            && Reason == other.Reason;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is DeliveryGap other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(FromPlayer);
            hash.Add(Epoch);
            hash.Add(FromSeq);
            hash.Add(ToSeq);
            hash.Add(Reason);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(DeliveryGap left, DeliveryGap right) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(DeliveryGap left, DeliveryGap right) => !left.Equals(right);

        /// <summary>
        /// Decodes one gap object. Unknown fields are skipped; a repeated
        /// key, a wrong-typed value, or an unknown reason token is
        /// rejected. Returns <see langword="false"/> for malformed input
        /// or a missing required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out DeliveryGap gap)
        {
            gap = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            Guid fromPlayer = default;
            uint epoch = 0;
            ulong fromSeq = 0;
            ulong toSeq = 0;
            DeliveryGapReason reason = default(DeliveryGapReason);
            bool playerSeen = false;
            bool epochSeen = false;
            bool fromSeqSeen = false;
            bool toSeqSeen = false;
            bool reasonSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "from_player"))
                {
                    if (playerSeen || !scanner.TryReadGuid(valueRaw, out fromPlayer))
                    {
                        return false;
                    }

                    playerSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "epoch"))
                {
                    if (epochSeen || !scanner.TryReadUInt32(valueRaw, out epoch))
                    {
                        return false;
                    }

                    epochSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "from_seq"))
                {
                    if (fromSeqSeen || !scanner.TryReadUInt64(valueRaw, out fromSeq))
                    {
                        return false;
                    }

                    fromSeqSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "to_seq"))
                {
                    if (toSeqSeen || !scanner.TryReadUInt64(valueRaw, out toSeq))
                    {
                        return false;
                    }

                    toSeqSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "reason"))
                {
                    if (reasonSeen || !TryReadReasonToken(scanner, valueRaw, out reason))
                    {
                        return false;
                    }

                    reasonSeen = true;
                }

                state = scanner.EndMember();
            }

            if (
                state != JsonMemberState.EndObject
                || !playerSeen
                || !epochSeen
                || !fromSeqSeen
                || !toSeqSeen
                || !reasonSeen
            )
            {
                return false;
            }

            gap = new DeliveryGap(fromPlayer, epoch, fromSeq, toSeq, reason);
            return true;
        }

        /// <summary>
        /// Reads a scanned value that must be a JSON array of gap objects
        /// into a list (decode path; allocates the result). A malformed
        /// element fails the decode.
        /// </summary>
        internal static bool TryReadArray(
            ReadOnlyMemory<byte> data,
            Range valueRaw,
            out IReadOnlyList<DeliveryGap>? values
        )
        {
            values = null;
            (int Offset, int Length) s = valueRaw.GetOffsetAndLength(data.Length);
            if (s.Length < 2 || data.Span[s.Offset] != (byte)'[')
            {
                return false;
            }

            JsonScanner scanner = new JsonScanner(data.Span.Slice(s.Offset, s.Length));
            scanner.SkipWhitespace();
            if (scanner.Expect((byte)'[') != default(DecodeError))
            {
                return false;
            }

            List<DeliveryGap> list = new List<DeliveryGap>();
            scanner.SkipWhitespace();
            if (scanner.Peek == (byte)']')
            {
                if (scanner.Expect((byte)']') != default(DecodeError))
                {
                    return false;
                }
            }
            else
            {
                while (true)
                {
                    scanner.SkipWhitespace();

                    /*
                        Elements validate at member-value depth, the same
                        level JsonScanner.ScanMember uses for payload values.
                    */
                    if (
                        scanner.ScanValueRaw(2, JsonScanner.MaxDepth, out Range element)
                        != default(DecodeError)
                    )
                    {
                        return false;
                    }

                    (int EOffset, int ELength) e = element.GetOffsetAndLength(s.Length);
                    if (
                        !TryDecode(data.Slice(s.Offset + e.EOffset, e.ELength), out DeliveryGap gap)
                    )
                    {
                        return false;
                    }

                    list.Add(gap);
                    scanner.SkipWhitespace();
                    byte next = scanner.Peek;
                    if (next == (byte)',')
                    {
                        if (scanner.Expect((byte)',') != default(DecodeError))
                        {
                            return false;
                        }

                        continue;
                    }

                    if (next == (byte)']')
                    {
                        if (scanner.Expect((byte)']') != default(DecodeError))
                        {
                            return false;
                        }

                        break;
                    }

                    return false;
                }
            }

            scanner.SkipWhitespace();
            if (!scanner.IsEof)
            {
                return false;
            }

            values = list;
            return true;
        }

        /// <summary>
        /// Reads a wire reason token
        /// (<c>latest_superseded</c>/<c>latest_dropped_full</c>/<c>volatile_dropped</c>/<c>unsupported_format</c>)
        /// as its enum value without materializing the string. Non-string
        /// values fail; any other spelling is a malformed gap.
        /// </summary>
        private static bool TryReadReasonToken(
            JsonScanner scanner,
            Range valueRaw,
            out DeliveryGapReason reason
        )
        {
            if (!scanner.IsQuotedValue(valueRaw))
            {
                reason = default(DeliveryGapReason);
                return false;
            }

            if (scanner.KeyIs(valueRaw, "latest_superseded"))
            {
                reason = DeliveryGapReason.LatestSuperseded;
                return true;
            }

            if (scanner.KeyIs(valueRaw, "latest_dropped_full"))
            {
                reason = DeliveryGapReason.LatestDroppedFull;
                return true;
            }

            if (scanner.KeyIs(valueRaw, "volatile_dropped"))
            {
                reason = DeliveryGapReason.VolatileDropped;
                return true;
            }

            if (scanner.KeyIs(valueRaw, "unsupported_format"))
            {
                reason = DeliveryGapReason.UnsupportedFormat;
                return true;
            }

            reason = default(DeliveryGapReason);
            return false;
        }
    }

    /// <summary>
    /// Payload of the inbound v3 <c>DeliveryReport</c> message (S→C):
    /// per-class delivery accounting for this connection plus the
    /// undelivered ranges the retention policies withheld. The
    /// <c>per_class</c> block is required; <c>gaps</c> is optional
    /// (absent or explicit JSON null means none).
    /// </summary>
    public readonly struct DeliveryReportMessage : IEquatable<DeliveryReportMessage>
    {
        /// <summary>Gets the per-class delivery counters (required).</summary>
        public DeliveryCountersByClass PerClass { get; }

        /// <summary>Gets the withheld ranges (empty when the frame omits them).</summary>
        public IReadOnlyList<DeliveryGap> Gaps { get; }

        /// <summary>Initializes a new <see cref="DeliveryReportMessage"/> payload.</summary>
        public DeliveryReportMessage(
            in DeliveryCountersByClass perClass,
            IReadOnlyList<DeliveryGap>? gaps = null
        )
        {
            PerClass = perClass;
            Gaps = gaps ?? Array.Empty<DeliveryGap>();
        }

        /// <inheritdoc />
        public bool Equals(DeliveryReportMessage other) =>
            PerClass.Equals(other.PerClass) && GapsSequenceEquals(Gaps, other.Gaps);

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is DeliveryReportMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(PerClass);
            hash.Add(Gaps.Count);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(DeliveryReportMessage left, DeliveryReportMessage right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(DeliveryReportMessage left, DeliveryReportMessage right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>DeliveryReport</c>
        /// envelope (the <see cref="EnvelopeEvent.Data"/> slice). Unknown
        /// fields are skipped; a repeated key, a wrong-typed value, a
        /// missing counter sub-object or field, or an unknown gap reason
        /// is rejected. Returns <see langword="false"/> for malformed
        /// input or a missing <c>per_class</c>.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out DeliveryReportMessage message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            DeliveryCountersByClass perClass = default;
            IReadOnlyList<DeliveryGap>? gaps = null;
            bool perClassSeen = false;
            bool gapsSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "per_class"))
                {
                    if (
                        perClassSeen
                        || !JsonScanner.TryReadObjectSlice(
                            data,
                            valueRaw,
                            out ReadOnlyMemory<byte> slice
                        )
                        || !DeliveryCountersByClass.TryDecode(slice, out perClass)
                    )
                    {
                        return false;
                    }

                    perClassSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "gaps"))
                {
                    if (gapsSeen)
                    {
                        return false;
                    }

                    gapsSeen = true;
                    if (!scanner.TryReadNull(valueRaw))
                    {
                        if (!DeliveryGap.TryReadArray(data, valueRaw, out gaps))
                        {
                            return false;
                        }
                    }
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || !perClassSeen)
            {
                return false;
            }

            message = new DeliveryReportMessage(perClass, gaps);
            return true;
        }

        /// <summary>
        /// Element-wise equality for gap lists; two
        /// <see langword="null"/> lists are equal, a
        /// <see langword="null"/> list never equals a non-null one.
        /// </summary>
        private static bool GapsSequenceEquals(
            IReadOnlyList<DeliveryGap>? left,
            IReadOnlyList<DeliveryGap>? right
        )
        {
            if (left is null || right is null)
            {
                return left is null && right is null;
            }

            if (left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Payload of the inbound v3 <c>RelayStats</c> message (S→C):
    /// per-interval relay accounting for this connection (frames sent to
    /// and dropped for this client, plus relay backpressure events). All
    /// fields are required.
    /// </summary>
    public readonly struct RelayStatsMessage : IEquatable<RelayStatsMessage>
    {
        /// <summary>Gets the accounting interval length in milliseconds (required).</summary>
        public ulong IntervalMs { get; }

        /// <summary>Gets the frames sent to this client in the interval (required).</summary>
        public ulong SentToYou { get; }

        /// <summary>Gets the frames dropped for this client in the interval (required).</summary>
        public ulong DroppedForYou { get; }

        /// <summary>Gets the relay backpressure events in the interval (required).</summary>
        public ulong BackpressureEvents { get; }

        /// <summary>Initializes a new <see cref="RelayStatsMessage"/> payload.</summary>
        public RelayStatsMessage(
            ulong intervalMs,
            ulong sentToYou,
            ulong droppedForYou,
            ulong backpressureEvents
        )
        {
            IntervalMs = intervalMs;
            SentToYou = sentToYou;
            DroppedForYou = droppedForYou;
            BackpressureEvents = backpressureEvents;
        }

        /// <inheritdoc />
        public bool Equals(RelayStatsMessage other) =>
            IntervalMs == other.IntervalMs
            && SentToYou == other.SentToYou
            && DroppedForYou == other.DroppedForYou
            && BackpressureEvents == other.BackpressureEvents;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is RelayStatsMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(IntervalMs);
            hash.Add(SentToYou);
            hash.Add(DroppedForYou);
            hash.Add(BackpressureEvents);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(RelayStatsMessage left, RelayStatsMessage right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(RelayStatsMessage left, RelayStatsMessage right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>RelayStats</c> envelope
        /// (the <see cref="EnvelopeEvent.Data"/> slice). Unknown fields are
        /// skipped; a repeated key or a wrong-typed value is rejected.
        /// Returns <see langword="false"/> for malformed input or a
        /// missing required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out RelayStatsMessage message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            ulong intervalMs = 0;
            ulong sentToYou = 0;
            ulong droppedForYou = 0;
            ulong backpressureEvents = 0;
            bool intervalSeen = false;
            bool sentSeen = false;
            bool droppedSeen = false;
            bool backpressureSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "interval_ms"))
                {
                    if (intervalSeen || !scanner.TryReadUInt64(valueRaw, out intervalMs))
                    {
                        return false;
                    }

                    intervalSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "sent_to_you"))
                {
                    if (sentSeen || !scanner.TryReadUInt64(valueRaw, out sentToYou))
                    {
                        return false;
                    }

                    sentSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "dropped_for_you"))
                {
                    if (droppedSeen || !scanner.TryReadUInt64(valueRaw, out droppedForYou))
                    {
                        return false;
                    }

                    droppedSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "backpressure_events"))
                {
                    if (
                        backpressureSeen || !scanner.TryReadUInt64(valueRaw, out backpressureEvents)
                    )
                    {
                        return false;
                    }

                    backpressureSeen = true;
                }

                state = scanner.EndMember();
            }

            if (
                state != JsonMemberState.EndObject
                || !intervalSeen
                || !sentSeen
                || !droppedSeen
                || !backpressureSeen
            )
            {
                return false;
            }

            message = new RelayStatsMessage(
                intervalMs,
                sentToYou,
                droppedForYou,
                backpressureEvents
            );
            return true;
        }
    }
}
