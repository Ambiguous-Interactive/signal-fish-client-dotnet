namespace SignalFish.Client.Core
{
    /// <summary>
    /// Driver-level session facts the state machine reacts to — the decoded
    /// subset of server envelopes (and transport liveness) that can change
    /// phase, membership, or the membership fence. Unknown server messages
    /// never produce a session event.
    /// </summary>
    public enum SessionEventKind
    {
        /// <summary>The transport handshake completed; the wire is open.</summary>
        TransportReady = 0,

        /// <summary>Server confirmed authentication (carries the assigned player id).</summary>
        Authenticated = 1,

        /// <summary>Confirmed player membership (carries the membership).</summary>
        RoomJoined = 2,

        /// <summary>Confirmed spectator membership (carries the membership).</summary>
        SpectatorJoined = 3,

        /// <summary>Confirmed player exit; connection stays open.</summary>
        RoomLeft = 4,

        /// <summary>Confirmed spectator exit; connection stays open.</summary>
        SpectatorLeft = 5,

        /// <summary>Typed join-player failure; releases only a pending JoinPlayer.</summary>
        JoinRoomFailed = 6,

        /// <summary>Typed join-spectator failure; releases only a pending JoinSpectator.</summary>
        JoinSpectatorFailed = 7,

        /// <summary>Typed reconnect failure; releases only a pending ReconnectPlayer.</summary>
        ReconnectFailed = 8,

        /// <summary>Membership reclaimed after reconnection (carries the membership).</summary>
        Reconnected = 9,

        /// <summary>Generic server error envelope. Informational only: it never
        /// releases the fence (fail-closed) and never changes phase.</summary>
        ServerError = 10,

        /// <summary>Transport closed or failed; the session is terminal.</summary>
        Disconnected = 11,
    }
}
