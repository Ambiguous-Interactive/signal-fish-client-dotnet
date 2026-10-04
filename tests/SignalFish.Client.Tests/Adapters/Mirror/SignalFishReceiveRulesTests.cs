namespace SignalFish.Client.Tests.Adapters.Mirror
{
    using System;
    using NUnit.Framework;
    using SignalFish.Client.Adapters.Mirror;

    /// <summary>
    /// The full routing decision matrix over data: who consumes which
    /// relayed frame on a broadcast-only relay. Every drop names its
    /// reason, and the authority's server feed and a client's filtered
    /// feed are both exercised against the same rule set.
    /// </summary>
    [TestFixture]
    public class SignalFishReceiveRulesTests
    {
        private static readonly Guid LocalPlayer = new Guid("00000000-0000-0000-0000-00000000000a");

        private static readonly Guid AuthorityPlayer = new Guid(
            "00000000-0000-0000-0000-00000000000b"
        );

        private static readonly Guid OtherPlayer = new Guid("00000000-0000-0000-0000-00000000000c");

        private static readonly TestCaseData[] Routes =
        {
            // A machine never consumes its own broadcast; Mirror's local connection delivered it.
            new TestCaseData(
                true,
                AuthorityPlayer,
                AuthorityPlayer,
                OtherPlayer,
                MirrorAdapterWire.BroadcastTarget,
                MirrorFrameRoute.ConsumeAsServer
            ).SetName("AuthorityConsumesPeerBroadcast"),
            new TestCaseData(
                true,
                AuthorityPlayer,
                AuthorityPlayer,
                OtherPlayer,
                LocalPlayer,
                MirrorFrameRoute.ConsumeAsServer
            ).SetName("AuthorityConsumesPeerTargetedFrame"),
            new TestCaseData(
                true,
                AuthorityPlayer,
                AuthorityPlayer,
                AuthorityPlayer,
                MirrorAdapterWire.BroadcastTarget,
                MirrorFrameRoute.DropSelfOrigin
            ).SetName("AuthorityDropsOwnEcho"),
            new TestCaseData(
                false,
                LocalPlayer,
                AuthorityPlayer,
                AuthorityPlayer,
                MirrorAdapterWire.BroadcastTarget,
                MirrorFrameRoute.ConsumeAsClient
            ).SetName("ClientConsumesAuthorityBroadcast"),
            new TestCaseData(
                false,
                LocalPlayer,
                AuthorityPlayer,
                AuthorityPlayer,
                LocalPlayer,
                MirrorFrameRoute.ConsumeAsClient
            ).SetName("ClientConsumesAuthorityFrameAddressedToIt"),
            new TestCaseData(
                false,
                LocalPlayer,
                AuthorityPlayer,
                AuthorityPlayer,
                OtherPlayer,
                MirrorFrameRoute.DropWrongTarget
            ).SetName("ClientDropsAuthorityFrameAddressedElsewhere"),
            new TestCaseData(
                false,
                LocalPlayer,
                AuthorityPlayer,
                OtherPlayer,
                MirrorAdapterWire.BroadcastTarget,
                MirrorFrameRoute.DropForeignSender
            ).SetName("ClientDropsNonAuthorityBroadcast"),
            new TestCaseData(
                false,
                LocalPlayer,
                AuthorityPlayer,
                OtherPlayer,
                LocalPlayer,
                MirrorFrameRoute.DropForeignSender
            ).SetName("ClientDropsNonAuthorityFrameEvenWhenAddressedToIt"),
            new TestCaseData(
                false,
                LocalPlayer,
                AuthorityPlayer,
                LocalPlayer,
                MirrorAdapterWire.BroadcastTarget,
                MirrorFrameRoute.DropSelfOrigin
            ).SetName("ClientDropsOwnEcho"),
        };

        [TestCaseSource(nameof(Routes))]
        public void FrameRoutesByRoleSenderAndTarget(
            bool localIsAuthority,
            Guid localPlayerId,
            Guid authorityPlayerId,
            Guid senderId,
            Guid targetId,
            MirrorFrameRoute expected
        )
        {
            MirrorFrameRoute route = SignalFishReceiveRules.Route(
                localIsAuthority,
                localPlayerId,
                authorityPlayerId,
                senderId,
                targetId
            );

            Assert.That(route, Is.EqualTo(expected));
        }
    }
}
