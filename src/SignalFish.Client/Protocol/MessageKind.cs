namespace SignalFish.Client.Protocol
{
    using System;

    /// <summary>
    /// Known protocol message types, routed by the wire <c>type</c>
    /// discriminator (PascalCase). Covers the mandatory v2 relay floor plus
    /// the v3 additions. Unknown wire types never map to a kind here — they
    /// surface as <see cref="EnvelopeEventKind.UnknownMessage"/> events so the
    /// additive protocol stays forward-compatible.
    /// </summary>
    public enum MessageKind : byte
    {
        /// <summary>Sentinel for <c>default(MessageKind)</c>; produced by failed decodes.</summary>
        [Obsolete(
            "This value only exists so the enum default (0) is not a routed message. Compare against default(MessageKind) instead."
        )]
        None = 0,

        /// <summary><c>Authenticate</c> (C→S): optional credentials + v3 capabilities.</summary>
        Authenticate,

        /// <summary><c>Authenticated</c> (S→C): accepted session, rate limit budgets.</summary>
        Authenticated,

        /// <summary><c>AuthorityRequest</c> (C→S): ask to become/keep room authority.</summary>
        AuthorityRequest,

        /// <summary><c>AuthorityResponse</c> (S→C): authority grant/deny result.</summary>
        AuthorityResponse,

        /// <summary><c>DeliveryReport</c> (S→C, v3): per-class delivery accounting + gaps.</summary>
        DeliveryReport,

        /// <summary><c>Error</c> (S→C): protocol-level failure with an <c>error_code</c>.</summary>
        Error,

        /// <summary><c>GameData</c> (both): the relay payload.</summary>
        GameData,

        /// <summary><c>GameStarting</c> (S→C): the authority started the game.</summary>
        GameStarting,

        /// <summary><c>JoinAsSpectator</c> (C→S): join a room as a spectator.</summary>
        JoinAsSpectator,

        /// <summary><c>JoinRoom</c> (C→S): join a room by code or create one.</summary>
        JoinRoom,

        /// <summary><c>LeaveRoom</c> (C→S): leave the current room.</summary>
        LeaveRoom,

        /// <summary><c>LeaveSpectator</c> (C→S): stop spectating.</summary>
        LeaveSpectator,

        /// <summary><c>LobbyStateChanged</c> (S→C): lobby/readiness state broadcast.</summary>
        LobbyStateChanged,

        /// <summary><c>NewPeer</c> (S→C, v3): mesh peer discovery with initiation flag.</summary>
        NewPeer,

        /// <summary><c>Ping</c> (both): application-level heartbeat request.</summary>
        Ping,

        /// <summary><c>Pong</c> (both): application-level heartbeat reply.</summary>
        Pong,

        /// <summary><c>PlayerJoined</c> (S→C): a player joined the room.</summary>
        PlayerJoined,

        /// <summary><c>PlayerLeft</c> (S→C): a player left the room.</summary>
        PlayerLeft,

        /// <summary><c>PlayerReady</c> (C→S): readiness signal (never auto-starts).</summary>
        PlayerReady,

        /// <summary><c>ProtocolInfo</c> (S→C): server capabilities and limits.</summary>
        ProtocolInfo,

        /// <summary><c>ProvideConnectionInfo</c> (C→S): publish engine connection info.</summary>
        ProvideConnectionInfo,

        /// <summary><c>Reconnect</c> (C→S): resume a room with the server-issued token.</summary>
        Reconnect,

        /// <summary><c>Reconnected</c> (S→C): reconnection accepted, control-event replay.</summary>
        Reconnected,

        /// <summary><c>RoomJoined</c> (S→C): room membership established.</summary>
        RoomJoined,

        /// <summary><c>RoomLeft</c> (S→C): room membership ended.</summary>
        RoomLeft,

        /// <summary><c>RoomOperation</c> (C→S, v3): room admin operation with an ID.</summary>
        RoomOperation,

        /// <summary><c>RoomOperationResult</c> (S→C, v3): result for a room operation ID.</summary>
        RoomOperationResult,

        /// <summary><c>SessionPlan</c> (S→C, v3): authoritative mesh/host/relay plan.</summary>
        SessionPlan,

        /// <summary><c>Signal</c> (both, v3): WebRTC signaling payload relay.</summary>
        Signal,

        /// <summary><c>StartGame</c> (C→S): authority-gated game start request.</summary>
        StartGame,

        /// <summary><c>TransportStatus</c> (C→S, v3): local transport state report.</summary>
        TransportStatus,

        /// <summary><c>PeerTransportStatus</c> (S→C, v3): peer transport state report.</summary>
        PeerTransportStatus,

        /// <summary><c>GoingAway</c> (S→C, v3): server draining notice with a deadline.</summary>
        GoingAway,

        /// <summary><c>AuthenticationError</c> (S→C): required error + error_code.</summary>
        AuthenticationError,

        /// <summary><c>AuthorityChanged</c> (S→C): authority grant/vacate broadcast.</summary>
        AuthorityChanged,

        /// <summary><c>NewSpectatorJoined</c> (S→C): spectator fan-out announcement.</summary>
        NewSpectatorJoined,

        /// <summary><c>PlayerReconnected</c> (S→C): player liveness restored.</summary>
        PlayerReconnected,

        /// <summary><c>ReconnectionFailed</c> (S→C): typed reconnect refusal; error_code required.</summary>
        ReconnectionFailed,

        /// <summary><c>RoomJoinFailed</c> (S→C): typed player-join refusal.</summary>
        RoomJoinFailed,

        /// <summary><c>SpectatorDisconnected</c> (S→C): spectator fan-out removal.</summary>
        SpectatorDisconnected,

        /// <summary><c>SpectatorJoinFailed</c> (S→C): typed spectator-join refusal.</summary>
        SpectatorJoinFailed,

        /// <summary><c>SpectatorJoined</c> (S→C): spectator membership established.</summary>
        SpectatorJoined,

        /// <summary><c>SpectatorLeft</c> (S→C): spectator membership ended.</summary>
        SpectatorLeft,
    }
}
