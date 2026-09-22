namespace SignalFish.Client.Reconnection
{
    using System;

    /// <summary>
    /// The recovery action the Rust client's reconnection policy prescribes
    /// for a <c>ReconnectionFailed</c> error code.
    /// </summary>
    public enum ReconnectAction
    {
        /// <summary>Sentinel for <c>default(ReconnectAction)</c>; not an action.</summary>
        [Obsolete(
            "This value only exists so the enum default (0) is not a valid action. Classify an error code instead of using the default."
        )]
        None = 0,

        /// <summary>
        /// The server no longer knows this seat (window expired or token
        /// invalid): give up the token and join again as a new player.
        /// </summary>
        FreshJoin = 1,

        /// <summary>
        /// Another live connection still holds the seat: wait for it to
        /// exit (or drop) before retrying.
        /// </summary>
        WaitForSeat = 2,

        /// <summary>
        /// Anything else (transient refusal, drain, ban, unknown future
        /// code): retry with backoff while the room is worth rejoining.
        /// </summary>
        RetryWithBackoff = 3,
    }

    /// <summary>
    /// The Rust client's end-to-end recovery decision tree as data: given
    /// the <c>error_code</c> carried by a <c>ReconnectionFailed</c> event,
    /// classifies what the application should do next. Applications that
    /// want different behavior for a specific code (a ban, for example)
    /// special-case it before consulting this tree.
    /// </summary>
    public static class ReconnectRecovery
    {
        /// <summary>
        /// Classifies a <c>ReconnectionFailed</c> error code; null or
        /// unknown codes fall back to <see cref="ReconnectAction.RetryWithBackoff"/>
        /// (additive server codes must not push clients off the wire).
        /// </summary>
        public static ReconnectAction Classify(string? errorCode)
        {
            switch (errorCode)
            {
                case "RECONNECTION_EXPIRED":
                case "RECONNECTION_TOKEN_INVALID":
                    return ReconnectAction.FreshJoin;
                case "PLAYER_ALREADY_CONNECTED":
                    return ReconnectAction.WaitForSeat;
                default:
                    return ReconnectAction.RetryWithBackoff;
            }
        }
    }
}
