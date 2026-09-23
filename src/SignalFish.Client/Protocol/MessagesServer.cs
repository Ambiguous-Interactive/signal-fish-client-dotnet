namespace SignalFish.Client.Protocol
{
    using System;

    /// <summary>
    /// Session-critical fields of the inbound <c>RoomJoined</c> payload
    /// (S→C): the confirmed player membership. The remaining v2 fields
    /// (<c>game_name</c>, <c>max_players</c>, <c>current_players</c>, …)
    /// are tolerated as unknown and land with the M3.4 event surface.
    /// </summary>
    public readonly struct RoomJoinedMessage : IEquatable<RoomJoinedMessage>
    {
        /// <summary>Gets the player identity confirmed by the server (required).</summary>
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
        /// Gets the reconnection token the frame carried (null when
        /// absent — the v2 wire omits it; tolerant capture).
        /// </summary>
        public string? ReconnectionToken { get; }

        /// <summary>Initializes a new <see cref="RoomJoinedMessage"/> payload.</summary>
        public RoomJoinedMessage(
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
        public bool Equals(RoomJoinedMessage other) =>
            PlayerId == other.PlayerId
            && RoomId == other.RoomId
            && AuthenticateMessage.NullableStringEquals(RoomCode, other.RoomCode)
            && AuthenticateMessage.NullableStringEquals(ReconnectionToken, other.ReconnectionToken);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is RoomJoinedMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(PlayerId);
            hash.Add(RoomId);
            hash.Add(RoomCode);
            hash.Add(ReconnectionToken);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(RoomJoinedMessage left, RoomJoinedMessage right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(RoomJoinedMessage left, RoomJoinedMessage right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>RoomJoined</c> envelope
        /// (the <see cref="EnvelopeEvent.Data"/> slice). Unknown fields are
        /// skipped; a repeated session-critical key is rejected (fail-closed,
        /// matching the envelope layer's first-wins posture). The
        /// <c>reconnection_token</c> key is session-critical when present
        /// (a repeated, non-string, or empty value is rejected; an explicit
        /// JSON null counts as absent, the canonical wire form). Returns
        /// <see langword="false"/> for malformed input or a missing
        /// session-critical field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out RoomJoinedMessage message)
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

                        // Fail-closed: an empty string is no credential.
                        if (reconnectionToken.Length == 0)
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
            message = new RoomJoinedMessage(
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
    /// Session-critical fields of the inbound <c>SpectatorJoined</c> payload
    /// (S→C): the confirmed spectator membership. The remaining v2 fields
    /// (<c>game_name</c>, <c>current_players</c>, …) are tolerated as
    /// unknown and land with the M3.4 event surface. A stray
    /// <c>reconnection_token</c> is likewise unknown-field noise: the
    /// protocol has no spectator reconnect, so it is never mapped
    /// (unlike <c>RoomJoined</c>/<c>Reconnected</c>, which reject a
    /// malformed one).
    /// </summary>
    public readonly struct SpectatorJoinedMessage : IEquatable<SpectatorJoinedMessage>
    {
        /// <summary>Gets the spectator identity confirmed by the server (required).</summary>
        public Guid SpectatorId { get; }

        /// <summary>Gets the server-assigned room identity (required).</summary>
        public Guid RoomId { get; }

        /// <summary>Gets the human-shareable room code (required).</summary>
        public string RoomCode { get; }

        /// <summary>
        /// Gets the room-state snapshot the frame carried (default when
        /// absent). Advisory state: excluded from equality.
        /// </summary>
        public RoomSnapshot Snapshot { get; }

        /// <summary>Initializes a new <see cref="SpectatorJoinedMessage"/> payload.</summary>
        public SpectatorJoinedMessage(
            Guid spectatorId,
            Guid roomId,
            string roomCode,
            RoomSnapshot? snapshot = null
        )
        {
            SpectatorId = spectatorId;
            RoomId = roomId;
            RoomCode = roomCode;
            Snapshot = snapshot ?? default;
        }

        /// <inheritdoc />
        public bool Equals(SpectatorJoinedMessage other) =>
            SpectatorId == other.SpectatorId
            && RoomId == other.RoomId
            && AuthenticateMessage.NullableStringEquals(RoomCode, other.RoomCode);

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is SpectatorJoinedMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(SpectatorId);
            hash.Add(RoomId);
            hash.Add(RoomCode);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(SpectatorJoinedMessage left, SpectatorJoinedMessage right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(SpectatorJoinedMessage left, SpectatorJoinedMessage right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>SpectatorJoined</c>
        /// envelope (the <see cref="EnvelopeEvent.Data"/> slice). Unknown
        /// fields are skipped; a repeated session-critical key is rejected
        /// (fail-closed, matching the envelope layer's first-wins posture).
        /// Returns <see langword="false"/> for malformed input or a missing
        /// session-critical field.
        /// </summary>
        internal static bool TryDecode(
            ReadOnlyMemory<byte> data,
            out SpectatorJoinedMessage message
        )
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            Guid spectatorId = default;
            Guid roomId = default;
            string? roomCode = null;
            bool spectatorSeen = false;
            bool roomSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "spectator_id"))
                {
                    if (spectatorSeen || !scanner.TryReadGuid(valueRaw, out spectatorId))
                    {
                        return false;
                    }

                    spectatorSeen = true;
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

                state = scanner.EndMember();
            }

            if (
                state != JsonMemberState.EndObject
                || !spectatorSeen
                || !roomSeen
                || roomCode is null
            )
            {
                return false;
            }

            if (!RoomSnapshot.TryDecode(data, out RoomSnapshot snapshot))
            {
                return false;
            }
            message = new SpectatorJoinedMessage(spectatorId, roomId, roomCode, snapshot);
            return true;
        }
    }

    /// <summary>
    /// Payload of the inbound v3 <c>GoingAway</c> message (S→C): the server
    /// is draining and will close the connection at the deadline. Both
    /// fields are required; the game owns the reconnect scheduling.
    /// </summary>
    public readonly struct GoingAwayMessage : IEquatable<GoingAwayMessage>
    {
        /// <summary>Gets the Unix-epoch millisecond deadline of the close (required).</summary>
        public ulong DeadlineMs { get; }

        /// <summary>Gets the suggested wait before reconnecting, in seconds (required).</summary>
        public ulong RetryAfterSecs { get; }

        /// <summary>Initializes a new <see cref="GoingAwayMessage"/> payload.</summary>
        public GoingAwayMessage(ulong deadlineMs, ulong retryAfterSecs)
        {
            DeadlineMs = deadlineMs;
            RetryAfterSecs = retryAfterSecs;
        }

        /// <inheritdoc />
        public bool Equals(GoingAwayMessage other) =>
            DeadlineMs == other.DeadlineMs && RetryAfterSecs == other.RetryAfterSecs;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is GoingAwayMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(DeadlineMs);
            hash.Add(RetryAfterSecs);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(GoingAwayMessage left, GoingAwayMessage right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(GoingAwayMessage left, GoingAwayMessage right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>GoingAway</c> envelope
        /// (the <see cref="EnvelopeEvent.Data"/> slice). Unknown fields are
        /// skipped; a repeated key or a wrong-typed value is rejected.
        /// Returns <see langword="false"/> for malformed input or a
        /// missing required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out GoingAwayMessage message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            ulong deadlineMs = 0;
            ulong retryAfterSecs = 0;
            bool deadlineSeen = false;
            bool retrySeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "deadline_ms"))
                {
                    if (deadlineSeen || !scanner.TryReadUInt64(valueRaw, out deadlineMs))
                    {
                        return false;
                    }

                    deadlineSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "retry_after_secs"))
                {
                    if (retrySeen || !scanner.TryReadUInt64(valueRaw, out retryAfterSecs))
                    {
                        return false;
                    }

                    retrySeen = true;
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || !deadlineSeen || !retrySeen)
            {
                return false;
            }

            message = new GoingAwayMessage(deadlineMs, retryAfterSecs);
            return true;
        }
    }
}
