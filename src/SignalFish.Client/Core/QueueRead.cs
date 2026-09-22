namespace SignalFish.Client.Core
{
    using System;

    /// <summary>
    /// The result of one bounded-queue dequeue: either one item or the
    /// end-of-stream signal — <c>default(QueueRead&lt;T&gt;)</c> means the
    /// queue completed and drained. The generic-queue shape of Rust's
    /// <c>Option</c>: <c>T?</c> cannot express null for value-type items on
    /// an unconstrained generic.
    /// </summary>
    public readonly struct QueueRead<T>
    {
        /// <summary>Gets a value indicating whether an item was read.</summary>
        public bool HasValue { get; }

        /// <summary>Gets the item; meaningful only when <see cref="HasValue"/> is true.</summary>
        public T Value { get; }

        /// <summary>Creates the result carrying one item; <c>default</c> is the end-of-stream result.</summary>
        public QueueRead(T value)
        {
            HasValue = true;
            Value = value;
        }
    }
}
