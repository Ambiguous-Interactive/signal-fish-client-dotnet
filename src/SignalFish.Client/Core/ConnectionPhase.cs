namespace SignalFish.Client.Core
{
    using System;

    /// <summary>
    /// The phase table mirrored from the Rust client: derived from
    /// connection flags and room membership, in strict order.
    /// </summary>
    public enum ConnectionPhase
    {
        /// <summary>
        /// Sentinel for <c>default(ConnectionPhase)</c>; not a valid phase.
        /// </summary>
        [Obsolete(
            "This value only exists so the enum default (0) is not a valid phase. Read the phase from the state machine instead."
        )]
        None = 0,

        /// <summary>Constructed, handshake not yet observed.</summary>
        Connecting = 1,

        /// <summary>Transport open; server handshake not yet confirmed.</summary>
        TransportReady = 2,

        /// <summary>Server confirmed authentication; outside any room.</summary>
        Authenticated = 3,

        /// <summary>Confirmed player or spectator membership.</summary>
        InRoom = 4,

        /// <summary>Session ended (transport closed). Absorbing: the machine stays here.</summary>
        Terminal = 5,
    }
}
