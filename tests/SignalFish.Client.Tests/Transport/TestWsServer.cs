namespace SignalFish.Client.Tests.Transport
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Net;
    using System.Net.Sockets;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>One frame read from a WebSocket peer: opcode plus payload.</summary>
    internal readonly struct TestWsFrame
    {
        public TestWsFrame(int opcode, byte[] payload)
        {
            Opcode = opcode;
            Payload = payload;
        }

        public int Opcode { get; }

        public byte[] Payload { get; }
    }

    /// <summary>
    /// A single accepted WebSocket connection on the loopback server. Frames
    /// are driven pull/push style by the test; no protocol logic lives here.
    /// </summary>
    internal sealed class TestWsConnection : IAsyncDisposable
    {
        private const int OpcodeText = 0x1;
        private const int OpcodeBinary = 0x2;
        private const int OpcodeClose = 0x8;

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;

        public TestWsConnection(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        /// <summary>Sends a server-originated text frame (unmasked, FIN).</summary>
        public Task SendTextAsync(string text)
        {
            return SendFrameAsync(OpcodeText, Encoding.UTF8.GetBytes(text));
        }

        /// <summary>Sends a server-originated binary frame (unmasked, FIN).</summary>
        public Task SendBinaryAsync(byte[] payload)
        {
            return SendFrameAsync(OpcodeBinary, payload);
        }

        /// <summary>Sends a server-originated close frame carrying only a status code.</summary>
        public Task SendCloseAsync(int code)
        {
            byte[] payload = new byte[] { (byte)(code >> 8), (byte)(code & 0xFF) };
            return SendFrameAsync(OpcodeClose, payload);
        }

        /// <summary>Reads one complete client frame (masked), returning opcode and unmasked payload.</summary>
        public async Task<TestWsFrame> ReceiveFrameAsync(CancellationToken ct)
        {
            byte[] header = await ReadExactlyAsync(_stream, 2, ct);
            int opcode = header[0] & 0x0F;
            bool masked = (header[1] & 0x80) != 0;
            long length = header[1] & 0x7F;
            if (length == 126)
            {
                byte[] extended = await ReadExactlyAsync(_stream, 2, ct);
                length = (extended[0] << 8) | extended[1];
            }
            else if (length == 127)
            {
                byte[] extended = await ReadExactlyAsync(_stream, 8, ct);
                length = 0;
                for (int i = 0; i < 8; i++)
                {
                    length = (length << 8) | extended[i];
                }
            }

            byte[] mask = masked ? await ReadExactlyAsync(_stream, 4, ct) : new byte[4];
            byte[] payload = await ReadExactlyAsync(_stream, (int)length, ct);
            if (masked)
            {
                for (int i = 0; i < payload.Length; i++)
                {
                    payload[i] ^= mask[i % 4];
                }
            }

            return new TestWsFrame(opcode, payload);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            _client.Dispose();
            return default;
        }

        private async Task SendFrameAsync(int opcode, byte[] payload)
        {
            byte[] header;
            if (payload.Length < 126)
            {
                header = new byte[] { (byte)(0x80 | opcode), (byte)payload.Length };
            }
            else if (payload.Length < 65536)
            {
                header = new byte[]
                {
                    (byte)(0x80 | opcode),
                    126,
                    (byte)(payload.Length >> 8),
                    (byte)(payload.Length & 0xFF),
                };
            }
            else
            {
                header = new byte[]
                {
                    (byte)(0x80 | opcode),
                    127,
                    0,
                    0,
                    0,
                    0,
                    (byte)(payload.Length >> 24),
                    (byte)((payload.Length >> 16) & 0xFF),
                    (byte)((payload.Length >> 8) & 0xFF),
                    (byte)(payload.Length & 0xFF),
                };
            }

            await _stream.WriteAsync(header, CancellationToken.None);
            await _stream.WriteAsync(payload, CancellationToken.None);
            await _stream.FlushAsync(CancellationToken.None);
        }

        private static async Task<byte[]> ReadExactlyAsync(
            Stream stream,
            int count,
            CancellationToken ct
        )
        {
            byte[] buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int chunk = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
                if (chunk == 0)
                {
                    throw new IOException(
                        FormattableString.Invariant(
                            $"Peer closed mid-frame (wanted {count}, got {read})."
                        )
                    );
                }

                read += chunk;
            }

            return buffer;
        }
    }

    /// <summary>
    /// Hand-rolled loopback WebSocket test server (TCP + RFC 6455 handshake
    /// and framing) so transport tests run identically on Linux and Windows
    /// CI without OS URL ACLs or extra packages. Also serves the
    /// <c>/v2/client-config</c> sizing endpoint the transport probes.
    /// </summary>
    internal sealed class TestWsServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly ConcurrentQueue<TestWsConnection> _connections =
            new ConcurrentQueue<TestWsConnection>();
        private readonly SemaphoreSlim _connectionSignal = new SemaphoreSlim(0);

        private TestWsServer(TcpListener listener)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        /// <summary>Gets the bound loopback port.</summary>
        public int Port { get; }

        /// <summary>Gets or sets the client-config JSON body; <see langword="null"/> makes the endpoint 404.</summary>
        public string? ClientConfigJson { get; set; }

        /// <summary>Gets the HTTP request paths observed so far (probe assertions).</summary>
        public ConcurrentQueue<string> HttpRequestPaths { get; } = new ConcurrentQueue<string>();

        /// <summary>Starts listening on an ephemeral loopback port.</summary>
        public static TestWsServer Start(string? clientConfigJson)
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            TestWsServer server = new TestWsServer(listener);
            server.ClientConfigJson = clientConfigJson;
            _ = server.AcceptLoopAsync();
            return server;
        }

        /// <summary>
        /// Waits for the next WebSocket connection to complete its handshake.
        /// Semaphore-counted handoff: stale (timed-out) waiters cannot steal
        /// or lose connections.
        /// </summary>
        public async Task<TestWsConnection> WaitForConnectionAsync(CancellationToken ct)
        {
            while (true)
            {
                await _connectionSignal.WaitAsync(ct);
                if (_connections.TryDequeue(out TestWsConnection? connection))
                {
                    return connection;
                }
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync();
            _listener.Stop();
            _shutdown.Dispose();
            _connectionSignal.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                _ = HandleClientAsync(client);
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            NetworkStream stream = client.GetStream();
            try
            {
                string head = await ReadHttpHeadAsync(stream, _shutdown.Token);
                string path = ParseRequestPath(head);
                HttpRequestPaths.Enqueue(path);
                string? key = ParseHeader(head, "sec-websocket-key");

                if (key != null)
                {
                    await WriteUpgradeResponseAsync(stream, key);
                    _connections.Enqueue(new TestWsConnection(client));
                    _connectionSignal.Release();
                }
                else
                {
                    await WriteHttpResponseAsync(stream, path);
                    client.Dispose();
                }
            }
            catch
            {
                client.Dispose();
            }
        }

        private async Task WriteHttpResponseAsync(NetworkStream stream, string path)
        {
            bool served =
                ClientConfigJson != null
                && path.EndsWith("/client-config", StringComparison.Ordinal);
            string body = served ? ClientConfigJson! : string.Empty;
            string status = served ? "200 OK" : "404 Not Found";
            string response =
                "HTTP/1.1 "
                + status
                + "\r\n"
                + "Content-Type: application/json\r\n"
                + "Content-Length: "
                + Encoding.UTF8.GetByteCount(body)
                + "\r\n"
                + "Connection: close\r\n"
                + "\r\n"
                + body;
            byte[] bytes = Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(bytes, _shutdown.Token);
            await stream.FlushAsync(_shutdown.Token);
        }

        private static async Task WriteUpgradeResponseAsync(NetworkStream stream, string key)
        {
            string accept = ComputeAcceptKey(key);
            string response =
                "HTTP/1.1 101 Switching Protocols\r\n"
                + "Upgrade: websocket\r\n"
                + "Connection: Upgrade\r\n"
                + "Sec-WebSocket-Accept: "
                + accept
                + "\r\n"
                + "\r\n";
            byte[] bytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(bytes, CancellationToken.None);
            await stream.FlushAsync(CancellationToken.None);
        }

        private static string ComputeAcceptKey(string key)
        {
            const string Magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            byte[] hash = SHA1.HashData(Encoding.ASCII.GetBytes(key + Magic));
            return Convert.ToBase64String(hash);
        }

        private static async Task<string> ReadHttpHeadAsync(
            NetworkStream stream,
            CancellationToken ct
        )
        {
            byte[] buffer = new byte[8 * 1024];
            int length = 0;
            while (length < buffer.Length)
            {
                int chunk = await stream.ReadAsync(buffer.AsMemory(length), ct);
                if (chunk == 0)
                {
                    break;
                }

                length += chunk;
                if (ContainsDoubleCrlf(buffer, length))
                {
                    break;
                }
            }

            return Encoding.ASCII.GetString(buffer, 0, length);
        }

        private static bool ContainsDoubleCrlf(byte[] buffer, int length)
        {
            for (int i = 3; i < length; i++)
            {
                if (
                    buffer[i - 3] == (byte)'\r'
                    && buffer[i - 2] == (byte)'\n'
                    && buffer[i - 1] == (byte)'\r'
                    && buffer[i] == (byte)'\n'
                )
                {
                    return true;
                }
            }

            return false;
        }

        private static string ParseRequestPath(string head)
        {
            string requestLine = head.Split('\n')[0].TrimEnd('\r');
            string[] parts = requestLine.Split(' ');
            return parts.Length >= 2 ? parts[1] : string.Empty;
        }

        private static string? ParseHeader(string head, string headerName)
        {
            string[] lines = head.Split('\n');
            foreach (string line in lines)
            {
                string trimmed = line.TrimEnd('\r');
                string[] pair = trimmed.Split(':', 2);
                if (pair.Length < 2)
                {
                    continue;
                }

                string name = pair[0].Trim();
                if (string.Equals(name, headerName, StringComparison.OrdinalIgnoreCase))
                {
                    return pair[1].Trim();
                }
            }

            return null;
        }
    }
}
