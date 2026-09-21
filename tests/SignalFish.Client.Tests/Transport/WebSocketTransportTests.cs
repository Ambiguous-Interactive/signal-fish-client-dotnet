namespace SignalFish.Client.Tests.Transport
{
    using System;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using NUnit.Framework;
    using SignalFish.Client.Transport;

    /// <summary>
    /// M2.2 red-green: <see cref="WebSocketTransport"/> against a hand-rolled
    /// loopback WebSocket server — real handshake, real frames, real close
    /// codes, no extra packages and no OS URL ACLs. Pins the transport
    /// contract end to end: client-config probe and receive sizing, text and
    /// binary frames, close-code mapping, the 64 KiB outbound cap, and
    /// single-reader discipline.
    /// </summary>
    [TestFixture]
    public class WebSocketTransportTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

        private static CancellationToken TestToken()
        {
            CancellationTokenSource cts = new CancellationTokenSource(TestTimeout);
            return cts.Token;
        }

        private static Uri ServerUri(int port)
        {
            return new Uri(FormattableString.Invariant($"ws://127.0.0.1:{port}/v2/ws"));
        }

        public static readonly TestCaseData[] CloseCodeCases =
        {
            new TestCaseData(4000, TransportCloseKind.ServerShutdown),
            new TestCaseData(4001, TransportCloseKind.AuthTimeout),
            new TestCaseData(4002, TransportCloseKind.SlowConsumer),
            new TestCaseData(4003, TransportCloseKind.ActivityTimeout),
            new TestCaseData(4004, TransportCloseKind.IdleTimeout),
            new TestCaseData(4005, TransportCloseKind.RoomInactive),
            new TestCaseData(4006, TransportCloseKind.InboundRateLimited),
            new TestCaseData(4007, TransportCloseKind.Kicked),
            new TestCaseData(1009, TransportCloseKind.MessageTooBig),
            new TestCaseData(4999, TransportCloseKind.Unknown),
        };

        [TestCaseSource(nameof(CloseCodeCases))]
        public async Task Receive_ServerClose_MapsCodeToKind(
            int wireCode,
            TransportCloseKind expectedKind
        )
        {
            await using TestWsServer server = TestWsServer.Start(
                "{\"max_outbound_message_size\":8388608}"
            );
            WebSocketTransport transport = new WebSocketTransport();
            await transport.ConnectAsync(ServerUri(server.Port), TestToken());

            TestWsConnection connection = await server.WaitForConnectionAsync(TestToken());
            await connection.SendCloseAsync(wireCode);

            TransportFrame close = await transport.ReceiveAsync(TestToken());

            Assert.That(close.IsClose, Is.True);
            Assert.That(close.Close.Code, Is.EqualTo(wireCode));
            Assert.That(close.Close.Kind, Is.EqualTo(expectedKind));

            TransportClosedException thrown = Assert.ThrowsAsync<TransportClosedException>(
                (Func<Task>)(async () => await transport.ReceiveAsync(TestToken()))
            );
            Assert.That(thrown, Is.Not.Null);
            Assert.That(thrown.Close.Code, Is.EqualTo(wireCode));
        }

        [Test]
        public async Task Connect_ProbesClientConfig_BeforeUpgrade()
        {
            await using TestWsServer server = TestWsServer.Start(
                "{\"max_outbound_message_size\":8388608}"
            );
            WebSocketTransport transport = new WebSocketTransport();
            await transport.ConnectAsync(ServerUri(server.Port), TestToken());

            Assert.That(server.HttpRequestPaths.ToArray(), Does.Contain("/v2/client-config"));
            Assert.That(server.HttpRequestPaths.ToArray(), Does.Contain("/v2/ws"));
        }

        [Test]
        public async Task Receive_SupportsTextAndBinaryFrames()
        {
            await using TestWsServer server = TestWsServer.Start(
                "{\"max_outbound_message_size\":8388608}"
            );
            WebSocketTransport transport = new WebSocketTransport();
            await transport.ConnectAsync(ServerUri(server.Port), TestToken());

            TestWsConnection connection = await server.WaitForConnectionAsync(TestToken());
            string envelope = "{\"type\":\"Authenticated\"}";
            await connection.SendTextAsync(envelope);
            await connection.SendBinaryAsync(new byte[] { 0x9A, 0x00, 0xFF });

            TransportFrame text = await transport.ReceiveAsync(TestToken());
            TransportFrame binary = await transport.ReceiveAsync(TestToken());

            Assert.That(text.IsText, Is.True);
            Assert.That(Encoding.UTF8.GetString(text.Payload.Span), Is.EqualTo(envelope));
            Assert.That(binary.IsText, Is.False);
            Assert.That(binary.Payload.ToArray(), Is.EqualTo(new byte[] { 0x9A, 0x00, 0xFF }));
        }

        [Test]
        public async Task Send_FramesReachTheServerVerbatim()
        {
            await using TestWsServer server = TestWsServer.Start(
                "{\"max_outbound_message_size\":8388608}"
            );
            WebSocketTransport transport = new WebSocketTransport();
            await transport.ConnectAsync(ServerUri(server.Port), TestToken());

            TestWsConnection connection = await server.WaitForConnectionAsync(TestToken());
            byte[] frame = Encoding.UTF8.GetBytes(
                "{\"type\":\"JoinRoom\",\"data\":{\"player_name\":\"Ada\"}}"
            );
            int written = await transport.SendAsync(frame, TestToken());
            TestWsFrame received = await connection.ReceiveFrameAsync(TestToken());

            Assert.That(written, Is.EqualTo(frame.Length));
            Assert.That(received.Opcode, Is.EqualTo(0x1));
            Assert.That(received.Payload, Is.EqualTo(frame));
        }

        [Test]
        public async Task Send_OverOutboundCap_IsRejectedClientSide()
        {
            await using TestWsServer server = TestWsServer.Start(
                "{\"max_outbound_message_size\":8388608}"
            );
            WebSocketTransport transport = new WebSocketTransport(outboundCapBytes: 64);
            await transport.ConnectAsync(ServerUri(server.Port), TestToken());

            byte[] oversized = new byte[65];
            await Assert.ThatAsync(
                (Func<Task>)(async () => await transport.SendAsync(oversized, TestToken())),
                Throws.TypeOf<ArgumentOutOfRangeException>()
            );

            byte[] fits = Encoding.UTF8.GetBytes("{\"type\":\"Ping\"}");
            int written = await transport.SendAsync(fits, TestToken());
            Assert.That(written, Is.EqualTo(fits.Length));
        }

        [Test]
        public async Task Receive_ProbedMaxReceiveBytes_IsEnforced()
        {
            await using TestWsServer server = TestWsServer.Start(
                "{\"max_outbound_message_size\":16}"
            );
            WebSocketTransport transport = new WebSocketTransport();
            await transport.ConnectAsync(ServerUri(server.Port), TestToken());

            TestWsConnection connection = await server.WaitForConnectionAsync(TestToken());
            await connection.SendBinaryAsync(new byte[17]);

            TransportFrame close = await transport.ReceiveAsync(TestToken());

            Assert.That(close.IsClose, Is.True);
            Assert.That(close.Close.Code, Is.EqualTo(1009));
        }

        [Test]
        public async Task Receive_ProbedCap_AtTheBoundary_IsDelivered()
        {
            await using TestWsServer server = TestWsServer.Start(
                "{\"max_outbound_message_size\":5000}"
            );
            WebSocketTransport transport = new WebSocketTransport();
            await transport.ConnectAsync(ServerUri(server.Port), TestToken());

            TestWsConnection connection = await server.WaitForConnectionAsync(TestToken());
            await connection.SendBinaryAsync(new byte[5000]);

            TransportFrame frame = await transport.ReceiveAsync(TestToken());

            Assert.That(frame.IsClose, Is.False);
            Assert.That(frame.Payload.Length, Is.EqualTo(5000));
        }

        [Test]
        public async Task Receive_ProbedCap_ExceedingAcrossBufferBuckets_Closes1009()
        {
            await using TestWsServer server = TestWsServer.Start(
                "{\"max_outbound_message_size\":5000}"
            );
            WebSocketTransport transport = new WebSocketTransport();
            await transport.ConnectAsync(ServerUri(server.Port), TestToken());

            // 5000 grows the receive buffer via a Rent call whose bucket
            // (8192) exceeds the probed cap; 5001 must still be refused.
            TestWsConnection connection = await server.WaitForConnectionAsync(TestToken());
            await connection.SendBinaryAsync(new byte[5001]);

            TransportFrame close = await transport.ReceiveAsync(TestToken());

            Assert.That(close.IsClose, Is.True);
            Assert.That(close.Close.Code, Is.EqualTo(1009));
        }

        [Test]
        public async Task Dispose_DuringConnect_CleansUpAndThrows()
        {
            await using TestWsServer server = TestWsServer.Start(
                "{\"max_outbound_message_size\":8388608}"
            );
            server.ClientConfigGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            WebSocketTransport transport = new WebSocketTransport();

            Task connect = transport.ConnectAsync(ServerUri(server.Port), TestToken());
            await WaitForProbeArrivalAsync(server);
            await transport.DisposeAsync();
            server.ClientConfigGate.TrySetResult(true);

            await Assert.ThatAsync(
                (Func<Task>)(async () => await connect),
                Throws.TypeOf<ObjectDisposedException>()
            );
            await Assert.ThatAsync(
                (Func<Task>)(async () => await transport.DisposeAsync()),
                Throws.Nothing
            );

            // Either no socket reached the wire (expected) or the one that
            // did was torn down promptly. A leaked socket would keep this
            // connection open and the read would hang until its timeout.
            TestWsConnection? connection = await WaitForConnectionOrNullAsync(server);
            if (connection != null)
            {
                byte[] probe = new byte[16];
                CancellationTokenSource closedCts = new CancellationTokenSource(
                    TimeSpan.FromSeconds(5)
                );
                IOException? closed = Assert.ThrowsAsync<IOException>(
                    (Func<Task>)(async () => await connection.ReceiveFrameAsync(closedCts.Token))
                );
                Assert.That(closed, Is.Not.Null);
            }
        }

        private static async Task<TestWsConnection?> WaitForConnectionOrNullAsync(
            TestWsServer server
        )
        {
            CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                return await server.WaitForConnectionAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        private static async Task WaitForProbeArrivalAsync(TestWsServer server)
        {
            for (int attempt = 0; attempt < 200; attempt++)
            {
                foreach (string path in server.HttpRequestPaths.ToArray())
                {
                    if (path.EndsWith("/client-config", StringComparison.Ordinal))
                    {
                        return;
                    }
                }

                await Task.Delay(10);
            }

            Assert.Fail("The client-config probe never reached the server.");
        }

        [Test]
        public async Task Connect_ProbeFailure_StillConnects()
        {
            await using TestWsServer server = TestWsServer.Start(null);
            WebSocketTransport transport = new WebSocketTransport();
            await transport.ConnectAsync(ServerUri(server.Port), TestToken());

            TestWsConnection connection = await server.WaitForConnectionAsync(TestToken());
            await connection.SendTextAsync("{\"type\":\"ProtocolInfo\"}");

            TransportFrame frame = await transport.ReceiveAsync(TestToken());
            Assert.That(
                Encoding.UTF8.GetString(frame.Payload.Span),
                Is.EqualTo("{\"type\":\"ProtocolInfo\"}")
            );
        }

        [Test]
        public async Task Receive_IsSingleReader()
        {
            await using TestWsServer server = TestWsServer.Start(null);
            WebSocketTransport transport = new WebSocketTransport();
            await transport.ConnectAsync(ServerUri(server.Port), TestToken());

            Task<TransportFrame> pending = transport.ReceiveAsync(TestToken()).AsTask();

            await Assert.ThatAsync(
                (Func<Task>)(async () => await transport.ReceiveAsync(TestToken())),
                Throws.InvalidOperationException
            );

            TestWsConnection connection = await server.WaitForConnectionAsync(TestToken());
            await connection.SendTextAsync("{}");
            TransportFrame frame = await pending;
            Assert.That(Encoding.UTF8.GetString(frame.Payload.Span), Is.EqualTo("{}"));
        }

        [Test]
        public async Task Dispose_DuringPendingReceive_SurfacesCloseExactlyOnce()
        {
            await using TestWsServer server = TestWsServer.Start(null);
            WebSocketTransport transport = new WebSocketTransport();
            await transport.ConnectAsync(ServerUri(server.Port), TestToken());

            Task<TransportFrame> pending = transport.ReceiveAsync(TestToken()).AsTask();
            await transport.DisposeAsync();

            TransportFrame close = await pending;
            Assert.That(close.IsClose, Is.True);
            Assert.That(close.Close.Kind, Is.EqualTo(TransportCloseKind.Abnormal));

            // Dispose beats close: post-dispose use is ObjectDisposedException.
            await Assert.ThatAsync(
                (Func<Task>)(async () => await transport.ReceiveAsync(TestToken())),
                Throws.TypeOf<ObjectDisposedException>()
            );
            await Assert.ThatAsync(
                (Func<Task>)(async () => await transport.DisposeAsync()),
                Throws.Nothing
            );
        }

        [Test]
        public async Task Receive_AfterDispose_ThrowsObjectDisposed()
        {
            await using TestWsServer server = TestWsServer.Start(null);
            WebSocketTransport transport = new WebSocketTransport();
            await transport.ConnectAsync(ServerUri(server.Port), TestToken());

            await transport.DisposeAsync();

            await Assert.ThatAsync(
                (Func<Task>)(async () => await transport.ReceiveAsync(TestToken())),
                Throws.TypeOf<ObjectDisposedException>()
            );
            await Assert.ThatAsync(
                (Func<Task>)(
                    async () => await transport.SendAsync(Encoding.UTF8.GetBytes("{}"), TestToken())
                ),
                Throws.TypeOf<ObjectDisposedException>()
            );
            await Assert.ThatAsync(
                (Func<Task>)(async () => await transport.DisposeAsync()),
                Throws.Nothing
            );
        }
    }
}
