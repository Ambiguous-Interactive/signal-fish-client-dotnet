namespace SignalFish.Client.Transport
{
    /// <summary>
    /// The server-defined meaning of a WebSocket close code, mapped from the
    /// canonical protocol close-code table (4000-4007, 1009). Error codes are
    /// data: reaction logic keys on this enum, never on prose.
    /// </summary>
    public enum TransportCloseKind
    {
        /// <summary>No close observed yet (default value).</summary>
        None = 0,

        /// <summary>1000: normal closure.</summary>
        Normal = 1000,

        /// <summary>1006: locally observed abnormal closure (never sent on the wire).</summary>
        Abnormal = 1006,

        /// <summary>1009: outbound_message_too_large.</summary>
        MessageTooBig = 1009,

        /// <summary>4000: server_shutdown — surface it; retry later.</summary>
        ServerShutdown = 4000,

        /// <summary>4001: auth_timeout — reconnect and authenticate promptly.</summary>
        AuthTimeout = 4001,

        /// <summary>4002: slow_consumer — back off and shrink the send rate.</summary>
        SlowConsumer = 4002,

        /// <summary>4003: activity_timeout — heartbeat was missed.</summary>
        ActivityTimeout = 4003,

        /// <summary>4004: idle_timeout — same reaction as <see cref="ActivityTimeout"/>.</summary>
        IdleTimeout = 4004,

        /// <summary>4005: room_inactive — the room is gone; rejoin from scratch.</summary>
        RoomInactive = 4005,

        /// <summary>4006: inbound_rate_limited — honor Authenticated rate-limit budgets.</summary>
        InboundRateLimited = 4006,

        /// <summary>4007: kicked — never auto-rejoin; surface it to the user.</summary>
        Kicked = 4007,

        /// <summary>Any code outside the server-defined table.</summary>
        Unknown = 1,
    }
}
