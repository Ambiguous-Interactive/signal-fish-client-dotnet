namespace SignalFish.Client.Transport
{
    using System;

    /// <summary>
    /// Thrown when the transport is used after its terminal close was already
    /// surfaced, or when an operation races a close and loses. Carries the
    /// close descriptor so reaction logic can key on the close code (data),
    /// not prose.
    /// </summary>
    public sealed class TransportClosedException : InvalidOperationException
    {
        /// <summary>Initializes the exception for a specific close.</summary>
        public TransportClosedException(TransportClose close)
            : base(FormattableString.Invariant($"The transport is closed ({close})."))
        {
            Close = close;
        }

        /// <summary>Initializes the exception for a specific close, wrapping the cause.</summary>
        public TransportClosedException(TransportClose close, Exception innerException)
            : base(
                FormattableString.Invariant($"The transport is closed ({close})."),
                innerException
            )
        {
            Close = close;
        }

        /// <summary>Initializes a standard exception instance.</summary>
        public TransportClosedException()
            : this(default(TransportClose)) { }

        /// <summary>Initializes a standard exception instance.</summary>
        public TransportClosedException(string message)
            : base(message)
        {
            Close = default;
        }

        /// <summary>Initializes a standard exception instance.</summary>
        public TransportClosedException(string message, Exception innerException)
            : base(message, innerException)
        {
            Close = default;
        }

        /// <summary>The close that terminated the transport (default when not close-driven).</summary>
        public TransportClose Close { get; }
    }
}
