#nullable enable
namespace SignalFish.Client.Adapters.Mirror
{
    using System;

    /// <summary>
    /// What one inbound Signal Fish game-data frame means to this
    /// machine's Mirror side: consume it into the local server feed, the
    /// local client feed, or drop it for a named reason. The relay is a
    /// room broadcast, so every peer sees every frame; Mirror's star
    /// topology — clients talk only to the server — is realized by these
    /// rules: the authority consumes its peers' upstream frames, and
    /// everyone else consumes only the authority's downstream frames
    /// addressed to them (or to everyone).
    /// </summary>
    public enum MirrorFrameRoute : byte
    {
        /// <summary>Sentinel for <c>default(MirrorFrameRoute)</c>; not a route.</summary>
        [Obsolete(
            "This value only exists so the enum default (0) is not a route. Route decisions are never default."
        )]
        None = 0,

        /// <summary>Feed the local Mirror server (this machine is the authority).</summary>
        ConsumeAsServer = 1,

        /// <summary>Feed the local Mirror client.</summary>
        ConsumeAsClient = 2,

        /// <summary>The sender is this machine; the local connection already delivered it.</summary>
        DropSelfOrigin = 3,

        /// <summary>The sender is not the authority and this machine is not the authority.</summary>
        DropForeignSender = 4,

        /// <summary>The frame is addressed to another peer.</summary>
        DropWrongTarget = 5,
    }

    /// <summary>
    /// The pure routing decision for one inbound adapter frame. The rules,
    /// in order: a machine never consumes its own broadcast (Mirror's local
    /// connection delivers the host's own client traffic in-process, and
    /// the relay floor does not echo a sender's own broadcast back — pinned
    /// by the conformance suite; the rule is defense in depth); the
    /// authority consumes every other player's frame on its server feed; a
    /// non-authority consumes only the authority's frames, and only when
    /// they are broadcast or addressed to it.
    /// </summary>
    public static class SignalFishReceiveRules
    {
        /// <summary>
        /// Routes one frame. <paramref name="authorityPlayerId"/> is the
        /// confirmed room authority (from the session snapshot), which may
        /// equal <paramref name="localPlayerId"/> when this machine is the
        /// authority.
        /// </summary>
        public static MirrorFrameRoute Route(
            bool localIsAuthority,
            Guid localPlayerId,
            Guid authorityPlayerId,
            Guid senderId,
            Guid targetId
        )
        {
            if (senderId == localPlayerId)
            {
                return MirrorFrameRoute.DropSelfOrigin;
            }

            if (localIsAuthority)
            {
                return MirrorFrameRoute.ConsumeAsServer;
            }

            if (senderId != authorityPlayerId)
            {
                return MirrorFrameRoute.DropForeignSender;
            }

            if (targetId != MirrorAdapterWire.BroadcastTarget && targetId != localPlayerId)
            {
                return MirrorFrameRoute.DropWrongTarget;
            }

            return MirrorFrameRoute.ConsumeAsClient;
        }
    }
}
