namespace SignalFish.Client.Core
{
    /// <summary>
    /// Why a command was refused locally. Precedence matches the Rust
    /// client: <see cref="NotConnected"/> → <see cref="NotAuthenticated"/> →
    /// <see cref="RoomOperationPending"/> → membership/role.
    /// <see cref="None"/> means admitted.
    /// </summary>
    public enum AdmissionError
    {
        /// <summary>Admitted; the caller may send.</summary>
        None = 0,

        /// <summary>No live session (never connected, or terminal).</summary>
        NotConnected = 1,

        /// <summary>Directed room operations require a confirmed authentication first.</summary>
        NotAuthenticated = 2,

        /// <summary>A directed room operation is awaiting its typed result.</summary>
        RoomOperationPending = 3,

        /// <summary>Join-style command refused: already a confirmed room member.</summary>
        AlreadyInRoom = 4,

        /// <summary>Room-scoped command refused: no confirmed membership.</summary>
        NotInRoom = 5,

        /// <summary>Command requires a different room role (player vs spectator).</summary>
        WrongRoomRole = 6,
    }
}
