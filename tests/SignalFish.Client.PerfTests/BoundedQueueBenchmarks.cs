namespace SignalFish.Client.PerfTests
{
    using System;
    using System.Buffers;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;
    using BenchmarkDotNet.Attributes;
    using SignalFish.Client.Async;
    using SignalFish.Client.Core;

    /// <summary>
    /// M4.1 spike: the hand-rolled <see cref="BoundedQueue{T}"/> against a
    /// <see cref="Channel{T}"/>-backed adapter on the same interface — the
    /// data that decides the zero-dependency default. The adapter mirrors
    /// the interface's fail-fast/awaiting contract, proving the abstraction
    /// is honestly swap-in.
    /// </summary>
    [MemoryDiagnoser]
    public class BoundedQueueBenchmarks
    {
        private BoundedQueue<int> _handRolled = null!;
        private ChannelsBoundedQueue<int> _channels = null!;

        [GlobalSetup]
        public void Setup()
        {
            _handRolled = new BoundedQueue<int>(64);
            _channels = new ChannelsBoundedQueue<int>(64);
        }

        [Benchmark(Baseline = true)]
        public long HandRolledTryRoundtrip()
        {
            long checksum = 0;
            for (int iteration = 0; iteration < 1000; iteration++)
            {
                _handRolled.TryEnqueue(iteration);
                _handRolled.TryDequeue(out int item);
                checksum += item;
            }

            return checksum;
        }

        [Benchmark]
        public long ChannelsTryRoundtrip()
        {
            long checksum = 0;
            for (int iteration = 0; iteration < 1000; iteration++)
            {
                _channels.TryEnqueue(iteration);
                _channels.TryDequeue(out int item);
                checksum += item;
            }

            return checksum;
        }

        [Benchmark]
        public async Task<long> HandRolledAwaitHandoff()
        {
            long checksum = 0;
            for (int iteration = 0; iteration < 200; iteration++)
            {
                Task<bool> enqueued = EnqueueWhenFull(_handRolled, iteration);
                _handRolled.TryDequeue(out int freed);
                checksum += freed;
                await enqueued;
                _handRolled.TryDequeue(out int item);
                checksum += item;
            }

            return checksum;
        }

        [Benchmark]
        public async Task<long> ChannelsAwaitHandoff()
        {
            long checksum = 0;
            for (int iteration = 0; iteration < 200; iteration++)
            {
                Task<bool> enqueued = EnqueueWhenFull(_channels, iteration);
                _channels.TryDequeue(out int freed);
                checksum += freed;
                await enqueued;
                _channels.TryDequeue(out int item);
                checksum += item;
            }

            return checksum;
        }

        private static async Task<bool> EnqueueWhenFull(IBoundedQueue<int> queue, int item)
        {
            return await queue.EnqueueAsync(item);
        }
    }

    /// <summary>
    /// Tooling-only <see cref="IBoundedQueue{T}"/> over
    /// <see cref="Channel{T}"/> (inbox on net8.0): the M4.1 spike's
    /// comparison arm. Lives outside the library — the shipped default
    /// stays channel-free.
    /// </summary>
    public sealed class ChannelsBoundedQueue<T> : IBoundedQueue<T>
    {
        public int Capacity => _capacity;

        public int Count => _channel.Reader.Count;

        private readonly Channel<T> _channel;
        private readonly int _capacity;

        public ChannelsBoundedQueue(int capacity)
        {
            _capacity = capacity;
            _channel = Channel.CreateBounded<T>(
                new BoundedChannelOptions(capacity)
                {
                    SingleReader = false,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait,
                }
            );
        }

        public bool TryEnqueue(T item)
        {
            return _channel.Writer.TryWrite(item);
        }

        public async ValueTask<bool> EnqueueAsync(T item, CancellationToken ct = default)
        {
            await _channel.Writer.WriteAsync(item, ct).ConfigureAwait(false);
            return true;
        }

        public bool TryDequeue(out T item)
        {
            return _channel.Reader.TryRead(out item!);
        }

        public ValueTask<QueueRead<T>> DequeueAsync(CancellationToken ct = default)
        {
            return ReadAsync(ct);
        }

        public void Complete()
        {
            _channel.Writer.TryComplete();
        }

        public ValueTask DisposeAsync()
        {
            Complete();
            return default;
        }

        private async ValueTask<QueueRead<T>> ReadAsync(CancellationToken ct)
        {
            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                if (_channel.Reader.TryRead(out T? item))
                {
                    return new QueueRead<T>(item!);
                }
            }

            return default;
        }
    }
}
