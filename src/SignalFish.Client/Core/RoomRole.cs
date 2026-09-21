namespace SignalFish.Client.Core
{
    using System;

    /// <summary>
    /// Membership role inside a room. Absence is expressed as
    /// <c>default(RoomRole?)</c> or an absent <see cref="RoomMembership"/>,
    /// never as a sentinel value.
    /// </summary>
    public enum RoomRole
    {
        /// <summary>Sentinel for <c>default(RoomRole)</c>; not a confirmed role.</summary>
        [Obsolete(
            "This value only exists so the enum default (0) is not a valid role. Membership presence is expressed by RoomMembership.IsPresent."
        )]
        None = 0,

        /// <summary>Full participant: may relay game data, ready up, and start the game.</summary>
        Player = 1,

        /// <summary>Observer: may watch and leave; cannot relay or ready up.</summary>
        Spectator = 2,
    }
}
