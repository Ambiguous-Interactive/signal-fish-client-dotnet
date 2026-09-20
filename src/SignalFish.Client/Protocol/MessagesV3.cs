namespace SignalFish.Client.Protocol
{
    using System;

    /// <summary>
    /// The room command wrapped by a v3 <c>RoomOperation</c> envelope: the
    /// five legacy room lifecycle commands plus the six authority-only
    /// moderation commands.
    /// </summary>
    public enum RoomOperationCommandKind : byte
    {
        /// <summary>Join or create a room (full <see cref="JoinRoomMessage"/> payload).</summary>
        JoinRoom = 0,

        /// <summary>Leave the current room (no payload).</summary>
        LeaveRoom = 1,

        /// <summary>Resume a seat with the server-issued token (full <see cref="ReconnectMessage"/> payload).</summary>
        Reconnect = 2,

        /// <summary>Join as a spectator (full <see cref="JoinAsSpectatorMessage"/> payload).</summary>
        JoinAsSpectator = 3,

        /// <summary>Stop spectating (no payload).</summary>
        LeaveSpectator = 4,

        /// <summary>Remove a seated player or pending reconnection holder (player_id payload).</summary>
        KickPlayer = 5,

        /// <summary>Rotate the room code (no payload).</summary>
        RegenerateRoomCode = 6,

        /// <summary>Seal or reopen the room (nullable password payload).</summary>
        SetRoomAccess = 7,

        /// <summary>Kick and ban a member for the room's lifetime (player_id payload).</summary>
        BanPlayer = 8,

        /// <summary>Lift a room ban, idempotently (player_id payload).</summary>
        UnbanPlayer = 9,

        /// <summary>Transfer authority to a seated player (player_id payload).</summary>
        TransferAuthority = 10,
    }

    /// <summary>
    /// The command carried inside a v3 <c>RoomOperation</c> envelope. Each
    /// kind pairs with exactly one payload shape: the legacy room commands
    /// reuse their full payloads, the moderation commands carry a
    /// <c>player_id</c>, and <see cref="RoomOperationCommandKind.SetRoomAccess"/>
    /// carries an explicit password (nullable — <see langword="null"/>
    /// reopens the room). Build via the static factories; the kind and its
    /// payload cannot disagree.
    /// </summary>
    public readonly struct RoomOperationCommand : IEquatable<RoomOperationCommand>
    {
        /// <summary>Gets the wrapped command kind.</summary>
        public RoomOperationCommandKind Kind { get; }

        /// <summary>Gets the join payload; valid only for <see cref="RoomOperationCommandKind.JoinRoom"/>.</summary>
        public JoinRoomMessage JoinRoomPayload { get; }

        /// <summary>Gets the spectator-join payload; valid only for <see cref="RoomOperationCommandKind.JoinAsSpectator"/>.</summary>
        public JoinAsSpectatorMessage JoinAsSpectatorPayload { get; }

        /// <summary>Gets the reconnect payload; valid only for <see cref="RoomOperationCommandKind.Reconnect"/>.</summary>
        public ReconnectMessage ReconnectPayload { get; }

        /// <summary>
        /// Gets the target player id; valid for
        /// <see cref="RoomOperationCommandKind.KickPlayer"/>,
        /// <see cref="RoomOperationCommandKind.BanPlayer"/>,
        /// <see cref="RoomOperationCommandKind.UnbanPlayer"/>, and
        /// <see cref="RoomOperationCommandKind.TransferAuthority"/>.
        /// </summary>
        public string? PlayerId { get; }

        /// <summary>
        /// Gets the access password for <see cref="RoomOperationCommandKind.SetRoomAccess"/>;
        /// <see langword="null"/> reopens the room.
        /// </summary>
        public string? Password { get; }

        private RoomOperationCommand(
            RoomOperationCommandKind kind,
            JoinRoomMessage joinRoom = default,
            JoinAsSpectatorMessage joinAsSpectator = default,
            ReconnectMessage reconnect = default,
            string? playerId = null,
            string? password = null)
        {
            Kind = kind;
            JoinRoomPayload = joinRoom;
            JoinAsSpectatorPayload = joinAsSpectator;
            ReconnectPayload = reconnect;
            PlayerId = playerId;
            Password = password;
        }

        /// <summary>Wraps a seated join command.</summary>
        public static RoomOperationCommand JoinRoom(in JoinRoomMessage joinRoom) =>
            new RoomOperationCommand(RoomOperationCommandKind.JoinRoom, joinRoom: joinRoom);

        /// <summary>Wraps a leave-room command.</summary>
        public static RoomOperationCommand LeaveRoom() =>
            new RoomOperationCommand(RoomOperationCommandKind.LeaveRoom);

        /// <summary>Wraps a reconnection command.</summary>
        public static RoomOperationCommand Reconnect(in ReconnectMessage reconnect) =>
            new RoomOperationCommand(RoomOperationCommandKind.Reconnect, reconnect: reconnect);

        /// <summary>Wraps a spectator-join command.</summary>
        public static RoomOperationCommand JoinAsSpectator(in JoinAsSpectatorMessage joinAsSpectator) =>
            new RoomOperationCommand(RoomOperationCommandKind.JoinAsSpectator, joinAsSpectator: joinAsSpectator);

        /// <summary>Wraps a leave-spectator command.</summary>
        public static RoomOperationCommand LeaveSpectator() =>
            new RoomOperationCommand(RoomOperationCommandKind.LeaveSpectator);

        /// <summary>Wraps a kick command naming a seated player or pending reconnection holder.</summary>
        public static RoomOperationCommand KickPlayer(string playerId) =>
            new RoomOperationCommand(RoomOperationCommandKind.KickPlayer, playerId: playerId);

        /// <summary>Wraps a room-code rotation command.</summary>
        public static RoomOperationCommand RegenerateRoomCode() =>
            new RoomOperationCommand(RoomOperationCommandKind.RegenerateRoomCode);

        /// <summary>Wraps an access command; <paramref name="password"/> seals the room, <see langword="null"/> reopens it.</summary>
        public static RoomOperationCommand SetRoomAccess(string? password) =>
            new RoomOperationCommand(RoomOperationCommandKind.SetRoomAccess, password: password);

        /// <summary>Wraps a ban command naming a seated player or pending reconnection holder.</summary>
        public static RoomOperationCommand BanPlayer(string playerId) =>
            new RoomOperationCommand(RoomOperationCommandKind.BanPlayer, playerId: playerId);

        /// <summary>Wraps an idempotent unban command.</summary>
        public static RoomOperationCommand UnbanPlayer(string playerId) =>
            new RoomOperationCommand(RoomOperationCommandKind.UnbanPlayer, playerId: playerId);

        /// <summary>Wraps an authority-transfer command naming a seated player.</summary>
        public static RoomOperationCommand TransferAuthority(string playerId) =>
            new RoomOperationCommand(RoomOperationCommandKind.TransferAuthority, playerId: playerId);

        /// <inheritdoc />
        public bool Equals(RoomOperationCommand other)
        {
            if (Kind != other.Kind)
            {
                return false;
            }

            return Kind switch
            {
                RoomOperationCommandKind.JoinRoom => JoinRoomPayload.Equals(other.JoinRoomPayload),
                RoomOperationCommandKind.JoinAsSpectator => JoinAsSpectatorPayload.Equals(other.JoinAsSpectatorPayload),
                RoomOperationCommandKind.Reconnect => ReconnectPayload.Equals(other.ReconnectPayload),
                RoomOperationCommandKind.SetRoomAccess => AuthenticateMessage.NullableStringEquals(Password, other.Password),
                RoomOperationCommandKind.KickPlayer => AuthenticateMessage.NullableStringEquals(PlayerId, other.PlayerId),
                RoomOperationCommandKind.BanPlayer => AuthenticateMessage.NullableStringEquals(PlayerId, other.PlayerId),
                RoomOperationCommandKind.UnbanPlayer => AuthenticateMessage.NullableStringEquals(PlayerId, other.PlayerId),
                RoomOperationCommandKind.TransferAuthority => AuthenticateMessage.NullableStringEquals(PlayerId, other.PlayerId),
                _ => true,
            };
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is RoomOperationCommand other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(Kind);
            switch (Kind)
            {
                case RoomOperationCommandKind.JoinRoom:
                    hash.Add(JoinRoomPayload);
                    break;
                case RoomOperationCommandKind.JoinAsSpectator:
                    hash.Add(JoinAsSpectatorPayload);
                    break;
                case RoomOperationCommandKind.Reconnect:
                    hash.Add(ReconnectPayload);
                    break;
                case RoomOperationCommandKind.SetRoomAccess:
                    hash.Add(Password);
                    break;
                case RoomOperationCommandKind.KickPlayer:
                case RoomOperationCommandKind.BanPlayer:
                case RoomOperationCommandKind.UnbanPlayer:
                case RoomOperationCommandKind.TransferAuthority:
                    hash.Add(PlayerId);
                    break;
            }

            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(RoomOperationCommand left, RoomOperationCommand right) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(RoomOperationCommand left, RoomOperationCommand right) => !left.Equals(right);

        /// <summary>
        /// Decodes the wrapped <c>operation</c> value of a
        /// <c>RoomOperation</c> payload: a tagged object
        /// <c>{"type": "&lt;Command&gt;", "data": { ... }}</c> whose data uses
        /// the legacy command shape. Unknown fields are skipped. Returns
        /// <see langword="false"/> for malformed input, an unknown command
        /// type, or a missing required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out RoomOperationCommand command)
        {
            command = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            string? type = null;
            ReadOnlyMemory<byte> payload = default;
            bool payloadSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "type"))
                {
                    if (!scanner.TryReadString(valueRaw, out type))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "data"))
                {
                    (int Offset, int Length) slice = valueRaw.GetOffsetAndLength(data.Length);
                    payload = data.Slice(slice.Offset, slice.Length);
                    payloadSeen = true;
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || type is null)
            {
                return false;
            }

            switch (type)
            {
                case "JoinRoom":
                    if (!payloadSeen || !JoinRoomMessage.TryDecode(payload, out JoinRoomMessage joinRoom))
                    {
                        return false;
                    }

                    command = JoinRoom(joinRoom);
                    return true;
                case "LeaveRoom":
                    command = LeaveRoom();
                    return true;
                case "Reconnect":
                    if (!payloadSeen || !ReconnectMessage.TryDecode(payload, out ReconnectMessage reconnect))
                    {
                        return false;
                    }

                    command = Reconnect(reconnect);
                    return true;
                case "JoinAsSpectator":
                    if (!payloadSeen
                        || !JoinAsSpectatorMessage.TryDecode(payload, out JoinAsSpectatorMessage joinAsSpectator))
                    {
                        return false;
                    }

                    command = JoinAsSpectator(joinAsSpectator);
                    return true;
                case "LeaveSpectator":
                    command = LeaveSpectator();
                    return true;
                case "KickPlayer":
                    return TryDecodePlayerId(payload, RoomOperationCommandKind.KickPlayer, out command);
                case "RegenerateRoomCode":
                    command = RegenerateRoomCode();
                    return true;
                case "SetRoomAccess":
                    return TryDecodePassword(payload, out command);
                case "BanPlayer":
                    return TryDecodePlayerId(payload, RoomOperationCommandKind.BanPlayer, out command);
                case "UnbanPlayer":
                    return TryDecodePlayerId(payload, RoomOperationCommandKind.UnbanPlayer, out command);
                case "TransferAuthority":
                    return TryDecodePlayerId(payload, RoomOperationCommandKind.TransferAuthority, out command);
                default:
                    return false;
            }
        }

        private static bool TryDecodePlayerId(ReadOnlyMemory<byte> data, RoomOperationCommandKind kind, out RoomOperationCommand command)
        {
            command = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            string? playerId = null;

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

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || playerId is null)
            {
                return false;
            }

            command = kind switch
            {
                RoomOperationCommandKind.KickPlayer => KickPlayer(playerId),
                RoomOperationCommandKind.BanPlayer => BanPlayer(playerId),
                RoomOperationCommandKind.UnbanPlayer => UnbanPlayer(playerId),
                _ => TransferAuthority(playerId),
            };
            return true;
        }

        private static bool TryDecodePassword(ReadOnlyMemory<byte> data, out RoomOperationCommand command)
        {
            command = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            string? password = null;
            bool seen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "password"))
                {
                    if (scanner.TryReadString(valueRaw, out string? present))
                    {
                        password = present;
                    }
                    else if (!scanner.TryReadNull(valueRaw))
                    {
                        return false;
                    }

                    seen = true;
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || !seen)
            {
                return false;
            }

            command = SetRoomAccess(password);
            return true;
        }
    }

    /// <summary>
    /// Payload of the outbound v3 <c>RoomOperation</c> message (C→S): wrap
    /// one room command with a client-generated UUID so the server can echo
    /// the terminal result. Requires the <c>room_operation_ids</c>
    /// capability. <paramref name="operationId"/> must be the lowercase
    /// hyphenated canonical UUID form; any other encoding is rejected by the
    /// server as a malformed frame.
    /// </summary>
    public readonly struct RoomOperationMessage : IEquatable<RoomOperationMessage>
    {
        /// <summary>Gets the client-generated correlation UUID (lowercase hyphenated canonical form).</summary>
        public string OperationId { get; }

        /// <summary>Gets the wrapped room command.</summary>
        public RoomOperationCommand Command { get; }

        /// <summary>Initializes a new <see cref="RoomOperationMessage"/> payload.</summary>
        public RoomOperationMessage(string operationId, in RoomOperationCommand command)
        {
            OperationId = operationId;
            Command = command;
        }

        /// <inheritdoc />
        public bool Equals(RoomOperationMessage other) =>
            AuthenticateMessage.NullableStringEquals(OperationId, other.OperationId)
            && Command.Equals(other.Command);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is RoomOperationMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(OperationId);
            hash.Add(Command);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(RoomOperationMessage left, RoomOperationMessage right) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(RoomOperationMessage left, RoomOperationMessage right) => !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>RoomOperation</c> envelope
        /// (the <see cref="EnvelopeEvent.Data"/> slice). Unknown fields are
        /// skipped. Returns <see langword="false"/> for malformed input or a
        /// missing required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out RoomOperationMessage message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            string? operationId = null;
            ReadOnlyMemory<byte> operation = default;
            bool operationSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "operation_id"))
                {
                    if (!scanner.TryReadString(valueRaw, out operationId))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "operation"))
                {
                    if (!scanner.TryReadObjectSlice(data, valueRaw, out operation))
                    {
                        return false;
                    }

                    operationSeen = true;
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject
                || operationId is null
                || !operationSeen
                || !RoomOperationCommand.TryDecode(operation, out RoomOperationCommand command))
            {
                return false;
            }

            message = new RoomOperationMessage(operationId, command);
            return true;
        }
    }

    /// <summary>
    /// Payload of the outbound v3 <c>Signal</c> message (C→S): relay an
    /// opaque WebRTC signal to a specific same-room peer. The
    /// <paramref name="signal"/> value is a verbatim JSON value forwarded
    /// without inspection (by convention one of <c>{"Offer": "..."}</c>,
    /// <c>{"Answer": "..."}</c>, or <c>{"IceCandidate": "..."}</c>); it must
    /// be valid UTF-8 JSON.
    /// </summary>
    public readonly struct SignalMessage : IEquatable<SignalMessage>
    {
        private readonly ReadOnlyMemory<byte> _signal;

        /// <summary>Initializes a new <see cref="SignalMessage"/> payload.</summary>
        /// <param name="to">The target peer's player UUID.</param>
        /// <param name="generation">The generation UUID from the sender's latest <c>SessionPlan</c>.</param>
        /// <param name="signal">The signal JSON value, as UTF-8 bytes.</param>
        public SignalMessage(string to, string generation, ReadOnlyMemory<byte> signal)
        {
            To = to;
            Generation = generation;
            _signal = signal;
        }

        /// <summary>Gets the target peer's player UUID.</summary>
        public string To { get; }

        /// <summary>Gets the generation UUID from the sender's latest <c>SessionPlan</c>.</summary>
        public string Generation { get; }

        /// <summary>Gets the signal JSON value, as UTF-8 bytes (relayed verbatim).</summary>
        public ReadOnlyMemory<byte> Signal => _signal;

        /// <inheritdoc />
        public bool Equals(SignalMessage other) =>
            AuthenticateMessage.NullableStringEquals(To, other.To)
            && AuthenticateMessage.NullableStringEquals(Generation, other.Generation)
            && _signal.Span.SequenceEqual(other._signal.Span);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is SignalMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(To);
            hash.Add(Generation);
            hash.Add(ProtocolHash.Of(_signal.Span));
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(SignalMessage left, SignalMessage right) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(SignalMessage left, SignalMessage right) => !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>Signal</c> envelope (the
        /// <see cref="EnvelopeEvent.Data"/> slice) into the target, plan
        /// generation, and verbatim signal value. Unknown fields are
        /// skipped. Returns <see langword="false"/> for malformed input or a
        /// missing required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out SignalMessage message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            string? to = null, generation = null;
            ReadOnlyMemory<byte> signal = default;
            bool signalSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "to"))
                {
                    if (!scanner.TryReadString(valueRaw, out to))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "generation"))
                {
                    if (!scanner.TryReadString(valueRaw, out generation))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "signal"))
                {
                    (int Offset, int Length) slice = valueRaw.GetOffsetAndLength(data.Length);
                    signal = data.Slice(slice.Offset, slice.Length);
                    signalSeen = true;
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || to is null || generation is null || !signalSeen)
            {
                return false;
            }

            message = new SignalMessage(to, generation, signal);
            return true;
        }
    }

    /// <summary>
    /// Payload of the outbound v3 <c>TransportStatus</c> message (C→S):
    /// report the current data-path transport state (informational; drives
    /// server metrics and peer fan-out).
    /// </summary>
    public readonly struct TransportStatusMessage : IEquatable<TransportStatusMessage>
    {
        /// <summary>Gets the data-path transport token (e.g. <c>webrtc</c>); must be in the negotiated set.</summary>
        public string Transport { get; }

        /// <summary>Gets a value indicating whether the transport is connected.</summary>
        public bool Connected { get; }

        /// <summary>Initializes a new <see cref="TransportStatusMessage"/> payload.</summary>
        public TransportStatusMessage(string transport, bool connected)
        {
            Transport = transport;
            Connected = connected;
        }

        /// <inheritdoc />
        public bool Equals(TransportStatusMessage other) =>
            AuthenticateMessage.NullableStringEquals(Transport, other.Transport)
            && Connected == other.Connected;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is TransportStatusMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(Transport);
            hash.Add(Connected);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(TransportStatusMessage left, TransportStatusMessage right) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(TransportStatusMessage left, TransportStatusMessage right) => !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>TransportStatus</c>
        /// envelope (the <see cref="EnvelopeEvent.Data"/> slice). Unknown
        /// fields are skipped. Returns <see langword="false"/> for malformed
        /// input or a missing required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out TransportStatusMessage message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            string? transport = null;
            bool connected = false;
            bool connectedSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "transport"))
                {
                    if (!scanner.TryReadString(valueRaw, out transport))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "connected"))
                {
                    if (!scanner.TryReadBoolean(valueRaw, out connected))
                    {
                        return false;
                    }

                    connectedSeen = true;
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || transport is null || !connectedSeen)
            {
                return false;
            }

            message = new TransportStatusMessage(transport, connected);
            return true;
        }
    }
}
