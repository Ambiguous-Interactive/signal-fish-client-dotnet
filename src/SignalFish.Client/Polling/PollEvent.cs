namespace SignalFish.Client.Polling
{
    using System;
    using SignalFish.Client.Core;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.Transport;

    /// <summary>
    /// One drained polling event: a readonly struct carrying the decoded
    /// typed payloads of a single inbound frame. Payload properties are
    /// meaningful only for their <see cref="Kind"/> (documented per
    /// property) and hold <c>default</c> values otherwise, so events
    /// enqueue with zero per-field branching. Never compared (hot-path
    /// union; see the repository suppression note), constructed only by
    /// <see cref="SignalFishPollingClient"/> through the internal
    /// <c>From</c> factories.
    /// </summary>
    public readonly struct PollEvent
    {
        /// <summary>What happened.</summary>
        public PollEventKind Kind { get; }

        /// <summary>
        /// The confirmed membership; meaningful only for
        /// <see cref="PollEventKind.RoomJoined"/>,
        /// <see cref="PollEventKind.SpectatorJoined"/>, and
        /// <see cref="PollEventKind.Reconnected"/>.
        /// </summary>
        public RoomMembership Membership { get; }

        /// <summary>
        /// The decoded room state (players, lobby, relay); meaningful only
        /// for the membership-confirming kinds. Fields the frame omitted are
        /// <c>null</c>/<c>default</c>.
        /// </summary>
        public RoomSnapshot Snapshot { get; }

        /// <summary>
        /// The typed failure (reason + error code); meaningful only for
        /// <see cref="PollEventKind.RoomJoinFailed"/>,
        /// <see cref="PollEventKind.SpectatorJoinFailed"/>,
        /// <see cref="PollEventKind.ReconnectionFailed"/>, and
        /// <see cref="PollEventKind.ServerError"/>.
        /// </summary>
        public FailureMessage Failure { get; }

        /// <summary>
        /// The close descriptor; meaningful only for
        /// <see cref="PollEventKind.Disconnected"/>.
        /// </summary>
        public TransportClose Close { get; }

        /// <summary>
        /// The offending message kind; meaningful only for
        /// <see cref="PollEventKind.ProtocolViolation"/> (the default kind
        /// for transport-level violations such as an oversized or binary
        /// frame).
        /// </summary>
        public MessageKind Violation { get; }

        /// <summary>The bounded failure reason; meaningful only for <see cref="PollEventKind.DecodeFailed"/>.</summary>
        public DecodeError Error { get; }

        /// <summary>Byte offset of the failure; meaningful only for <see cref="PollEventKind.DecodeFailed"/>.</summary>
        public int ErrorOffset { get; }

        /// <summary>
        /// The unrecognized type text; meaningful only for
        /// <see cref="PollEventKind.UnknownMessage"/>.
        /// </summary>
        public string? TypeText { get; }

        /// <summary>The rate-limit budgets; meaningful only for <see cref="PollEventKind.Authenticated"/>.</summary>
        public AuthenticatedMessage Authenticated { get; }

        /// <summary>The server capabilities; meaningful only for <see cref="PollEventKind.ProtocolInfo"/>.</summary>
        public ProtocolInfoMessage ProtocolInfo { get; }

        /// <summary>The lobby state; meaningful only for <see cref="PollEventKind.LobbyStateChanged"/>.</summary>
        public LobbyStateChangedMessage Lobby { get; }

        /// <summary>The joining player; meaningful only for <see cref="PollEventKind.PlayerJoined"/>.</summary>
        public PlayerJoinedMessage PlayerJoined { get; }

        /// <summary>
        /// The departed/reconnected player id; meaningful only for
        /// <see cref="PollEventKind.PlayerLeft"/> and
        /// <see cref="PollEventKind.PlayerReconnected"/>.
        /// </summary>
        public Guid LeftPlayerId { get; }

        /// <summary>The peer connection plan; meaningful only for <see cref="PollEventKind.GameStarting"/>.</summary>
        public GameStartingMessage GameStart { get; }

        /// <summary>The authority answer; meaningful only for <see cref="PollEventKind.AuthorityResponse"/>.</summary>
        public AuthorityResponseMessage AuthorityResponse { get; }

        /// <summary>The authority change; meaningful only for <see cref="PollEventKind.AuthorityChanged"/>.</summary>
        public AuthorityChangedMessage AuthorityChanged { get; }

        /// <summary>The relayed payload; meaningful only for <see cref="PollEventKind.GameData"/>.</summary>
        public IncomingGameData GameData { get; }

        /// <summary>The spectator exit; meaningful only for <see cref="PollEventKind.SpectatorLeft"/>.</summary>
        public SpectatorLeftMessage SpectatorLeft { get; }

        /// <summary>The new spectator; meaningful only for <see cref="PollEventKind.NewSpectatorJoined"/>.</summary>
        public NewSpectatorJoinedMessage NewSpectator { get; }

        /// <summary>The dropped spectator; meaningful only for <see cref="PollEventKind.SpectatorDisconnected"/>.</summary>
        public SpectatorDisconnectedMessage SpectatorDisconnected { get; }

        /// <summary>The complete raw frame, as received (advanced inspection; empty for synthetic events).</summary>
        public ReadOnlyMemory<byte> Raw { get; }

        private PollEvent(
            PollEventKind kind,
            RoomMembership membership,
            RoomSnapshot snapshot,
            FailureMessage failure,
            TransportClose close,
            MessageKind violation,
            DecodeError error,
            int errorOffset,
            string? typeText,
            AuthenticatedMessage authenticated,
            ProtocolInfoMessage protocolInfo,
            LobbyStateChangedMessage lobby,
            PlayerJoinedMessage playerJoined,
            Guid leftPlayerId,
            GameStartingMessage gameStart,
            AuthorityResponseMessage authorityResponse,
            AuthorityChangedMessage authorityChanged,
            IncomingGameData gameData,
            SpectatorLeftMessage spectatorLeft,
            NewSpectatorJoinedMessage newSpectator,
            SpectatorDisconnectedMessage spectatorDisconnected,
            ReadOnlyMemory<byte> raw
        )
        {
            Kind = kind;
            Membership = membership;
            Snapshot = snapshot;
            Failure = failure;
            Close = close;
            Violation = violation;
            Error = error;
            ErrorOffset = errorOffset;
            TypeText = typeText;
            Authenticated = authenticated;
            ProtocolInfo = protocolInfo;
            Lobby = lobby;
            PlayerJoined = playerJoined;
            LeftPlayerId = leftPlayerId;
            GameStart = gameStart;
            AuthorityResponse = authorityResponse;
            AuthorityChanged = authorityChanged;
            GameData = gameData;
            SpectatorLeft = spectatorLeft;
            NewSpectator = newSpectator;
            SpectatorDisconnected = spectatorDisconnected;
            Raw = raw;
        }

        /// <summary>Creates the transport-ready event (no wire frame behind it).</summary>
        internal static PollEvent TransportReady()
        {
            return new PollEvent(
                PollEventKind.TransportReady,
                default,
                default,
                default,
                default,
                default,
                default,
                0,
                null,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default
            );
        }

        /// <summary>Creates the terminal disconnect event.</summary>
        internal static PollEvent Disconnected(TransportClose close)
        {
            return new PollEvent(
                PollEventKind.Disconnected,
                default,
                default,
                default,
                close,
                default,
                default,
                0,
                null,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default
            );
        }

        /// <summary>Creates a payload-less session event (e.g. <c>RoomLeft</c>).</summary>
        internal static PollEvent From(PollEventKind kind)
        {
            return new PollEvent(
                kind,
                default,
                default,
                default,
                default,
                default,
                default,
                0,
                null,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default
            );
        }

        /// <summary>Creates a membership-confirming event (join/reconnect kinds).</summary>
        internal static PollEvent FromMembership(
            PollEventKind kind,
            RoomMembership membership,
            in RoomSnapshot snapshot,
            ReadOnlyMemory<byte> raw
        )
        {
            return new PollEvent(
                kind,
                membership,
                snapshot,
                default,
                default,
                default,
                default,
                0,
                null,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                raw
            );
        }

        /// <summary>Creates a typed failure event (failure kinds and server errors).</summary>
        internal static PollEvent FromFailure(
            PollEventKind kind,
            in FailureMessage failure,
            ReadOnlyMemory<byte> raw
        )
        {
            return new PollEvent(
                kind,
                default,
                default,
                failure,
                default,
                default,
                default,
                0,
                null,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                raw
            );
        }

        /// <summary>Creates a protocol-violation event.</summary>
        internal static PollEvent FromViolation(MessageKind kind, ReadOnlyMemory<byte> raw)
        {
            return new PollEvent(
                PollEventKind.ProtocolViolation,
                default,
                default,
                default,
                default,
                kind,
                default,
                0,
                null,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                raw
            );
        }

        /// <summary>Creates a forward-compatibility unknown-message event.</summary>
        internal static PollEvent FromUnknown(string? typeText, ReadOnlyMemory<byte> raw)
        {
            return new PollEvent(
                PollEventKind.UnknownMessage,
                default,
                default,
                default,
                default,
                default,
                default,
                0,
                typeText,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                raw
            );
        }

        /// <summary>Creates a bounded decode-failure event.</summary>
        internal static PollEvent FromDecodeFailed(
            DecodeError error,
            int errorOffset,
            ReadOnlyMemory<byte> raw
        )
        {
            return new PollEvent(
                PollEventKind.DecodeFailed,
                default,
                default,
                default,
                default,
                default,
                error,
                errorOffset,
                null,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                raw
            );
        }

        /// <summary>Creates the server-authenticated event with its payload.</summary>
        internal static PollEvent FromAuthenticated(
            in AuthenticatedMessage payload,
            ReadOnlyMemory<byte> raw
        )
        {
            return new PollEvent(
                PollEventKind.Authenticated,
                default,
                default,
                default,
                default,
                default,
                default,
                0,
                null,
                payload,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                raw
            );
        }

        /// <summary>Creates the protocol-info event with its payload.</summary>
        internal static PollEvent FromProtocolInfo(
            in ProtocolInfoMessage payload,
            ReadOnlyMemory<byte> raw
        )
        {
            return new PollEvent(
                PollEventKind.ProtocolInfo,
                default,
                default,
                default,
                default,
                default,
                default,
                0,
                null,
                default,
                payload,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                raw
            );
        }

        /// <summary>Creates the lobby-state event with its payload.</summary>
        internal static PollEvent FromLobby(
            in LobbyStateChangedMessage payload,
            ReadOnlyMemory<byte> raw
        )
        {
            return new PollEvent(
                PollEventKind.LobbyStateChanged,
                default,
                default,
                default,
                default,
                default,
                default,
                0,
                null,
                default,
                default,
                payload,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                raw
            );
        }

        /// <summary>Creates the player-joined event with its payload.</summary>
        internal static PollEvent FromPlayerJoined(
            in PlayerJoinedMessage payload,
            ReadOnlyMemory<byte> raw
        )
        {
            return new PollEvent(
                PollEventKind.PlayerJoined,
                default,
                default,
                default,
                default,
                default,
                default,
                0,
                null,
                default,
                default,
                default,
                payload,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                raw
            );
        }

        /// <summary>Creates the player-left event.</summary>
        internal static PollEvent FromPlayerLeft(Guid playerId, ReadOnlyMemory<byte> raw)
        {
            return new PollEvent(
                PollEventKind.PlayerLeft,
                default,
                default,
                default,
                default,
                default,
                default,
                0,
                null,
                default,
                default,
                default,
                default,
                playerId,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                raw
            );
        }

        /// <summary>Creates the player-reconnected event.</summary>
        internal static PollEvent FromPlayerReconnected(Guid playerId, ReadOnlyMemory<byte> raw)
        {
            return new PollEvent(
                PollEventKind.PlayerReconnected,
                default,
                default,
                default,
                default,
                default,
                default,
                0,
                null,
                default,
                default,
                default,
                default,
                playerId,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                raw
            );
        }

        /// <summary>Creates the game-starting event with its payload.</summary>
        internal static PollEvent FromGameStart(
            in GameStartingMessage payload,
            ReadOnlyMemory<byte> raw
        )
        {
            return new PollEvent(
                PollEventKind.GameStarting,
                default,
                default,
                default,
                default,
                default,
                default,
                0,
                null,
                default,
                default,
                default,
                default,
                default,
                payload,
                default,
                default,
                default,
                default,
                default,
                default,
                raw
            );
        }

        /// <summary>Creates the authority-response event with its payload.</summary>
        internal static PollEvent FromAuthorityResponse(
            in AuthorityResponseMessage payload,
            ReadOnlyMemory<byte> raw
        )
        {
            return new PollEvent(
                PollEventKind.AuthorityResponse,
                default,
                default,
                default,
                default,
                default,
                default,
                0,
                null,
                default,
                default,
                default,
                default,
                default,
                default,
                payload,
                default,
                default,
                default,
                default,
                default,
                raw
            );
        }

        /// <summary>Creates the authority-changed event with its payload.</summary>
        internal static PollEvent FromAuthorityChanged(
            in AuthorityChangedMessage payload,
            ReadOnlyMemory<byte> raw
        )
        {
            return new PollEvent(
                PollEventKind.AuthorityChanged,
                default,
                default,
                default,
                default,
                default,
                default,
                0,
                null,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                payload,
                default,
                default,
                default,
                default,
                raw
            );
        }

        /// <summary>Creates the game-data event with its payload.</summary>
        internal static PollEvent FromGameData(
            in IncomingGameData payload,
            ReadOnlyMemory<byte> raw
        )
        {
            return new PollEvent(
                PollEventKind.GameData,
                default,
                default,
                default,
                default,
                default,
                default,
                0,
                null,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                payload,
                default,
                default,
                default,
                raw
            );
        }

        /// <summary>Creates the spectator-left event with its payload.</summary>
        internal static PollEvent FromSpectatorLeft(
            in SpectatorLeftMessage payload,
            ReadOnlyMemory<byte> raw
        )
        {
            return new PollEvent(
                PollEventKind.SpectatorLeft,
                default,
                default,
                default,
                default,
                default,
                default,
                0,
                null,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                payload,
                default,
                default,
                raw
            );
        }

        /// <summary>Creates the new-spectator event with its payload.</summary>
        internal static PollEvent FromNewSpectator(
            in NewSpectatorJoinedMessage payload,
            ReadOnlyMemory<byte> raw
        )
        {
            return new PollEvent(
                PollEventKind.NewSpectatorJoined,
                default,
                default,
                default,
                default,
                default,
                default,
                0,
                null,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                payload,
                default,
                raw
            );
        }

        /// <summary>Creates the spectator-disconnected event with its payload.</summary>
        internal static PollEvent FromSpectatorDisconnected(
            in SpectatorDisconnectedMessage payload,
            ReadOnlyMemory<byte> raw
        )
        {
            return new PollEvent(
                PollEventKind.SpectatorDisconnected,
                default,
                default,
                default,
                default,
                default,
                default,
                0,
                null,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                default,
                payload,
                raw
            );
        }
    }
}
