namespace SignalFish.Client.Transport
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A single bidirectional connection to a Signal Fish server. The
    /// protocol layer never sees sockets: implementations hide the platform
    /// transport (ClientWebSocket on .NET, browser WebSocket on WebGL, fakes
    /// in tests).
    /// </summary>
    public interface ITransport : IAsyncDisposable
    {
        /// <summary>
        /// Opens the connection. Must not retry internally; retry policy
        /// belongs to the client/reconnect layer. Connects at most once.
        /// </summary>
        Task ConnectAsync(Uri uri, CancellationToken ct = default);

        /// <summary>
        /// Sends one frame verbatim. Returns the number of bytes written
        /// (always the full frame length) or throws. Single writer: safe to
        /// call concurrently; sends are serialized, never interleaved.
        /// </summary>
        ValueTask<int> SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default);

        /// <summary>
        /// Receives the next frame. Single reader: concurrent calls are a
        /// misuse and throw. Returns the terminal close frame exactly once
        /// (including for abrupt local aborts and network failures, which
        /// surface as abnormal closes rather than exceptions); every call
        /// after that throws <see cref="TransportClosedException"/>.
        /// </summary>
        ValueTask<TransportFrame> ReceiveAsync(CancellationToken ct = default);
    }
}
