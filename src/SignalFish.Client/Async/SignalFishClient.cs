namespace SignalFish.Client.Async
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using SignalFish.Client.Core;
    using SignalFish.Client.Polling;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.Transport;

    /// <summary>
    /// Thread-safe async client: one background driver loop multiplexes
    /// command sends, frame receives, and heartbeat timing over the
    /// transport while callers queue commands and await events from any
    /// thread. Commands flow through a bounded queue (fail-fast sends
    /// report <see cref="AdmissionError.SendBufferFull"/> when full;
    /// <see cref="SendGameDataReliableAsync"/> waits for a slot instead)
    /// and events through a bounded queue that never drops — a full event
    /// queue pauses the loop, which is the backpressure contract. Admission
    /// (state machine) and queueing are one atomic step per send: a refused
    /// command never touches the wire and never wedges a fence. Frames are
    /// processed strictly in arrival order, so the event stream order is
    /// deterministic under concurrency. Reconnection stays manual (see the
    /// Rust client's recovery procedure); the graceful staged shutdown is
    /// plan M4.3 — <see cref="DisposeAsync"/> currently performs the
    /// immediate teardown.
    /// </summary>
    public sealed class SignalFishClient : IAsyncDisposable
    {
        /// <summary>Gets the derived connection phase.</summary>
        public ConnectionPhase Phase
        {
            get
            {
                lock (_gate)
                {
                    return _machine.Phase;
                }
            }
        }

        /// <summary>Gets a value indicating whether the session is live (not terminal).</summary>
        public bool IsConnected
        {
            get
            {
                lock (_gate)
                {
                    return _machine.IsConnected;
                }
            }
        }

        /// <summary>Gets a value indicating whether the server authenticated this connection.</summary>
        public bool IsAuthenticated
        {
            get
            {
                lock (_gate)
                {
                    return _machine.IsAuthenticated;
                }
            }
        }

        /// <summary>Gets the confirmed membership; absent outside a confirmed room.</summary>
        public RoomMembership Membership
        {
            get
            {
                lock (_gate)
                {
                    return _machine.Membership;
                }
            }
        }

        /// <summary>
        /// Gets one coherent snapshot of the session state (phase fields,
        /// membership identity, latest reconnection token) — prefer it
        /// whenever multiple fields must describe the same instant.
        /// </summary>
        public ClientSnapshot Snapshot
        {
            get
            {
                lock (_gate)
                {
                    return _machine.CreateSnapshot();
                }
            }
        }

        /// <summary>Gets the in-flight directed room operation, if any.</summary>
        public PendingRoomOperation PendingOperation
        {
            get
            {
                lock (_gate)
                {
                    return _machine.PendingOperation;
                }
            }
        }

        /// <summary>Gets the number of events waiting to be dequeued.</summary>
        public int PendingEventCount => _events.Count;

        /// <summary>Gets how many more commands can queue before fail-fast sends report full.</summary>
        public int SendCapacity => _commands.Capacity - _commands.Count;

        /// <summary>Gets the configured command-queue capacity.</summary>
        public int MaxSendCapacity => _commands.Capacity;

        private readonly ITransport _transport;
        private readonly ISignalFishClock _clock;
        private readonly SignalFishClientOptions _options;
        private readonly SignalFishStateMachine _machine = new SignalFishStateMachine();
        private readonly IBoundedQueue<PollEvent> _events;
        private readonly IBoundedQueue<byte[]> _commands;
        private readonly FrameBufferWriter _sendBuffer = new FrameBufferWriter();
        private readonly FrameBufferWriter _pingBuffer = new FrameBufferWriter();
        private readonly SemaphoreSlim _wake = new SemaphoreSlim(0);
        private readonly object _gate = new object();

        private CancellationTokenSource? _shutdownSource;
        private Task? _loopTask;
        private long _lastServerFrameMs;
        private long _lastPingMs;
        private TransportClose _teardownClose;
        private bool _terminal;
        private bool _terminalDelivered;
        private bool _connectCalled;
        private bool _disposed;

        /// <summary>Creates the client over an (unconnected) transport.</summary>
        public SignalFishClient(
            ITransport transport,
            ISignalFishClock clock,
            SignalFishClientOptions? options = null
        )
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _options = options ?? new SignalFishClientOptions();
            _events = new BoundedQueue<PollEvent>(_options.EventCapacity);
            _commands = new BoundedQueue<byte[]>(_options.CommandCapacity);
        }

        /// <summary>
        /// Connects the transport, marks the session transport-ready, and
        /// starts the driver loop. May be called once per client instance;
        /// a failure leaves the session unusable (construct a fresh client
        /// over a fresh transport to retry).
        /// </summary>
        public async Task ConnectAsync(Uri endpoint, CancellationToken ct = default)
        {
            if (endpoint is null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            /*
                The connect slot is claimed before the transport handshake
                (polling-client parity): a failed connect leaves the client
                unusable, as documented, and concurrent calls lose cleanly
                instead of both reaching the transport.
            */
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_connectCalled)
                {
                    throw new InvalidOperationException(
                        "ConnectAsync may be called once per client."
                    );
                }

                _connectCalled = true;
            }

            await _transport.ConnectAsync(endpoint, ct).ConfigureAwait(false);

            CancellationTokenSource shutdown = new CancellationTokenSource();
            lock (_gate)
            {
                if (_disposed)
                {
                    /*
                        Dispose raced the handshake: teardown already ran
                        (the transport was disposed with the session), so
                        just drop the fresh shutdown source.
                    */
                    shutdown.Dispose();
                    return;
                }

                _shutdownSource = shutdown;
                _machine.Apply(SessionEvent.From(SessionEventKind.TransportReady));
                long now = _clock.ElapsedMilliseconds;
                _lastServerFrameMs = now;
                _lastPingMs = now;
            }

            _events.TryEnqueue(PollEvent.TransportReady());
            _loopTask = RunLoopAsync(shutdown.Token);
        }

        /// <summary>
        /// Sends the application handshake. In allowlist mode this must be
        /// the first message; in open mode it is optional but must precede
        /// every application message when used — the server, not this
        /// client, rejects a late or repeated handshake.
        /// </summary>
        public CommandSend SendAuthenticate(in AuthenticateMessage message)
        {
            return QueueCommand(
                ClientCommand.Authenticate,
                static (FrameBufferWriter writer, AuthenticateMessage payload) =>
                    EnvelopeWriter.WriteAuthenticate(writer, payload),
                message
            );
        }

        /// <summary>
        /// Joins (or creates) a room as a player and arms the join fence
        /// until <c>RoomJoined</c> or <c>RoomJoinFailed</c>.
        /// </summary>
        public CommandSend SendJoinRoom(in JoinRoomMessage message)
        {
            return QueueCommand(
                ClientCommand.JoinRoom,
                static (FrameBufferWriter writer, JoinRoomMessage payload) =>
                    EnvelopeWriter.WriteJoinRoom(writer, payload),
                message
            );
        }

        /// <summary>
        /// Joins a room as a spectator and arms the spectator-join fence
        /// until <c>SpectatorJoined</c> or <c>SpectatorJoinFailed</c>.
        /// </summary>
        public CommandSend SendJoinAsSpectator(in JoinAsSpectatorMessage message)
        {
            return QueueCommand(
                ClientCommand.JoinAsSpectator,
                static (FrameBufferWriter writer, JoinAsSpectatorMessage payload) =>
                    EnvelopeWriter.WriteJoinAsSpectator(writer, payload),
                message
            );
        }

        /// <summary>
        /// Reclaims a prior seat with the server-issued token and arms the
        /// reconnect fence until <c>Reconnected</c> or
        /// <c>ReconnectionFailed</c>.
        /// </summary>
        public CommandSend SendReconnect(in ReconnectMessage message)
        {
            return QueueCommand(
                ClientCommand.Reconnect,
                static (FrameBufferWriter writer, ReconnectMessage payload) =>
                    EnvelopeWriter.WriteReconnect(writer, payload),
                message
            );
        }

        /// <summary>Toggles this player's readiness flag; the answer arrives as <c>LobbyStateChanged</c>.</summary>
        public CommandSend SendPlayerReady()
        {
            return QueueCommand(
                ClientCommand.SetReady,
                static (FrameBufferWriter writer, byte _) =>
                    EnvelopeWriter.WritePlayerReady(writer),
                (byte)0
            );
        }

        /// <summary>Requests the game start (readiness and authority rules apply server-side).</summary>
        public CommandSend SendStartGame()
        {
            return QueueCommand(
                ClientCommand.StartGame,
                static (FrameBufferWriter writer, byte _) => EnvelopeWriter.WriteStartGame(writer),
                (byte)0
            );
        }

        /// <summary>
        /// Leaves the current room as a player and arms the leave fence
        /// until <c>RoomLeft</c>.
        /// </summary>
        public CommandSend SendLeaveRoom()
        {
            return QueueCommand(
                ClientCommand.LeaveRoom,
                static (FrameBufferWriter writer, byte _) => EnvelopeWriter.WriteLeaveRoom(writer),
                (byte)0
            );
        }

        /// <summary>
        /// Leaves the current room as a spectator and arms the
        /// spectator-leave fence until <c>SpectatorLeft</c>.
        /// </summary>
        public CommandSend SendLeaveSpectator()
        {
            return QueueCommand(
                ClientCommand.LeaveSpectator,
                static (FrameBufferWriter writer, byte _) =>
                    EnvelopeWriter.WriteLeaveSpectator(writer),
                (byte)0
            );
        }

        /// <summary>
        /// Relays a game-data payload to the other players (player role
        /// only). Fails fast with <see cref="AdmissionError.SendBufferFull"/>
        /// when the command queue is full; nothing is queued and nothing is
        /// dropped.
        /// </summary>
        public CommandSend SendGameData(in GameDataMessage message)
        {
            return QueueCommand(
                ClientCommand.SendGameData,
                static (FrameBufferWriter writer, GameDataMessage payload) =>
                    EnvelopeWriter.WriteGameData(writer, payload),
                message
            );
        }

        /// <summary>
        /// The backpressure-aware counterpart to <see cref="SendGameData"/>:
        /// when the command queue is full, waits for a slot instead of
        /// failing fast, pacing the caller to actual transport throughput —
        /// the recommended shape for high-rate payloads. Returns the same
        /// admission verdicts; if the session ends while waiting the
        /// verdict is <see cref="AdmissionError.NotConnected"/> and the
        /// payload is not queued.
        /// </summary>
        public async Task<CommandSend> SendGameDataReliableAsync(
            GameDataMessage message,
            CancellationToken ct = default
        )
        {
            /*
                The fail-fast attempt is one atomic step under the gate (a
                dead session can never report Admitted); the waiting path
                re-checks the session after the slot is granted, because a
                teardown may complete the queue while the caller parks.
                SignalWake stays outside the gate — a Release can inline the
                loop's continuation, which must not run inside a critical
                section it also locks.
            */
            byte[] frame;
            bool queued;
            lock (_gate)
            {
                ThrowIfDisposed();
                AdmissionError refusal = AdmissionRefusal(ClientCommand.SendGameData);
                if (refusal != default(AdmissionError))
                {
                    return CommandSend.Refused(refusal);
                }

                _sendBuffer.Reset();
                EnvelopeWriter.WriteGameData(_sendBuffer, message);
                frame = _sendBuffer.WrittenSpan.ToArray();
                queued = _commands.TryEnqueue(frame);
            }

            if (queued)
            {
                SignalWake();
                return CommandSend.Admitted;
            }

            if (!await _commands.EnqueueAsync(frame, ct).ConfigureAwait(false))
            {
                return CommandSend.Refused(AdmissionError.NotConnected);
            }

            lock (_gate)
            {
                if (_terminal)
                {
                    return CommandSend.Refused(AdmissionError.NotConnected);
                }
            }

            SignalWake();
            return CommandSend.Admitted;
        }

        /// <summary>
        /// Consumes the oldest pending event without waiting. Returns false
        /// when nothing is buffered right now.
        /// </summary>
        public bool TryDequeueEvent(out PollEvent pollEvent)
        {
            if (_events.TryDequeue(out pollEvent!))
            {
                return true;
            }

            PollEvent? terminal = ConsumeTerminal();
            if (terminal is not null)
            {
                pollEvent = terminal.GetValueOrDefault();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Awaits the next event. Returns null once the session is terminal
        /// and every event was consumed — including the terminal
        /// <c>Disconnected</c>, which is delivered exactly once, last
        /// (synthesized from the teardown close if the event queue had no
        /// room for it). Consumers must drain continuously: the driver loop
        /// pauses on a full event queue, so an abandoned consumer
        /// eventually stalls the session's outbound work.
        /// </summary>
        public async ValueTask<PollEvent?> DequeueEventAsync(CancellationToken ct = default)
        {
            QueueRead<PollEvent> read = await _events.DequeueAsync(ct).ConfigureAwait(false);
            if (read.HasValue)
            {
                return read.Value;
            }

            return ConsumeTerminal();
        }

        /// <summary>
        /// Stops the session (idempotent): tears down with a normal close,
        /// stops the driver loop, and completes the event stream. Sends
        /// after this throw <see cref="ObjectDisposedException"/>.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            Task? loop;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                loop = _loopTask;
            }

            Teardown(new TransportClose(0));
            _shutdownSource?.Cancel();

            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Shutdown is best-effort; the session is already terminal.
                }
            }

            await _events.DisposeAsync().ConfigureAwait(false);
            await _commands.DisposeAsync().ConfigureAwait(false);

            /*
                The shutdown source stays undisposed on purpose: the quiet
                transport dispose may still hold registrations on its token,
                and CTS disposal while callbacks are in flight is a race.
                The source is collected with the client.
            */
            _wake.Dispose();
        }

        private CommandSend QueueCommand<TState>(
            ClientCommand command,
            Action<FrameBufferWriter, TState> write,
            TState state
        )
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                AdmissionError refusal = AdmissionRefusal(command);
                if (refusal != default(AdmissionError))
                {
                    return CommandSend.Refused(refusal);
                }

                _sendBuffer.Reset();
                write(_sendBuffer, state);
                if (!_commands.TryEnqueue(_sendBuffer.WrittenSpan.ToArray()))
                {
                    return CommandSend.Refused(AdmissionError.SendBufferFull);
                }

                /*
                    Arming happens only after the command holds a queue slot,
                    so a full queue can never wedge a fence.
                */
                PendingRoomOperation? fence = SignalFishStateMachine.PendingOperationFor(command);
                if (fence is not null)
                {
                    _machine.Arm(fence.GetValueOrDefault());
                }
            }

            SignalWake();
            return CommandSend.Admitted;
        }

        /// <summary>
        /// Runs admission on the calling thread (under the gate); a refused
        /// command never reaches the encoder or the wire. NotConnected wins
        /// first — the machine alone cannot see the connect call — then the
        /// machine's own precedence applies.
        /// </summary>
        private AdmissionError AdmissionRefusal(ClientCommand command)
        {
            if (!_connectCalled || _terminal)
            {
                return AdmissionError.NotConnected;
            }

            _machine.TryAdmit(command, out AdmissionError refusal);
            return refusal;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SignalFishClient));
            }
        }

        private void SignalWake()
        {
            try
            {
                _wake.Release();
            }
            catch (ObjectDisposedException)
            {
                // Raced with disposal; the loop is already stopping.
            }
        }

        private async Task RunLoopAsync(CancellationToken shutdown)
        {
            Task<TransportFrame>? receive = null;

            /*
                Wake protocol: every queued command releases the semaphore;
                the parked waiter consumes one release, and a release that
                lands while no waiter is parked leaves a count that the
                next refresh's WaitAsync consumes immediately — so no
                signal is ever lost. Exactly one waiter exists at any time:
                refreshed only when completed (a fresh waiter per
                iteration would leave stale waiters competing for
                releases).
            */
            Task wakeWait = _wake.WaitAsync(shutdown);
            try
            {
                while (!_terminal)
                {
                    await DrainCommandsAsync().ConfigureAwait(false);
                    if (_terminal)
                    {
                        break;
                    }

                    await RunHeartbeatAsync().ConfigureAwait(false);
                    if (_terminal)
                    {
                        break;
                    }

                    if (receive is null && !TryIssueReceive(shutdown, out receive))
                    {
                        break;
                    }

                    if (receive.IsCompleted)
                    {
                        TransportFrame frame = ConsumeReceive(receive);
                        receive = null;
                        if (_terminal)
                        {
                            break;
                        }

                        await ProcessFrameAsync(frame).ConfigureAwait(false);
                        continue;
                    }

                    /*
                        Idle: park until a frame lands, a command queues, or
                        the next heartbeat deadline passes. The heartbeat leg
                        doubles as the liveness clock: it is the only wake-up
                        that can pass without producing outbound work.
                    */
                    if (wakeWait.IsCompleted)
                    {
                        wakeWait = _wake.WaitAsync(shutdown);
                    }

                    long now = _clock.ElapsedMilliseconds;
                    long untilPing = (_lastPingMs + _options.HeartbeatIntervalMilliseconds) - now;
                    long untilDeath =
                        (_lastServerFrameMs + _options.HeartbeatTimeoutMilliseconds) - now;
                    long waitTicks = Math.Min(Math.Max(untilPing, 0), Math.Max(untilDeath, 0));
                    int waitMilliseconds = waitTicks > int.MaxValue ? int.MaxValue : (int)waitTicks;
                    Task heartbeat = _clock.DelayAsync(waitMilliseconds, shutdown);
                    await Task.WhenAny(receive, wakeWait, heartbeat).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Disposal cancelled the park legs; teardown already ran.
            }
            finally
            {
                /*
                    Observes the in-flight receive abandoned by exit so a
                    late fault (the transport dying after teardown) can
                    never surface as an unobserved task exception.
                */
                Task<TransportFrame>? abandoned = receive;
                abandoned?.ContinueWith(
                    static finished => _ = finished.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default
                );
            }

            if (!_terminal)
            {
                Teardown(new TransportClose(FramePipeline.LivenessCloseCode));
            }
        }

        private async Task DrainCommandsAsync()
        {
            int budget = _options.CommandsPerWake;
            while (budget-- > 0 && _commands.TryDequeue(out byte[]? frame))
            {
                await SendFrameAsync(frame).ConfigureAwait(false);
                if (_terminal)
                {
                    return;
                }
            }
        }

        private async Task RunHeartbeatAsync()
        {
            long now = _clock.ElapsedMilliseconds;
            if (now - _lastServerFrameMs >= _options.HeartbeatTimeoutMilliseconds)
            {
                // No server frame within the liveness window: the session is dead.
                Teardown(new TransportClose(FramePipeline.LivenessCloseCode));
                return;
            }

            if (now - _lastPingMs >= _options.HeartbeatIntervalMilliseconds)
            {
                _lastPingMs = now;
                _pingBuffer.Reset();
                EnvelopeWriter.WritePing(_pingBuffer);
                await SendFrameAsync(_pingBuffer.WrittenSpan.ToArray()).ConfigureAwait(false);
            }
        }

        private async Task ProcessFrameAsync(TransportFrame frame)
        {
            _lastServerFrameMs = _clock.ElapsedMilliseconds;
            FramePipeline.Translate(frame, _options.MaxFrameBytes, out FrameTranslation translated);
            if (translated.IsClose)
            {
                Teardown(translated.Close);
                return;
            }

            if (translated.HasFact)
            {
                lock (_gate)
                {
                    if (!_terminal)
                    {
                        _machine.Apply(translated.Fact);
                    }
                }
            }

            if (translated.HasEvent)
            {
                /*
                    Backpressure: a full event queue parks the loop here —
                    events are never dropped, and the pause is what trips
                    server-side slow-consumer detection. A false return
                    means the session went terminal mid-wait (teardown
                    completed the queue): the in-flight frame is superseded
                    by the terminal, whose delivery is guaranteed.
                */
                await _events.EnqueueAsync(translated.Event).ConfigureAwait(false);
            }
        }

        private async Task SendFrameAsync(byte[] frame)
        {
            try
            {
                ValueTask<int> send = _transport.SendAsync(frame);
                if (!send.IsCompletedSuccessfully)
                {
                    await send.ConfigureAwait(false);
                }
            }
            catch (Exception failure)
            {
                /*
                    A dead wire surfaces here (close, abort, socket error);
                    the thrown close code is kept when one exists.
                */
                Teardown(
                    failure is TransportClosedException closed
                        ? closed.Close
                        : new TransportClose(FramePipeline.LivenessCloseCode)
                );
            }
        }

        private bool TryIssueReceive(CancellationToken shutdown, out Task<TransportFrame> receive)
        {
            try
            {
                receive = _transport.ReceiveAsync(shutdown).AsTask();
                return true;
            }
            catch (Exception failure)
            {
                receive = null!;
                Teardown(ToClose(failure));
                return false;
            }
        }

        private TransportFrame ConsumeReceive(Task<TransportFrame> receive)
        {
            if (receive.IsCompletedSuccessfully)
            {
                return receive.Result;
            }

            if (receive.IsCanceled)
            {
                /*
                    A canceled receive is the disposal path (the shutdown
                    token) or a transport that self-cancels; either way the
                    session ends here. Teardown is idempotent, so the
                    disposal case (already terminal) is a no-op.
                */
                Teardown(new TransportClose(FramePipeline.LivenessCloseCode));
                return default;
            }

            Exception failure = receive.Exception!.InnerException ?? receive.Exception;
            Teardown(ToClose(failure));
            return default;
        }

        private static TransportClose ToClose(Exception failure)
        {
            return failure is TransportClosedException closed
                ? closed.Close
                : new TransportClose(FramePipeline.LivenessCloseCode);
        }

        /// <summary>
        /// Marks the session terminal exactly once: applies the
        /// disconnected fact, delivers the terminal event, and completes
        /// the event stream. Safe from any thread; the machine mutation is
        /// fenced against concurrent sends and reads.
        /// </summary>
        private void Teardown(TransportClose close)
        {
            lock (_gate)
            {
                if (_terminal)
                {
                    return;
                }

                _terminal = true;
                _teardownClose = close;
                _machine.Apply(SessionEvent.From(SessionEventKind.Disconnected));
            }

            /*
                The terminal event is not enqueued (a full queue could drop
                it): DequeueEventAsync/TryDequeueEvent synthesize it
                one-shot when the completed queue drains to end-of-stream,
                so Disconnected is delivered exactly once, last, in every
                teardown scenario. The staged shutdown (plan M4.3) bounds
                the drain wait properly.
            */
            _events.Complete();
            SignalWake();
            _ = DisposeTransportQuietlyAsync();
        }

        /// <summary>
        /// The one-shot terminal delivery: once the session is terminal and
        /// the completed queue drained, the first consumer to ask receives
        /// the synthesized <c>Disconnected</c>; every later read sees the
        /// plain end-of-stream.
        /// </summary>
        private PollEvent? ConsumeTerminal()
        {
            lock (_gate)
            {
                if (!_terminal || _terminalDelivered)
                {
                    return null;
                }

                _terminalDelivered = true;
                return PollEvent.Disconnected(_teardownClose);
            }
        }

        private async Task DisposeTransportQuietlyAsync()
        {
            try
            {
                await _transport.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Teardown is best-effort; the session is already terminal.
            }
        }
    }
}
