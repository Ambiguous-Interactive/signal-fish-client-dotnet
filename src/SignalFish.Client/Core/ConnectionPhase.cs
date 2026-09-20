namespace SignalFish.Client.Core
{
    /// <summary>
    /// The phase table mirrored from the Rust client: derived from
    /// connection flags and room membership, in strict order.
    /// </summary>
    public enum ConnectionPhase
    {
        /// <summary>Constructed, handshake not yet observed.</summary>
        Connecting = 0,

        /// <summary>Transport open; server handshake not yet confirmed.</summary>
        TransportReady = 1,

        /// <summary>Server confirmed authentication; outside any room.</summary>
        Authenticated = 2,

        /// <summary>Confirmed player or spectator membership.</summary>
        InRoom = 3,

        /// <summary>Session ended (transport closed). Absorbing: the machine stays here.</summary>
        Terminal = 4,
    }
}
