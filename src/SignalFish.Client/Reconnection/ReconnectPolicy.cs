namespace SignalFish.Client.Reconnection
{
    using System;
    using System.Collections.Generic;
    using SignalFish.Client.Transport;

    /// <summary>
    /// Opt-in automatic reconnection for <see cref="SignalFish.Client.Async.SignalFishClient"/>:
    /// a transport factory plus a deterministic exponential backoff (no
    /// jitter — the SDK carries no RNG; de-synchronize retry storms by
    /// varying the initial delay per client or add jitter inside the
    /// factory). The driver waits the computed delay between attempts,
    /// opens a fresh transport from the factory, re-authenticates, and
    /// reclaims a retained player seat automatically. Absent a policy,
    /// recovery stays fully manual. The attempt budget resets whenever a
    /// connection reaches the authenticated phase; a close code listed in
    /// <see cref="TerminalCloseCodes"/> ends the session instead of
    /// retrying (unlisted codes — notably a server going away — keep
    /// reconnecting).
    /// </summary>
    public sealed class ReconnectPolicy
    {
        /// <summary>The default delay before the first reconnect attempt.</summary>
        public const int DefaultInitialBackoffMilliseconds = 500;

        /// <summary>The default ceiling for a single backoff delay.</summary>
        public const int DefaultMaxBackoffMilliseconds = 8_000;

        /// <summary>The default backoff growth factor per consecutive attempt.</summary>
        public const double DefaultMultiplier = 2.0;

        /// <summary>The default reconnect-attempt budget between authentications.</summary>
        public const int DefaultMaxAttempts = 5;

        /// <summary>Gets the factory producing a fresh, unconnected transport per attempt.</summary>
        public Func<ITransport> TransportFactory { get; }

        /// <summary>Gets the delay in milliseconds before the first reconnect attempt.</summary>
        public int InitialBackoffMilliseconds { get; }

        /// <summary>Gets the ceiling in milliseconds for any single backoff delay.</summary>
        public int MaxBackoffMilliseconds { get; }

        /// <summary>Gets the backoff growth factor applied per consecutive attempt.</summary>
        public double Multiplier { get; }

        /// <summary>
        /// Gets the reconnect-attempt budget; it resets whenever a
        /// connection reaches the authenticated phase. Exhaustion emits
        /// <c>ReconnectAbandoned</c> and ends the session.
        /// </summary>
        public int MaxAttempts { get; }

        /// <summary>
        /// Gets the close codes that end the session instead of
        /// reconnecting (empty by default: no close code is inspected).
        /// </summary>
        public IReadOnlyList<int> TerminalCloseCodes { get; }

        /// <summary>Initializes the policy; every parameter has the documented default.</summary>
        public ReconnectPolicy(
            Func<ITransport> transportFactory,
            int initialBackoffMilliseconds = DefaultInitialBackoffMilliseconds,
            int maxBackoffMilliseconds = DefaultMaxBackoffMilliseconds,
            double multiplier = DefaultMultiplier,
            int maxAttempts = DefaultMaxAttempts
        )
        {
            if (transportFactory is null)
            {
                throw new ArgumentNullException(nameof(transportFactory));
            }

            if (initialBackoffMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(initialBackoffMilliseconds),
                    "The initial backoff cannot be negative."
                );
            }

            if (maxBackoffMilliseconds < initialBackoffMilliseconds)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxBackoffMilliseconds),
                    "The backoff ceiling cannot sit below the initial delay."
                );
            }

            if (multiplier < 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(multiplier),
                    "The backoff multiplier must be at least 1."
                );
            }

            if (maxAttempts < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxAttempts),
                    "The attempt budget must be positive."
                );
            }

            TransportFactory = transportFactory;
            InitialBackoffMilliseconds = initialBackoffMilliseconds;
            MaxBackoffMilliseconds = maxBackoffMilliseconds;
            Multiplier = multiplier;
            MaxAttempts = maxAttempts;
            TerminalCloseCodes = Array.Empty<int>();
        }

        private ReconnectPolicy(ReconnectPolicy source, int[] terminalCloseCodes)
        {
            TransportFactory = source.TransportFactory;
            InitialBackoffMilliseconds = source.InitialBackoffMilliseconds;
            MaxBackoffMilliseconds = source.MaxBackoffMilliseconds;
            Multiplier = source.Multiplier;
            MaxAttempts = source.MaxAttempts;
            TerminalCloseCodes = terminalCloseCodes;
        }

        /// <summary>
        /// Derives a policy that ends the session (right after the
        /// terminal <c>Disconnected</c>) when a close carries one of
        /// <paramref name="closeCodes"/>; every unlisted code keeps
        /// reconnecting. Classify known-permanent closes here (a kicked
        /// seat, for example) to skip the doomed reconnect round.
        /// </summary>
        public ReconnectPolicy WithTerminalCloseCodes(params int[] closeCodes)
        {
            if (closeCodes is null)
            {
                throw new ArgumentNullException(nameof(closeCodes));
            }

            int[] codes = (int[])closeCodes.Clone();
            Array.Sort(codes);
            return new ReconnectPolicy(this, codes);
        }

        /// <summary>
        /// The deterministic delay before attempt <paramref name="attempt"/>
        /// (1-based): the initial delay grown by the multiplier per
        /// consecutive attempt, capped at the ceiling. No jitter, ever.
        /// </summary>
        public long BackoffForAttempt(int attempt)
        {
            if (attempt < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(attempt),
                    "Attempt numbers are 1-based."
                );
            }

            double grown = InitialBackoffMilliseconds * Math.Pow(Multiplier, attempt - 1);
            double capped = Math.Min(grown, MaxBackoffMilliseconds);
            return (long)Math.Round(capped);
        }

        /// <summary>True when this close code ends the session instead of reconnecting.</summary>
        public bool IsTerminalClose(int code)
        {
            foreach (int terminal in TerminalCloseCodes)
            {
                if (terminal == code)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
