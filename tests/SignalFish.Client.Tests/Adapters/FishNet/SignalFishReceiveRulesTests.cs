namespace SignalFish.Client.Tests
{
    using System;
    using NUnit.Framework;
    using SignalFish.Client.Adapters.FishNet;

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
            // A machine never consumes its own broadcast; loopback delivered it.
            new TestCaseData(
                true,
                AuthorityPlayer,
                AuthorityPlayer,
                OtherPlayer,
                FishNetAdapterWire.BroadcastTarget,
                FishNetFrameRoute.ConsumeAsServer
            ).SetName("AuthorityConsumesPeerBroadcast"),
            new TestCaseData(
                true,
                AuthorityPlayer,
                AuthorityPlayer,
                OtherPlayer,
                LocalPlayer,
                FishNetFrameRoute.ConsumeAsServer
            ).SetName("AuthorityConsumesPeerTargetedFrame"),
            new TestCaseData(
                true,
                AuthorityPlayer,
                AuthorityPlayer,
                AuthorityPlayer,
                FishNetAdapterWire.BroadcastTarget,
                FishNetFrameRoute.DropSelfOrigin
            ).SetName("AuthorityDropsOwnEcho"),
            new TestCaseData(
                false,
                LocalPlayer,
                AuthorityPlayer,
                AuthorityPlayer,
                FishNetAdapterWire.BroadcastTarget,
                FishNetFrameRoute.ConsumeAsClient
            ).SetName("ClientConsumesAuthorityBroadcast"),
            new TestCaseData(
                false,
                LocalPlayer,
                AuthorityPlayer,
                AuthorityPlayer,
                LocalPlayer,
                FishNetFrameRoute.ConsumeAsClient
            ).SetName("ClientConsumesAuthorityFrameAddressedToIt"),
            new TestCaseData(
                false,
                LocalPlayer,
                AuthorityPlayer,
                AuthorityPlayer,
                OtherPlayer,
                FishNetFrameRoute.DropWrongTarget
            ).SetName("ClientDropsAuthorityFrameAddressedElsewhere"),
            new TestCaseData(
                false,
                LocalPlayer,
                AuthorityPlayer,
                OtherPlayer,
                FishNetAdapterWire.BroadcastTarget,
                FishNetFrameRoute.DropForeignSender
            ).SetName("ClientDropsNonAuthorityBroadcast"),
            new TestCaseData(
                false,
                LocalPlayer,
                AuthorityPlayer,
                OtherPlayer,
                LocalPlayer,
                FishNetFrameRoute.DropForeignSender
            ).SetName("ClientDropsNonAuthorityFrameEvenWhenAddressedToIt"),
            new TestCaseData(
                false,
                LocalPlayer,
                AuthorityPlayer,
                LocalPlayer,
                FishNetAdapterWire.BroadcastTarget,
                FishNetFrameRoute.DropSelfOrigin
            ).SetName("ClientDropsOwnEcho"),
        };

        [TestCaseSource(nameof(Routes))]
        public void FrameRoutesByRoleSenderAndTarget(
            bool localIsAuthority,
            Guid localPlayerId,
            Guid authorityPlayerId,
            Guid senderId,
            Guid targetId,
            FishNetFrameRoute expected
        )
        {
            FishNetFrameRoute route = SignalFishReceiveRules.Route(
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
