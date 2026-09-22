namespace SignalFish.Client.Async
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using SignalFish.Client.Core;

    /// <summary>
    /// Channel-free bounded FIFO queue over one ring buffer: the zero-
    /// dependency <see cref="IBoundedQueue{T}"/> default. Single lock, no
    /// allocations on the fail-fast paths (try-enqueue, try-dequeue); waiter
    /// objects exist only while a producer or consumer actually pauses.
    /// Waiter handoffs happen under the lock before the slot or item is
    /// consumed, so a waiter that cancelled concurrently can never steal
    /// capacity or reorder the stream. FIFO holds across waiters: a granted
    /// producer's item is newer than everything buffered and takes the tail.
    /// </summary>
    public sealed class BoundedQueue<T> : IBoundedQueue<T>
    {
        private sealed class DequeueWaiter
        {
            internal readonly TaskCompletionSource<QueueRead<T>> Completion =
                new TaskCompletionSource<QueueRead<T>>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
        }

        private sealed class EnqueueWaiter
        {
            /// <summary>The item offered by the parked producer.</summary>
            internal T Item { get; }

            internal readonly TaskCompletionSource<bool> Completion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal EnqueueWaiter(T item)
            {
                Item = item;
            }
        }

        /// <inheritdoc />
        public int Capacity
        {
            get { return _items.Length; }
        }

        /// <inheritdoc />
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _count;
                }
            }
        }

        private readonly object _gate = new object();
        private readonly T[] _items;
        private readonly Queue<EnqueueWaiter> _enqueueWaiters = new Queue<EnqueueWaiter>();
        private readonly Queue<DequeueWaiter> _dequeueWaiters = new Queue<DequeueWaiter>();
        private int _head;
        private int _count;
        private bool _completed;

        /// <summary>Creates the queue; <paramref name="capacity"/> must be positive.</summary>
        public BoundedQueue(int capacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capacity),
                    "The queue capacity must be positive."
                );
            }

            _items = new T[capacity];
        }

        /// <inheritdoc />
        public bool TryEnqueue(T item)
        {
            lock (_gate)
            {
                if (_completed || _count == _items.Length)
                {
                    return false;
                }

                StoreLocked(item);
                return true;
            }
        }

        /// <inheritdoc />
        public ValueTask<bool> EnqueueAsync(T item, CancellationToken ct = default)
        {
            if (ct.IsCancellationRequested)
            {
                return new ValueTask<bool>(Task.FromCanceled<bool>(ct));
            }

            lock (_gate)
            {
                if (_completed)
                {
                    return new ValueTask<bool>(false);
                }

                if (_count < _items.Length)
                {
                    StoreLocked(item);
                    return new ValueTask<bool>(true);
                }

                EnqueueWaiter waiter = new EnqueueWaiter(item);
                _enqueueWaiters.Enqueue(waiter);
                return new ValueTask<bool>(AwaitSlotAsync(waiter, ct));
            }
        }

        /// <inheritdoc />
        public bool TryDequeue(out T item)
        {
            lock (_gate)
            {
                return TryTakeLocked(out item);
            }
        }

        /// <inheritdoc />
        public ValueTask<QueueRead<T>> DequeueAsync(CancellationToken ct = default)
        {
            if (ct.IsCancellationRequested)
            {
                return new ValueTask<QueueRead<T>>(Task.FromCanceled<QueueRead<T>>(ct));
            }

            lock (_gate)
            {
                if (TryTakeLocked(out T item))
                {
                    return new ValueTask<QueueRead<T>>(new QueueRead<T>(item));
                }

                if (_completed)
                {
                    return new ValueTask<QueueRead<T>>(default(QueueRead<T>));
                }

                DequeueWaiter waiter = new DequeueWaiter();
                _dequeueWaiters.Enqueue(waiter);
                return new ValueTask<QueueRead<T>>(AwaitItemAsync(waiter, ct));
            }
        }

        /// <inheritdoc />
        public void Complete()
        {
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;

                while (_enqueueWaiters.Count > 0)
                {
                    _enqueueWaiters.Dequeue().Completion.TrySetResult(false);
                }

                while (_count > 0 && _dequeueWaiters.Count > 0)
                {
                    DequeueWaiter waiter = _dequeueWaiters.Dequeue();
                    if (!waiter.Completion.TrySetResult(new QueueRead<T>(_items[_head])))
                    {
                        continue;
                    }

                    _items[_head] = default!;
                    _head = (_head + 1) % _items.Length;
                    _count--;
                }

                if (_count == 0)
                {
                    while (_dequeueWaiters.Count > 0)
                    {
                        _dequeueWaiters.Dequeue().Completion.TrySetResult(default(QueueRead<T>));
                    }
                }
            }
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Complete();
            return default;
        }

        /*
            Callers hold _gate. A live waiter (one whose completion was not
            already cancelled) takes the item or slot; cancelled waiters are
            skipped without consuming anything.
        */

        private void StoreLocked(T item)
        {
            while (_dequeueWaiters.Count > 0)
            {
                DequeueWaiter waiter = _dequeueWaiters.Dequeue();
                if (waiter.Completion.TrySetResult(new QueueRead<T>(item)))
                {
                    return;
                }
            }

            _items[(_head + _count) % _items.Length] = item;
            _count++;
        }

        private bool TryTakeLocked(out T item)
        {
            if (_count == 0)
            {
                item = default!;
                return false;
            }

            item = _items[_head];
            _items[_head] = default!;
            _head = (_head + 1) % _items.Length;
            _count--;
            GrantSlotLocked();
            return true;
        }

        private void GrantSlotLocked()
        {
            while (_enqueueWaiters.Count > 0)
            {
                EnqueueWaiter waiter = _enqueueWaiters.Dequeue();
                if (waiter.Completion.TrySetResult(true))
                {
                    /*
                        The granted item is newer than everything buffered
                        (it was offered when the queue was already full), so
                        it belongs at the tail — which, the queue having
                        been full, is exactly the slot just freed.
                    */
                    int slot = (_head - 1 + _items.Length) % _items.Length;
                    _items[slot] = waiter.Item;
                    _count++;
                    return;
                }
            }
        }

        private static async Task<bool> AwaitSlotAsync(EnqueueWaiter waiter, CancellationToken ct)
        {
            CancellationTokenRegistration registration = RegisterCancelAsync(waiter.Completion, ct);
            try
            {
                return await waiter.Completion.Task.ConfigureAwait(false);
            }
            finally
            {
                await registration.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static async Task<QueueRead<T>> AwaitItemAsync(
            DequeueWaiter waiter,
            CancellationToken ct
        )
        {
            CancellationTokenRegistration registration = RegisterCancelAsync(waiter.Completion, ct);
            try
            {
                return await waiter.Completion.Task.ConfigureAwait(false);
            }
            finally
            {
                await registration.DisposeAsync().ConfigureAwait(false);
            }
        }

        /*
            Completes the wait as cancelled when the token fires; the
            registration is disposed by the awaiting method only after the
            wait resolved, so cancellation stays armed for as long as the
            wait is live. A waiter cancelled after a grant/completion simply
            loses the race and changes nothing.
        */
        private static CancellationTokenRegistration RegisterCancelAsync<TC>(
            TaskCompletionSource<TC> completion,
            CancellationToken ct
        )
        {
            return ct.Register(
                static state =>
                {
                    (TaskCompletionSource<TC> Completion, CancellationToken Token) canceled = ((
                        TaskCompletionSource<TC>,
                        CancellationToken
                    ))
                        state!;
                    canceled.Completion.TrySetCanceled(canceled.Token);
                },
                (completion, ct)
            );
        }
    }
}
