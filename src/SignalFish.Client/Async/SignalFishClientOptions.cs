namespace SignalFish.Client.Async
{
    using System;
    using SignalFish.Client.Reconnection;

    /// <summary>
    /// Buffers and heartbeat timing for <see cref="SignalFishClient"/>:
    /// a 256-event bounded queue (a full queue pauses the driver loop —
    /// events are never dropped), a 1024-command bounded queue (a full
    /// queue fails fast sends; Reliable variants wait), a 64 KiB inbound
    /// per-frame bound, and the ~30 s ping with a 2x-ping liveness
    /// timeout. Also carries the authentication credentials the client
    /// sends on every (re)connect handshake.
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

        /// <summary>The default graceful shutdown budget in milliseconds.</summary>
        public const int DefaultShutdownTimeoutMilliseconds = 1_000;

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

        /// <summary>
        /// Gets the graceful shutdown budget in milliseconds: disposing
        /// inside a room first sends the role's leave and waits up to this
        /// long for the typed confirmation before aborting the connection.
        /// Zero aborts immediately.
        /// </summary>
        public int ShutdownTimeoutMilliseconds { get; }

        /// <summary>
        /// Gets the opt-in automatic-reconnection policy; <c>null</c>
        /// (the default) keeps recovery fully manual.
        /// </summary>
        public ReconnectPolicy? ReconnectPolicy { get; }

        /// <summary>
        /// Gets the public app ID sent on every <c>Authenticate</c>
        /// (<c>null</c> omits it — open deployments accept the handshake
        /// without one).
        /// </summary>
        public string? AppId { get; }

        /// <summary>
        /// Gets the optional tenant credential minted by the deployment's
        /// control plane, sent on every <c>Authenticate</c>
        /// (<c>null</c> omits it). A secret: <see cref="ToString"/>
        /// redacts it; rotate by issuing a fresh token and building new
        /// options.
        /// </summary>
        public string? ConnectToken { get; }

        /// <summary>Gets the SDK version string sent on every <c>Authenticate</c>.</summary>
        public string SdkVersion { get; }

        /// <summary>Gets the platform token sent on every <c>Authenticate</c>.</summary>
        public string Platform { get; }

        /// <summary>Initializes the options; every parameter has the documented default.</summary>
        public SignalFishClientOptions(
            int eventCapacity = DefaultEventCapacity,
            int commandCapacity = DefaultCommandCapacity,
            int maxFrameBytes = DefaultMaxFrameBytes,
            int heartbeatIntervalMilliseconds = DefaultHeartbeatIntervalMilliseconds,
            int heartbeatTimeoutMilliseconds = DefaultHeartbeatTimeoutMilliseconds,
            int commandsPerWake = DefaultCommandsPerWake,
            int shutdownTimeoutMilliseconds = DefaultShutdownTimeoutMilliseconds,
            ReconnectPolicy? reconnectPolicy = null,
            string? appId = null,
            string? connectToken = null,
            string? sdkVersion = SignalFishClientInfo.SdkVersion,
            string? platform = SignalFishClientInfo.Platform
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

            if (shutdownTimeoutMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(shutdownTimeoutMilliseconds),
                    "The shutdown budget cannot be negative."
                );
            }

            EventCapacity = eventCapacity;
            CommandCapacity = commandCapacity;
            MaxFrameBytes = maxFrameBytes;
            HeartbeatIntervalMilliseconds = heartbeatIntervalMilliseconds;
            HeartbeatTimeoutMilliseconds = heartbeatTimeoutMilliseconds;
            CommandsPerWake = commandsPerWake;
            ShutdownTimeoutMilliseconds = shutdownTimeoutMilliseconds;
            ReconnectPolicy = reconnectPolicy;
            AppId = appId;
            ConnectToken = connectToken;
            SdkVersion = sdkVersion ?? SignalFishClientInfo.SdkVersion;
            Platform = platform ?? SignalFishClientInfo.Platform;
        }

        /// <summary>
        /// Log-safe form: the connect token is redacted to presence
        /// (<c>&lt;redacted&gt;</c>/<c>&lt;none&gt;</c>), never its value.
        /// </summary>
        public override string ToString()
        {
            return nameof(EventCapacity)
                + "="
                + EventCapacity
                + ", "
                + nameof(CommandCapacity)
                + "="
                + CommandCapacity
                + ", "
                + nameof(AppId)
                + "="
                + (AppId ?? "<none>")
                + ", "
                + nameof(ConnectToken)
                + "="
                + (ConnectToken is null ? "<none>" : "<redacted>")
                + ", "
                + nameof(SdkVersion)
                + "="
                + SdkVersion
                + ", "
                + nameof(Platform)
                + "="
                + Platform;
        }
    }
}
