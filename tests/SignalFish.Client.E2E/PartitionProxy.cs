namespace SignalFish.Client.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading.Tasks;

    /// <summary>
    /// Raw TCP man-in-the-middle for the directional-liveness drill
    /// (conformance checklist item 7): listens on loopback, forwards bytes
    /// to the live server, and can sever each direction independently.
    /// A severed direction reads and discards — local sends keep
    /// "succeeding" while nothing reaches the far end — which is exactly
    /// the one-way-partition semantics the checklist asks for. The
    /// WebSocket session (handshake, frames, close) is opaque payload; the
    /// proxy never parses it.
    /// </summary>
    internal sealed class PartitionProxy : IAsyncDisposable
    {
        /// <summary>Gets whether the client→server direction is severed.</summary>
        internal volatile bool ClientToServerBlocked;

        /// <summary>Gets whether the server→client direction is severed.</summary>
        internal volatile bool ServerToClientBlocked;

        private readonly TcpListener _listener;
        private readonly string _targetHost;
        private readonly int _targetPort;
        private readonly object _gate = new object();
        private readonly List<Task> _relays = new List<Task>();
        private readonly List<TcpClient> _downstreams = new List<TcpClient>();

        private PartitionProxy(string targetHost, int targetPort)
        {
            _targetHost = targetHost;
            _targetPort = targetPort;
            _listener = new TcpListener(IPAddress.Loopback, 0);
        }

        /// <summary>Stops listening and tears down every relayed connection.</summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                _listener.Stop();
                _listener.Dispose();
            }
            catch (Exception)
            {
                // Best-effort shutdown.
            }

            Task[] running;
            lock (_gate)
            {
                foreach (TcpClient downstream in _downstreams)
                {
                    CloseQuietly(downstream);
                }

                running = _relays.ToArray();
            }

            try
            {
                await Task.WhenAll(running)
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Relays end when their sockets die; never block disposal.
            }
        }

        /// <summary>
        /// Starts forwarding to the server behind <paramref name="serverBaseUrl"/>.
        /// </summary>
        internal static PartitionProxy Start(Uri serverBaseUrl)
        {
            PartitionProxy proxy = new PartitionProxy(serverBaseUrl.Host, serverBaseUrl.Port);
            proxy._listener.Start();
            _ = proxy.AcceptLoopAsync();
            return proxy;
        }

        /// <summary>Gets the v2 WebSocket endpoint served through the proxy.</summary>
        internal Uri ClientV2Endpoint()
        {
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            return new Uri($"ws://127.0.0.1:{port}/v2/ws");
        }

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                TcpClient downstream;
                try
                {
                    downstream = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Listener stopped (or fatally broken): no more relays.
                    return;
                }

                Task relay = RelayOneAsync(downstream);
                lock (_gate)
                {
                    _downstreams.Add(downstream);
                    _relays.Add(relay);
                }

                _ = ForgetAsync(downstream, relay);
            }
        }

        private async Task RelayOneAsync(TcpClient downstream)
        {
            TcpClient? upstream = null;
            try
            {
                upstream = new TcpClient();
                await upstream.ConnectAsync(_targetHost, _targetPort).ConfigureAwait(false);
                Task toServer = PumpAsync(
                    downstream.GetStream(),
                    upstream.GetStream(),
                    () => ClientToServerBlocked
                );
                Task toClient = PumpAsync(
                    upstream.GetStream(),
                    downstream.GetStream(),
                    () => ServerToClientBlocked
                );
                await Task.WhenAll(toServer, toClient).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A relay ends when either side closes; that is its job.
            }
            finally
            {
                /*
                    After both pumps ended: disposing the client closes its
                    socket and the stream the pumps read from.
                */
                upstream?.Dispose();
            }
        }

        /// <summary>
        /// Copies one direction until the read end closes. A severed
        /// direction drains and discards, keeping the near end's TCP flow
        /// control open (its sends never fail locally).
        /// </summary>
        private static async Task PumpAsync(
            NetworkStream readEnd,
            NetworkStream writeEnd,
            Func<bool> severed
        )
        {
            byte[] buffer = new byte[16 * 1024];
            while (true)
            {
                int read = await readEnd.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                if (!severed())
                {
                    await writeEnd.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                }
            }
        }

        private async Task ForgetAsync(TcpClient downstream, Task relay)
        {
            await relay.ConfigureAwait(false);
            lock (_gate)
            {
                _downstreams.Remove(downstream);
                _relays.Remove(relay);
            }

            CloseQuietly(downstream);
        }

        private static void CloseQuietly(TcpClient client)
        {
            try
            {
                client.Close();
            }
            catch (Exception)
            {
                // Best-effort shutdown.
            }
        }
    }
}
