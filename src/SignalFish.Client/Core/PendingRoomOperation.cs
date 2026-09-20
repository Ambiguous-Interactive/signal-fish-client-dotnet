namespace SignalFish.Client.Core
{
    using System;

    /// <summary>
    /// A directed room operation with a typed terminal answer on the wire.
    /// While one is pending, the machine is fenced: every command except
    /// <see cref="ClientCommand.Ping"/> is refused until the typed result
    /// (success or typed failure) or session teardown releases it.
    /// </summary>
    public enum PendingRoomOperation
    {
        /// <summary>Sentinel for <c>default(PendingRoomOperation)</c>; no operation in flight.</summary>
        [Obsolete(
            "This value only exists so the enum default (0) is not a valid operation. Compare against default(PendingRoomOperation) instead."
        )]
        None = 0,

        /// <summary>Awaiting RoomJoined / RoomJoinFailed.</summary>
        JoinPlayer = 1,

        /// <summary>Awaiting RoomLeft.</summary>
        LeavePlayer = 2,

        /// <summary>Awaiting Reconnected / ReconnectionFailed.</summary>
        ReconnectPlayer = 3,

        /// <summary>Awaiting SpectatorJoined / SpectatorJoinFailed.</summary>
        JoinSpectator = 4,

        /// <summary>Awaiting SpectatorLeft.</summary>
        LeaveSpectator = 5,
    }
}
