namespace SignalFish.Client.Polling
{
    using System;

    /// <summary>
    /// Fixed-capacity ring of <see cref="PollEvent"/> structs: allocated
    /// once by the polling client, zero allocation on enqueue/drain. Not
    /// thread-safe — one producer/consumer (the poll loop's owner).
    /// Backpressure is cooperative: a full ring rejects enqueues and the
    /// client stops consuming transport frames until the game drains.
    /// </summary>
    internal sealed class EventRingBuffer
    {
        /// <summary>Gets the number of pending events.</summary>
        internal int Count { get; private set; }

        /// <summary>Gets a value indicating whether the ring can accept no more events.</summary>
        internal bool IsFull => Count == _items.Length;

        private readonly PollEvent[] _items;
        private int _head;

        /// <summary>Creates the ring; <paramref name="capacity"/> must be positive.</summary>
        internal EventRingBuffer(int capacity)
        {
            _items = new PollEvent[capacity];
        }

        /// <summary>
        /// Enqueues one event. Returns false when the ring is full (the
        /// event is not enqueued; the caller stops consuming frames).
        /// </summary>
        internal bool TryEnqueue(in PollEvent pollEvent)
        {
            if (Count == _items.Length)
            {
                return false;
            }

            _items[(_head + Count) % _items.Length] = pollEvent;
            Count++;
            return true;
        }

        /// <summary>Starts a drain walk over the pending events (oldest first).</summary>
        internal EventDrain DrainEvents()
        {
            return new EventDrain(this);
        }

        /// <summary>
        /// Consumes the oldest event: clears its slot (releasing payload
        /// references) and advances the ring. Called by the drain walk
        /// after the event has been copied out.
        /// </summary>
        internal void ConsumeOne()
        {
            _items[_head] = default;
            _head = (_head + 1) % _items.Length;
            Count--;
        }

        /// <summary>Reads the oldest pending event without consuming it.</summary>
        internal PollEvent PeekHead()
        {
            return _items[_head];
        }
    }

    /// <summary>
    /// Ref-struct drain walk over a ring buffer: allocates nothing and
    /// consumes events as it goes, so a fully completed drain empties the
    /// ring and abandoning a walk mid-way retains only the unconsumed
    /// tail. <see cref="Current"/> is a copy pinned between
    /// <see cref="MoveNext"/> calls. Single-threaded (the poll loop's
    /// owner); nested drains of the same client are not supported.
    /// </summary>
    public ref struct EventDrain
    {
        /// <summary>Gets the current event (valid until the next <see cref="MoveNext"/>).</summary>
        public PollEvent Current => _current;

        private readonly EventRingBuffer _ring;
        private PollEvent _current;
        private int _remaining;

        internal EventDrain(EventRingBuffer ring)
        {
            _ring = ring;
            _current = default;
            _remaining = ring.Count;
        }

        /// <summary>Returns itself; enables <c>foreach</c> over <see cref="EventDrain"/>.</summary>
        public EventDrain GetEnumerator()
        {
            return this;
        }

        /// <summary>
        /// Advances to the next pending event, consuming it. Returns false
        /// when the drain is complete.
        /// </summary>
        public bool MoveNext()
        {
            if (_remaining == 0 || _ring.Count == 0)
            {
                _remaining = 0;
                return false;
            }

            _current = _ring.PeekHead();
            _ring.ConsumeOne();
            _remaining--;
            return true;
        }
    }
}
