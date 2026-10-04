#nullable enable
namespace SignalFish.Client.Adapters
{
    using System;

    /// <summary>
    /// What one inbound Signal Fish game-data frame means to this
    /// machine's engine side: consume it into the local server feed, the
    /// local client feed, or drop it for a named reason. The relay is a
    /// room broadcast, so every peer sees every frame; the engine's star
    /// topology — clients talk only to the server — is realized by these
    /// rules: the authority consumes its peers' upstream frames, and
    /// everyone else consumes only the authority's downstream frames
    /// addressed to them (or to everyone).
    /// </summary>
    public enum AdapterFrameRoute : byte
    {
        /// <summary>Sentinel for <c>default(AdapterFrameRoute)</c>; not a route.</summary>
        [Obsolete(
            "This value only exists so the enum default (0) is not a route. Route decisions are never default."
        )]
        None = 0,

        /// <summary>Feed the local engine server (this machine is the authority).</summary>
        ConsumeAsServer = 1,

        /// <summary>Feed the local engine client.</summary>
        ConsumeAsClient = 2,

        /// <summary>The sender is this machine; the engine's local path already delivered it.</summary>
        DropSelfOrigin = 3,

        /// <summary>The sender is not the authority and this machine is not the authority.</summary>
        DropForeignSender = 4,

        /// <summary>The frame is addressed to another peer.</summary>
        DropWrongTarget = 5,
    }

    /// <summary>
    /// The pure routing decision for one inbound adapter frame. The rules,
    /// in order: a machine never consumes its own broadcast (the engine's
    /// local path — a host-mode loopback or local connection — delivers
    /// local sends without a relay round trip, and the relay floor does
    /// not echo a sender's own broadcast back — pinned by the conformance
    /// suite; the rule is defense in depth); the authority consumes every
    /// other player's frame on its server feed; a non-authority consumes
    /// only the authority's frames, and only when they are broadcast or
    /// addressed to it.
    /// </summary>
    public static class SignalFishReceiveRules
    {
        /// <summary>
        /// Routes one frame. <paramref name="authorityPlayerId"/> is the
        /// confirmed room authority (from the session snapshot), which may
        /// equal <paramref name="localPlayerId"/> when this machine is the
        /// authority.
        /// </summary>
        public static AdapterFrameRoute Route(
            bool localIsAuthority,
            Guid localPlayerId,
            Guid authorityPlayerId,
            Guid senderId,
            Guid targetId
        )
        {
            if (senderId == localPlayerId)
            {
                return AdapterFrameRoute.DropSelfOrigin;
            }

            if (localIsAuthority)
            {
                return AdapterFrameRoute.ConsumeAsServer;
            }

            if (senderId != authorityPlayerId)
            {
                return AdapterFrameRoute.DropForeignSender;
            }

            if (targetId != AdapterWire.BroadcastTarget && targetId != localPlayerId)
            {
                return AdapterFrameRoute.DropWrongTarget;
            }

            return AdapterFrameRoute.ConsumeAsClient;
        }
    }
}
