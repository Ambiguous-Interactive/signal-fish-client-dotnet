namespace SignalFish.Client.Polling
{
    using System;

    /// <summary>
    /// Budgets and heartbeat timing for <see cref="SignalFishPollingClient"/>.
    /// Defaults match the protocol's per-poll discipline: at most 64 frames
    /// per poll (frames above the budget stay queued in the transport for
    /// the next poll), a 64 KiB per-frame bound, a 256-event ring, a ~30 s
    /// ping cadence, and a 2x-ping liveness timeout that declares the
    /// session dead.
    /// </summary>
    public sealed class PollingClientOptions
    {
        /// <summary>The default per-poll frame budget.</summary>
        public const int DefaultMaxFramesPerPoll = 64;

        /// <summary>The default inbound per-frame byte bound.</summary>
        public const int DefaultMaxFrameBytes = 64 * 1024;

        /// <summary>The default event-ring capacity.</summary>
        public const int DefaultEventCapacity = 256;

        /// <summary>The default heartbeat cadence in milliseconds.</summary>
        public const int DefaultHeartbeatIntervalMilliseconds = 30_000;

        /// <summary>The default liveness timeout in milliseconds (silence longer than this is death).</summary>
        public const int DefaultHeartbeatTimeoutMilliseconds = 60_000;

        /// <summary>Gets the maximum number of transport frames consumed per <c>Poll</c>.</summary>
        public int MaxFramesPerPoll { get; }

        /// <summary>Gets the maximum inbound frame size in bytes; larger frames are protocol violations.</summary>
        public int MaxFrameBytes { get; }

        /// <summary>Gets the event-ring capacity; a full ring pauses frame consumption until drained.</summary>
        public int EventCapacity { get; }

        /// <summary>Gets the heartbeat cadence in milliseconds (elapsed since the last ping sent).</summary>
        public int HeartbeatIntervalMilliseconds { get; }

        /// <summary>
        /// Gets the liveness timeout in milliseconds: when no server frame
        /// arrives within this window the session is declared dead and torn
        /// down. The protocol default pairs 2x the ping cadence with it.
        /// </summary>
        public int HeartbeatTimeoutMilliseconds { get; }

        /// <summary>Initializes the options; every parameter has the documented default.</summary>
        public PollingClientOptions(
            int maxFramesPerPoll = DefaultMaxFramesPerPoll,
            int maxFrameBytes = DefaultMaxFrameBytes,
            int eventCapacity = DefaultEventCapacity,
            int heartbeatIntervalMilliseconds = DefaultHeartbeatIntervalMilliseconds,
            int heartbeatTimeoutMilliseconds = DefaultHeartbeatTimeoutMilliseconds
        )
        {
            if (maxFramesPerPoll < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxFramesPerPoll),
                    "The per-poll frame budget must be positive."
                );
            }

            if (maxFrameBytes < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxFrameBytes),
                    "The per-frame byte bound must be positive."
                );
            }

            if (eventCapacity < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(eventCapacity),
                    "The event-ring capacity must be positive."
                );
            }

            if (heartbeatIntervalMilliseconds < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(heartbeatIntervalMilliseconds),
                    "The heartbeat cadence must be positive."
                );
            }

            if (heartbeatTimeoutMilliseconds < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(heartbeatTimeoutMilliseconds),
                    "The liveness timeout must be positive."
                );
            }

            MaxFramesPerPoll = maxFramesPerPoll;
            MaxFrameBytes = maxFrameBytes;
            EventCapacity = eventCapacity;
            HeartbeatIntervalMilliseconds = heartbeatIntervalMilliseconds;
            HeartbeatTimeoutMilliseconds = heartbeatTimeoutMilliseconds;
        }
    }
}
