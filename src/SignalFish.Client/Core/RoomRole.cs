namespace SignalFish.Client.Core
{
    /// <summary>
    /// Membership role inside a room. Absence is expressed as
    /// <c>default(RoomRole?)</c> or an absent <see cref="RoomMembership"/>,
    /// never as a sentinel value.
    /// </summary>
    public enum RoomRole
    {
        /// <summary>Absent membership (the default); never a confirmed role.</summary>
        None = 0,

        /// <summary>Full participant: may relay game data, ready up, and start the game.</summary>
        Player = 1,

        /// <summary>Observer: may watch and leave; cannot relay or ready up.</summary>
        Spectator = 2,
    }
}
