namespace SignalFish.Client.Tests.Async
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using NUnit.Framework;
    using SignalFish.Client.Async;
    using SignalFish.Client.Core;

    /// <summary>
    /// Red-green anchors for the M4.1 bounded queue: FIFO order, capacity
    /// backpressure, waiter handoff (grant-into-freed-slot ordering),
    /// cancellation hygiene, completion semantics, a concurrent
    /// producer/consumer stress, and the 0 B fail-fast gate.
    /// </summary>
    [TestFixture]
    public class BoundedQueueTests
    {
        [Test]
        public void TryRoundtripPreservesFifoOrder()
        {
            BoundedQueue<int> queue = new BoundedQueue<int>(4);
            for (int expected = 0; expected < 4; expected++)
            {
                Assert.That(queue.TryEnqueue(expected), Is.True);
            }

            for (int expected = 0; expected < 4; expected++)
            {
                Assert.That(queue.TryDequeue(out int actual), Is.True);
                Assert.That(actual, Is.EqualTo(expected));
            }
        }

        [Test]
        public void TryEnqueueRefusesWhenFullUntilSlotFrees()
        {
            BoundedQueue<int> queue = new BoundedQueue<int>(2);
            Assert.That(queue.TryEnqueue(1), Is.True);
            Assert.That(queue.TryEnqueue(2), Is.True);
            Assert.That(queue.TryEnqueue(3), Is.False, "full queue refuses");
            Assert.That(queue.Count, Is.EqualTo(2));

            Assert.That(queue.TryDequeue(out int first), Is.True);
            Assert.That(first, Is.EqualTo(1));
            Assert.That(queue.TryEnqueue(3), Is.True, "the freed slot is reusable");
            Assert.That(queue.TryDequeue(out int second), Is.True);
            Assert.That(second, Is.EqualTo(2));
            Assert.That(queue.TryDequeue(out int third), Is.True);
            Assert.That(third, Is.EqualTo(3));
        }

        [Test]
        public void TryDequeueReturnsFalseWhenEmpty()
        {
            BoundedQueue<int> queue = new BoundedQueue<int>(2);
            Assert.That(queue.TryDequeue(out int _), Is.False);
            Assert.That(queue.TryEnqueue(7), Is.True);
            Assert.That(queue.TryDequeue(out int actual), Is.True);
            Assert.That(actual, Is.EqualTo(7));
        }

        [Test]
        public async Task EnqueueAsyncAwaitsCapacityAndKeepsFifoOrder()
        {
            BoundedQueue<int> queue = new BoundedQueue<int>(2);
            Assert.That(queue.TryEnqueue(1), Is.True);
            Assert.That(queue.TryEnqueue(2), Is.True);

            Task<bool> awaited = queue.EnqueueAsync(3).AsTask();
            Assert.That(awaited.IsCompleted, Is.False, "a full queue parks the producer");

            Assert.That(queue.TryDequeue(out int first), Is.True);
            Assert.That(first, Is.EqualTo(1));
            Assert.That(await awaited, Is.True, "the freed slot completes the parked producer");

            /*
                The granted item was offered last: it dequeues after the
                items that already held slots (global FIFO across waiters).
            */
            Assert.That(queue.TryDequeue(out int second), Is.True);
            Assert.That(second, Is.EqualTo(2));
            Assert.That(queue.TryDequeue(out int third), Is.True);
            Assert.That(third, Is.EqualTo(3));
        }

        [Test]
        public async Task DequeueAsyncAwaitsItemUntilEnqueued()
        {
            BoundedQueue<int> queue = new BoundedQueue<int>(2);
            Task<QueueRead<int>> read = queue.DequeueAsync().AsTask();
            Assert.That(read.IsCompleted, Is.False, "an empty queue parks the consumer");

            Assert.That(queue.TryEnqueue(41), Is.True);
            QueueRead<int> result = await read;
            Assert.That(result.HasValue, Is.True);
            Assert.That(result.Value, Is.EqualTo(41));
        }

        [Test]
        public async Task CancelledEnqueueWaitKeepsSlotAndDropsNothing()
        {
            BoundedQueue<int> queue = new BoundedQueue<int>(1);
            Assert.That(queue.TryEnqueue(1), Is.True);

            CancellationTokenSource cancellation = new CancellationTokenSource();
            Task<bool> awaited = queue.EnqueueAsync(99, cancellation.Token).AsTask();
            await cancellation.CancelAsync();
            Assert.ThrowsAsync<TaskCanceledException>((Func<Task>)(async () => await awaited));

            /*
                The cancelled item never surfaces and the capacity is whole:
                the next producer takes the slot normally.
            */
            Assert.That(queue.TryDequeue(out int first), Is.True);
            Assert.That(first, Is.EqualTo(1));
            Assert.That(queue.TryEnqueue(2), Is.True);
            Assert.That(queue.TryDequeue(out int second), Is.True);
            Assert.That(second, Is.EqualTo(2));
        }

        [Test]
        public async Task CancelledDequeueWaitDoesNotStealLaterItem()
        {
            BoundedQueue<int> queue = new BoundedQueue<int>(2);
            CancellationTokenSource cancellation = new CancellationTokenSource();
            Task<QueueRead<int>> read = queue.DequeueAsync(cancellation.Token).AsTask();
            await cancellation.CancelAsync();
            Assert.ThrowsAsync<TaskCanceledException>((Func<Task>)(async () => await read));

            Assert.That(queue.TryEnqueue(5), Is.True);
            Assert.That(queue.TryDequeue(out int actual), Is.True);
            Assert.That(actual, Is.EqualTo(5), "the cancelled waiter must not consume it");
        }

        [Test]
        public void CompleteDeliversBufferedItemsThenEndOfStream()
        {
            BoundedQueue<int> queue = new BoundedQueue<int>(4);
            Assert.That(queue.TryEnqueue(1), Is.True);
            Assert.That(queue.TryEnqueue(2), Is.True);

            queue.Complete();
            queue.Complete();

            Assert.That(queue.TryEnqueue(3), Is.False, "completed queues refuse producers");
            Assert.That(queue.TryDequeue(out int first), Is.True);
            Assert.That(first, Is.EqualTo(1));
            Assert.That(queue.TryDequeue(out int second), Is.True);
            Assert.That(second, Is.EqualTo(2));
            Assert.That(queue.TryDequeue(out int _), Is.False, "buffer drains, then nothing");

            Task<QueueRead<int>> read = queue.DequeueAsync().AsTask();
            Assert.That(read.IsCompleted, Is.True);
            Assert.That(read.Result.HasValue, Is.False, "end of stream after the drain");
        }

        [Test]
        public async Task CompleteFeedsPendingConsumerThenEndsStream()
        {
            BoundedQueue<int> queue = new BoundedQueue<int>(4);
            Assert.That(queue.TryEnqueue(1), Is.True);
            Task<QueueRead<int>> parked = queue.DequeueAsync().AsTask();

            queue.Complete();

            QueueRead<int> first = await parked;
            Assert.That(first.HasValue, Is.True, "buffered items reach parked consumers first");
            Assert.That(first.Value, Is.EqualTo(1));

            QueueRead<int> second = await queue.DequeueAsync();
            Assert.That(second.HasValue, Is.False, "then the stream ends");
        }

        [Test]
        public async Task CompleteRefusesPendingEnqueueWaiters()
        {
            BoundedQueue<int> queue = new BoundedQueue<int>(1);
            Assert.That(queue.TryEnqueue(1), Is.True);
            Task<bool> awaited = queue.EnqueueAsync(2).AsTask();

            queue.Complete();

            Assert.That(await awaited, Is.False, "a completed queue accepts nothing");
            Assert.That(queue.TryDequeue(out int actual), Is.True);
            Assert.That(actual, Is.EqualTo(1));
        }

        [Test]
        public void CapacityBelowOneIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>((Action)(() => new BoundedQueue<int>(0)));
            Assert.Throws<ArgumentOutOfRangeException>((Action)(() => new BoundedQueue<int>(-1)));
        }

        [Test]
        public async Task ConcurrentProducersAndConsumerKeepPerProducerOrder()
        {
            BoundedQueue<int> queue = new BoundedQueue<int>(8);
            const int perProducer = 500;
            Task producerA = Task.Run(() =>
            {
                for (int sequence = 0; sequence < perProducer; sequence++)
                {
                    SpinUntilEnqueued(queue, sequence);
                }
            });
            Task producerB = Task.Run(() =>
            {
                for (int sequence = 0; sequence < perProducer; sequence++)
                {
                    SpinUntilEnqueued(queue, sequence + perProducer);
                }
            });

            int[] lastPerProducer = new int[2];
            int consumed = 0;
            while (consumed < perProducer * 2)
            {
                if (!queue.TryDequeue(out int item))
                {
                    await Task.Yield();
                    continue;
                }

                int producer = item / perProducer;
                int sequence = item % perProducer;
                Assert.That(
                    sequence,
                    Is.EqualTo(lastPerProducer[producer]),
                    $"producer {producer} order broke at sequence {sequence}"
                );
                lastPerProducer[producer] = sequence + 1;
                consumed++;
            }

            await Task.WhenAll(producerA, producerB);
            Assert.That(queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void FailFastRoundtripAllocatesNothing()
        {
            BoundedQueue<int> queue = new BoundedQueue<int>(64);
            for (int warmup = 0; warmup < 64; warmup++)
            {
                queue.TryEnqueue(warmup);
                queue.TryDequeue(out int _);
            }

            long minDelta = long.MaxValue;
            for (int pass = 0; pass < 4; pass++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int iteration = 0; iteration < 1000; iteration++)
                {
                    queue.TryEnqueue(iteration);
                    queue.TryDequeue(out int _);
                }

                long after = GC.GetAllocatedBytesForCurrentThread();
                minDelta = Math.Min(minDelta, after - before);
            }

            Assert.That(minDelta, Is.EqualTo(0), "fail-fast enqueue/dequeue must not allocate.");
        }

        [Test]
        public void PreCancelledWaitsThrowImmediately()
        {
            BoundedQueue<int> queue = new BoundedQueue<int>(1);
            CancellationTokenSource cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Task<bool> enqueue = queue.EnqueueAsync(1, cancellation.Token).AsTask();
            Assert.That(enqueue.IsCompleted, Is.True);
            Assert.ThrowsAsync<TaskCanceledException>((Func<Task>)(async () => await enqueue));

            Task<QueueRead<int>> dequeue = queue.DequeueAsync(cancellation.Token).AsTask();
            Assert.That(dequeue.IsCompleted, Is.True);
            Assert.ThrowsAsync<TaskCanceledException>((Func<Task>)(async () => await dequeue));
        }

        private static void SpinUntilEnqueued(BoundedQueue<int> queue, int item)
        {
            while (!queue.TryEnqueue(item))
            {
                Thread.Yield();
            }
        }
    }
}
