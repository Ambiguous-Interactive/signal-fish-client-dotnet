namespace SignalFish.Client.Protocol
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The server-issued rate limits of an accepted authentication (messages
    /// per rolling window). All three windows are required.
    /// </summary>
    public readonly struct RateLimits : IEquatable<RateLimits>
    {
        /// <summary>Gets the per-minute message allowance.</summary>
        public uint PerMinute { get; }

        /// <summary>Gets the per-hour message allowance.</summary>
        public uint PerHour { get; }

        /// <summary>Gets the per-day message allowance.</summary>
        public uint PerDay { get; }

        /// <summary>Initializes a new <see cref="RateLimits"/> value.</summary>
        public RateLimits(uint perMinute, uint perHour, uint perDay)
        {
            PerMinute = perMinute;
            PerHour = perHour;
            PerDay = perDay;
        }

        /// <inheritdoc />
        public bool Equals(RateLimits other) =>
            PerMinute == other.PerMinute && PerHour == other.PerHour && PerDay == other.PerDay;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is RateLimits other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(PerMinute);
            hash.Add(PerHour);
            hash.Add(PerDay);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(RateLimits left, RateLimits right) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(RateLimits left, RateLimits right) => !left.Equals(right);
    }

    /// <summary>
    /// Payload of the inbound <c>Authenticated</c> message (S→C): the
    /// accepted app identity plus the server-issued rate limits. All fields
    /// are required. Wire order: <c>app_name</c>, <c>organization</c>,
    /// <c>rate_limits</c>. (Distinct from the outbound
    /// <see cref="AuthenticateMessage"/>.)
    /// </summary>
    public readonly struct AuthenticatedMessage : IEquatable<AuthenticatedMessage>
    {
        /// <summary>Gets the public app name the deployment accepted (required).</summary>
        public string AppName { get; }

        /// <summary>Gets the organization that owns the app (required).</summary>
        public string Organization { get; }

        /// <summary>Gets the per-window message allowances (required).</summary>
        public RateLimits RateLimits { get; }

        /// <summary>Initializes a new <see cref="AuthenticatedMessage"/> payload.</summary>
        public AuthenticatedMessage(string appName, string organization, RateLimits rateLimits)
        {
            AppName = appName;
            Organization = organization;
            RateLimits = rateLimits;
        }

        /// <inheritdoc />
        public bool Equals(AuthenticatedMessage other) =>
            AuthenticateMessage.NullableStringEquals(AppName, other.AppName)
            && AuthenticateMessage.NullableStringEquals(Organization, other.Organization)
            && RateLimits == other.RateLimits;

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is AuthenticatedMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(AppName);
            hash.Add(Organization);
            hash.Add(RateLimits);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(AuthenticatedMessage left, AuthenticatedMessage right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(AuthenticatedMessage left, AuthenticatedMessage right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of an <c>Authenticated</c> envelope
        /// (the <see cref="EnvelopeEvent.Data"/> slice). Unknown fields are
        /// skipped; a repeated key or a wrong-typed value is rejected
        /// (fail-closed, matching the envelope layer's first-wins posture).
        /// Returns <see langword="false"/> for malformed input or a missing
        /// required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out AuthenticatedMessage message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            string? appName = null;
            string? organization = null;
            RateLimits rateLimits = default;
            bool rateLimitsSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "app_name"))
                {
                    if (appName is not null || !scanner.TryReadString(valueRaw, out appName))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "organization"))
                {
                    if (
                        organization is not null
                        || !scanner.TryReadString(valueRaw, out organization)
                    )
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "rate_limits"))
                {
                    if (
                        rateLimitsSeen
                        || !JsonScanner.TryReadObjectSlice(
                            data,
                            valueRaw,
                            out ReadOnlyMemory<byte> slice
                        )
                        || !TryDecodeRateLimits(slice, out rateLimits)
                    )
                    {
                        return false;
                    }

                    rateLimitsSeen = true;
                }

                state = scanner.EndMember();
            }

            if (
                state != JsonMemberState.EndObject
                || appName is null
                || organization is null
                || !rateLimitsSeen
            )
            {
                return false;
            }

            message = new AuthenticatedMessage(appName, organization, rateLimits);
            return true;
        }

        private static bool TryDecodeRateLimits(ReadOnlyMemory<byte> data, out RateLimits limits)
        {
            limits = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            uint perMinute = 0;
            uint perHour = 0;
            uint perDay = 0;
            bool minuteSeen = false;
            bool hourSeen = false;
            bool daySeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "per_minute"))
                {
                    if (minuteSeen || !scanner.TryReadUInt32(valueRaw, out perMinute))
                    {
                        return false;
                    }

                    minuteSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "per_hour"))
                {
                    if (hourSeen || !scanner.TryReadUInt32(valueRaw, out perHour))
                    {
                        return false;
                    }

                    hourSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "per_day"))
                {
                    if (daySeen || !scanner.TryReadUInt32(valueRaw, out perDay))
                    {
                        return false;
                    }

                    daySeen = true;
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || !minuteSeen || !hourSeen || !daySeen)
            {
                return false;
            }

            limits = new RateLimits(perMinute, perHour, perDay);
            return true;
        }
    }

    /// <summary>
    /// Payload of the inbound <c>ProtocolInfo</c> message (S→C): the
    /// protocol capability tokens and the game-data formats the endpoint
    /// supports. Both lists are required but may be empty. Wire order:
    /// <c>capabilities</c>, <c>game_data_formats</c>.
    /// </summary>
    public readonly struct ProtocolInfoMessage : IEquatable<ProtocolInfoMessage>
    {
        /// <summary>Gets the protocol capability tokens (required; may be empty).</summary>
        public IReadOnlyList<string> Capabilities { get; }

        /// <summary>Gets the supported game-data format tokens (required; may be empty).</summary>
        public IReadOnlyList<string> GameDataFormats { get; }

        /// <summary>Initializes a new <see cref="ProtocolInfoMessage"/> payload.</summary>
        public ProtocolInfoMessage(
            IReadOnlyList<string> capabilities,
            IReadOnlyList<string> gameDataFormats
        )
        {
            Capabilities = capabilities;
            GameDataFormats = gameDataFormats;
        }

        /// <inheritdoc />
        public bool Equals(ProtocolInfoMessage other) =>
            AuthenticateMessage.SequenceEquals(Capabilities, other.Capabilities)
            && AuthenticateMessage.SequenceEquals(GameDataFormats, other.GameDataFormats);

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is ProtocolInfoMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(SequenceHashCode(Capabilities));
            hash.Add(SequenceHashCode(GameDataFormats));
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(ProtocolInfoMessage left, ProtocolInfoMessage right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(ProtocolInfoMessage left, ProtocolInfoMessage right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>ProtocolInfo</c> envelope
        /// (the <see cref="EnvelopeEvent.Data"/> slice). Unknown fields are
        /// skipped; a repeated key or a wrong-typed value (including a
        /// wrong-typed array element) is rejected. Returns
        /// <see langword="false"/> for malformed input or a missing
        /// required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out ProtocolInfoMessage message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            IReadOnlyList<string>? capabilities = null;
            IReadOnlyList<string>? gameDataFormats = null;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "capabilities"))
                {
                    if (
                        capabilities is not null
                        || !JsonScanner.TryReadStringArray(
                            data,
                            valueRaw,
                            out IReadOnlyList<string>? parsed
                        )
                        || parsed is null
                    )
                    {
                        return false;
                    }

                    capabilities = parsed;
                }
                else if (scanner.KeyIs(keyRaw, "game_data_formats"))
                {
                    if (
                        gameDataFormats is not null
                        || !JsonScanner.TryReadStringArray(
                            data,
                            valueRaw,
                            out IReadOnlyList<string>? parsed
                        )
                        || parsed is null
                    )
                    {
                        return false;
                    }

                    gameDataFormats = parsed;
                }

                state = scanner.EndMember();
            }

            if (
                state != JsonMemberState.EndObject
                || capabilities is null
                || gameDataFormats is null
            )
            {
                return false;
            }

            message = new ProtocolInfoMessage(capabilities, gameDataFormats);
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

    /// <summary>
    /// Payload of the inbound <c>LobbyStateChanged</c> message (S→C): the
    /// current lobby phase, the players that declared readiness, and whether
    /// every seated player is ready. All fields are required. Wire order:
    /// <c>lobby_state</c>, <c>ready_players</c>, <c>all_ready</c>.
    /// </summary>
    public readonly struct LobbyStateChangedMessage : IEquatable<LobbyStateChangedMessage>
    {
        /// <summary>Gets the lobby phase token (required).</summary>
        public string LobbyState { get; }

        /// <summary>Gets the player ids that declared readiness (required; may be empty).</summary>
        public IReadOnlyList<Guid> ReadyPlayers { get; }

        /// <summary>Gets a value indicating whether every seated player is ready (required).</summary>
        public bool AllReady { get; }

        /// <summary>Initializes a new <see cref="LobbyStateChangedMessage"/> payload.</summary>
        public LobbyStateChangedMessage(
            string lobbyState,
            IReadOnlyList<Guid> readyPlayers,
            bool allReady
        )
        {
            LobbyState = lobbyState;
            ReadyPlayers = readyPlayers;
            AllReady = allReady;
        }

        /// <inheritdoc />
        public bool Equals(LobbyStateChangedMessage other) =>
            AuthenticateMessage.NullableStringEquals(LobbyState, other.LobbyState)
            && SequenceEquals(ReadyPlayers, other.ReadyPlayers)
            && AllReady == other.AllReady;

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is LobbyStateChangedMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(LobbyState);
            hash.Add(SequenceHashCode(ReadyPlayers));
            hash.Add(AllReady);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(
            LobbyStateChangedMessage left,
            LobbyStateChangedMessage right
        ) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(
            LobbyStateChangedMessage left,
            LobbyStateChangedMessage right
        ) => !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>LobbyStateChanged</c>
        /// envelope (the <see cref="EnvelopeEvent.Data"/> slice). Unknown
        /// fields are skipped; a repeated key or a wrong-typed value is
        /// rejected. Returns <see langword="false"/> for malformed input or
        /// a missing required field.
        /// </summary>
        internal static bool TryDecode(
            ReadOnlyMemory<byte> data,
            out LobbyStateChangedMessage message
        )
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            string? lobbyState = null;
            IReadOnlyList<Guid>? readyPlayers = null;
            bool allReady = false;
            bool allReadySeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "lobby_state"))
                {
                    if (lobbyState is not null || !scanner.TryReadString(valueRaw, out lobbyState))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "ready_players"))
                {
                    if (
                        readyPlayers is not null
                        || !JsonScanner.TryReadGuidArray(
                            data,
                            valueRaw,
                            out IReadOnlyList<Guid>? parsed
                        )
                        || parsed is null
                    )
                    {
                        return false;
                    }

                    readyPlayers = parsed;
                }
                else if (scanner.KeyIs(keyRaw, "all_ready"))
                {
                    if (allReadySeen || !scanner.TryReadBoolean(valueRaw, out allReady))
                    {
                        return false;
                    }

                    allReadySeen = true;
                }

                state = scanner.EndMember();
            }

            if (
                state != JsonMemberState.EndObject
                || lobbyState is null
                || readyPlayers is null
                || !allReadySeen
            )
            {
                return false;
            }

            message = new LobbyStateChangedMessage(lobbyState, readyPlayers, allReady);
            return true;
        }

        private static bool SequenceEquals(IReadOnlyList<Guid>? left, IReadOnlyList<Guid>? right)
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
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static int SequenceHashCode(IReadOnlyList<Guid>? values)
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

    /// <summary>
    /// Payload of the inbound <c>AuthorityResponse</c> message (S→C): the
    /// outcome of an authority request. <see cref="Reason"/> is optional —
    /// an absent key or an explicit JSON <c>null</c> decodes as
    /// <see langword="null"/>. Wire order: <c>granted</c>, <c>reason</c>.
    /// </summary>
    public readonly struct AuthorityResponseMessage : IEquatable<AuthorityResponseMessage>
    {
        public bool Granted { get; }

        /// <summary>
        /// Gets the denial reason; <see langword="null"/> when absent or an
        /// explicit JSON <c>null</c>.
        /// </summary>
        public string? Reason { get; }

        /// <summary>Initializes a new <see cref="AuthorityResponseMessage"/> payload.</summary>
        public AuthorityResponseMessage(bool granted, string? reason)
        {
            Granted = granted;
            Reason = reason;
        }

        /// <inheritdoc />
        public bool Equals(AuthorityResponseMessage other) =>
            Granted == other.Granted
            && AuthenticateMessage.NullableStringEquals(Reason, other.Reason);

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is AuthorityResponseMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(Granted);
            hash.Add(Reason);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(
            AuthorityResponseMessage left,
            AuthorityResponseMessage right
        ) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(
            AuthorityResponseMessage left,
            AuthorityResponseMessage right
        ) => !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of an <c>AuthorityResponse</c>
        /// envelope (the <see cref="EnvelopeEvent.Data"/> slice). Unknown
        /// fields are skipped; a repeated key or a wrong-typed value is
        /// rejected. Returns <see langword="false"/> for malformed input or
        /// a missing required field.
        /// </summary>
        internal static bool TryDecode(
            ReadOnlyMemory<byte> data,
            out AuthorityResponseMessage message
        )
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            bool granted = false;
            bool grantedSeen = false;
            string? reason = null;
            bool reasonSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "granted"))
                {
                    if (grantedSeen || !scanner.TryReadBoolean(valueRaw, out granted))
                    {
                        return false;
                    }

                    grantedSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "reason"))
                {
                    /*
                        Optional: an explicit JSON null decodes as absent.
                        The seen flag still rejects a repeated key.
                    */
                    if (reasonSeen)
                    {
                        return false;
                    }

                    if (scanner.TryReadNull(valueRaw))
                    {
                        reason = null;
                    }
                    else if (!scanner.TryReadString(valueRaw, out reason))
                    {
                        return false;
                    }

                    reasonSeen = true;
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || !grantedSeen)
            {
                return false;
            }

            message = new AuthorityResponseMessage(granted, reason);
            return true;
        }
    }

    /// <summary>
    /// Payload of the inbound <c>AuthorityChanged</c> message (S→C): the new
    /// authority holder and whether this connection is it.
    /// <see cref="AuthorityPlayer"/> is required but nullable — an explicit
    /// JSON <c>null</c> means the authority seat vacated with no successor.
    /// <see cref="YouAreAuthority"/> is required. Wire order:
    /// <c>authority_player</c>, <c>you_are_authority</c>.
    /// </summary>
    public readonly struct AuthorityChangedMessage : IEquatable<AuthorityChangedMessage>
    {
        /// <summary>Gets the new authority holder; <see langword="null"/> when the seat vacated.</summary>
        public Guid? AuthorityPlayer { get; }

        /// <summary>Gets a value indicating whether this connection is the new authority (required).</summary>
        public bool YouAreAuthority { get; }

        /// <summary>Initializes a new <see cref="AuthorityChangedMessage"/> payload.</summary>
        public AuthorityChangedMessage(Guid? authorityPlayer, bool youAreAuthority)
        {
            AuthorityPlayer = authorityPlayer;
            YouAreAuthority = youAreAuthority;
        }

        /// <inheritdoc />
        public bool Equals(AuthorityChangedMessage other) =>
            AuthorityPlayer == other.AuthorityPlayer && YouAreAuthority == other.YouAreAuthority;

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is AuthorityChangedMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(AuthorityPlayer);
            hash.Add(YouAreAuthority);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(
            AuthorityChangedMessage left,
            AuthorityChangedMessage right
        ) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(
            AuthorityChangedMessage left,
            AuthorityChangedMessage right
        ) => !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of an <c>AuthorityChanged</c>
        /// envelope (the <see cref="EnvelopeEvent.Data"/> slice). Unknown
        /// fields are skipped; a repeated key or a wrong-typed value is
        /// rejected. An explicit JSON <c>null</c> for
        /// <c>authority_player</c> decodes as the vacated seat (the field
        /// is required-present but spec-nullable). Returns
        /// <see langword="false"/> for malformed input or a missing
        /// required field.
        /// </summary>
        internal static bool TryDecode(
            ReadOnlyMemory<byte> data,
            out AuthorityChangedMessage message
        )
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            Guid? authorityPlayer = null;
            bool youAreAuthority = false;
            bool playerSeen = false;
            bool youAreSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "authority_player"))
                {
                    if (playerSeen)
                    {
                        return false;
                    }

                    playerSeen = true;
                    if (!scanner.TryReadNull(valueRaw))
                    {
                        if (!scanner.TryReadGuid(valueRaw, out Guid player))
                        {
                            return false;
                        }

                        authorityPlayer = player;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "you_are_authority"))
                {
                    if (youAreSeen || !scanner.TryReadBoolean(valueRaw, out youAreAuthority))
                    {
                        return false;
                    }

                    youAreSeen = true;
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || !playerSeen || !youAreSeen)
            {
                return false;
            }

            message = new AuthorityChangedMessage(authorityPlayer, youAreAuthority);
            return true;
        }
    }
}
