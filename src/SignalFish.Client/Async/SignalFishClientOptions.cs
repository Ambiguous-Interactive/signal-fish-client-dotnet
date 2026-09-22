namespace SignalFish.Client.Async
{
    using System;

    /// <summary>
    /// Buffers and heartbeat timing for <see cref="SignalFishClient"/>:
    /// a 256-event bounded queue (a full queue pauses the driver loop —
    /// events are never dropped), a 1024-command bounded queue (a full
    /// queue fails fast sends; Reliable variants wait), a 64 KiB inbound
    /// per-frame bound, and the ~30 s ping with a 2x-ping liveness
    /// timeout.
    /// </summary>
    public sealed class SignalFishClientOptions
    {
        /// <summary>The default event-queue capacity.</summary>
        public const int DefaultEventCapacity = 256;

        /// <summary>The default command-queue capacity.</summary>
        public const int DefaultCommandCapacity = 1024;

        /// <summary>The default inbound per-frame byte bound.</summary>
        public const int DefaultMaxFrameBytes = 64 * 1024;

        /// <summary>The default heartbeat cadence in milliseconds.</summary>
        public const int DefaultHeartbeatIntervalMilliseconds = 30_000;

        /// <summary>The default liveness timeout in milliseconds (silence longer than this is death).</summary>
        public const int DefaultHeartbeatTimeoutMilliseconds = 60_000;

        /// <summary>The default per-wake command-send budget.</summary>
        public const int DefaultCommandsPerWake = 64;

        /// <summary>Gets the event-queue capacity; a full queue pauses the driver loop until drained.</summary>
        public int EventCapacity { get; }

        /// <summary>Gets the command-queue capacity; a full queue fails fast sends until drained.</summary>
        public int CommandCapacity { get; }

        /// <summary>Gets the maximum inbound frame size in bytes; larger frames are protocol violations.</summary>
        public int MaxFrameBytes { get; }

        /// <summary>Gets the heartbeat cadence in milliseconds (elapsed since the last ping sent).</summary>
        public int HeartbeatIntervalMilliseconds { get; }

        /// <summary>
        /// Gets the liveness timeout in milliseconds: when no server frame
        /// arrives within this window the session is declared dead and torn
        /// down. The protocol default pairs 2x the ping cadence with it.
        /// </summary>
        public int HeartbeatTimeoutMilliseconds { get; }

        /// <summary>Gets the maximum number of queued commands sent per driver-loop wake.</summary>
        public int CommandsPerWake { get; }

        /// <summary>Initializes the options; every parameter has the documented default.</summary>
        public SignalFishClientOptions(
            int eventCapacity = DefaultEventCapacity,
            int commandCapacity = DefaultCommandCapacity,
            int maxFrameBytes = DefaultMaxFrameBytes,
            int heartbeatIntervalMilliseconds = DefaultHeartbeatIntervalMilliseconds,
            int heartbeatTimeoutMilliseconds = DefaultHeartbeatTimeoutMilliseconds,
            int commandsPerWake = DefaultCommandsPerWake
        )
        {
            if (eventCapacity < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(eventCapacity),
                    "The event-queue capacity must be positive."
                );
            }

            if (commandCapacity < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(commandCapacity),
                    "The command-queue capacity must be positive."
                );
            }

            if (maxFrameBytes < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxFrameBytes),
                    "The per-frame byte bound must be positive."
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

            if (commandsPerWake < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(commandsPerWake),
                    "The per-wake command budget must be positive."
                );
            }

            EventCapacity = eventCapacity;
            CommandCapacity = commandCapacity;
            MaxFrameBytes = maxFrameBytes;
            HeartbeatIntervalMilliseconds = heartbeatIntervalMilliseconds;
            HeartbeatTimeoutMilliseconds = heartbeatTimeoutMilliseconds;
            CommandsPerWake = commandsPerWake;
        }
    }
}
