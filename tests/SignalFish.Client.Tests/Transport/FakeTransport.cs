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
        /// <summary>A wire wait parked until its send count is recorded.</summary>
        private sealed class SendWaiter
        {
            internal int Threshold { get; }

            internal TaskCompletionSource<bool> Completion { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal SendWaiter(int threshold)
            {
                Threshold = threshold;
            }
        }

        private const int StateNew = 0;
        private const int StateConnected = 1;
        private const int StateClosed = 2;
        private const int StateDisposed = 3;

        /// <summary>Gets a value indicating whether the transport is connected.</summary>
        public bool IsConnected => Volatile.Read(ref _state) == StateConnected;

        /// <summary>Gets how many times <see cref="ConnectAsync"/> was called.</summary>
        public int ConnectCount => Volatile.Read(ref _connectCount);

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

        /// <summary>Gets the binary frames sent so far, in order.</summary>
        public IReadOnlyList<byte[]> SentBinary
        {
            get
            {
                lock (_gate)
                {
                    List<byte[]> copy = new List<byte[]>();
                    for (int i = 0; i < _sent.Count; i++)
                    {
                        if (!_sentKinds[i])
                        {
                            copy.Add(_sent[i]);
                        }
                    }

                    return copy;
                }
            }
        }

        private readonly object _gate = new object();
        private readonly Queue<TransportFrame> _incoming = new Queue<TransportFrame>();
        private readonly List<byte[]> _sent = new List<byte[]>();
        private readonly List<bool> _sentKinds = new List<bool>();
        private readonly List<SendWaiter> _textSendWaiters = new List<SendWaiter>();
        private readonly List<SendWaiter> _binarySendWaiters = new List<SendWaiter>();
        private TaskCompletionSource<TransportFrame>? _pendingReceive;
        private TaskCompletionSource<bool>? _sendGate;
        private int _state = StateNew;
        private int _connectCount;
        private int _doomedCloseCode;
        private int _textSends;
        private int _binarySends;
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

        /// <summary>
        /// Awaits until at least <paramref name="count"/> text sends are
        /// recorded — the event-based wire wait. The awaiting test wakes the
        /// moment the driver loop flushes the frame, with no poll cadence
        /// and no wall-clock deadline to lose against a saturated CI runner
        /// (issue #93). Disposal releases every unmet wait so a test whose
        /// session died mid-wait asserts on the real count instead of
        /// hanging.
        /// </summary>
        public Task WaitSentTextAsync(int count, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (_textSends >= count)
                {
                    return Task.CompletedTask;
                }

                return RegisterSendWaiterLocked(_textSendWaiters, count, ct);
            }
        }

        /// <summary>
        /// Awaits until at least <paramref name="count"/> binary sends are
        /// recorded — the binary twin of <see cref="WaitSentTextAsync"/>.
        /// </summary>
        public Task WaitSentBinaryAsync(int count, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (_binarySends >= count)
                {
                    return Task.CompletedTask;
                }

                return RegisterSendWaiterLocked(_binarySendWaiters, count, ct);
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

        /// <summary>
        /// Scripts the server-initiated close with a raw close code.
        /// </summary>
        public void EnqueueClose(int code)
        {
            lock (_gate)
            {
                _closeCode = code;
                _state = StateClosed;
            }
        }

        /// <summary>
        /// Arms the close to land right after <see cref="ConnectAsync"/>:
        /// the handshake succeeds, then every send fails and the first
        /// receive delivers the close frame — a connection that dies
        /// immediately (a retryable reconnect round).
        /// </summary>
        public void DoomWithClose(int code)
        {
            lock (_gate)
            {
                _closeCode = code;
                _doomedCloseCode = code;
            }
        }

        /// <summary>
        /// Fails every send held by <see cref="HoldSendsUntil"/> with a
        /// typed close and marks the connection closed — a wire death
        /// while a send is parked mid-flight.
        /// </summary>
        public void FailHeldSends(int code)
        {
            TaskCompletionSource<bool>? sendGate;
            lock (_gate)
            {
                _closeCode = code;
                _state = StateClosed;
                sendGate = _sendGate;
                _sendGate = null;
            }

            sendGate?.TrySetException(new TransportClosedException(new TransportClose(code)));
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
            Interlocked.Increment(ref _connectCount);
            if (Interlocked.CompareExchange(ref _state, StateConnected, StateNew) != StateNew)
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant(
                        $"{nameof(FakeTransport)} can connect at most once (state {_state})."
                    )
                );
            }

            if (Volatile.Read(ref _doomedCloseCode) != 0)
            {
                Volatile.Write(ref _state, StateClosed);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
        {
            return SendFrameAsync(frame, isText: true, ct);
        }

        /// <inheritdoc />
        public ValueTask<int> SendBinaryAsync(
            ReadOnlyMemory<byte> frame,
            CancellationToken ct = default
        )
        {
            return SendFrameAsync(frame, isText: false, ct);
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
            TaskCompletionSource<bool>? sendGate;
            lock (_gate)
            {
                sendGate = _sendGate;
                _sendGate = null;

                /*
                    The wire is gone: sends can never reach an unmet count,
                    so parkers wake and assert on the real state instead of
                    riding the test's deadline.
                */
                ReleaseSendWaitersLocked(_textSendWaiters);
                ReleaseSendWaitersLocked(_binarySendWaiters);
            }

            /*
                Disposal aborts a stalled wire, so held sends complete like
                a real transport's aborted socket would.
            */
            sendGate?.TrySetResult(true);
            Abort();
            Interlocked.Exchange(ref _state, StateDisposed);
            return default;
        }

        private async ValueTask<int> SendFrameAsync(
            ReadOnlyMemory<byte> frame,
            bool isText,
            CancellationToken ct
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
                _sentKinds.Add(isText);
                if (isText)
                {
                    _textSends++;
                    CompleteSendWaitersLocked(_textSendWaiters, _textSends);
                }
                else
                {
                    _binarySends++;
                    CompleteSendWaitersLocked(_binarySendWaiters, _binarySends);
                }

                sendGate = _sendGate;
            }

            if (sendGate is not null)
            {
                await sendGate.Task;
            }

            return frame.Length;
        }

        /*
            Wire-wait plumbing. All of it requires _gate on entry: a waiter
            joins the list and a send completes satisfied waiters under the
            same lock, so a wake always observes the count it waited for.
        */

        private static Task RegisterSendWaiterLocked(
            List<SendWaiter> waiters,
            int count,
            CancellationToken ct
        )
        {
            SendWaiter waiter = new SendWaiter(count);
            waiters.Add(waiter);
            return WaitWithCancelAsync(waiter.Completion, ct);
        }

        private static void CompleteSendWaitersLocked(List<SendWaiter> waiters, int recorded)
        {
            for (int i = waiters.Count - 1; i >= 0; i--)
            {
                if (waiters[i].Threshold <= recorded)
                {
                    waiters[i].Completion.TrySetResult(true);
                    waiters.RemoveAt(i);
                }
            }
        }

        private static void ReleaseSendWaitersLocked(List<SendWaiter> waiters)
        {
            foreach (SendWaiter waiter in waiters)
            {
                waiter.Completion.TrySetResult(false);
            }

            waiters.Clear();
        }

        private static async Task WaitWithCancelAsync(
            TaskCompletionSource<bool> completion,
            CancellationToken ct
        )
        {
            CancellationTokenRegistration registration = ct.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
                completion
            );
            try
            {
                await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                await registration.DisposeAsync().ConfigureAwait(false);
            }
        }

        private TransportFrame TakeCloseFrame()
        {
            _closeDelivered = true;
            return TransportFrame.FromClose(new TransportClose(_closeCode));
        }
    }
}
