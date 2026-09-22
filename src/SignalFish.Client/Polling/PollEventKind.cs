namespace SignalFish.Client.Polling
{
    using System;

    /// <summary>
    /// The classification of one polled frame: session facts, gameplay
    /// payloads, and forward-compatibility events. One inbound frame yields
    /// at most one <see cref="PollEvent"/>; frames with no v2 event surface
    /// (heartbeat replies, client-to-server echoes, v3-only kinds) are
    /// absorbed silently and still refresh liveness.
    /// </summary>
    public enum PollEventKind : byte
    {
        /// <summary>Sentinel for <c>default(PollEventKind)</c>; not an event.</summary>
        [Obsolete(
            "This value only exists so the enum default (0) is not an event. Compare against default(PollEventKind) instead."
        )]
        None = 0,

        /// <summary>The transport handshake completed; the wire is open.</summary>
        TransportReady = 1,

        /// <summary>Server confirmed authentication (carries the rate-limit payload).</summary>
        Authenticated = 2,

        /// <summary>Confirmed player membership (carries membership + room snapshot).</summary>
        RoomJoined = 3,

        /// <summary>Confirmed spectator membership (carries membership + room snapshot).</summary>
        SpectatorJoined = 4,

        /// <summary>Membership reclaimed after reconnection (carries membership + room snapshot).</summary>
        Reconnected = 5,

        /// <summary>Confirmed player exit; connection stays open.</summary>
        RoomLeft = 6,

        /// <summary>Confirmed spectator exit (carries the spectator payload).</summary>
        SpectatorLeft = 7,

        /// <summary>Typed player-join failure (carries reason + error code).</summary>
        RoomJoinFailed = 8,

        /// <summary>Typed spectator-join failure (carries reason + error code).</summary>
        SpectatorJoinFailed = 9,

        /// <summary>Typed reconnect failure (carries reason + error code).</summary>
        ReconnectionFailed = 10,

        /// <summary>Generic server error envelope (carries reason + error code).</summary>
        ServerError = 11,

        /// <summary>
        /// A routed frame could not be honored: a session-fact frame with
        /// malformed session-critical fields, or a known message whose
        /// payload violated the wire contract. Carries the offending
        /// <see cref="MessageKind"/>. The session state machine stays
        /// fail-closed; nothing is applied for this frame.
        /// </summary>
        ProtocolViolation = 12,

        /// <summary>The transport closed or died; the session is terminal.</summary>
        Disconnected = 13,

        /// <summary>Server capabilities and limits.</summary>
        ProtocolInfo = 14,

        /// <summary>Lobby/readiness broadcast (state, ready players, all-ready flag).</summary>
        LobbyStateChanged = 15,

        /// <summary>A player joined the room (carries the player info).</summary>
        PlayerJoined = 16,

        /// <summary>A player left the room (carries the player id).</summary>
        PlayerLeft = 17,

        /// <summary>A player reconnected (carries the player id).</summary>
        PlayerReconnected = 18,

        /// <summary>The authority started the game (carries peer connections).</summary>
        GameStarting = 19,

        /// <summary>Authority grant/deny answer (carries granted + reason).</summary>
        AuthorityResponse = 20,

        /// <summary>Authority moved (carries the new authority + self flag).</summary>
        AuthorityChanged = 21,

        /// <summary>Relayed game data (carries sender id + verbatim JSON payload).</summary>
        GameData = 22,

        /// <summary>A spectator joined the room (carries spectator + roster).</summary>
        NewSpectatorJoined = 23,

        /// <summary>A spectator dropped without leaving (carries id + reason + roster).</summary>
        SpectatorDisconnected = 24,

        /// <summary>An unrecognized (forward-compatible) type discriminator.</summary>
        UnknownMessage = 25,

        /// <summary>The frame was malformed; carries the bounded decode error.</summary>
        DecodeFailed = 26,

        /// <summary>
        /// The opt-in reconnect policy scheduled an attempt (carries the
        /// attempt number and the deterministic wait). Produced only by the
        /// async client; the polling client is caller-driven and never
        /// emits it.
        /// </summary>
        Reconnecting = 27,

        /// <summary>
        /// The reconnect attempt budget ran out (carries attempts spent and
        /// the last close reason); the session ends after it. Produced only
        /// by the async client.
        /// </summary>
        ReconnectAbandoned = 28,
    }
}
