namespace SignalFish.Client
{
    using System;
    using System.Reflection;

    /// <summary>
    /// Protocol and SDK constants shared across the client.
    /// The public API surface targets the 0.1.0 milestone; it is not yet
    /// frozen by a tagged release.
    /// </summary>
    public static class SignalFishClientInfo
    {
        /// <summary>Platform identifier reported in <c>Authenticate.platform</c>.</summary>
        public const string Platform = "dotnet";

        /// <summary>Default Signal Fish server port.</summary>
        public const int DefaultPort = 3536;

        /// <summary>WebSocket path for the mandatory v2 relay floor.</summary>
        public const string V2WebSocketPath = "/v2/ws";

        /// <summary>WebSocket path for optional negotiated v3 capabilities.</summary>
        public const string V3WebSocketPath = "/v3/ws";

        /// <summary>
        /// The version reported when the assembly carries no MinVer stamp
        /// (Unity and other engine consumers compile the mirrored source
        /// without the git tag pipeline).
        /// </summary>
        private const string DefaultSdkVersion = "0.1.0";

        /// <summary>
        /// The informational version toolchains emit when they stamp no
        /// meaningful version (Unity's default assembly info).
        /// </summary>
        private const string UnstampedVersion = "0.0.0";

        /// <summary>
        /// Gets the SDK version reported to the server in
        /// <c>Authenticate.sdk_version</c>: the git-tag version MinVer
        /// stamps into the assembly at build time (M9.1), falling back to
        /// <see cref="DefaultSdkVersion"/> for source distributions
        /// (Unity) whose assembly carries no stamp.
        /// </summary>
        public static string SdkVersion { get; } = ResolveSdkVersion();

        /// <summary>
        /// Builds the default v2 endpoint, e.g. <c>ws://localhost:3536/v2/ws</c>.
        /// IPv6 hosts are bracketed (<c>::1</c> becomes <c>ws://[::1]:3536/v2/ws</c>).
        /// </summary>
        public static Uri DefaultServerUri(string host = "localhost")
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("Host must not be null or empty.", nameof(host));
            }

            if (host.Contains(':', StringComparison.Ordinal) && host[0] != '[')
            {
                host = $"[{host}]";
            }

            return new Uri($"ws://{host}:{DefaultPort}{V2WebSocketPath}");
        }

        private static string ResolveSdkVersion()
        {
            string? informational = typeof(SignalFishClientInfo)
                .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (string.IsNullOrEmpty(informational) || informational == UnstampedVersion)
            {
                return DefaultSdkVersion;
            }

            /*
                The .NET SDK appends the source revision after '+' when it
                builds in CI; the server gets the bare semantic version.
            */
            int metadata = informational.IndexOf('+', StringComparison.Ordinal);
            return metadata < 0 ? informational : informational.Substring(0, metadata);
        }
    }
}
