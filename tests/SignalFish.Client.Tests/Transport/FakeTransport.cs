namespace SignalFish.Client.Tests.Transport
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using SignalFish.Client.Transport;

    /// <summary>
    /// In-memory scripted <see cref="ITransport"/> for contract tests: frames
    /// come from a scripted queue, sends are recorded verbatim, and
    /// <see cref="Abort"/> injects an abrupt (abnormal) close while a receive
    /// is pending. No sockets anywhere.
    /// </summary>
    internal sealed class FakeTransport : ITransport
    {
        private const int StateNew = 0;
        private const int StateConnected = 1;
        private const int StateClosed = 2;
        private const int StateDisposed = 3;

        /// <summary>Gets a value indicating whether the transport is connected.</summary>
        public bool IsConnected => Volatile.Read(ref _state) == StateConnected;

        /// <summary>Gets the frames sent so far, in order, as UTF-8 text (for assertions).</summary>
        public IReadOnlyList<string> SentText
        {
            get
            {
                lock (_gate)
                {
                    List<string> copy = new List<string>(_sent.Count);
                    foreach (byte[] frame in _sent)
                    {
                        copy.Add(Encoding.UTF8.GetString(frame));
                    }

                    return copy;
                }
            }
        }

        private readonly object _gate = new object();
        private readonly Queue<TransportFrame> _incoming = new Queue<TransportFrame>();
        private readonly List<byte[]> _sent = new List<byte[]>();
        private TaskCompletionSource<TransportFrame>? _pendingReceive;
        private TaskCompletionSource<bool>? _sendGate;
        private int _state = StateNew;
        private bool _closeDelivered;
        private int _closeCode;

        /// <summary>
        /// Holds every send (after recording it) until the gate completes —
        /// scripts a stalled wire so callers can observe send-queue
        /// backpressure deterministically.
        /// </summary>
        public void HoldSendsUntil(TaskCompletionSource<bool> sendGate)
        {
            lock (_gate)
            {
                _sendGate = sendGate;
            }
        }

        /// <summary>Scripts an inbound text frame.</summary>
        public void EnqueueText(string text)
        {
            Enqueue(Encoding.UTF8.GetBytes(text), isText: true);
        }

        /// <summary>Scripts an inbound frame with explicit text/binary classification.</summary>
        public void Enqueue(byte[] payload, bool isText)
        {
            TaskCompletionSource<TransportFrame>? pending = null;
            lock (_gate)
            {
                TransportFrame frame = new TransportFrame(payload, isText);
                if (_pendingReceive != null)
                {
                    pending = _pendingReceive;
                    _pendingReceive = null;
                }
                else
                {
                    _incoming.Enqueue(frame);
                }
            }

            pending?.TrySetResult(new TransportFrame(payload, isText));
        }

        /// <summary>Scripts the server-initiated close with a raw close code.</summary>
        public void EnqueueClose(int code)
        {
            lock (_gate)
            {
                _closeCode = code;
                _state = StateClosed;
            }
        }

        /// <summary>
        /// Completes the outstanding receive with a typed close (the
        /// <see cref="WebSocketTransport"/> contract for a close landing
        /// mid-receive).
        /// </summary>
        public void FailPendingReceive(int code)
        {
            TaskCompletionSource<TransportFrame>? pending;
            lock (_gate)
            {
                _closeCode = code;
                _state = StateClosed;
                pending = _pendingReceive;
                _pendingReceive = null;
            }

            pending?.SetException(new TransportClosedException(new TransportClose(code)));
        }

        /// <summary>Injects an abrupt local abort (pending receive completes with the close frame).</summary>
        public void Abort(int code = 1006)
        {
            TaskCompletionSource<TransportFrame>? pending;
            lock (_gate)
            {
                if (_closeCode == 0)
                {
                    _closeCode = code;
                }

                _state = StateClosed;
                pending = _pendingReceive;
                _pendingReceive = null;
            }

            pending?.TrySetResult(TakeCloseFrame());
        }

        /// <inheritdoc />
        public Task ConnectAsync(Uri uri, CancellationToken ct = default)
        {
            if (Interlocked.CompareExchange(ref _state, StateConnected, StateNew) != StateNew)
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant(
                        $"{nameof(FakeTransport)} can connect at most once (state {_state})."
                    )
                );
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async ValueTask<int> SendAsync(
            ReadOnlyMemory<byte> frame,
            CancellationToken ct = default
        )
        {
            int state = Volatile.Read(ref _state);
            ObjectDisposedException.ThrowIf(state == StateDisposed, typeof(FakeTransport));

            if (state != StateConnected)
            {
                throw new TransportClosedException(
                    new TransportClose(Volatile.Read(ref _closeCode))
                );
            }

            TaskCompletionSource<bool>? sendGate;
            lock (_gate)
            {
                _sent.Add(frame.ToArray());
                sendGate = _sendGate;
            }

            if (sendGate is not null)
            {
                await sendGate.Task;
            }

            return frame.Length;
        }

        /// <inheritdoc />
        public ValueTask<TransportFrame> ReceiveAsync(CancellationToken ct = default)
        {
            int state = Volatile.Read(ref _state);
            ObjectDisposedException.ThrowIf(state == StateDisposed, typeof(FakeTransport));

            lock (_gate)
            {
                if (_closeDelivered)
                {
                    throw new TransportClosedException(new TransportClose(_closeCode));
                }

                if (_incoming.Count > 0)
                {
                    return new ValueTask<TransportFrame>(_incoming.Dequeue());
                }

                if (_state == StateClosed)
                {
                    _closeDelivered = true;
                    return new ValueTask<TransportFrame>(TakeCloseFrame());
                }

                TaskCompletionSource<TransportFrame> pending =
                    new TaskCompletionSource<TransportFrame>(
                        TaskCreationOptions.RunContinuationsAsynchronously
                    );
                _pendingReceive = pending;
                return new ValueTask<TransportFrame>(pending.Task);
            }
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Abort();
            Interlocked.Exchange(ref _state, StateDisposed);
            return default;
        }

        private TransportFrame TakeCloseFrame()
        {
            _closeDelivered = true;
            return TransportFrame.FromClose(new TransportClose(_closeCode));
        }
    }
}
