namespace SignalFish.Client.Tests.Adapters.Core
{
    using System;
    using NUnit.Framework;
    using SignalFish.Client.Adapters;

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
                AdapterWire.BroadcastTarget,
                AdapterFrameRoute.ConsumeAsServer
            ).SetName("AuthorityConsumesPeerBroadcast"),
            new TestCaseData(
                true,
                AuthorityPlayer,
                AuthorityPlayer,
                OtherPlayer,
                LocalPlayer,
                AdapterFrameRoute.ConsumeAsServer
            ).SetName("AuthorityConsumesPeerTargetedFrame"),
            new TestCaseData(
                true,
                AuthorityPlayer,
                AuthorityPlayer,
                AuthorityPlayer,
                AdapterWire.BroadcastTarget,
                AdapterFrameRoute.DropSelfOrigin
            ).SetName("AuthorityDropsOwnEcho"),
            new TestCaseData(
                false,
                LocalPlayer,
                AuthorityPlayer,
                AuthorityPlayer,
                AdapterWire.BroadcastTarget,
                AdapterFrameRoute.ConsumeAsClient
            ).SetName("ClientConsumesAuthorityBroadcast"),
            new TestCaseData(
                false,
                LocalPlayer,
                AuthorityPlayer,
                AuthorityPlayer,
                LocalPlayer,
                AdapterFrameRoute.ConsumeAsClient
            ).SetName("ClientConsumesAuthorityFrameAddressedToIt"),
            new TestCaseData(
                false,
                LocalPlayer,
                AuthorityPlayer,
                AuthorityPlayer,
                OtherPlayer,
                AdapterFrameRoute.DropWrongTarget
            ).SetName("ClientDropsAuthorityFrameAddressedElsewhere"),
            new TestCaseData(
                false,
                LocalPlayer,
                AuthorityPlayer,
                OtherPlayer,
                AdapterWire.BroadcastTarget,
                AdapterFrameRoute.DropForeignSender
            ).SetName("ClientDropsNonAuthorityBroadcast"),
            new TestCaseData(
                false,
                LocalPlayer,
                AuthorityPlayer,
                OtherPlayer,
                LocalPlayer,
                AdapterFrameRoute.DropForeignSender
            ).SetName("ClientDropsNonAuthorityFrameEvenWhenAddressedToIt"),
            new TestCaseData(
                false,
                LocalPlayer,
                AuthorityPlayer,
                LocalPlayer,
                AdapterWire.BroadcastTarget,
                AdapterFrameRoute.DropSelfOrigin
            ).SetName("ClientDropsOwnEcho"),
        };

        [TestCaseSource(nameof(Routes))]
        public void FrameRoutesByRoleSenderAndTarget(
            bool localIsAuthority,
            Guid localPlayerId,
            Guid authorityPlayerId,
            Guid senderId,
            Guid targetId,
            AdapterFrameRoute expected
        )
        {
            AdapterFrameRoute route = SignalFishReceiveRules.Route(
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
