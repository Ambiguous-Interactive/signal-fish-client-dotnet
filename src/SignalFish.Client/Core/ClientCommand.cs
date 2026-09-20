namespace SignalFish.Client.Core
{
    /// <summary>
    /// Commands the client can issue. Admission is decided by
    /// <see cref="SignalFishStateMachine.Admit"/>; v3-only commands are added
    /// with the M6 negotiation work (the enum is additive).
    /// </summary>
    public enum ClientCommand
    {
        /// <summary>Application-level heartbeat. Admitted in any live phase.</summary>
        Ping = 0,

        /// <summary>Join (or create) a room as a player.</summary>
        JoinRoom = 1,

        /// <summary>Join a room as a spectator.</summary>
        JoinAsSpectator = 2,

        /// <summary>Leave the current room (player role).</summary>
        LeaveRoom = 3,

        /// <summary>Leave the current room (spectator role).</summary>
        LeaveSpectator = 4,

        /// <summary>Reclaim a prior membership on a fresh, re-authenticated connection.</summary>
        Reconnect = 5,

        /// <summary>Set this player's readiness flag.</summary>
        SetReady = 6,

        /// <summary>Authority-gated game start request (authority tracking lands in M5.2).</summary>
        StartGame = 7,

        /// <summary>Relay a game-data payload (player role).</summary>
        SendGameData = 8,
    }
}
