namespace SignalFish.Client.Core
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Fixed-capacity FIFO queue connecting producers and consumers across
    /// threads — the client's event and command channels
    /// (<c>Async/BoundedQueue</c> is the channel-free default; the
    /// abstraction keeps alternatives swap-in, e.g. benchmarks against
    /// <c>System.Threading.Channels</c>). Backpressure contract: a full
    /// queue refuses fail-fast producers; awaiting producers pause until a
    /// slot frees. Items are never dropped.
    /// </summary>
    /// <typeparam name="T">The item type; element copies are shallow.</typeparam>
    public interface IBoundedQueue<T> : IAsyncDisposable
    {
        /// <summary>Gets the configured item capacity.</summary>
        int Capacity { get; }

        /// <summary>Gets the number of buffered items (not in-flight handoffs).</summary>
        int Count { get; }

        /// <summary>
        /// Enqueues one item without waiting. Returns false when full or
        /// completed; the item is not enqueued and nothing is dropped.
        /// </summary>
        bool TryEnqueue(T item);

        /// <summary>
        /// Enqueues one item, pausing until a slot frees. Returns false
        /// only when the queue completed before the item was accepted;
        /// cancellation throws <see cref="OperationCanceledException"/>.
        /// </summary>
        ValueTask<bool> EnqueueAsync(T item, CancellationToken ct = default);

        /// <summary>
        /// Consumes the oldest buffered item without waiting. Returns false
        /// when the queue is empty.
        /// </summary>
        bool TryDequeue(out T item);

        /// <summary>
        /// Consumes the oldest item, pausing until one is buffered. Returns
        /// <see cref="QueueRead{T}.Empty"/> once completed and fully
        /// drained; cancellation throws
        /// <see cref="OperationCanceledException"/>.
        /// </summary>
        ValueTask<QueueRead<T>> DequeueAsync(CancellationToken ct = default);

        /// <summary>
        /// Ends the stream: further enqueues fail, buffered items stay
        /// consumable, and paused/next consumers drain to empty then
        /// observe completion. Idempotent.
        /// </summary>
        void Complete();
    }
}
