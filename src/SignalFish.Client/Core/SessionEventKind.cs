namespace SignalFish.Client.Core
{
    using System;

    /// <summary>
    /// Driver-level session facts the state machine reacts to — the decoded
    /// subset of server envelopes (and transport liveness) that can change
    /// phase, membership, or the membership fence. Unknown server messages
    /// never produce a session event.
    /// </summary>
    public enum SessionEventKind
    {
        /// <summary>Sentinel for <c>default(SessionEventKind)</c>; not a session fact.</summary>
        [Obsolete(
            "This value only exists so the enum default (0) is not a session fact. Apply ignores it."
        )]
        None = 0,

        /// <summary>The transport handshake completed; the wire is open.</summary>
        TransportReady = 1,

        /// <summary>Server confirmed authentication. The v2 wire carries no
        /// player id here — the identity is confirmed with the membership at
        /// join/reconnect time.</summary>
        Authenticated = 2,

        /// <summary>Confirmed player membership (carries the membership).</summary>
        RoomJoined = 3,

        /// <summary>Confirmed spectator membership (carries the membership).</summary>
        SpectatorJoined = 4,

        /// <summary>Confirmed player exit; connection stays open.</summary>
        RoomLeft = 5,

        /// <summary>Confirmed spectator exit; connection stays open.</summary>
        SpectatorLeft = 6,

        /// <summary>Typed player-join failure; releases only a pending JoinPlayer.</summary>
        RoomJoinFailed = 7,

        /// <summary>Typed spectator-join failure; releases only a pending JoinSpectator.</summary>
        SpectatorJoinFailed = 8,

        /// <summary>Typed reconnect failure; releases only a pending ReconnectPlayer.</summary>
        ReconnectionFailed = 9,

        /// <summary>Membership reclaimed after reconnection (carries the membership).</summary>
        Reconnected = 10,

        /// <summary>Generic server error envelope. Informational only: it never
        /// releases the fence (fail-closed) and never changes phase.</summary>
        ServerError = 11,

        /// <summary>Transport closed or failed; the session is terminal.</summary>
        Disconnected = 12,

        /// <summary>Authority moved (carries whether this connection holds it).</summary>
        AuthorityChanged = 13,

        /// <summary>
        /// Server negotiation echo (carries the negotiated protocol
        /// version; null on a v2 negotiation). Per-connection: cleared at
        /// teardown and re-echoed on every new connection.
        /// </summary>
        ProtocolInfo = 14,
    }
}
