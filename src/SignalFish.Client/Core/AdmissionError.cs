namespace SignalFish.Client.Core
{
    using System;

    /// <summary>
    /// Why a command was refused locally. Precedence matches the Rust
    /// client: <see cref="NotConnected"/> → <see cref="NotAuthenticated"/> →
    /// <see cref="RoomOperationPending"/> → membership/role →
    /// <see cref="ProtocolUnsupported"/>.
    /// <see cref="None"/> means admitted.
    /// </summary>
    public enum AdmissionError
    {
        /// <summary>Sentinel for <c>default(AdmissionError)</c>; no error (the command is admitted).</summary>
        [Obsolete(
            "This value only exists so the enum default (0) is not an error. Use TryAdmit and branch on its bool instead of referencing this member."
        )]
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

        /// <summary>
        /// The bounded command queue is full: fail-fast sends queue nothing
        /// (the message is not silently dropped); a Reliable send variant
        /// waits for a slot instead.
        /// </summary>
        SendBufferFull = 7,

        /// <summary>
        /// Relinquishing authority (<c>SendAuthorityRequest(false)</c>)
        /// refused: this connection does not hold the authority.
        /// </summary>
        AuthorityRequired = 8,

        /// <summary>
        /// A v3-only send was refused: the connection has not negotiated
        /// v3 (either <c>ProtocolInfo</c> has not arrived yet, or it
        /// negotiated the v2 relay floor). Checked last — membership and
        /// role verdicts take precedence, Rust parity.
        /// </summary>
        ProtocolUnsupported = 9,

        /// <summary>
        /// A binary game-data send was refused: the connection negotiated
        /// the JSON game-data encoding (or none), so raw binary frames are
        /// not part of this session's contract. Checked after the v3
        /// verdict — the encoding rides a negotiated-v3 connection.
        /// </summary>
        BinaryFormatNotNegotiated = 10,

        /// <summary>
        /// A Signal send was refused: no SessionPlan has been observed on
        /// this connection, the selected transport is not webrtc, or the
        /// target is not a plan peer. The server would refuse or misroute
        /// the signal.
        /// </summary>
        SessionPlanUnavailable = 11,

        /// <summary>
        /// A Signal send was refused: its generation does not match the
        /// latest SessionPlan generation (a re-plan superseded it). Stamp
        /// signals from the current plan's generation.
        /// </summary>
        StaleSessionGeneration = 12,
    }
}
