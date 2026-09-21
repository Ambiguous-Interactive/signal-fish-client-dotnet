namespace SignalFish.Client.Protocol
{
    using System;

    /// <summary>
    /// Payload of the inbound <c>Reconnected</c> frame (S→C): a prior
    /// membership reclaimed on a fresh, re-authenticated connection, plus
    /// the room-state snapshot. The v2 <c>missed_events</c> marker is
    /// tolerated as unknown; replay semantics never rely on it.
    /// </summary>
    public readonly struct ReconnectedMessage : IEquatable<ReconnectedMessage>
    {
        /// <summary>Gets the player identity reclaimed by the server (required).</summary>
        public Guid PlayerId { get; }

        /// <summary>Gets the server-assigned room identity (required).</summary>
        public Guid RoomId { get; }

        /// <summary>Gets the human-shareable room code (required).</summary>
        public string RoomCode { get; }

        /// <summary>
        /// Gets the room-state snapshot the frame carried (default when
        /// absent). Advisory state: excluded from equality.
        /// </summary>
        public RoomSnapshot Snapshot { get; }

        /// <summary>
        /// Gets the rotated reconnection token the frame carried (null
        /// when absent — the v2 wire omits it; tolerant capture).
        /// </summary>
        public string? ReconnectionToken { get; }

        /// <summary>Initializes a new <see cref="ReconnectedMessage"/> payload.</summary>
        public ReconnectedMessage(
            Guid playerId,
            Guid roomId,
            string roomCode,
            RoomSnapshot? snapshot = null,
            string? reconnectionToken = null
        )
        {
            PlayerId = playerId;
            RoomId = roomId;
            RoomCode = roomCode;
            Snapshot = snapshot ?? default;
            ReconnectionToken = reconnectionToken;
        }

        /// <inheritdoc />
        public bool Equals(ReconnectedMessage other) =>
            PlayerId == other.PlayerId
            && RoomId == other.RoomId
            && AuthenticateMessage.NullableStringEquals(RoomCode, other.RoomCode);

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is ReconnectedMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(PlayerId);
            hash.Add(RoomId);
            hash.Add(RoomCode);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(ReconnectedMessage left, ReconnectedMessage right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(ReconnectedMessage left, ReconnectedMessage right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>Reconnected</c> envelope
        /// (the <see cref="EnvelopeEvent.Data"/> slice). Unknown fields are
        /// skipped; a repeated session-critical key is rejected
        /// (fail-closed). The <c>reconnection_token</c> key carries the
        /// rotated credential when present (a repeated or non-string value
        /// is rejected; an explicit JSON null counts as absent, the
        /// canonical wire form). Returns <see langword="false"/> for
        /// malformed input or a missing session-critical field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out ReconnectedMessage message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            Guid playerId = default;
            Guid roomId = default;
            string? roomCode = null;
            string? reconnectionToken = null;
            bool playerSeen = false;
            bool roomSeen = false;
            bool tokenSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "player_id"))
                {
                    if (playerSeen || !scanner.TryReadGuid(valueRaw, out playerId))
                    {
                        return false;
                    }

                    playerSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "room_id"))
                {
                    if (roomSeen || !scanner.TryReadGuid(valueRaw, out roomId))
                    {
                        return false;
                    }

                    roomSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "room_code"))
                {
                    if (roomCode is not null || !scanner.TryReadString(valueRaw, out roomCode))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "reconnection_token"))
                {
                    if (tokenSeen)
                    {
                        return false;
                    }

                    tokenSeen = true;
                    if (!scanner.TryReadNull(valueRaw))
                    {
                        if (!scanner.TryReadString(valueRaw, out reconnectionToken))
                        {
                            return false;
                        }
                    }
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || !playerSeen || !roomSeen || roomCode is null)
            {
                return false;
            }

            if (!RoomSnapshot.TryDecode(data, out RoomSnapshot snapshot))
            {
                return false;
            }
            message = new ReconnectedMessage(
                playerId,
                roomId,
                roomCode,
                snapshot,
                reconnectionToken
            );
            return true;
        }
    }

    /// <summary>
    /// Payload of the outbound <c>JoinAsSpectator</c> message (C→S): join a
    /// room as a read-only observer. All of <see cref="GameName"/>,
    /// <see cref="RoomCode"/>, and <see cref="SpectatorName"/> are required;
    /// <see cref="Password"/> is optional (<see langword="null"/> omits it).
    /// Wire order: <c>game_name</c>, <c>room_code</c>,
    /// <c>spectator_name</c>, <c>password</c>.
    /// </summary>
    public readonly struct JoinAsSpectatorMessage : IEquatable<JoinAsSpectatorMessage>
    {
        /// <summary>Gets the name of the game (required).</summary>
        public string GameName { get; }

        /// <summary>Gets the room code to spectate; matched case-insensitively by the server (required).</summary>
        public string RoomCode { get; }

        /// <summary>Gets the display name for the spectator (required).</summary>
        public string SpectatorName { get; }

        /// <summary>Gets the password of an authority-sealed room.</summary>
        public string? Password { get; }

        /// <summary>Initializes a new <see cref="JoinAsSpectatorMessage"/> payload.</summary>
        public JoinAsSpectatorMessage(
            string gameName,
            string roomCode,
            string spectatorName,
            string? password = null
        )
        {
            GameName = gameName;
            RoomCode = roomCode;
            SpectatorName = spectatorName;
            Password = password;
        }

        /// <inheritdoc />
        public bool Equals(JoinAsSpectatorMessage other) =>
            AuthenticateMessage.NullableStringEquals(GameName, other.GameName)
            && AuthenticateMessage.NullableStringEquals(RoomCode, other.RoomCode)
            && AuthenticateMessage.NullableStringEquals(SpectatorName, other.SpectatorName)
            && AuthenticateMessage.NullableStringEquals(Password, other.Password);

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is JoinAsSpectatorMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(GameName);
            hash.Add(RoomCode);
            hash.Add(SpectatorName);
            hash.Add(Password);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(JoinAsSpectatorMessage left, JoinAsSpectatorMessage right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(JoinAsSpectatorMessage left, JoinAsSpectatorMessage right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>JoinAsSpectator</c>
        /// envelope (the <see cref="EnvelopeEvent.Data"/> slice). Unknown
        /// fields are skipped. Returns <see langword="false"/> for malformed
        /// input or a missing required field.
        /// </summary>
        internal static bool TryDecode(
            ReadOnlyMemory<byte> data,
            out JoinAsSpectatorMessage message
        )
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            string? gameName = null,
                roomCode = null,
                spectatorName = null,
                password = null;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "game_name"))
                {
                    if (!scanner.TryReadString(valueRaw, out gameName))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "room_code"))
                {
                    if (!scanner.TryReadString(valueRaw, out roomCode))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "spectator_name"))
                {
                    if (!scanner.TryReadString(valueRaw, out spectatorName))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "password"))
                {
                    /*
                        Canonical frames may carry explicit nulls for absent
                        optionals; null decodes as absent.
                    */
                    if (scanner.TryReadNull(valueRaw))
                    {
                        password = null;
                    }
                    else if (!scanner.TryReadString(valueRaw, out password))
                    {
                        return false;
                    }
                }

                state = scanner.EndMember();
            }

            if (
                state != JsonMemberState.EndObject
                || gameName is null
                || roomCode is null
                || spectatorName is null
            )
            {
                return false;
            }

            message = new JoinAsSpectatorMessage(gameName, roomCode, spectatorName, password);
            return true;
        }
    }

    /// <summary>
    /// Payload of the outbound <c>Reconnect</c> message (C→S): resume a room
    /// seat after a disconnection using the server-issued token. All fields
    /// are required. Wire order: <c>player_id</c>, <c>room_id</c>,
    /// <c>auth_token</c>.
    /// </summary>
    public readonly struct ReconnectMessage : IEquatable<ReconnectMessage>
    {
        /// <summary>Gets the player identity issued at join time (required).</summary>
        public string PlayerId { get; }

        /// <summary>Gets the room identity issued at join time (required).</summary>
        public string RoomId { get; }

        /// <summary>Gets the reconnection token from <c>RoomJoined</c>/<c>Reconnected</c> (required).</summary>
        public string AuthToken { get; }

        /// <summary>
        /// Gets the room-state snapshot the frame carried (default when
        /// absent). Advisory state: excluded from equality.
        /// </summary>
        public RoomSnapshot Snapshot { get; }

        /// <summary>Initializes a new <see cref="ReconnectMessage"/> payload.</summary>
        public ReconnectMessage(
            string playerId,
            string roomId,
            string authToken,
            RoomSnapshot? snapshot = null
        )
        {
            PlayerId = playerId;
            RoomId = roomId;
            AuthToken = authToken;
            Snapshot = snapshot ?? default;
        }

        /// <inheritdoc />
        public bool Equals(ReconnectMessage other) =>
            AuthenticateMessage.NullableStringEquals(PlayerId, other.PlayerId)
            && AuthenticateMessage.NullableStringEquals(RoomId, other.RoomId)
            && AuthenticateMessage.NullableStringEquals(AuthToken, other.AuthToken);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is ReconnectMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(PlayerId);
            hash.Add(RoomId);
            hash.Add(AuthToken);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(ReconnectMessage left, ReconnectMessage right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(ReconnectMessage left, ReconnectMessage right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>Reconnect</c> envelope (the
        /// <see cref="EnvelopeEvent.Data"/> slice). Unknown fields are
        /// skipped. Returns <see langword="false"/> for malformed input or a
        /// missing required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out ReconnectMessage message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            string? playerId = null,
                roomId = null,
                authToken = null;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "player_id"))
                {
                    if (!scanner.TryReadString(valueRaw, out playerId))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "room_id"))
                {
                    if (!scanner.TryReadString(valueRaw, out roomId))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "auth_token"))
                {
                    if (!scanner.TryReadString(valueRaw, out authToken))
                    {
                        return false;
                    }
                }

                state = scanner.EndMember();
            }

            if (
                state != JsonMemberState.EndObject
                || playerId is null
                || roomId is null
                || authToken is null
            )
            {
                return false;
            }

            if (!RoomSnapshot.TryDecode(data, out RoomSnapshot snapshot))
            {
                return false;
            }
            message = new ReconnectMessage(playerId, roomId, authToken, snapshot);
            return true;
        }
    }

    /// <summary>
    /// Payload of the outbound <c>JoinRoom</c> message (C→S): join or create
    /// a room. <see cref="GameName"/> and <see cref="PlayerName"/> are
    /// required; everything else is optional (<see langword="null"/> omits
    /// the field). Omitting <see cref="RoomCode"/> creates a generated code.
    /// Wire order: <c>game_name</c>, <c>player_name</c>, <c>room_code</c>,
    /// <c>max_players</c>, <c>supports_authority</c>,
    /// <c>relay_transport</c>, <c>password</c>.
    /// </summary>
    public readonly struct JoinRoomMessage : IEquatable<JoinRoomMessage>
    {
        /// <summary>Gets the name of the game (required).</summary>
        public string GameName { get; }

        /// <summary>Gets the display name for the player (required).</summary>
        public string PlayerName { get; }

        /// <summary>Gets the room code to join or claim; absent creates a generated code.</summary>
        public string? RoomCode { get; }

        /// <summary>Gets the player ceiling applied only when this join creates the room.</summary>
        public uint? MaxPlayers { get; }

        /// <summary>Gets a value indicating whether authority support is advertised (applied on create only).</summary>
        public bool? SupportsAuthority { get; }

        /// <summary>Gets the reserved compatibility hint; new clients should omit it.</summary>
        public string? RelayTransport { get; }

        /// <summary>Gets the password of an authority-sealed room.</summary>
        public string? Password { get; }

        /// <summary>Initializes a new <see cref="JoinRoomMessage"/> payload.</summary>
        public JoinRoomMessage(
            string gameName,
            string playerName,
            string? roomCode = null,
            uint? maxPlayers = null,
            bool? supportsAuthority = null,
            string? relayTransport = null,
            string? password = null
        )
        {
            GameName = gameName;
            PlayerName = playerName;
            RoomCode = roomCode;
            MaxPlayers = maxPlayers;
            SupportsAuthority = supportsAuthority;
            RelayTransport = relayTransport;
            Password = password;
        }

        /// <inheritdoc />
        public bool Equals(JoinRoomMessage other) =>
            AuthenticateMessage.NullableStringEquals(GameName, other.GameName)
            && AuthenticateMessage.NullableStringEquals(PlayerName, other.PlayerName)
            && AuthenticateMessage.NullableStringEquals(RoomCode, other.RoomCode)
            && MaxPlayers == other.MaxPlayers
            && SupportsAuthority == other.SupportsAuthority
            && AuthenticateMessage.NullableStringEquals(RelayTransport, other.RelayTransport)
            && AuthenticateMessage.NullableStringEquals(Password, other.Password);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is JoinRoomMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(GameName);
            hash.Add(PlayerName);
            hash.Add(RoomCode);
            hash.Add(MaxPlayers);
            hash.Add(SupportsAuthority);
            hash.Add(RelayTransport);
            hash.Add(Password);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(JoinRoomMessage left, JoinRoomMessage right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(JoinRoomMessage left, JoinRoomMessage right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>JoinRoom</c> envelope (the
        /// <see cref="EnvelopeEvent.Data"/> slice). Unknown fields are
        /// skipped. Returns <see langword="false"/> for malformed input.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out JoinRoomMessage message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            string? gameName = null,
                playerName = null,
                roomCode = null,
                relayTransport = null,
                password = null;
            uint? maxPlayers = null;
            bool? supportsAuthority = null;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "game_name"))
                {
                    if (!scanner.TryReadString(valueRaw, out gameName))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "player_name"))
                {
                    if (!scanner.TryReadString(valueRaw, out playerName))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "room_code"))
                {
                    /*
                        Canonical frames carry explicit nulls for absent
                        optionals; null decodes as absent.
                    */
                    if (scanner.TryReadNull(valueRaw))
                    {
                        roomCode = null;
                    }
                    else if (!scanner.TryReadString(valueRaw, out roomCode))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "max_players"))
                {
                    if (scanner.TryReadNull(valueRaw))
                    {
                        maxPlayers = null;
                    }
                    else if (!scanner.TryReadUInt32(valueRaw, out uint maxPlayersValue))
                    {
                        return false;
                    }
                    else
                    {
                        maxPlayers = maxPlayersValue;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "supports_authority"))
                {
                    if (scanner.TryReadNull(valueRaw))
                    {
                        supportsAuthority = null;
                    }
                    else if (!scanner.TryReadBoolean(valueRaw, out bool supportsAuthorityValue))
                    {
                        return false;
                    }
                    else
                    {
                        supportsAuthority = supportsAuthorityValue;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "relay_transport"))
                {
                    if (scanner.TryReadNull(valueRaw))
                    {
                        relayTransport = null;
                    }
                    else if (!scanner.TryReadString(valueRaw, out relayTransport))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "password"))
                {
                    if (scanner.TryReadNull(valueRaw))
                    {
                        password = null;
                    }
                    else if (!scanner.TryReadString(valueRaw, out password))
                    {
                        return false;
                    }
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || gameName is null || playerName is null)
            {
                return false;
            }

            message = new JoinRoomMessage(
                gameName,
                playerName,
                roomCode,
                maxPlayers,
                supportsAuthority,
                relayTransport,
                password
            );
            return true;
        }
    }
}
