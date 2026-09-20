namespace SignalFish.Client.Protocol
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Payload of the outbound <c>Authenticate</c> message (C→S): optional
    /// credentials plus optional v3 capability negotiation fields. Every
    /// field is optional on the wire — <see langword="null"/> omits it.
    /// Fields are emitted in the canonical order pinned by the golden
    /// fixtures: <c>app_id</c>, <c>sdk_version</c>, <c>platform</c>,
    /// <c>game_data_format</c>, <c>protocol_version</c>,
    /// <c>supported_transports</c>, <c>supported_topologies</c>,
    /// <c>requested_capabilities</c>, <c>connect_token</c>.
    /// </summary>
    public readonly struct AuthenticateMessage : IEquatable<AuthenticateMessage>
    {
        /// <summary>Gets the public app ID (required when the deployment enforces its allowlist).</summary>
        public string? AppId { get; }

        /// <summary>Gets the SDK version for debugging and analytics.</summary>
        public string? SdkVersion { get; }

        /// <summary>Gets the platform token (e.g. <c>unity</c>).</summary>
        public string? Platform { get; }

        /// <summary>Gets the preferred game-data encoding (absent = JSON text frames).</summary>
        public string? GameDataFormat { get; }

        /// <summary>
        /// Gets the highest protocol version the client speaks (v3
        /// negotiation; absent = endpoint default). The server caps down.
        /// </summary>
        public uint? ProtocolVersion { get; }

        /// <summary>Gets the supported data-path transport tokens (v3; absent = relay-only).</summary>
        public IReadOnlyList<string>? SupportedTransports { get; }

        /// <summary>Gets the supported session topology tokens (v3; absent = relay-only).</summary>
        public IReadOnlyList<string>? SupportedTopologies { get; }

        /// <summary>
        /// Gets the additive capability tokens the client is prepared to use
        /// (v3; unknown tokens are ignored by the server).
        /// </summary>
        public IReadOnlyList<string>? RequestedCapabilities { get; }

        /// <summary>Gets the optional tenant credential minted by the deployment's control plane.</summary>
        public string? ConnectToken { get; }

        /// <summary>Initializes a new <see cref="AuthenticateMessage"/> payload.</summary>
        public AuthenticateMessage(
            string? appId = null,
            string? sdkVersion = null,
            string? platform = null,
            string? gameDataFormat = null,
            uint? protocolVersion = null,
            IReadOnlyList<string>? supportedTransports = null,
            IReadOnlyList<string>? supportedTopologies = null,
            IReadOnlyList<string>? requestedCapabilities = null,
            string? connectToken = null)
        {
            AppId = appId;
            SdkVersion = sdkVersion;
            Platform = platform;
            GameDataFormat = gameDataFormat;
            ProtocolVersion = protocolVersion;
            SupportedTransports = supportedTransports;
            SupportedTopologies = supportedTopologies;
            RequestedCapabilities = requestedCapabilities;
            ConnectToken = connectToken;
        }

        /// <inheritdoc />
        public bool Equals(AuthenticateMessage other) =>
            NullableStringEquals(AppId, other.AppId)
            && NullableStringEquals(SdkVersion, other.SdkVersion)
            && NullableStringEquals(Platform, other.Platform)
            && NullableStringEquals(GameDataFormat, other.GameDataFormat)
            && ProtocolVersion == other.ProtocolVersion
            && SequenceEquals(SupportedTransports, other.SupportedTransports)
            && SequenceEquals(SupportedTopologies, other.SupportedTopologies)
            && SequenceEquals(RequestedCapabilities, other.RequestedCapabilities)
            && NullableStringEquals(ConnectToken, other.ConnectToken);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is AuthenticateMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(AppId);
            hash.Add(SdkVersion);
            hash.Add(Platform);
            hash.Add(GameDataFormat);
            hash.Add(ProtocolVersion);
            hash.Add(SequenceHashCode(SupportedTransports));
            hash.Add(SequenceHashCode(SupportedTopologies));
            hash.Add(SequenceHashCode(RequestedCapabilities));
            hash.Add(ConnectToken);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(AuthenticateMessage left, AuthenticateMessage right) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(AuthenticateMessage left, AuthenticateMessage right) => !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of an <c>Authenticate</c> envelope
        /// (the <see cref="EnvelopeEvent.Data"/> slice). Unknown fields are
        /// skipped; missing fields decode as absent. Returns
        /// <see langword="false"/> when <paramref name="data"/> is not a
        /// well-formed JSON object.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out AuthenticateMessage message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            string? appId = null, sdkVersion = null, platform = null, gameDataFormat = null, connectToken = null;
            uint? protocolVersion = null;
            IReadOnlyList<string>? supportedTransports = null, supportedTopologies = null, requestedCapabilities = null;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "app_id"))
                {
                    if (!scanner.TryReadString(valueRaw, out appId))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "sdk_version"))
                {
                    if (!scanner.TryReadString(valueRaw, out sdkVersion))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "platform"))
                {
                    if (!scanner.TryReadString(valueRaw, out platform))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "game_data_format"))
                {
                    if (!scanner.TryReadString(valueRaw, out gameDataFormat))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "protocol_version"))
                {
                    if (!scanner.TryReadUInt32(valueRaw, out uint version))
                    {
                        return false;
                    }

                    protocolVersion = version;
                }
                else if (scanner.KeyIs(keyRaw, "supported_transports"))
                {
                    if (!scanner.TryReadStringArray(data, valueRaw, out supportedTransports))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "supported_topologies"))
                {
                    if (!scanner.TryReadStringArray(data, valueRaw, out supportedTopologies))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "requested_capabilities"))
                {
                    if (!scanner.TryReadStringArray(data, valueRaw, out requestedCapabilities))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "connect_token"))
                {
                    if (!scanner.TryReadString(valueRaw, out connectToken))
                    {
                        return false;
                    }
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject)
            {
                return false;
            }

            message = new AuthenticateMessage(
                appId, sdkVersion, platform, gameDataFormat, protocolVersion,
                supportedTransports, supportedTopologies, requestedCapabilities, connectToken);
            return true;
        }

        internal static bool NullableStringEquals(string? left, string? right) =>
            left is null ? right is null : string.Equals(left, right, StringComparison.Ordinal);

        internal static bool SequenceEquals(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
        {
            if (left is null || right is null)
            {
                return left is null && right is null;
            }

            if (left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (!NullableStringEquals(left[i], right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static int SequenceHashCode(IReadOnlyList<string>? values)
        {
            if (values is null)
            {
                return 0;
            }

            HashCode hash = default;
            for (int i = 0; i < values.Count; i++)
            {
                hash.Add(values[i]);
            }

            return hash.ToHashCode();
        }
    }
}
