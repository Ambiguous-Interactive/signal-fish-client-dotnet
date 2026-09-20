namespace SignalFish.Client.Transport
{
    using System;

    /// <summary>
    /// One frame received from the transport: either a data payload or the
    /// terminal close. The close frame is surfaced exactly once; every
    /// receive after it throws <see cref="TransportClosedException"/>.
    /// The payload memory is owned by the caller (an exact-size copy); the
    /// transport never reuses it.
    /// </summary>
    public readonly struct TransportFrame : IEquatable<TransportFrame>
    {
        /// <summary>Initializes a data frame.</summary>
        public TransportFrame(ReadOnlyMemory<byte> payload, bool isText)
        {
            Payload = payload;
            IsText = isText;
            IsClose = false;
            Close = default;
        }

        /// <summary>Initializes the terminal close frame.</summary>
        public static TransportFrame FromClose(TransportClose close)
        {
            return new TransportFrame(close);
        }

        private TransportFrame(TransportClose close)
        {
            Payload = ReadOnlyMemory<byte>.Empty;
            IsText = false;
            IsClose = true;
            Close = close;
        }

        /// <summary>
        /// The frame payload (exact-size copy, caller-owned). Empty for the
        /// close frame.
        /// </summary>
        public ReadOnlyMemory<byte> Payload { get; }

        /// <summary>
        /// True when the peer sent the frame as a text frame (JSON floor);
        /// false for binary frames and the close frame.
        /// </summary>
        public bool IsText { get; }

        /// <summary>True when this is the terminal close frame.</summary>
        public bool IsClose { get; }

        /// <summary>The close descriptor; meaningful only when <see cref="IsClose"/>.</summary>
        public TransportClose Close { get; }

        /// <summary>Content equality (payload bytes compared element-wise).</summary>
        public bool Equals(TransportFrame other)
        {
            return IsText == other.IsText
                && IsClose == other.IsClose
                && Close == other.Close
                && Payload.Span.SequenceEqual(other.Payload.Span);
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is TransportFrame other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = IsText ? 1 : 0;
                hash = (hash * 31) + (IsClose ? 1 : 0);
                hash = (hash * 31) + Close.Code;
                hash = (hash * 31) + Payload.Length;
                return hash;
            }
        }

        /// <summary>Equality by content.</summary>
        public static bool operator ==(TransportFrame left, TransportFrame right) =>
            left.Equals(right);

        /// <summary>Inequality by content.</summary>
        public static bool operator !=(TransportFrame left, TransportFrame right) =>
            !left.Equals(right);
    }
}
