namespace SignalFish.Client.Protocol
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Payload of the inbound <c>SpectatorLeft</c> message (S→C): a
    /// spectator left a room (voluntarily or by removal). All fields are
    /// required. Wire order: <c>room_id</c>, <c>room_code</c>,
    /// <c>reason</c>, <c>current_spectators</c>.
    /// </summary>
    public readonly struct SpectatorLeftMessage : IEquatable<SpectatorLeftMessage>
    {
        /// <summary>Gets the server-assigned room identity (required).</summary>
        public Guid RoomId { get; }

        /// <summary>Gets the human-shareable room code (required).</summary>
        public string RoomCode { get; }

        /// <summary>Gets the leave reason token (required).</summary>
        public string Reason { get; }

        /// <summary>Gets the spectators that remain (required; may be empty).</summary>
        public IReadOnlyList<SpectatorInfo> CurrentSpectators { get; }

        /// <summary>Initializes a new <see cref="SpectatorLeftMessage"/> payload.</summary>
        public SpectatorLeftMessage(
            Guid roomId,
            string roomCode,
            string reason,
            IReadOnlyList<SpectatorInfo> currentSpectators
        )
        {
            RoomId = roomId;
            RoomCode = roomCode;
            Reason = reason;
            CurrentSpectators = currentSpectators;
        }

        /// <inheritdoc />
        public bool Equals(SpectatorLeftMessage other) =>
            RoomId == other.RoomId
            && AuthenticateMessage.NullableStringEquals(RoomCode, other.RoomCode)
            && AuthenticateMessage.NullableStringEquals(Reason, other.Reason)
            && SpectatorInfo.SequenceEquals(CurrentSpectators, other.CurrentSpectators);

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is SpectatorLeftMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(RoomId);
            hash.Add(RoomCode);
            hash.Add(Reason);
            hash.Add(SequenceHashCode(CurrentSpectators));
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(SpectatorLeftMessage left, SpectatorLeftMessage right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(SpectatorLeftMessage left, SpectatorLeftMessage right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>SpectatorLeft</c> envelope
        /// (the <see cref="EnvelopeEvent.Data"/> slice). Unknown fields are
        /// skipped; a repeated key or a wrong-typed value is rejected.
        /// Returns <see langword="false"/> for malformed input or a missing
        /// required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out SpectatorLeftMessage message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            Guid roomId = default;
            string? roomCode = null;
            string? reason = null;
            IReadOnlyList<SpectatorInfo>? currentSpectators = null;
            bool roomSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "room_id"))
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
                else if (scanner.KeyIs(keyRaw, "reason"))
                {
                    if (reason is not null || !scanner.TryReadString(valueRaw, out reason))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "current_spectators"))
                {
                    if (
                        currentSpectators is not null
                        || !SpectatorInfo.TryReadArray(
                            data,
                            valueRaw,
                            out IReadOnlyList<SpectatorInfo>? parsed
                        )
                        || parsed is null
                    )
                    {
                        return false;
                    }

                    currentSpectators = parsed;
                }

                state = scanner.EndMember();
            }

            if (
                state != JsonMemberState.EndObject
                || !roomSeen
                || roomCode is null
                || reason is null
                || currentSpectators is null
            )
            {
                return false;
            }

            message = new SpectatorLeftMessage(roomId, roomCode, reason, currentSpectators);
            return true;
        }

        private static int SequenceHashCode(IReadOnlyList<SpectatorInfo>? values)
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
    /// Payload of the inbound <c>NewSpectatorJoined</c> message (S→C): a new
    /// spectator joined the room. All fields are required. Wire order:
    /// <c>spectator</c>, <c>current_spectators</c>, <c>reason</c>.
    /// </summary>
    public readonly struct NewSpectatorJoinedMessage : IEquatable<NewSpectatorJoinedMessage>
    {
        /// <summary>Gets the spectator that joined (required).</summary>
        public SpectatorInfo Spectator { get; }

        /// <summary>Gets the spectators now in the room, including the newcomer (required; may be empty).</summary>
        public IReadOnlyList<SpectatorInfo> CurrentSpectators { get; }

        /// <summary>Gets the join reason token (required).</summary>
        public string Reason { get; }

        /// <summary>Initializes a new <see cref="NewSpectatorJoinedMessage"/> payload.</summary>
        public NewSpectatorJoinedMessage(
            SpectatorInfo spectator,
            IReadOnlyList<SpectatorInfo> currentSpectators,
            string reason
        )
        {
            Spectator = spectator;
            CurrentSpectators = currentSpectators;
            Reason = reason;
        }

        /// <inheritdoc />
        public bool Equals(NewSpectatorJoinedMessage other) =>
            Spectator == other.Spectator
            && SpectatorInfo.SequenceEquals(CurrentSpectators, other.CurrentSpectators)
            && AuthenticateMessage.NullableStringEquals(Reason, other.Reason);

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is NewSpectatorJoinedMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(Spectator);
            hash.Add(SequenceHashCode(CurrentSpectators));
            hash.Add(Reason);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(
            NewSpectatorJoinedMessage left,
            NewSpectatorJoinedMessage right
        ) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(
            NewSpectatorJoinedMessage left,
            NewSpectatorJoinedMessage right
        ) => !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>NewSpectatorJoined</c>
        /// envelope (the <see cref="EnvelopeEvent.Data"/> slice). Unknown
        /// fields are skipped; a repeated key or a wrong-typed value is
        /// rejected. Returns <see langword="false"/> for malformed input or
        /// a missing required field.
        /// </summary>
        internal static bool TryDecode(
            ReadOnlyMemory<byte> data,
            out NewSpectatorJoinedMessage message
        )
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            SpectatorInfo spectator = default;
            IReadOnlyList<SpectatorInfo>? currentSpectators = null;
            string? reason = null;
            bool spectatorSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "spectator"))
                {
                    /*
                        The member value is a sub-object: slice the outer
                        payload and hand the slice to the element decoder.
                    */
                    if (
                        spectatorSeen
                        || !JsonScanner.TryReadObjectSlice(
                            data,
                            valueRaw,
                            out ReadOnlyMemory<byte> slice
                        )
                        || !SpectatorInfo.TryDecode(slice, out spectator)
                    )
                    {
                        return false;
                    }

                    spectatorSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "current_spectators"))
                {
                    if (
                        currentSpectators is not null
                        || !SpectatorInfo.TryReadArray(
                            data,
                            valueRaw,
                            out IReadOnlyList<SpectatorInfo>? parsed
                        )
                        || parsed is null
                    )
                    {
                        return false;
                    }

                    currentSpectators = parsed;
                }
                else if (scanner.KeyIs(keyRaw, "reason"))
                {
                    if (reason is not null || !scanner.TryReadString(valueRaw, out reason))
                    {
                        return false;
                    }
                }

                state = scanner.EndMember();
            }

            if (
                state != JsonMemberState.EndObject
                || !spectatorSeen
                || currentSpectators is null
                || reason is null
            )
            {
                return false;
            }

            message = new NewSpectatorJoinedMessage(spectator, currentSpectators, reason);
            return true;
        }

        private static int SequenceHashCode(IReadOnlyList<SpectatorInfo>? values)
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
    /// Payload of the inbound <c>SpectatorDisconnected</c> message (S→C): a
    /// spectator's connection dropped. All fields are required. Wire order:
    /// <c>spectator_id</c>, <c>reason</c>, <c>current_spectators</c>.
    /// </summary>
    public readonly struct SpectatorDisconnectedMessage : IEquatable<SpectatorDisconnectedMessage>
    {
        /// <summary>Gets the spectator identity that dropped (required).</summary>
        public Guid SpectatorId { get; }

        /// <summary>Gets the disconnect reason token (required).</summary>
        public string Reason { get; }

        /// <summary>Gets the spectators that remain (required; may be empty).</summary>
        public IReadOnlyList<SpectatorInfo> CurrentSpectators { get; }

        /// <summary>Initializes a new <see cref="SpectatorDisconnectedMessage"/> payload.</summary>
        public SpectatorDisconnectedMessage(
            Guid spectatorId,
            string reason,
            IReadOnlyList<SpectatorInfo> currentSpectators
        )
        {
            SpectatorId = spectatorId;
            Reason = reason;
            CurrentSpectators = currentSpectators;
        }

        /// <inheritdoc />
        public bool Equals(SpectatorDisconnectedMessage other) =>
            SpectatorId == other.SpectatorId
            && AuthenticateMessage.NullableStringEquals(Reason, other.Reason)
            && SpectatorInfo.SequenceEquals(CurrentSpectators, other.CurrentSpectators);

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is SpectatorDisconnectedMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(SpectatorId);
            hash.Add(Reason);
            hash.Add(SequenceHashCode(CurrentSpectators));
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(
            SpectatorDisconnectedMessage left,
            SpectatorDisconnectedMessage right
        ) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(
            SpectatorDisconnectedMessage left,
            SpectatorDisconnectedMessage right
        ) => !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>SpectatorDisconnected</c>
        /// envelope (the <see cref="EnvelopeEvent.Data"/> slice). Unknown
        /// fields are skipped; a repeated key or a wrong-typed value is
        /// rejected. Returns <see langword="false"/> for malformed input or
        /// a missing required field.
        /// </summary>
        internal static bool TryDecode(
            ReadOnlyMemory<byte> data,
            out SpectatorDisconnectedMessage message
        )
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            Guid spectatorId = default;
            string? reason = null;
            IReadOnlyList<SpectatorInfo>? currentSpectators = null;
            bool spectatorSeen = false;

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
                else if (scanner.KeyIs(keyRaw, "reason"))
                {
                    if (reason is not null || !scanner.TryReadString(valueRaw, out reason))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "current_spectators"))
                {
                    if (
                        currentSpectators is not null
                        || !SpectatorInfo.TryReadArray(
                            data,
                            valueRaw,
                            out IReadOnlyList<SpectatorInfo>? parsed
                        )
                        || parsed is null
                    )
                    {
                        return false;
                    }

                    currentSpectators = parsed;
                }

                state = scanner.EndMember();
            }

            if (
                state != JsonMemberState.EndObject
                || !spectatorSeen
                || reason is null
                || currentSpectators is null
            )
            {
                return false;
            }

            message = new SpectatorDisconnectedMessage(spectatorId, reason, currentSpectators);
            return true;
        }

        private static int SequenceHashCode(IReadOnlyList<SpectatorInfo>? values)
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
