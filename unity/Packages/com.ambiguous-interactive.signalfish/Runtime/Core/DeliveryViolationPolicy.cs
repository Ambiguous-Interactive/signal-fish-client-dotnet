#nullable enable
namespace SignalFish.Client.Core
{
    using System;

    /// <summary>
    /// Selects how the client responds to a decoded server frame that
    /// violates delivery accountability (the negotiated-v3 relay
    /// contract: sender stamps, gap ranges, and lifecycle watermarks).
    /// The violation is always surfaced as a protocol-violation event
    /// with its diagnostic; the policy decides what happens to the
    /// session.
    /// </summary>
    public enum DeliveryViolationPolicy
    {
        /// <summary>Sentinel for <c>default(DeliveryViolationPolicy)</c>; not a policy.</summary>
        [Obsolete(
            "This value only exists so the enum default (0) is not a policy. Compare against default(DeliveryViolationPolicy) instead."
        )]
        None = 0,

        /// <summary>
        /// Emit the violation and suppress subsequent room game data until
        /// the next authoritative rebaseline; the session stays usable.
        /// The suppressed state is exposed as a quarantined snapshot.
        /// </summary>
        Quarantine = 1,

        /// <summary>Emit the violation and tear the signaling connection down.</summary>
        Disconnect = 2,

        /// <summary>
        /// Emit the violation and continue; frames keep their documented
        /// delivery behavior (authoritative baselines that fail
        /// validation are still refused — a broken roster must never
        /// replace the client's cursors).
        /// </summary>
        Observe = 3,
    }
}
