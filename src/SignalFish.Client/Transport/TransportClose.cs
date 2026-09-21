namespace SignalFish.Client.Transport
{
    using System;

    /// <summary>
    /// A WebSocket close: the raw wire code plus its server-defined meaning.
    /// </summary>
    public readonly struct TransportClose : IEquatable<TransportClose>
    {
        /// <summary>The raw close code as sent on the wire (0 = none).</summary>
        public int Code { get; }

        /// <summary>
        /// The server-defined meaning of <see cref="Code"/>; see the close-code
        /// table in the protocol reference for the reaction table.
        /// </summary>
        public TransportCloseKind Kind { get; }

        /// <summary>Initializes a close descriptor from a raw wire code.</summary>
        public TransportClose(int code)
        {
            Code = code;
            Kind = MapCode(code);
        }

        /// <summary>Maps a raw wire code to its server-defined meaning.</summary>
        public static TransportCloseKind MapCode(int code)
        {
            switch (code)
            {
                case 1000:
                    return TransportCloseKind.Normal;
                case 1006:
                    return TransportCloseKind.Abnormal;
                case 1009:
                    return TransportCloseKind.MessageTooBig;
                case 4000:
                    return TransportCloseKind.ServerShutdown;
                case 4001:
                    return TransportCloseKind.AuthTimeout;
                case 4002:
                    return TransportCloseKind.SlowConsumer;
                case 4003:
                    return TransportCloseKind.ActivityTimeout;
                case 4004:
                    return TransportCloseKind.IdleTimeout;
                case 4005:
                    return TransportCloseKind.RoomInactive;
                case 4006:
                    return TransportCloseKind.InboundRateLimited;
                case 4007:
                    return TransportCloseKind.Kicked;
                default:
                    return TransportCloseKind.Unknown;
            }
        }

        /// <inheritdoc />
        public bool Equals(TransportClose other) => Code == other.Code;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is TransportClose other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => Code;

        /// <inheritdoc />
        public override string ToString() => FormattableString.Invariant($"{Code} ({Kind})");

        /// <summary>Equality by wire code.</summary>
        public static bool operator ==(TransportClose left, TransportClose right) =>
            left.Equals(right);

        /// <summary>Inequality by wire code.</summary>
        public static bool operator !=(TransportClose left, TransportClose right) =>
            !left.Equals(right);
    }
}
