namespace SignalFish.Client.Tests.Polling
{
    using System;
    using System.Collections.Generic;
    using NUnit.Framework;
    using SignalFish.Client.Polling;
    using SignalFish.Client.Protocol;

    /// <summary>
    /// Red-green anchors for the event ring: FIFO order, consumption
    /// semantics, backpressure, and wraparound.
    /// </summary>
    [TestFixture]
    public class EventRingBufferTests
    {
        private static readonly int[] ThreeTags = { 1, 2, 3 };
        private static readonly int[] TwoTags = { 2, 3 };

        [Test]
        public void DrainWalksOldestFirstAndEmptiesTheRing()
        {
            EventRingBuffer ring = new EventRingBuffer(4);
            Assert.That(ring.TryEnqueue(Make(1)), Is.True);
            Assert.That(ring.TryEnqueue(Make(2)), Is.True);
            Assert.That(ring.TryEnqueue(Make(3)), Is.True);
            Assert.That(ring.Count, Is.EqualTo(3));

            List<int> seen = new List<int>();
            foreach (PollEvent pollEvent in ring.DrainEvents())
            {
                seen.Add(KindTag(pollEvent));
            }

            Assert.That(seen, Is.EqualTo(ThreeTags));
            Assert.That(ring.Count, Is.EqualTo(0));
        }

        [Test]
        public void PartialDrainRetainsTheUnconsumedTail()
        {
            EventRingBuffer ring = new EventRingBuffer(4);
            ring.TryEnqueue(Make(1));
            ring.TryEnqueue(Make(2));
            ring.TryEnqueue(Make(3));

            foreach (PollEvent pollEvent in ring.DrainEvents())
            {
                break;
            }

            Assert.That(ring.Count, Is.EqualTo(2), "one event consumed by the first MoveNext");

            List<int> seen = new List<int>();
            foreach (PollEvent pollEvent in ring.DrainEvents())
            {
                seen.Add(KindTag(pollEvent));
            }

            Assert.That(seen, Is.EqualTo(TwoTags));
            Assert.That(ring.Count, Is.EqualTo(0));
        }

        [Test]
        public void FullRingRejectsEnqueueAndDrainFreesIt()
        {
            EventRingBuffer ring = new EventRingBuffer(2);
            Assert.That(ring.TryEnqueue(Make(1)), Is.True);
            Assert.That(ring.TryEnqueue(Make(2)), Is.True);
            Assert.That(ring.IsFull, Is.True);
            Assert.That(ring.TryEnqueue(Make(3)), Is.False, "a full ring must backpressure");
            Assert.That(ring.Count, Is.EqualTo(2));

            foreach (PollEvent pollEvent in ring.DrainEvents()) { }

            Assert.That(ring.TryEnqueue(Make(4)), Is.True);
            Assert.That(KindTag(SingleDrain(ring)), Is.EqualTo(4));
        }

        [Test]
        public void WraparoundPreservesOrderAcrossManyCycles()
        {
            EventRingBuffer ring = new EventRingBuffer(3);
            List<int> expected = new List<int>();
            List<int> seen = new List<int>();
            for (int i = 0; i < 97; i++)
            {
                int tag = ((i * 31) % 250) + 1;
                expected.Add(tag);
                Assert.That(ring.TryEnqueue(Make(tag)), Is.True, $"enqueue {i}");
                if (i % 3 == 2)
                {
                    foreach (PollEvent pollEvent in ring.DrainEvents())
                    {
                        seen.Add(KindTag(pollEvent));
                    }
                }
            }

            foreach (PollEvent pollEvent in ring.DrainEvents())
            {
                seen.Add(KindTag(pollEvent));
            }

            Assert.That(seen, Is.EqualTo(expected));
            Assert.That(ring.Count, Is.EqualTo(0));
        }

        [Test]
        public void EmptyDrainCompletesImmediately()
        {
            EventRingBuffer ring = new EventRingBuffer(4);
            int moves = 0;
            foreach (PollEvent pollEvent in ring.DrainEvents())
            {
                moves++;
            }

            Assert.That(moves, Is.EqualTo(0));
        }

        private static PollEvent Make(int tag)
        {
            return PollEvent.FromDecodeFailed(
                DecodeError.Truncated,
                tag,
                ReadOnlyMemory<byte>.Empty
            );
        }

        private static int KindTag(PollEvent pollEvent)
        {
            return pollEvent.ErrorOffset;
        }

        private static PollEvent SingleDrain(EventRingBuffer ring)
        {
            foreach (PollEvent pollEvent in ring.DrainEvents())
            {
                return pollEvent;
            }

            throw new InvalidOperationException("Expected one event.");
        }
    }
}
