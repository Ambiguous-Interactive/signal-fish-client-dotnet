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

        /// <summary>Initializes a new <see cref="RoomJoinedMessage"/> payload.</summary>
        public RoomJoinedMessage(Guid playerId, Guid roomId, string roomCode)
        {
            PlayerId = playerId;
            RoomId = roomId;
            RoomCode = roomCode;
        }

        /// <inheritdoc />
        public bool Equals(RoomJoinedMessage other) =>
            PlayerId == other.PlayerId
            && RoomId == other.RoomId
            && AuthenticateMessage.NullableStringEquals(RoomCode, other.RoomCode);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is RoomJoinedMessage other && Equals(other);

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
        public static bool operator ==(RoomJoinedMessage left, RoomJoinedMessage right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(RoomJoinedMessage left, RoomJoinedMessage right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>RoomJoined</c> envelope
        /// (the <see cref="EnvelopeEvent.Data"/> slice). Unknown fields are
        /// skipped. Returns <see langword="false"/> for malformed input or a
        /// missing session-critical field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out RoomJoinedMessage message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            Guid playerId = default;
            Guid roomId = default;
            string? roomCode = null;
            bool playerSeen = false;
            bool roomSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "player_id"))
                {
                    if (!scanner.TryReadGuid(valueRaw, out playerId))
                    {
                        return false;
                    }

                    playerSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "room_id"))
                {
                    if (!scanner.TryReadGuid(valueRaw, out roomId))
                    {
                        return false;
                    }

                    roomSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "room_code"))
                {
                    if (!scanner.TryReadString(valueRaw, out roomCode))
                    {
                        return false;
                    }
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || !playerSeen || !roomSeen || roomCode is null)
            {
                return false;
            }

            message = new RoomJoinedMessage(playerId, roomId, roomCode);
            return true;
        }
    }

    /// <summary>
    /// Session-critical fields of the inbound <c>SpectatorJoined</c> payload
    /// (S→C): the confirmed spectator membership. The remaining v2 fields
    /// (<c>game_name</c>, <c>current_players</c>, …) are tolerated as
    /// unknown and land with the M3.4 event surface.
    /// </summary>
    public readonly struct SpectatorJoinedMessage : IEquatable<SpectatorJoinedMessage>
    {
        /// <summary>Gets the spectator identity confirmed by the server (required).</summary>
        public Guid SpectatorId { get; }

        /// <summary>Gets the server-assigned room identity (required).</summary>
        public Guid RoomId { get; }

        /// <summary>Gets the human-shareable room code (required).</summary>
        public string RoomCode { get; }

        /// <summary>Initializes a new <see cref="SpectatorJoinedMessage"/> payload.</summary>
        public SpectatorJoinedMessage(Guid spectatorId, Guid roomId, string roomCode)
        {
            SpectatorId = spectatorId;
            RoomId = roomId;
            RoomCode = roomCode;
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
        /// fields are skipped. Returns <see langword="false"/> for malformed
        /// input or a missing session-critical field.
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
                    if (!scanner.TryReadGuid(valueRaw, out spectatorId))
                    {
                        return false;
                    }

                    spectatorSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "room_id"))
                {
                    if (!scanner.TryReadGuid(valueRaw, out roomId))
                    {
                        return false;
                    }

                    roomSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "room_code"))
                {
                    if (!scanner.TryReadString(valueRaw, out roomCode))
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

            message = new SpectatorJoinedMessage(spectatorId, roomId, roomCode);
            return true;
        }
    }
}
