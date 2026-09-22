namespace SignalFish.Client.Reconnection
{
    using System;

    /// <summary>
    /// The automatic-reconnection scheduling details carried by a
    /// <c>Reconnecting</c> or <c>ReconnectAbandoned</c> event; fields are
    /// meaningful only for their event kind (documented per property) and
    /// hold <c>default</c> values otherwise.
    /// </summary>
    public readonly struct ReconnectStatus : IEquatable<ReconnectStatus>
    {
        /// <summary>
        /// The reconnect attempt: for <c>Reconnecting</c>, the 1-based
        /// attempt about to start; for <c>ReconnectAbandoned</c>, the
        /// number of attempts spent.
        /// </summary>
        public int Attempt { get; }

        /// <summary>
        /// The deterministic wait in milliseconds before the announced
        /// attempt; meaningful only for <c>Reconnecting</c>.
        /// </summary>
        public long BackoffMilliseconds { get; }

        /// <summary>
        /// Why the budget ran out (the last close description);
        /// meaningful only for <c>ReconnectAbandoned</c>.
        /// </summary>
        public string? LastReason { get; }

        /// <summary>Initializes the status; constructed only by the client.</summary>
        public ReconnectStatus(int attempt, long backoffMilliseconds, string? lastReason)
        {
            Attempt = attempt;
            BackoffMilliseconds = backoffMilliseconds;
            LastReason = lastReason;
        }

        /// <inheritdoc />
        public bool Equals(ReconnectStatus other)
        {
            return Attempt == other.Attempt
                && BackoffMilliseconds == other.BackoffMilliseconds
                && string.Equals(LastReason, other.LastReason, StringComparison.Ordinal);
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is ReconnectStatus other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + Attempt;
                hash = (hash * 31) + BackoffMilliseconds.GetHashCode();
                hash = (hash * 31) + (LastReason?.GetHashCode(StringComparison.Ordinal) ?? 0);
                return hash;
            }
        }

        /// <summary>Equality by content.</summary>
        public static bool operator ==(ReconnectStatus left, ReconnectStatus right)
        {
            return left.Equals(right);
        }

        /// <summary>Inequality by content.</summary>
        public static bool operator !=(ReconnectStatus left, ReconnectStatus right)
        {
            return !left.Equals(right);
        }
    }
}
