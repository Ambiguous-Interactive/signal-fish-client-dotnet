#nullable enable
namespace SignalFish.Client.Transport
{
    using System;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The WebGL transport: a browser WebSocket bridged through the Unity
    /// package's <c>SignalFishWebSocket.jslib</c> plugin. The plugin queues
    /// incoming frames on the JavaScript side; this class drains it by
    /// polling, because a browser page has no threads and
    /// <see cref="System.Net.WebSockets.ClientWebSocket"/> does not exist
    /// there. Everything else mirrors the .NET transport's contract: connect
    /// at most once, single reader, the terminal close surfaced exactly once
    /// (with the observed wire code, abnormal 1006 when the browser reports
    /// none), sends serialized by the browser's single thread and capped at
    /// the server inbound limit, dispose idempotent and polite (initiates the
    /// close handshake with code 1000). Receive throughput is bounded by the
    /// poll cadence — ample for signaling, not a relay throughput story.
    /// </summary>
    public sealed class SignalFishWebGLTransport : ITransport
    {
        /// <summary>Server inbound limit default: <c>max_message_size</c> = 64 KiB. Client sends must stay under it.</summary>
        public const int DefaultOutboundCapBytes = 64 * 1024;

        /// <summary>
        /// Server outbound limit default: <c>max_outbound_message_size</c> = 8 MiB,
        /// used as the receive bound. The browser cannot probe
        /// <c>client-config</c>, so the protocol default always applies.
        /// </summary>
        public const int DefaultMaxReceiveBytes = 8 * 1024 * 1024;

        private const int StateCreated = 0;
        private const int StateConnecting = 1;
        private const int StateConnected = 2;
        private const int StateClosed = 3;
        private const int StateDisposed = 4;

        private const int InitialReceiveBytes = 4 * 1024;
        private const int AbnormalCloseCode = 1006;
        private const int ClientCloseCode = 1000;
        private const int NoStatusCode = 1005;
        private const int ReceivePollMilliseconds = 1;

        // WebSocket readyState values reported by the plugin's Status entry.
        private const int SocketOpen = 1;
        private const int SocketClosing = 2;
        private const int SocketClosed = 3;

        // SignalFishWebSocketPoll result contract (mirrored in the jslib).
        private const int PollEmpty = 0;
        private const int PollClosed = -1;
        private const int PollTooBig = -2;
        private const int PollKindText = 0;
        private const int PollKindBinary = 1;

        private readonly int _outboundCapBytes;
        private readonly int _maxReceiveBytes;

        private int _state = StateCreated;
        private int _closeCode;
        private int _readerActive;
        private int _handle;
        private bool _closeFrameDelivered;

        /// <summary>Initializes the transport with the default 64 KiB outbound cap and 8 MiB receive bound.</summary>
        public SignalFishWebGLTransport()
            : this(DefaultOutboundCapBytes, DefaultMaxReceiveBytes) { }

        /// <summary>Initializes the transport with an explicit outbound cap (deployments may lower the server inbound limit).</summary>
        public SignalFishWebGLTransport(int outboundCapBytes)
            : this(outboundCapBytes, DefaultMaxReceiveBytes) { }

        /// <summary>Initializes the transport with explicit outbound and receive bounds.</summary>
        public SignalFishWebGLTransport(int outboundCapBytes, int maxReceiveBytes)
        {
            if (outboundCapBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(outboundCapBytes));
            }

            if (maxReceiveBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxReceiveBytes));
            }

            _outboundCapBytes = outboundCapBytes;
            _maxReceiveBytes = maxReceiveBytes;
        }

        /// <inheritdoc />
        public async Task ConnectAsync(Uri uri, CancellationToken ct = default)
        {
            if (uri == null)
            {
                throw new ArgumentNullException(nameof(uri));
            }

            ThrowIfDisposed();
            if (
                Interlocked.CompareExchange(ref _state, StateConnecting, StateCreated)
                != StateCreated
            )
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant(
                        $"{nameof(SignalFishWebGLTransport)} can connect at most once (state {_state})."
                    )
                );
            }

            int handle = SignalFishWebSocketOpen(uri.ToString());
            if (handle == 0)
            {
                Interlocked.CompareExchange(ref _state, StateClosed, StateConnecting);
                throw new TransportClosedException(new TransportClose(AbnormalCloseCode));
            }

            Volatile.Write(ref _handle, handle);
            try
            {
                while (true)
                {
                    /*
                        A concurrent dispose removes the registry entry, so a
                        disposed transport surfaces here as the abnormal close
                        below instead of an ObjectDisposedException — the
                        session teardown contract (a failed or torn-down
                        connect yields the close, never a raw fault).
                    */
                    int ready = SignalFishWebSocketStatus(handle);
                    if (ready == SocketOpen)
                    {
                        if (
                            Interlocked.CompareExchange(ref _state, StateConnected, StateConnecting)
                            == StateConnecting
                        )
                        {
                            return;
                        }

                        /*
                            A dispose overlapped the upgrade tail: its handle
                            exchange already ran, so this path owns the drop
                            (never leak a connected browser socket).
                        */
                        SignalFishWebSocketDispose(handle);
                        throw new TransportClosedException(new TransportClose(AbnormalCloseCode));
                    }

                    if (ready == SocketClosed || ready == SocketClosing)
                    {
                        Interlocked.CompareExchange(ref _state, StateClosed, StateConnecting);
                        throw new TransportClosedException(
                            new TransportClose(ResolveCloseCode(handle))
                        );
                    }

                    await Task.Delay(ReceivePollMilliseconds, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                /*
                    The connect slot is spent either way (documented contract:
                    a failed connect leaves the session unusable); drop the
                    socket so the cancellation never leaks a connecting
                    browser WebSocket.
                */
                Interlocked.CompareExchange(ref _state, StateClosed, StateConnecting);
                Volatile.Write(ref _handle, 0);
                SignalFishWebSocketDispose(handle);
                throw;
            }
        }

        /// <inheritdoc />
        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
        {
            return SendFrameAsync(frame, isBinary: false, ct);
        }

        /// <inheritdoc />
        public ValueTask<int> SendBinaryAsync(
            ReadOnlyMemory<byte> frame,
            CancellationToken ct = default
        )
        {
            return SendFrameAsync(frame, isBinary: true, ct);
        }

        /// <inheritdoc />
        public async ValueTask<TransportFrame> ReceiveAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            int state = Volatile.Read(ref _state);
            if (state == StateCreated || state == StateConnecting)
            {
                throw new InvalidOperationException("ReceiveAsync requires a connected transport.");
            }

            if (Volatile.Read(ref _closeFrameDelivered))
            {
                throw new TransportClosedException(
                    new TransportClose(Volatile.Read(ref _closeCode))
                );
            }

            if (Interlocked.CompareExchange(ref _readerActive, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "ReceiveAsync is single reader; a receive is already in flight."
                );
            }

            try
            {
                return await ReceiveCoreAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _readerActive, 0);
            }
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            int previous = TransitionToDisposed();
            if (previous == StateDisposed)
            {
                return default;
            }

            Interlocked.CompareExchange(ref _closeCode, AbnormalCloseCode, 0);
            int handle = Interlocked.Exchange(ref _handle, 0);
            if (handle != 0 && previous == StateConnected)
            {
                /*
                    Initiate the close handshake (code 1000) so the server
                    observes a client-initiated close. The browser completes
                    it on its own; dropping the registry entry does not abort
                    it, so no bounded wait is needed here.
                */
                SignalFishWebSocketClose(handle, ClientCloseCode);
            }

            if (handle != 0)
            {
                SignalFishWebSocketDispose(handle);
            }

            return default;
        }

        private async ValueTask<TransportFrame> ReceiveCoreAsync(CancellationToken ct)
        {
            int handle = Volatile.Read(ref _handle);
            if (handle == 0)
            {
                return EndWithClose(AbnormalCloseCode);
            }

            byte[] buffer = new byte[Math.Min(InitialReceiveBytes, _maxReceiveBytes)];
            while (true)
            {
                int kind;
                int length;
                int result = Poll(handle, buffer, out kind, out length);
                if (result > 0)
                {
                    byte[] payload = new byte[result];
                    Buffer.BlockCopy(buffer, 0, payload, 0, result);
                    return new TransportFrame(payload, kind == PollKindText);
                }

                if (result == PollEmpty)
                {
                    await Task.Delay(ReceivePollMilliseconds, ct).ConfigureAwait(false);
                    continue;
                }

                if (result == PollTooBig)
                {
                    if (length > _maxReceiveBytes)
                    {
                        return EndWithClose(1009);
                    }

                    buffer = new byte[length];
                    continue;
                }

                /*
                    PollClosed (or any unexpected code) is the terminal close:
                    kind carries the observed wire code. A missing code means
                    the browser never saw one (abnormal).
                */
                return EndWithClose(kind == 0 ? AbnormalCloseCode : kind);
            }
        }

        private ValueTask<int> SendFrameAsync(
            ReadOnlyMemory<byte> frame,
            bool isBinary,
            CancellationToken ct
        )
        {
            ThrowIfDisposed();
            if (Volatile.Read(ref _state) != StateConnected)
            {
                throw new TransportClosedException(
                    new TransportClose(Volatile.Read(ref _closeCode))
                );
            }

            if (frame.Length > _outboundCapBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frame),
                    frame.Length,
                    FormattableString.Invariant(
                        $"Frame exceeds the outbound cap of {_outboundCapBytes} bytes (server inbound limit)."
                    )
                );
            }

            int handle = Volatile.Read(ref _handle);
            int result = Send(handle, frame, isBinary ? 1 : 0);
            if (result != 0)
            {
                int code = ResolveCloseCode(handle);
                MarkClosed(code);
                throw new TransportClosedException(new TransportClose(code));
            }

            return new ValueTask<int>(frame.Length);
        }

        private TransportFrame EndWithClose(int code)
        {
            MarkClosed(code);
            _closeFrameDelivered = true;

            /*
                Release the plugin entry now — parity with the .NET
                transport releasing the socket at the terminal close. On
                the oversized-frame path the browser socket is still open
                and would otherwise keep buffering inbound frames into the
                JS queue until dispose.
            */
            int handle = Interlocked.Exchange(ref _handle, 0);
            if (handle != 0)
            {
                SignalFishWebSocketDispose(handle);
            }

            return TransportFrame.FromClose(new TransportClose(code));
        }

        private void MarkClosed(int code)
        {
            Interlocked.Exchange(ref _closeCode, code);
            Interlocked.CompareExchange(ref _state, StateClosed, StateConnected);
            Interlocked.CompareExchange(ref _state, StateClosed, StateConnecting);
        }

        /// <summary>
        /// The observed wire close code, normalized: no entry (never connected
        /// or already released) and the browser's "no status code" sentinel
        /// both map to the abnormal close, matching the .NET transport.
        /// </summary>
        private static int ResolveCloseCode(int handle)
        {
            if (handle == 0)
            {
                return AbnormalCloseCode;
            }

            int code = SignalFishWebSocketCloseCode(handle);
            return code == 0 || code == NoStatusCode ? AbnormalCloseCode : code;
        }

        /// <summary>
        /// Drains one queued message into <paramref name="buffer"/> through
        /// the plugin. The plugin copies whole messages only: a message that
        /// does not fit stays queued and the required length is reported, so
        /// the retry after growing never splits a frame.
        /// </summary>
        private static int Poll(int handle, byte[] buffer, out int kind, out int length)
        {
            GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                return SignalFishWebSocketPoll(
                    handle,
                    out kind,
                    out length,
                    Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0),
                    buffer.Length
                );
            }
            finally
            {
                pin.Free();
            }
        }

        private static int Send(int handle, ReadOnlyMemory<byte> frame, int isBinary)
        {
            byte[] array;
            int offset;
            if (MemoryMarshal.TryGetArray(frame, out ArraySegment<byte> segment))
            {
                array = segment.Array!;
                offset = segment.Offset;
            }
            else
            {
                array = frame.ToArray();
                offset = 0;
            }

            GCHandle pin = GCHandle.Alloc(array, GCHandleType.Pinned);
            try
            {
                return SignalFishWebSocketSend(
                    handle,
                    Marshal.UnsafeAddrOfPinnedArrayElement(array, offset),
                    frame.Length,
                    isBinary
                );
            }
            finally
            {
                pin.Free();
            }
        }

        /// <summary>
        /// Atomically moves any state to <see cref="StateDisposed"/> and
        /// returns the state seen before the move (the first caller wins;
        /// later callers observe <see cref="StateDisposed"/>).
        /// </summary>
        private int TransitionToDisposed()
        {
            while (true)
            {
                int seen = Volatile.Read(ref _state);
                if (Interlocked.CompareExchange(ref _state, StateDisposed, seen) == seen)
                {
                    return seen;
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _state) == StateDisposed)
            {
                throw new ObjectDisposedException(nameof(SignalFishWebGLTransport));
            }
        }

        /*
            The plugin bridge (unity/.../Plugins/SignalFishWebGL/
            SignalFishWebSocket.jslib). Entry points are pinned to the jslib
            exports by scripts/lint-webgl-plugin.ps1; a mismatch surfaces at
            CI, not as a runtime EntryPointNotFoundException in the browser.
        */

        [DllImport("__Internal")]
        private static extern int SignalFishWebSocketOpen(string url);

        [DllImport("__Internal")]
        private static extern int SignalFishWebSocketStatus(int handle);

        [DllImport("__Internal")]
        private static extern int SignalFishWebSocketCloseCode(int handle);

        [DllImport("__Internal")]
        private static extern int SignalFishWebSocketSend(
            int handle,
            IntPtr data,
            int length,
            int isBinary
        );

        [DllImport("__Internal")]
        private static extern int SignalFishWebSocketPoll(
            int handle,
            out int kind,
            out int length,
            IntPtr data,
            int dataMax
        );

        [DllImport("__Internal")]
        private static extern void SignalFishWebSocketClose(int handle, int code);

        [DllImport("__Internal")]
        private static extern void SignalFishWebSocketDispose(int handle);
    }
}
