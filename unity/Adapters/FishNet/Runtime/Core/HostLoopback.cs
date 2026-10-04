#nullable enable
namespace SignalFish.Client.Adapters.FishNet
{
    using System;

    /// <summary>One queued host-mode loopback frame (an owned copy).</summary>
    public readonly struct HostLoopbackFrame
    {
        /// <summary>Gets the FishNet channel byte the frame was sent on.</summary>
        public byte Channel { get; }

        /// <summary>Gets the frame segment (an owned copy).</summary>
        public ReadOnlyMemory<byte> Segment { get; }

        /// <summary>Initializes a new loopback frame.</summary>
        public HostLoopbackFrame(byte channel, byte[] segment)
        {
            Channel = channel;
            Segment = segment;
        }
    }

    /// <summary>
    /// The host-mode loopback: when FishNet runs a host (its server and
    /// client in one process), the host's own traffic loops between the
    /// two local FishNet sides without a Signal Fish relay round trip —
    /// the room is joined for matchmaking and the remote peers, not for
    /// talking to yourself. Each direction is a bounded FIFO; on overflow
    /// the newest frame is dropped and counted (a transport may drop —
    /// FishNet's channels are built for that — and a full loopback means
    /// the game stopped iterating FishNet, not that the relay is slow).
    /// All members are thread-safe.
    /// </summary>
    public sealed class HostLoopback
    {
        /// <summary>A fixed-capacity FIFO ring; overflow drops the newest frame.</summary>
        private sealed class Ring
        {
            internal int Count
            {
                get { return _count; }
            }

            private readonly HostLoopbackFrame[] _items;
            private int _head;
            private int _count;

            internal Ring(int capacity)
            {
                _items = new HostLoopbackFrame[capacity];
            }

            internal bool TryEnqueue(HostLoopbackFrame frame)
            {
                if (_count == _items.Length)
                {
                    return false;
                }

                _items[(_head + _count) % _items.Length] = frame;
                _count++;
                return true;
            }

            internal bool TryDequeue(out HostLoopbackFrame frame)
            {
                if (_count == 0)
                {
                    frame = default;
                    return false;
                }

                frame = _items[_head];
                _items[_head] = default;
                _head = (_head + 1) % _items.Length;
                _count--;
                return true;
            }
        }

        /// <summary>Gets how many frames overflowed every direction so far.</summary>
        public int Dropped
        {
            get
            {
                lock (_gate)
                {
                    return _dropped;
                }
            }
        }

        /// <summary>Gets the total frames queued across both directions.</summary>
        public int Depth
        {
            get
            {
                lock (_gate)
                {
                    return _clientToServer.Count + _serverToClient.Count;
                }
            }
        }

        private readonly object _gate = new object();
        private readonly Ring _clientToServer;
        private readonly Ring _serverToClient;
        private int _dropped;

        /// <summary>Initializes a loopback with the per-direction capacity.</summary>
        public HostLoopback(int capacityPerDirection)
        {
            if (capacityPerDirection < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capacityPerDirection),
                    capacityPerDirection,
                    "The loopback capacity must be at least one frame per direction."
                );
            }

            _clientToServer = new Ring(capacityPerDirection);
            _serverToClient = new Ring(capacityPerDirection);
        }

        /// <summary>
        /// Queues a frame from the local FishNet client into the local
        /// server feed; the sender presents as
        /// <see cref="SignalFishPeerRouter.HostConnectionId"/>.
        /// </summary>
        public bool TryEnqueueClientToServer(byte channel, ReadOnlySpan<byte> segment)
        {
            return Enqueue(_clientToServer, channel, segment);
        }

        /// <summary>Dequeues one frame for the local server feed.</summary>
        public bool TryDequeueClientToServer(out HostLoopbackFrame frame)
        {
            return Dequeue(_clientToServer, out frame);
        }

        /// <summary>Queues a frame from the local server into the local client feed.</summary>
        public bool TryEnqueueServerToClient(byte channel, ReadOnlySpan<byte> segment)
        {
            return Enqueue(_serverToClient, channel, segment);
        }

        /// <summary>Dequeues one frame for the local client feed.</summary>
        public bool TryDequeueServerToClient(out HostLoopbackFrame frame)
        {
            return Dequeue(_serverToClient, out frame);
        }

        private bool Enqueue(Ring ring, byte channel, ReadOnlySpan<byte> segment)
        {
            byte[] copy = segment.ToArray();
            lock (_gate)
            {
                if (!ring.TryEnqueue(new HostLoopbackFrame(channel, copy)))
                {
                    _dropped++;
                    return false;
                }

                return true;
            }
        }

        private bool Dequeue(Ring ring, out HostLoopbackFrame frame)
        {
            lock (_gate)
            {
                return ring.TryDequeue(out frame);
            }
        }
    }
}
