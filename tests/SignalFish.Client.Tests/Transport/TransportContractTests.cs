namespace SignalFish.Client.Tests.Transport
{
    using System;
    using System.Text;
    using System.Threading.Tasks;
    using NUnit.Framework;
    using SignalFish.Client.Transport;

    /// <summary>
    /// M2.1 red-green anchor: the <see cref="ITransport"/> contract (connect
    /// once, send verbatim, receive in order, close surfaced exactly once,
    /// abort-safe, dispose idempotent) asserted through the scripted
    /// in-memory fake. The WebSocket implementation must honor the same
    /// contract; WebSocketTransportTests pins it against real frames.
    /// </summary>
    [TestFixture]
    public class TransportContractTests
    {
        private static readonly Uri FakeUri = new Uri("ws://127.0.0.1:3536/v2/ws");
        private static readonly byte[] PingFrame = Encoding.UTF8.GetBytes("{\"type\":\"Ping\"}");
        private static readonly string[] PingTextSent = { "{\"type\":\"Ping\"}" };

        [Test]
        public async Task ConnectSecondCallIsMisuse()
        {
            FakeTransport transport = new FakeTransport();
            await Assert.ThatAsync(
                (Func<Task>)(async () => await transport.ConnectAsync(FakeUri)),
                Throws.Nothing
            );
            await Assert.ThatAsync(
                (Func<Task>)(async () => await transport.ConnectAsync(FakeUri)),
                Throws.InvalidOperationException
            );
        }

        [Test]
        public async Task SendBeforeConnectIsMisuse()
        {
            FakeTransport transport = new FakeTransport();
            await Assert.ThatAsync(
                (Func<Task>)(async () => await transport.SendAsync(PingFrame)),
                Throws.Exception.InstanceOf<InvalidOperationException>()
            );
        }

        [Test]
        public async Task SendReturnsFullLengthAndDeliversVerbatim()
        {
            FakeTransport transport = new FakeTransport();
            await transport.ConnectAsync(FakeUri);

            int written = await transport.SendAsync(PingFrame);

            Assert.That(written, Is.EqualTo(PingFrame.Length));
            Assert.That(transport.SentText, Is.EqualTo(PingTextSent));
        }

        [Test]
        public async Task ReceiveDrainsScriptedFramesInOrder()
        {
            FakeTransport transport = new FakeTransport();
            await transport.ConnectAsync(FakeUri);
            transport.EnqueueText("{\"type\":\"Authenticated\"}");
            transport.Enqueue(new byte[] { 0x01, 0x02 }, isText: false);

            TransportFrame first = await transport.ReceiveAsync();
            TransportFrame second = await transport.ReceiveAsync();

            Assert.That(first.IsText, Is.True);
            Assert.That(first.IsClose, Is.False);
            Assert.That(
                Encoding.UTF8.GetString(first.Payload.Span),
                Is.EqualTo("{\"type\":\"Authenticated\"}")
            );
            Assert.That(second.IsText, Is.False);
            Assert.That(second.Payload.ToArray(), Is.EqualTo(new byte[] { 0x01, 0x02 }));
        }

        [Test]
        public async Task CloseFrameIsSurfacedExactlyOnce()
        {
            FakeTransport transport = new FakeTransport();
            await transport.ConnectAsync(FakeUri);
            transport.EnqueueClose(4007);

            TransportFrame close = await transport.ReceiveAsync();

            Assert.That(close.IsClose, Is.True);
            Assert.That(close.Close.Code, Is.EqualTo(4007));
            Assert.That(close.Close.Kind, Is.EqualTo(TransportCloseKind.Kicked));

            TransportClosedException thrown = Assert.ThrowsAsync<TransportClosedException>(
                (Func<Task>)(async () => await transport.ReceiveAsync())
            );
            Assert.That(thrown, Is.Not.Null);
            Assert.That(thrown.Close.Code, Is.EqualTo(4007));
        }

        [Test]
        public async Task AbortDuringPendingReceiveSurfacesCloseExactlyOnce()
        {
            FakeTransport transport = new FakeTransport();
            await transport.ConnectAsync(FakeUri);

            Task<TransportFrame> pending = transport.ReceiveAsync().AsTask();
            transport.Abort();

            TransportFrame close = await pending;
            Assert.That(close.IsClose, Is.True);
            Assert.That(close.Close.Kind, Is.EqualTo(TransportCloseKind.Abnormal));

            TransportClosedException thrown = Assert.ThrowsAsync<TransportClosedException>(
                (Func<Task>)(async () => await transport.ReceiveAsync())
            );
            Assert.That(thrown, Is.Not.Null);
        }

        [Test]
        public async Task SendAfterCloseCarriesTheClose()
        {
            FakeTransport transport = new FakeTransport();
            await transport.ConnectAsync(FakeUri);
            transport.EnqueueClose(4000);

            TransportClosedException thrown = Assert.ThrowsAsync<TransportClosedException>(
                (Func<Task>)(async () => await transport.SendAsync(Encoding.UTF8.GetBytes("{}")))
            );
            Assert.That(thrown, Is.Not.Null);
            Assert.That(thrown.Close.Kind, Is.EqualTo(TransportCloseKind.ServerShutdown));
        }

        [Test]
        public async Task DisposeIsIdempotentEvenWhenConcurrent()
        {
            FakeTransport transport = new FakeTransport();
            await transport.ConnectAsync(FakeUri);

            await transport.DisposeAsync();

            Task[] concurrentDisposes = new Task[8];
            for (int i = 0; i < concurrentDisposes.Length; i++)
            {
                concurrentDisposes[i] = transport.DisposeAsync().AsTask();
            }

            await Assert.ThatAsync(
                (Func<Task>)(async () => await Task.WhenAll(concurrentDisposes)),
                Throws.Nothing
            );
            await Assert.ThatAsync(
                (Func<Task>)(async () => await transport.ReceiveAsync()),
                Throws.TypeOf<ObjectDisposedException>()
            );
        }
    }
}
