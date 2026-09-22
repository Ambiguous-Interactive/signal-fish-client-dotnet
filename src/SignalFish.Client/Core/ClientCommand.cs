namespace SignalFish.Client.Core
{
    using System;

    /// <summary>
    /// Commands the client can issue. Admission is decided by
    /// <see cref="SignalFishStateMachine.TryAdmit"/>; v3-only commands are
    /// added with the M6 negotiation work (the enum is additive).
    /// </summary>
    public enum ClientCommand
    {
        /// <summary>Sentinel for <c>default(ClientCommand)</c>; not a command.</summary>
        [Obsolete(
            "This value only exists so the enum default (0) is not a valid command. Pass an explicit command."
        )]
        None = 0,

        /// <summary>Application-level heartbeat. Admitted in any live phase.</summary>
        Ping = 1,

        /// <summary>Join (or create) a room as a player.</summary>
        JoinRoom = 2,

        /// <summary>Join a room as a spectator.</summary>
        JoinAsSpectator = 3,

        /// <summary>Leave the current room (player role).</summary>
        LeaveRoom = 4,

        /// <summary>Leave the current room (spectator role).</summary>
        LeaveSpectator = 5,

        /// <summary>Reclaim a prior membership on a fresh, re-authenticated connection.</summary>
        Reconnect = 6,

        /// <summary>Set this player's readiness flag.</summary>
        SetReady = 7,

        /// <summary>Authority-gated game start request (authority tracking lands in M5.2).</summary>
        StartGame = 8,

        /// <summary>Relay a game-data payload (player role).</summary>
        SendGameData = 9,

        /// <summary>Identify the app (and optionally negotiate v3) before any application message.</summary>
        Authenticate = 10,
    }
}
