using System;

namespace SignalFish.Client
{
    /// <summary>
    /// Protocol and SDK constants shared across the client.
    /// Placeholder scaffold: the public API surface is under active development.
    /// </summary>
    public static class SignalFishClientInfo
    {
        /// <summary>SDK version reported to the server in <c>Authenticate.sdk_version</c>.</summary>
        public const string SdkVersion = "0.1.0";

        /// <summary>Platform identifier reported in <c>Authenticate.platform</c>.</summary>
        public const string Platform = "dotnet";

        /// <summary>Default Signal Fish server port.</summary>
        public const int DefaultPort = 3536;

        /// <summary>WebSocket path for the mandatory v2 relay floor.</summary>
        public const string V2WebSocketPath = "/v2/ws";

        /// <summary>WebSocket path for optional negotiated v3 capabilities.</summary>
        public const string V3WebSocketPath = "/v3/ws";

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

            if (host.Contains(':') && host[0] != '[')
            {
                host = $"[{host}]";
            }

            return new Uri($"ws://{host}:{DefaultPort}{V2WebSocketPath}");
        }
    }
}
