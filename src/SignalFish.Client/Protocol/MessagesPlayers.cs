namespace SignalFish.Client.Protocol
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// One seated player as reported by the server in rosters and
    /// membership events. All fields are required.
    /// </summary>
    public readonly struct PlayerInfo : IEquatable<PlayerInfo>
    {
        public Guid Id { get; }

        /// <summary>Gets the display name (required).</summary>
        public string Name { get; }

        /// <summary>Gets a value indicating whether the player holds authority (required).</summary>
        public bool IsAuthority { get; }

        /// <summary>Gets a value indicating whether the player declared readiness (required).</summary>
        public bool IsReady { get; }

        /// <summary>
        /// Gets the connection timestamp as a verbatim ISO-8601 string.
        /// Optional: the server strips it from every protocol-v3 room
        /// snapshot, so it reads <see langword="null"/> there. Never parsed
        /// or normalized — the game owns time interpretation.
        /// </summary>
        public string? ConnectedAt { get; }

        /// <summary>Gets the player's reconnection epoch baseline (null when the frame omits it).</summary>
        public uint? Epoch { get; }

        /// <summary>Gets the player's next-sequence baseline (null when the frame omits it).</summary>
        public ulong? Seq { get; }

        /// <summary>Initializes a new <see cref="PlayerInfo"/> value.</summary>
        public PlayerInfo(
            Guid id,
            string name,
            bool isAuthority,
            bool isReady,
            string? connectedAt,
            uint? epoch = null,
            ulong? seq = null
        )
        {
            Id = id;
            Name = name;
            IsAuthority = isAuthority;
            IsReady = isReady;
            ConnectedAt = connectedAt;
            Epoch = epoch;
            Seq = seq;
        }

        /// <inheritdoc />
        public bool Equals(PlayerInfo other) =>
            Id == other.Id
            && AuthenticateMessage.NullableStringEquals(Name, other.Name)
            && IsAuthority == other.IsAuthority
            && IsReady == other.IsReady
            && AuthenticateMessage.NullableStringEquals(ConnectedAt, other.ConnectedAt)
            && Epoch == other.Epoch
            && Seq == other.Seq;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is PlayerInfo other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(Id);
            hash.Add(Name);
            hash.Add(IsAuthority);
            hash.Add(IsReady);
            hash.Add(ConnectedAt);
            hash.Add(Epoch);
            hash.Add(Seq);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(PlayerInfo left, PlayerInfo right) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(PlayerInfo left, PlayerInfo right) => !left.Equals(right);

        /// <summary>
        /// Element-wise equality for player rosters; two
        /// <see langword="null"/> lists are equal, a
        /// <see langword="null"/> list never equals a non-null one.
        /// </summary>
        internal static bool SequenceEquals(
            IReadOnlyList<PlayerInfo>? left,
            IReadOnlyList<PlayerInfo>? right
        )
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

        /// <summary>
        /// Decodes one <c>PlayerInfo</c> object (a sliced sub-object of a
        /// payload). Unknown fields are skipped; a repeated key or a
        /// wrong-typed value is rejected. Returns
        /// <see langword="false"/> for malformed input or a missing
        /// required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out PlayerInfo player)
        {
            player = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            Guid id = default;
            string? name = null;
            bool isAuthority = false;
            bool isReady = false;
            string? connectedAt = null;
            uint? epoch = null;
            ulong? seq = null;
            bool idSeen = false;
            bool authoritySeen = false;
            bool readySeen = false;
            bool epochSeen = false;
            bool seqSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "id"))
                {
                    if (idSeen || !scanner.TryReadGuid(valueRaw, out id))
                    {
                        return false;
                    }

                    idSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "name"))
                {
                    if (name is not null || !scanner.TryReadString(valueRaw, out name))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "is_authority"))
                {
                    if (authoritySeen || !scanner.TryReadBoolean(valueRaw, out isAuthority))
                    {
                        return false;
                    }

                    authoritySeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "is_ready"))
                {
                    if (readySeen || !scanner.TryReadBoolean(valueRaw, out isReady))
                    {
                        return false;
                    }

                    readySeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "connected_at"))
                {
                    if (
                        connectedAt is not null
                        || !scanner.TryReadString(valueRaw, out connectedAt)
                    )
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "epoch"))
                {
                    if (epochSeen)
                    {
                        return false;
                    }

                    epochSeen = true;
                    if (scanner.TryReadNull(valueRaw))
                    {
                        epoch = null;
                    }
                    else if (!scanner.TryReadUInt32(valueRaw, out uint epochValue))
                    {
                        return false;
                    }
                    else
                    {
                        epoch = epochValue;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "seq"))
                {
                    if (seqSeen)
                    {
                        return false;
                    }

                    seqSeen = true;
                    if (scanner.TryReadNull(valueRaw))
                    {
                        seq = null;
                    }
                    else if (!scanner.TryReadUInt64(valueRaw, out ulong seqValue))
                    {
                        return false;
                    }
                    else
                    {
                        seq = seqValue;
                    }
                }

                state = scanner.EndMember();
            }

            if (
                state != JsonMemberState.EndObject
                || !idSeen
                || name is null
                || !authoritySeen
                || !readySeen
            )
            {
                return false;
            }

            player = new PlayerInfo(id, name, isAuthority, isReady, connectedAt, epoch, seq);
            return true;
        }

        /// <summary>
        /// Reads a scanned value that must be a JSON array of
        /// <c>PlayerInfo</c> objects into a list (decode path; allocates the
        /// result).
        /// </summary>
        internal static bool TryReadArray(
            ReadOnlyMemory<byte> data,
            Range valueRaw,
            out IReadOnlyList<PlayerInfo>? values
        )
        {
            values = null;
            (int Offset, int Length) s = valueRaw.GetOffsetAndLength(data.Length);
            if (s.Length < 2 || data.Span[s.Offset] != (byte)'[')
            {
                return false;
            }

            JsonScanner scanner = new JsonScanner(data.Span.Slice(s.Offset, s.Length));
            scanner.SkipWhitespace();
            if (scanner.Expect((byte)'[') != default(DecodeError))
            {
                return false;
            }

            List<PlayerInfo> list = new List<PlayerInfo>();
            scanner.SkipWhitespace();
            if (scanner.Peek == (byte)']')
            {
                if (scanner.Expect((byte)']') != default(DecodeError))
                {
                    return false;
                }
            }
            else
            {
                while (true)
                {
                    scanner.SkipWhitespace();

                    /*
                        Elements validate at member-value depth, the same
                        level JsonScanner.ScanMember uses for payload values.
                    */
                    if (
                        scanner.ScanValueRaw(2, JsonScanner.MaxDepth, out Range element)
                        != default(DecodeError)
                    )
                    {
                        return false;
                    }

                    (int EOffset, int ELength) e = element.GetOffsetAndLength(s.Length);
                    if (
                        !TryDecode(
                            data.Slice(s.Offset + e.EOffset, e.ELength),
                            out PlayerInfo player
                        )
                    )
                    {
                        return false;
                    }

                    list.Add(player);
                    scanner.SkipWhitespace();
                    byte next = scanner.Peek;
                    if (next == (byte)',')
                    {
                        if (scanner.Expect((byte)',') != default(DecodeError))
                        {
                            return false;
                        }

                        continue;
                    }

                    if (next == (byte)']')
                    {
                        if (scanner.Expect((byte)']') != default(DecodeError))
                        {
                            return false;
                        }

                        break;
                    }

                    return false;
                }
            }

            scanner.SkipWhitespace();
            if (!scanner.IsEof)
            {
                return false;
            }

            values = list;
            return true;
        }
    }

    /// <summary>
    /// One connected spectator as reported by the server in spectator
    /// rosters and membership events. Identity and name are required; the
    /// timestamp is optional (absent on protocol-v3 snapshots).
    /// </summary>
    public readonly struct SpectatorInfo : IEquatable<SpectatorInfo>
    {
        public Guid Id { get; }

        /// <summary>Gets the display name (required).</summary>
        public string Name { get; }

        /// <summary>
        /// Gets the connection timestamp as a verbatim ISO-8601 string.
        /// Optional: the server strips it from every protocol-v3 room
        /// snapshot, so it reads <see langword="null"/> there. Never parsed
        /// or normalized — the game owns time interpretation.
        /// </summary>
        public string? ConnectedAt { get; }

        /// <summary>Initializes a new <see cref="SpectatorInfo"/> value.</summary>
        public SpectatorInfo(Guid id, string name, string? connectedAt)
        {
            Id = id;
            Name = name;
            ConnectedAt = connectedAt;
        }

        /// <inheritdoc />
        public bool Equals(SpectatorInfo other) =>
            Id == other.Id
            && AuthenticateMessage.NullableStringEquals(Name, other.Name)
            && AuthenticateMessage.NullableStringEquals(ConnectedAt, other.ConnectedAt);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is SpectatorInfo other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(Id);
            hash.Add(Name);
            hash.Add(ConnectedAt);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(SpectatorInfo left, SpectatorInfo right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(SpectatorInfo left, SpectatorInfo right) =>
            !left.Equals(right);

        /// <summary>
        /// Element-wise equality for spectator rosters; two
        /// <see langword="null"/> lists are equal, a
        /// <see langword="null"/> list never equals a non-null one.
        /// </summary>
        internal static bool SequenceEquals(
            IReadOnlyList<SpectatorInfo>? left,
            IReadOnlyList<SpectatorInfo>? right
        )
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

        /// <summary>
        /// Decodes one <c>SpectatorInfo</c> object (a sliced sub-object of a
        /// payload). Unknown fields are skipped; a repeated key or a
        /// wrong-typed value is rejected. Returns
        /// <see langword="false"/> for malformed input or a missing
        /// required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out SpectatorInfo spectator)
        {
            spectator = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            Guid id = default;
            string? name = null;
            string? connectedAt = null;
            bool idSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "id"))
                {
                    if (idSeen || !scanner.TryReadGuid(valueRaw, out id))
                    {
                        return false;
                    }

                    idSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "name"))
                {
                    if (name is not null || !scanner.TryReadString(valueRaw, out name))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "connected_at"))
                {
                    if (
                        connectedAt is not null
                        || !scanner.TryReadString(valueRaw, out connectedAt)
                    )
                    {
                        return false;
                    }
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || !idSeen || name is null)
            {
                return false;
            }

            spectator = new SpectatorInfo(id, name, connectedAt);
            return true;
        }

        /// <summary>
        /// Reads a scanned value that must be a JSON array of
        /// <c>SpectatorInfo</c> objects into a list (decode path; allocates
        /// the result).
        /// </summary>
        internal static bool TryReadArray(
            ReadOnlyMemory<byte> data,
            Range valueRaw,
            out IReadOnlyList<SpectatorInfo>? values
        )
        {
            values = null;
            (int Offset, int Length) s = valueRaw.GetOffsetAndLength(data.Length);
            if (s.Length < 2 || data.Span[s.Offset] != (byte)'[')
            {
                return false;
            }

            JsonScanner scanner = new JsonScanner(data.Span.Slice(s.Offset, s.Length));
            scanner.SkipWhitespace();
            if (scanner.Expect((byte)'[') != default(DecodeError))
            {
                return false;
            }

            List<SpectatorInfo> list = new List<SpectatorInfo>();
            scanner.SkipWhitespace();
            if (scanner.Peek == (byte)']')
            {
                if (scanner.Expect((byte)']') != default(DecodeError))
                {
                    return false;
                }
            }
            else
            {
                while (true)
                {
                    scanner.SkipWhitespace();

                    /*
                        Elements validate at member-value depth, the same
                        level JsonScanner.ScanMember uses for payload values.
                    */
                    if (
                        scanner.ScanValueRaw(2, JsonScanner.MaxDepth, out Range element)
                        != default(DecodeError)
                    )
                    {
                        return false;
                    }

                    (int EOffset, int ELength) e = element.GetOffsetAndLength(s.Length);
                    if (
                        !TryDecode(
                            data.Slice(s.Offset + e.EOffset, e.ELength),
                            out SpectatorInfo spectator
                        )
                    )
                    {
                        return false;
                    }

                    list.Add(spectator);
                    scanner.SkipWhitespace();
                    byte next = scanner.Peek;
                    if (next == (byte)',')
                    {
                        if (scanner.Expect((byte)',') != default(DecodeError))
                        {
                            return false;
                        }

                        continue;
                    }

                    if (next == (byte)']')
                    {
                        if (scanner.Expect((byte)']') != default(DecodeError))
                        {
                            return false;
                        }

                        break;
                    }

                    return false;
                }
            }

            scanner.SkipWhitespace();
            if (!scanner.IsEof)
            {
                return false;
            }

            values = list;
            return true;
        }
    }

    /// <summary>
    /// Payload of the inbound <c>PlayerJoined</c> message (S→C): a player
    /// joined the room. The <c>player</c> object is required.
    /// </summary>
    public readonly struct PlayerJoinedMessage : IEquatable<PlayerJoinedMessage>
    {
        /// <summary>Gets the player that joined (required).</summary>
        public PlayerInfo Player { get; }

        /// <summary>Initializes a new <see cref="PlayerJoinedMessage"/> payload.</summary>
        public PlayerJoinedMessage(PlayerInfo player)
        {
            Player = player;
        }

        /// <inheritdoc />
        public bool Equals(PlayerJoinedMessage other) => Player == other.Player;

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is PlayerJoinedMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(Player);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(PlayerJoinedMessage left, PlayerJoinedMessage right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(PlayerJoinedMessage left, PlayerJoinedMessage right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>PlayerJoined</c> envelope
        /// (the <see cref="EnvelopeEvent.Data"/> slice). Unknown fields are
        /// skipped; a repeated key or a wrong-typed value is rejected.
        /// Returns <see langword="false"/> for malformed input or a missing
        /// required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out PlayerJoinedMessage message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            PlayerInfo player = default;
            bool playerSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "player"))
                {
                    /*
                        The member value is a sub-object: slice the outer
                        payload and hand the slice to the element decoder.
                    */
                    if (
                        playerSeen
                        || !JsonScanner.TryReadObjectSlice(
                            data,
                            valueRaw,
                            out ReadOnlyMemory<byte> slice
                        )
                        || !PlayerInfo.TryDecode(slice, out player)
                    )
                    {
                        return false;
                    }

                    playerSeen = true;
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || !playerSeen)
            {
                return false;
            }

            message = new PlayerJoinedMessage(player);
            return true;
        }
    }

    /// <summary>
    /// Payload of the inbound <c>PlayerLeft</c> message (S→C): a player left
    /// the room. The <c>player_id</c> field is required.
    /// </summary>
    public readonly struct PlayerLeftMessage : IEquatable<PlayerLeftMessage>
    {
        /// <summary>Gets the player identity that left (required).</summary>
        public Guid PlayerId { get; }

        /// <summary>
        /// Gets the delivery epoch the seat ended on (null when the frame
        /// omits it — the v2 wire has no epoch).
        /// </summary>
        public uint? Epoch { get; }

        /// <summary>Gets the last sequence the player delivered (null when the frame omits it).</summary>
        public ulong? FinalSeq { get; }

        /// <summary>Initializes a new <see cref="PlayerLeftMessage"/> payload.</summary>
        public PlayerLeftMessage(Guid playerId, uint? epoch = null, ulong? finalSeq = null)
        {
            PlayerId = playerId;
            Epoch = epoch;
            FinalSeq = finalSeq;
        }

        /// <inheritdoc />
        public bool Equals(PlayerLeftMessage other) =>
            PlayerId == other.PlayerId && Epoch == other.Epoch && FinalSeq == other.FinalSeq;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is PlayerLeftMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(PlayerId);
            hash.Add(Epoch);
            hash.Add(FinalSeq);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(PlayerLeftMessage left, PlayerLeftMessage right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(PlayerLeftMessage left, PlayerLeftMessage right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>PlayerLeft</c> envelope
        /// (the <see cref="EnvelopeEvent.Data"/> slice). Unknown fields are
        /// skipped; a repeated key or a wrong-typed value is rejected.
        /// Returns <see langword="false"/> for malformed input or a missing
        /// required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out PlayerLeftMessage message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            Guid playerId = default;
            uint? epoch = null;
            ulong? finalSeq = null;
            bool playerSeen = false;
            bool epochSeen = false;
            bool finalSeqSeen = false;

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
                else if (scanner.KeyIs(keyRaw, "epoch"))
                {
                    if (epochSeen)
                    {
                        return false;
                    }

                    epochSeen = true;
                    if (scanner.TryReadNull(valueRaw))
                    {
                        epoch = null;
                    }
                    else if (!scanner.TryReadUInt32(valueRaw, out uint epochValue))
                    {
                        return false;
                    }
                    else
                    {
                        epoch = epochValue;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "final_seq"))
                {
                    if (finalSeqSeen)
                    {
                        return false;
                    }

                    finalSeqSeen = true;
                    if (scanner.TryReadNull(valueRaw))
                    {
                        finalSeq = null;
                    }
                    else if (!scanner.TryReadUInt64(valueRaw, out ulong finalSeqValue))
                    {
                        return false;
                    }
                    else
                    {
                        finalSeq = finalSeqValue;
                    }
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || !playerSeen)
            {
                return false;
            }

            message = new PlayerLeftMessage(playerId, epoch, finalSeq);
            return true;
        }
    }

    /// <summary>
    /// Payload of the inbound <c>PlayerReconnected</c> message (S→C): a
    /// player resumed a dropped seat. The <c>player_id</c> field is
    /// required.
    /// </summary>
    public readonly struct PlayerReconnectedMessage : IEquatable<PlayerReconnectedMessage>
    {
        public Guid PlayerId { get; }

        /// <summary>Gets the delivery epoch the seat resumed on (null when the frame omits it).</summary>
        public uint? Epoch { get; }

        /// <summary>Initializes a new <see cref="PlayerReconnectedMessage"/> payload.</summary>
        public PlayerReconnectedMessage(Guid playerId, uint? epoch = null)
        {
            PlayerId = playerId;
            Epoch = epoch;
        }

        /// <inheritdoc />
        public bool Equals(PlayerReconnectedMessage other) =>
            PlayerId == other.PlayerId && Epoch == other.Epoch;

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is PlayerReconnectedMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(PlayerId);
            hash.Add(Epoch);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(
            PlayerReconnectedMessage left,
            PlayerReconnectedMessage right
        ) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(
            PlayerReconnectedMessage left,
            PlayerReconnectedMessage right
        ) => !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>PlayerReconnected</c>
        /// envelope (the <see cref="EnvelopeEvent.Data"/> slice). Unknown
        /// fields are skipped; a repeated key or a wrong-typed value is
        /// rejected. Returns <see langword="false"/> for malformed input or
        /// a missing required field.
        /// </summary>
        internal static bool TryDecode(
            ReadOnlyMemory<byte> data,
            out PlayerReconnectedMessage message
        )
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            Guid playerId = default;
            uint? epoch = null;
            bool playerSeen = false;
            bool epochSeen = false;

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
                else if (scanner.KeyIs(keyRaw, "epoch"))
                {
                    if (epochSeen)
                    {
                        return false;
                    }

                    epochSeen = true;
                    if (scanner.TryReadNull(valueRaw))
                    {
                        epoch = null;
                    }
                    else if (!scanner.TryReadUInt32(valueRaw, out uint epochValue))
                    {
                        return false;
                    }
                    else
                    {
                        epoch = epochValue;
                    }
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || !playerSeen)
            {
                return false;
            }

            message = new PlayerReconnectedMessage(playerId, epoch);
            return true;
        }
    }
}
