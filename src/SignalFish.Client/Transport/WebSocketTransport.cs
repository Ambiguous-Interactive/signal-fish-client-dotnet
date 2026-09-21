namespace SignalFish.Client.Transport
{
    using System;
    using System.Buffers;
    using System.IO;
    using System.Net.Http;
    using System.Net.WebSockets;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The .NET transport: a <see cref="ClientWebSocket"/> connection with the
    /// Signal Fish sizing contract. The server's outbound cap is probed from
    /// <c>GET /v2|v3/client-config</c> before upgrading (best effort; falls
    /// back to the protocol default) and bounds receive buffering. Sends are
    /// capped at the server's inbound limit (default 64 KiB) so oversized
    /// frames never reach the wire. Concurrency: single reader, one
    /// semaphore-guarded writer, atomic state transitions (no
    /// check-then-act), close surfaced exactly once, idempotent dispose.
    /// </summary>
    public sealed class WebSocketTransport : ITransport
    {
        /// <summary>Server outbound limit default: <c>max_outbound_message_size</c> = 8 MiB.</summary>
        public const int DefaultServerMaxOutboundBytes = 8 * 1024 * 1024;

        /// <summary>Server inbound limit default: <c>max_message_size</c> = 64 KiB. Client sends must stay under it.</summary>
        public const int DefaultOutboundCapBytes = 64 * 1024;

        private const int StateCreated = 0;
        private const int StateConnecting = 1;
        private const int StateConnected = 2;
        private const int StateClosed = 3;
        private const int StateDisposed = 4;

        private const int InitialReceiveBytes = 4 * 1024;
        private const int AbnormalCloseCode = 1006;
        private const int ProbeTimeoutMilliseconds = 3_000;
        private const int MaxProbeResponseBytes = 64 * 1024;

        private const string ClientConfigPathSuffix = "client-config";
        private const string MaxOutboundKey = "max_outbound_message_size";

        private int _state = StateCreated;
        private int _closeCode;
        private int _readerActive;
        private int _maxReceiveBytes = DefaultServerMaxOutboundBytes;

        private ClientWebSocket? _socket;
        private HttpClient? _probeClient;
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly int _outboundCapBytes;
        private bool _closeFrameDelivered;

        /// <summary>Initializes the transport with the default 64 KiB outbound cap.</summary>
        public WebSocketTransport()
            : this(DefaultOutboundCapBytes) { }

        /// <summary>Initializes the transport with an explicit outbound cap (deployments may lower the server inbound limit).</summary>
        public WebSocketTransport(int outboundCapBytes)
        {
            if (outboundCapBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(outboundCapBytes));
            }

            _outboundCapBytes = outboundCapBytes;
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
                        $"{nameof(WebSocketTransport)} can connect at most once (state {_state})."
                    )
                );
            }

            _maxReceiveBytes = await ProbeServerMaxOutboundAsync(uri, ct).ConfigureAwait(false);

            ClientWebSocket socket = new ClientWebSocket();
            _socket = socket;
            try
            {
                /*
                    Dispose may race the probe above; a socket created after
                    it must never reach the wire. The catch below disposes it.
                */
                ThrowIfDisposed();
                await socket.ConnectAsync(uri, ct).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                if (ReferenceEquals(Interlocked.Exchange(ref _socket, null), socket))
                {
                    Interlocked.CompareExchange(ref _state, StateClosed, StateConnecting);
                }

                throw;
            }

            if (
                Interlocked.CompareExchange(ref _state, StateConnected, StateConnecting)
                != StateConnecting
            )
            {
                ThrowIfDisposed();
                throw new TransportClosedException(
                    new TransportClose(Volatile.Read(ref _closeCode))
                );
            }
        }

        /// <inheritdoc />
        public async ValueTask<int> SendAsync(
            ReadOnlyMemory<byte> frame,
            CancellationToken ct = default
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

            await _sendGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (Volatile.Read(ref _state) != StateConnected)
                {
                    throw new TransportClosedException(
                        new TransportClose(Volatile.Read(ref _closeCode))
                    );
                }

                ClientWebSocket socket = _socket!;
                try
                {
                    await socket
                        .SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (IsTransportFault(exception))
                {
                    int code = ResolveFaultCode();
                    MarkClosed(code);
                    throw new TransportClosedException(new TransportClose(code), exception);
                }

                return frame.Length;
            }
            finally
            {
                try
                {
                    _sendGate.Release();
                }
                catch (ObjectDisposedException) { }
            }
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
            if (TransitionToDisposed())
            {
                Interlocked.CompareExchange(ref _closeCode, AbnormalCloseCode, 0);
                ReleaseResources();
            }

            return default;
        }

        private async ValueTask<TransportFrame> ReceiveCoreAsync(CancellationToken ct)
        {
            ClientWebSocket? socket = _socket;
            if (socket == null)
            {
                return EndWithClose(Volatile.Read(ref _closeCode));
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(
                Math.Min(InitialReceiveBytes, _maxReceiveBytes)
            );
            int capacity = Math.Min(buffer.Length, _maxReceiveBytes);
            int length = 0;
            try
            {
                while (true)
                {
                    if (length == capacity && !Grow(ref buffer, ref capacity, length))
                    {
                        return EndWithClose(1009);
                    }

                    ValueWebSocketReceiveResult result;
                    try
                    {
                        result = await socket
                            .ReceiveAsync(buffer.AsMemory(length, capacity - length), ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (IsTransportFault(exception))
                    {
                        return EndWithClose(ResolveFaultCode());
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        WebSocketCloseStatus? closeStatus = socket.CloseStatus;
                        int code = closeStatus.HasValue
                            ? (int)closeStatus.Value
                            : AbnormalCloseCode;
                        return EndWithClose(code);
                    }

                    length += result.Count;
                    if (result.EndOfMessage)
                    {
                        byte[] payload = new byte[length];
                        Buffer.BlockCopy(buffer, 0, payload, 0, length);
                        return new TransportFrame(
                            payload,
                            result.MessageType == WebSocketMessageType.Text
                        );
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private bool Grow(ref byte[] buffer, ref int capacity, int length)
        {
            /*
                Rent returns buckets that can exceed the request; the usable
                cap is the probed limit, never the rented array length.
            */
            int nextCapacity = Math.Min(capacity * 2, _maxReceiveBytes);
            if (nextCapacity <= length)
            {
                return false;
            }

            byte[] grown = ArrayPool<byte>.Shared.Rent(nextCapacity);
            int grownCapacity = Math.Min(grown.Length, _maxReceiveBytes);
            Buffer.BlockCopy(buffer, 0, grown, 0, length);
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = grown;
            capacity = grownCapacity;
            return true;
        }

        private TransportFrame EndWithClose(int code)
        {
            MarkClosed(code);
            ReleaseResources();
            _closeFrameDelivered = true;
            return TransportFrame.FromClose(new TransportClose(code));
        }

        private void MarkClosed(int code)
        {
            Interlocked.Exchange(ref _closeCode, code);
            Interlocked.CompareExchange(ref _state, StateClosed, StateConnected);
            Interlocked.CompareExchange(ref _state, StateClosed, StateConnecting);
        }

        private int ResolveFaultCode()
        {
            ClientWebSocket? socket = _socket;
            return socket != null && socket.CloseStatus.HasValue
                ? (int)socket.CloseStatus.Value
                : AbnormalCloseCode;
        }

        private void ReleaseResources()
        {
            /*
                Single-releaser by construction: DisposeAsync is guarded by the
                state transition and the close path by the single-reader claim.
                ClientWebSocket/HttpClient dispose is idempotent, which covers
                the one overlap that remains (terminal close racing dispose).
            */
            ClientWebSocket? socket = _socket;
            _socket = null;
            HttpClient? probeClient = _probeClient;
            _probeClient = null;
            socket?.Dispose();
            probeClient?.Dispose();
            try
            {
                _sendGate.Dispose();
            }
            catch (ObjectDisposedException) { }
        }

        private bool TransitionToDisposed()
        {
            while (true)
            {
                int seen = Volatile.Read(ref _state);
                if (seen == StateDisposed)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _state, StateDisposed, seen) == seen)
                {
                    return true;
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _state) == StateDisposed)
            {
                throw new ObjectDisposedException(nameof(WebSocketTransport));
            }
        }

        private static bool IsTransportFault(Exception exception)
        {
            return exception is WebSocketException
                || exception is IOException
                || exception is ObjectDisposedException;
        }

        private async Task<int> ProbeServerMaxOutboundAsync(Uri uri, CancellationToken ct)
        {
            if (!TryBuildClientConfigUri(uri, out Uri? configUri))
            {
                return DefaultServerMaxOutboundBytes;
            }

            HttpClient client = new HttpClient();
            HttpClient? previous = Interlocked.Exchange(ref _probeClient, client);
            previous?.Dispose();

            CancellationTokenSource probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                probeCts.CancelAfter(ProbeTimeoutMilliseconds);
                using (
                    HttpResponseMessage response = await client
                        .GetAsync(
                            configUri,
                            HttpCompletionOption.ResponseContentRead,
                            probeCts.Token
                        )
                        .ConfigureAwait(false)
                )
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return DefaultServerMaxOutboundBytes;
                    }

                    byte[] body = await response
                        .Content.ReadAsByteArrayAsync()
                        .ConfigureAwait(false);
                    if (body.Length > MaxProbeResponseBytes)
                    {
                        return DefaultServerMaxOutboundBytes;
                    }

                    return TryScanMaxOutbound(body, out int value)
                        ? value
                        : DefaultServerMaxOutboundBytes;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return DefaultServerMaxOutboundBytes;
            }
            catch (Exception)
            {
                return DefaultServerMaxOutboundBytes;
            }
            finally
            {
                probeCts.Dispose();
                HttpClient? stale = Interlocked.CompareExchange(ref _probeClient, null, client);
                stale?.Dispose();
            }
        }

        private static bool TryBuildClientConfigUri(Uri uri, out Uri? configUri)
        {
            configUri = null;
            string scheme = uri.Scheme switch
            {
                "ws" => "http",
                "wss" => "https",
                "http" => "http",
                "https" => "https",
                _ => string.Empty,
            };

            if (scheme.Length == 0)
            {
                return false;
            }

            string path = uri.AbsolutePath;
            if (!path.EndsWith("/ws", StringComparison.Ordinal))
            {
                return false;
            }

            string trimmed = path[..^2];
            UriBuilder builder = new UriBuilder(uri)
            {
                Scheme = scheme,
                Path = trimmed + ClientConfigPathSuffix,
            };

            configUri = builder.Uri;
            return true;
        }

        private static bool TryScanMaxOutbound(byte[] body, out int value)
        {
            value = 0;
            if (!TryFindAscii(body, MaxOutboundKey, out int keyStart))
            {
                return false;
            }

            int index = SkipSpaces(body, keyStart + MaxOutboundKey.Length);
            if (index >= body.Length || body[index] != (byte)'"')
            {
                return false;
            }

            index = SkipSpaces(body, index + 1);
            if (index >= body.Length || body[index] != (byte)':')
            {
                return false;
            }

            index = SkipSpaces(body, index + 1);
            int parsed = 0;
            int digits = 0;
            while (index < body.Length && body[index] >= (byte)'0' && body[index] <= (byte)'9')
            {
                parsed = (parsed * 10) + (body[index] - (byte)'0');
                digits++;
                index++;
                if (parsed > DefaultServerMaxOutboundBytes)
                {
                    return false;
                }
            }

            if (digits == 0 || parsed < 1)
            {
                return false;
            }

            value = parsed;
            return true;
        }

        private static int SkipSpaces(byte[] body, int index)
        {
            while (index < body.Length && (body[index] == (byte)' ' || body[index] == (byte)'\t'))
            {
                index++;
            }

            return index;
        }

        private static bool TryFindAscii(byte[] haystack, string needle, out int position)
        {
            position = -1;
            int limit = haystack.Length - needle.Length;
            for (int start = 0; start <= limit; start++)
            {
                int offset = 0;
                while (offset < needle.Length && haystack[start + offset] == (byte)needle[offset])
                {
                    offset++;
                }

                if (offset == needle.Length)
                {
                    position = start;
                    return true;
                }
            }

            return false;
        }
    }
}
