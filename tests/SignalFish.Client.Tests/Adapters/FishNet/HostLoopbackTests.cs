namespace SignalFish.Client.Tests
{
    using System;
    using System.Text;
    using NUnit.Framework;
    using SignalFish.Client.Adapters;
    using SignalFish.Client.Adapters.FishNet;

    /// <summary>
    /// Contract coverage for the host-mode loopback: FIFO order per
    /// direction, direction independence, overflow dropping the newest
    /// frame with a visible counter, and the construction guards.
    /// </summary>
    [TestFixture]
    public class HostLoopbackTests
    {
        [Test]
        public void FramesDrainInSendOrderPerDirection()
        {
            HostLoopback loopback = new HostLoopback(capacityPerDirection: 4);
            Assert.That(
                loopback.TryEnqueueClientToServer(
                    AdapterWire.ReliableChannel,
                    Encoding.UTF8.GetBytes("first")
                ),
                Is.True
            );
            Assert.That(
                loopback.TryEnqueueClientToServer(
                    AdapterWire.UnreliableChannel,
                    Encoding.UTF8.GetBytes("second")
                ),
                Is.True
            );

            Assert.That(loopback.TryDequeueClientToServer(out HostLoopbackFrame first), Is.True);
            Assert.That(Encoding.UTF8.GetString(first.Segment.Span), Is.EqualTo("first"));
            Assert.That(first.Channel, Is.EqualTo(AdapterWire.ReliableChannel));

            Assert.That(loopback.TryDequeueClientToServer(out HostLoopbackFrame second), Is.True);
            Assert.That(Encoding.UTF8.GetString(second.Segment.Span), Is.EqualTo("second"));
            Assert.That(second.Channel, Is.EqualTo(AdapterWire.UnreliableChannel));
        }

        [Test]
        public void DirectionsAreIndependent()
        {
            HostLoopback loopback = new HostLoopback(capacityPerDirection: 2);
            Assert.That(
                loopback.TryEnqueueServerToClient(
                    AdapterWire.ReliableChannel,
                    Encoding.UTF8.GetBytes("down")
                ),
                Is.True
            );

            Assert.That(loopback.TryDequeueClientToServer(out _), Is.False);
            Assert.That(loopback.TryDequeueServerToClient(out HostLoopbackFrame down), Is.True);
            Assert.That(Encoding.UTF8.GetString(down.Segment.Span), Is.EqualTo("down"));
        }

        [Test]
        public void OverflowDropsTheNewestFrameAndCountsIt()
        {
            HostLoopback loopback = new HostLoopback(capacityPerDirection: 1);
            Assert.That(
                loopback.TryEnqueueClientToServer(
                    AdapterWire.ReliableChannel,
                    Encoding.UTF8.GetBytes("kept")
                ),
                Is.True
            );
            Assert.That(
                loopback.TryEnqueueClientToServer(
                    AdapterWire.ReliableChannel,
                    Encoding.UTF8.GetBytes("dropped")
                ),
                Is.False
            );

            Assert.That(loopback.Dropped, Is.EqualTo(1));
            Assert.That(loopback.TryDequeueClientToServer(out HostLoopbackFrame kept), Is.True);
            Assert.That(Encoding.UTF8.GetString(kept.Segment.Span), Is.EqualTo("kept"));
        }

        [Test]
        public void DepthCountsBothDirections()
        {
            HostLoopback loopback = new HostLoopback(capacityPerDirection: 2);
            Assert.That(
                loopback.TryEnqueueClientToServer(
                    AdapterWire.ReliableChannel,
                    Encoding.UTF8.GetBytes("up")
                ),
                Is.True
            );
            Assert.That(
                loopback.TryEnqueueServerToClient(
                    AdapterWire.ReliableChannel,
                    Encoding.UTF8.GetBytes("down")
                ),
                Is.True
            );

            Assert.That(loopback.Depth, Is.EqualTo(2));
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void NonPositiveCapacityIsRefused(int capacity)
        {
            Assert.Throws<ArgumentOutOfRangeException>((Action)(() => new HostLoopback(capacity)));
        }
    }
}
