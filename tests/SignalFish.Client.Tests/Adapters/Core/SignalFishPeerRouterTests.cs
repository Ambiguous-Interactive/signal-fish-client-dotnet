namespace SignalFish.Client.Tests.Adapters.Core
{
    using System;
    using NUnit.Framework;
    using SignalFish.Client.Adapters;

    /// <summary>
    /// Contract coverage for the peer router: monotonic never-reused
    /// connection ids starting after the host's reserved zero, both lookup
    /// directions, the once-per-peer-lifetime add, removal by either key,
    /// and the clear-that-keeps-the-counter behavior.
    /// </summary>
    [TestFixture]
    public class SignalFishPeerRouterTests
    {
        private static readonly Guid PlayerA = new Guid("00000000-0000-0000-0000-000000000001");

        private static readonly Guid PlayerB = new Guid("00000000-0000-0000-0000-000000000002");

        [Test]
        public void AddsAssignMonotonicIdsAfterTheHostReservation()
        {
            SignalFishPeerRouter router = new SignalFishPeerRouter();

            Assert.That(router.TryAddPeer(PlayerA, out int connectionA), Is.True);
            Assert.That(router.TryAddPeer(PlayerB, out int connectionB), Is.True);

            Assert.That(SignalFishPeerRouter.HostConnectionId, Is.EqualTo(0));
            Assert.That(connectionA, Is.EqualTo(1));
            Assert.That(connectionB, Is.EqualTo(2));
            Assert.That(router.PeerCount, Is.EqualTo(2));
        }

        [Test]
        public void LookupsResolveInBothDirections()
        {
            SignalFishPeerRouter router = new SignalFishPeerRouter();
            Assert.That(router.TryAddPeer(PlayerA, out int connectionA), Is.True);

            Assert.That(router.TryGetConnection(PlayerA, out int byPeer), Is.True);
            Assert.That(byPeer, Is.EqualTo(connectionA));
            Assert.That(router.TryGetPeer(connectionA, out Guid byConnection), Is.True);
            Assert.That(byConnection, Is.EqualTo(PlayerA));
        }

        [Test]
        public void DuplicateAddKeepsTheOriginalRoute()
        {
            SignalFishPeerRouter router = new SignalFishPeerRouter();
            Assert.That(router.TryAddPeer(PlayerA, out int first), Is.True);

            Assert.That(router.TryAddPeer(PlayerA, out int second), Is.False);

            Assert.That(router.TryGetConnection(PlayerA, out int resolved), Is.True);
            Assert.That(resolved, Is.EqualTo(first));
            Assert.That(second, Is.EqualTo(0));
        }

        [Test]
        public void EmptyPeerIdIsRefused()
        {
            SignalFishPeerRouter router = new SignalFishPeerRouter();

            Assert.That(router.TryAddPeer(Guid.Empty, out _), Is.False);
            Assert.That(router.PeerCount, Is.EqualTo(0));
        }

        [Test]
        public void RemovedPeerRejoiningGetsANewConnectionId()
        {
            SignalFishPeerRouter router = new SignalFishPeerRouter();
            Assert.That(router.TryAddPeer(PlayerA, out int original), Is.True);
            Assert.That(router.TryAddPeer(PlayerB, out _), Is.True);

            Assert.That(router.TryRemovePeer(PlayerA, out int removed), Is.True);

            Assert.That(removed, Is.EqualTo(original));
            Assert.That(router.TryGetConnection(PlayerA, out _), Is.False);
            Assert.That(router.TryGetPeer(original, out _), Is.False);

            Assert.That(router.TryAddPeer(PlayerA, out int rejoined), Is.True);
            Assert.That(rejoined, Is.EqualTo(original + 2), "ids are never reused");
        }

        [Test]
        public void ClearDropsRoutesButKeepsTheCounterAdvancing()
        {
            SignalFishPeerRouter router = new SignalFishPeerRouter();
            Assert.That(router.TryAddPeer(PlayerA, out _), Is.True);

            router.Clear();

            Assert.That(router.PeerCount, Is.EqualTo(0));
            Assert.That(router.TryAddPeer(PlayerB, out int after), Is.True);
            Assert.That(after, Is.EqualTo(2));
        }
    }
}
