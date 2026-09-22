namespace SignalFish.Client.Async
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using SignalFish.Client.Core;
    using SignalFish.Client.Polling;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.Reconnection;
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
    /// (state machine) and queuing are one atomic step per send: a refused
    /// command never touches the wire and never wedges a fence. Frames are
    /// processed strictly in arrival order, so the event stream order is
    /// deterministic under concurrency. Disposing inside a room stages a
    /// graceful shutdown (the role's leave first, then teardown, bounded
    /// by <see cref="SignalFishClientOptions.ShutdownTimeoutMilliseconds"/>).
    /// Recovery stays fully manual by default: persist the seat triple
    /// with <see cref="ReconnectContext.TryCapture"/> at every
    /// join/reconnect, rebuild over a fresh transport, authenticate, then
    /// <see cref="SendReconnect"/> — classify <c>ReconnectionFailed</c>
    /// with <see cref="ReconnectRecovery.Classify"/>. Supplying an opt-in
    /// <see cref="ReconnectPolicy"/> automates the transport-and-
    /// authentication core instead: after a retryable disconnect the
    /// driver waits the deterministic backoff, opens a fresh transport
    /// from the policy factory, re-authenticates, and reclaims a retained
    /// player seat, emitting <c>Reconnecting</c>/<c>ReconnectAbandoned</c>
    /// markers on the event stream.
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

        /// <summary>
        /// Gets how many more commands can queue before fail-fast sends
        /// report full.
        /// </summary>
        public int SendCapacity
        {
            get
            {
                IBoundedQueue<byte[]> commands = _commands;
                return commands.Capacity - commands.Count;
            }
        }

        /// <summary>Gets the configured command-queue capacity.</summary>
        public int MaxSendCapacity => _commands.Capacity;

        private ITransport _transport;
        private readonly ISignalFishClock _clock;
        private readonly SignalFishClientOptions _options;
        private SignalFishStateMachine _machine = new SignalFishStateMachine();
        private readonly IBoundedQueue<PollEvent> _events;
        private IBoundedQueue<byte[]> _commands;
        private readonly FrameBufferWriter _sendBuffer = new FrameBufferWriter();
        private readonly FrameBufferWriter _pingBuffer = new FrameBufferWriter();
        private readonly SemaphoreSlim _wake = new SemaphoreSlim(0);
        private readonly object _gate = new object();

        private CancellationTokenSource? _shutdownSource;
        private Task? _loopTask;
        private Uri? _endpoint;
        private long _lastServerFrameMs;
        private long _lastPingMs;
        private long _disposeRequestedMs = -1;
        private TransportClose _teardownClose;
        private TransportClose _severClose;
        private bool _terminal;
        private bool _terminalDelivered;
        private bool _connectCalled;
        private bool _disposed;
        private bool _severed;
        private bool _disconnectedDelivered;
        private int _reconnectAttempts;
        private ReconnectContext _autoSeat;
        private bool _autoSeatPending;

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
                _endpoint = endpoint;
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
                _events.TryEnqueue(PollEvent.TransportReady());

                /*
                    Stored under the gate so DisposeAsync can never miss it.
                    The loop parks before it needs the gate again.
                */
                _loopTask = RunSessionAsync(shutdown.Token);
            }
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

        /// <summary>
        /// Requests (or relinquishes) the room authority; the answers arrive
        /// as <c>AuthorityResponse</c> and, on a move, <c>AuthorityChanged</c>
        /// (also mirrored in <see cref="Snapshot"/>). Refused for spectators
        /// and for a relinquish while not holding the authority.
        /// </summary>
        public CommandSend SendAuthorityRequest(bool becomeAuthority)
        {
            return QueueCommand(
                ClientCommand.RequestAuthority,
                static (FrameBufferWriter writer, AuthorityRequestMessage payload) =>
                    EnvelopeWriter.WriteAuthorityRequest(writer, payload),
                new AuthorityRequestMessage(becomeAuthority),
                becomeAuthority
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
        /// admission verdicts; if the session ends while waiting, the
        /// verdict is <see cref="AdmissionError.NotConnected"/> (a payload
        /// already granted a slot during the wait stays queued but is
        /// never sent — queued work is discarded with the connection).
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
                if (_terminal || _severed)
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
        /// Stops the session (idempotent). Disposing inside a room stages
        /// a graceful shutdown within the configured budget: the role's
        /// leave goes out first and the teardown waits for its typed
        /// confirmation (or aborts at the budget, whichever comes first —
        /// a pending directed operation or a full command queue skips the
        /// stage). The transport is disposed either way, which is an
        /// abrupt socket close on the wire; only the leave sequencing is
        /// graceful. A zero budget, a session already terminal, and a
        /// client never connected all tear down immediately. The final
        /// <c>Disconnected</c> is delivered exactly once: a policy
        /// session's last connection death delivers its own marker
        /// (synthesized at end-of-stream only if that marker could not be
        /// queued), and disposing a live connection synthesizes one. Sends
        /// after this throw <see cref="ObjectDisposedException"/> from the
        /// moment disposal starts.
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

                /*
                    The leave stage rides the same single-writer queue as
                    every command; a pending directed fence or a full
                    queue skips it (the budget abort covers the wait).
                */
                if (
                    !_terminal
                    && loop is not null
                    && _options.ShutdownTimeoutMilliseconds > 0
                    && _machine.PendingOperation == default(PendingRoomOperation)
                    && _machine.Membership.IsPresent
                    && TryEnqueueGracefulLeave()
                )
                {
                    Volatile.Write(ref _disposeRequestedMs, _clock.ElapsedMilliseconds);
                }
            }

            SignalWake();

            if (Volatile.Read(ref _disposeRequestedMs) >= 0 && loop is not null)
            {
                /*
                    Graceful window: the loop closes itself on the typed
                    confirmation or at its clock deadline; this real-time
                    bound covers a loop parked outside its own checks (a
                    stalled send, an event queue nobody drains).
                */
                Task finished = await Task.WhenAny(
                        loop,
                        Task.Delay(_options.ShutdownTimeoutMilliseconds)
                    )
                    .ConfigureAwait(false);
                if (finished != loop)
                {
                    Finalize(new TransportClose(0));
                }
            }

            if (!_terminal)
            {
                Finalize(new TransportClose(0));
            }

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

        /// <summary>
        /// Encodes and queues the role's leave frame, arming its fence.
        /// Requires the gate, a confirmed membership, and no pending
        /// directed operation; false means the graceful stage is skipped.
        /// </summary>
        private bool TryEnqueueGracefulLeave()
        {
            RoomRole role = _machine.Membership.Role;
            _sendBuffer.Reset();
            if (role == RoomRole.Player)
            {
                EnvelopeWriter.WriteLeaveRoom(_sendBuffer);
            }
            else if (role == RoomRole.Spectator)
            {
                EnvelopeWriter.WriteLeaveSpectator(_sendBuffer);
            }
            else
            {
                return false;
            }

            if (!_commands.TryEnqueue(_sendBuffer.WrittenSpan.ToArray()))
            {
                return false;
            }

            _machine.Arm(
                role == RoomRole.Player
                    ? PendingRoomOperation.LeavePlayer
                    : PendingRoomOperation.LeaveSpectator
            );
            return true;
        }

        /// <summary>
        /// True when the graceful stage is over from the loop's seat: the
        /// leave was confirmed (membership cleared) or the budget ran
        /// out. The caller performs the teardown.
        /// </summary>
        private bool ShouldFinishGracefulClose()
        {
            lock (_gate)
            {
                long requested = Volatile.Read(ref _disposeRequestedMs);
                if (_severed || _terminal || requested < 0)
                {
                    return false;
                }

                return !_machine.Membership.IsPresent
                    || _clock.ElapsedMilliseconds - requested
                        >= _options.ShutdownTimeoutMilliseconds;
            }
        }

        private CommandSend QueueCommand<TState>(
            ClientCommand command,
            Action<FrameBufferWriter, TState> write,
            TState state,
            bool becomeAuthority = true
        )
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                AdmissionError refusal = AdmissionRefusal(command, becomeAuthority);
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
        private AdmissionError AdmissionRefusal(ClientCommand command, bool becomeAuthority = true)
        {
            if (!_connectCalled || _terminal || _severed)
            {
                return AdmissionError.NotConnected;
            }

            _machine.TryAdmit(command, becomeAuthority, out AdmissionError refusal);
            return refusal;
        }

        /// <summary>
        /// The per-round handshake payload: the credentials configured on
        /// the options (app id, SDK identity, optional tenant token), so a
        /// reconnect round re-authenticates exactly like the caller's
        /// explicit first handshake must have.
        /// </summary>
        private AuthenticateMessage BuildHandshakeMessage()
        {
            return new AuthenticateMessage(
                appId: _options.AppId,
                sdkVersion: _options.SdkVersion,
                platform: _options.Platform,
                connectToken: _options.ConnectToken
            );
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

        /// <summary>
        /// The session: one connection round at a time, then — only with an
        /// opt-in <see cref="ReconnectPolicy"/> — deterministic-backoff
        /// rounds over fresh transports until the budget runs out, a
        /// classified terminal close lands, or disposal is requested.
        /// </summary>
        private async Task RunSessionAsync(CancellationToken shutdown)
        {
            try
            {
                bool active = true;
                while (!_terminal)
                {
                    if (active)
                    {
                        await RunConnectionRoundAsync(shutdown).ConfigureAwait(false);
                        if (_terminal)
                        {
                            break;
                        }

                        /*
                            With a policy the stream continues past the death:
                            the Disconnected marker is a regular queued event
                            (in order, backpressure-bound, never dropped).
                            Without one the session is already final and the
                            marker is synthesized at end-of-stream, exactly
                            as before.
                        */
                        if (_options.ReconnectPolicy is not null)
                        {
                            TransportClose markerClose;
                            lock (_gate)
                            {
                                markerClose = _severClose;
                            }

                            bool delivered = await _events
                                .EnqueueAsync(
                                    PollEvent.Disconnected(markerClose),
                                    CancellationToken.None
                                )
                                .ConfigureAwait(false);
                            lock (_gate)
                            {
                                _disconnectedDelivered |= delivered;
                            }
                        }
                    }

                    ReconnectPolicy? policy = _options.ReconnectPolicy;
                    if (policy is null)
                    {
                        // Legacy mode: severing already finalized the session.
                        break;
                    }

                    TransportClose close;
                    bool disposed;
                    lock (_gate)
                    {
                        close = _severClose;
                        disposed = _disposed;
                    }

                    if (disposed || policy.IsTerminalClose(close.Code))
                    {
                        Finalize(close);
                        break;
                    }

                    int attempt;
                    lock (_gate)
                    {
                        attempt = ++_reconnectAttempts;
                    }

                    if (attempt > policy.MaxAttempts)
                    {
                        /*
                            The budget is spent: announce the abandonment on
                            the never-dropping stream, then end it.
                        */
                        await _events
                            .EnqueueAsync(
                                PollEvent.FromReconnectAbandoned(attempt - 1, Describe(close)),
                                CancellationToken.None
                            )
                            .ConfigureAwait(false);
                        Finalize(close);
                        break;
                    }

                    long backoff = policy.BackoffForAttempt(attempt);
                    await _events
                        .EnqueueAsync(
                            PollEvent.FromReconnecting(attempt, backoff),
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
                    if (_terminal)
                    {
                        break;
                    }

                    try
                    {
                        await _clock
                            .DelayAsync((int)Math.Min(backoff, int.MaxValue), shutdown)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Disposal cancelled the backoff; finalize below.
                        break;
                    }

                    active = await ActivateRoundAsync(policy, shutdown).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Disposal cancelled a park leg; teardown already ran.
            }

            if (!_terminal)
            {
                Finalize(new TransportClose(FramePipeline.LivenessCloseCode));
            }
        }

        /// <summary>
        /// One connection round: the existing single-connection loop. Every
        /// exit path severs the connection first (or observes a sever);
        /// session finalization belongs to <see cref="RunSessionAsync"/>.
        /// </summary>
        private async Task RunConnectionRoundAsync(CancellationToken shutdown)
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
                while (!_terminal && !_severed)
                {
                    if (ShouldFinishGracefulClose())
                    {
                        /*
                            The leave was confirmed (or never staged) or
                            the shutdown budget spent: end the session.
                        */
                        Sever(new TransportClose(0));
                        break;
                    }

                    TryIssueAutoReconnect();

                    await DrainCommandsAsync().ConfigureAwait(false);
                    if (_terminal || _severed)
                    {
                        break;
                    }

                    await RunHeartbeatAsync().ConfigureAwait(false);
                    if (_terminal || _severed)
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
                        if (_terminal || _severed)
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

                    /*
                        The graceful window caps the park: an idle loop must
                        wake at the shutdown deadline to close the session.
                    */
                    long requested = Volatile.Read(ref _disposeRequestedMs);
                    if (requested >= 0)
                    {
                        long untilClose = _options.ShutdownTimeoutMilliseconds - (now - requested);
                        waitTicks = Math.Min(waitTicks, Math.Max(untilClose, 0));
                    }

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

            if (!_terminal && !_severed)
            {
                Sever(new TransportClose(FramePipeline.LivenessCloseCode));
            }
        }

        /// <summary>
        /// Consumes a retained seat once the fresh connection reaches the
        /// authenticated phase and no directed operation is in flight: the
        /// same directed reconnect the manual procedure prescribes. The
        /// call never restarts the caller's iteration — a refused reclaim
        /// keeps the seat and retries are paced by later iterations (a
        /// frame, a wake, a heartbeat), never by a same-condition spin.
        /// </summary>
        private void TryIssueAutoReconnect()
        {
            ReconnectMessage? seat = null;
            lock (_gate)
            {
                if (
                    _autoSeatPending
                    && !_severed
                    && !_terminal
                    && !_disposed
                    && _machine.IsAuthenticated
                )
                {
                    if (_machine.Membership.IsPresent)
                    {
                        /*
                            A confirmed membership this round did not reclaim
                            (a deliberate application join) supersedes the
                            retained seat.
                        */
                        _autoSeatPending = false;
                    }
                    else if (_machine.PendingOperation == default(PendingRoomOperation))
                    {
                        _autoSeatPending = false;
                        seat = new ReconnectMessage(
                            _autoSeat.PlayerId.ToString(),
                            _autoSeat.RoomId.ToString(),
                            _autoSeat.Token
                        );
                    }
                }
            }

            if (seat is null)
            {
                return;
            }

            CommandSend verdict;
            try
            {
                verdict = SendReconnect(seat.GetValueOrDefault());
            }
            catch (ObjectDisposedException)
            {
                /*
                    Disposal raced the issue; the attempt is lost with the
                    round, like any command still queued at the death.
                */
                return;
            }

            /*
                A refused reclaim (a full send queue, for example) never
                went out: the seat stays retained for a later iteration
                instead of being dropped with this round.
            */
            if (!verdict.Accepted)
            {
                lock (_gate)
                {
                    _autoSeatPending = true;
                }
            }
        }

        /// <summary>
        /// Opens a reconnect round: builds the fresh transport from the
        /// policy factory, connects it, resets the machine and queues, and
        /// re-authenticates. A failure consumes the attempt and leaves the
        /// decision to the session loop.
        /// </summary>
        private async Task<bool> ActivateRoundAsync(
            ReconnectPolicy policy,
            CancellationToken shutdown
        )
        {
            ITransport fresh;
            try
            {
                fresh = policy.TransportFactory();
            }
            catch (Exception)
            {
                // A broken factory consumes the attempt, like a dead wire.
                return false;
            }

            if (fresh is null)
            {
                return false;
            }

            try
            {
                await fresh.ConnectAsync(_endpoint!, shutdown).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await DisposeQuietlyAsync(fresh).ConfigureAwait(false);
                return false;
            }

            ITransport? raced = null;
            lock (_gate)
            {
                if (_terminal || _disposed)
                {
                    raced = fresh;
                }
                else
                {
                    _transport = fresh;
                    _machine.Apply(SessionEvent.From(SessionEventKind.TransportReady));
                    long now = _clock.ElapsedMilliseconds;
                    _lastServerFrameMs = now;
                    _lastPingMs = now;
                    _severed = false;

                    /*
                        The death marker is per-round: this round owes its
                        own terminal Disconnected if it dies (a marker lost
                        to a concurrent finalization falls back to the
                        synthesized one), and a previous death's close must
                        not leak into this round's bookkeeping.
                    */
                    _disconnectedDelivered = false;
                    _severClose = default;
                    _commands = new BoundedQueue<byte[]>(_options.CommandCapacity);

                    /*
                        The fresh connection re-runs the handshake
                        immediately, so the seat reclaim can follow the
                        Authenticated fact; the handshake carries the
                        configured credentials (M5.3).
                    */
                    _pingBuffer.Reset();
                    EnvelopeWriter.WriteAuthenticate(_pingBuffer, BuildHandshakeMessage());
                    _commands.TryEnqueue(_pingBuffer.WrittenSpan.ToArray());
                }
            }

            if (raced is not null)
            {
                await DisposeQuietlyAsync(raced).ConfigureAwait(false);
                return false;
            }

            /*
                The transport-ready marker rides the never-dropping stream:
                a wedged consumer parks the round here until it drains, and
                disposal completing the queue bounds the wait.
            */
            bool announced = await _events
                .EnqueueAsync(PollEvent.TransportReady(), CancellationToken.None)
                .ConfigureAwait(false);
            if (!announced)
            {
                return false;
            }

            SignalWake();
            return true;
        }

        private static string Describe(TransportClose close)
        {
            return "close "
                + close.Code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private async Task DrainCommandsAsync()
        {
            int budget = _options.CommandsPerWake;
            while (budget-- > 0 && _commands.TryDequeue(out byte[]? frame))
            {
                await SendFrameAsync(frame).ConfigureAwait(false);
                if (_terminal || _severed)
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
                Sever(new TransportClose(FramePipeline.LivenessCloseCode));
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
                Sever(translated.Close);
                return;
            }

            if (translated.HasFact)
            {
                lock (_gate)
                {
                    if (!_severed && !_terminal)
                    {
                        _machine.Apply(translated.Fact);
                        if (_machine.IsAuthenticated)
                        {
                            /*
                                The attempt budget resets whenever a
                                connection reaches the authenticated phase.
                            */
                            _reconnectAttempts = 0;
                        }
                    }
                }
            }

            if (translated.HasEvent)
            {
                /*
                    Backpressure: a full event queue parks the loop here —
                    events are never dropped, and the pause is what trips
                    server-side slow-consumer detection. A false return
                    means the connection ended mid-wait (a sever or the
                    terminal superseded the frame): the in-flight frame is
                    dropped, never reordered past the Disconnected marker.
                */
                lock (_gate)
                {
                    if (_severed || _terminal)
                    {
                        return;
                    }
                }

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
                Sever(
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
                Sever(ToClose(failure));
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
                    connection ends here. Severing is idempotent, so the
                    disposal case is a no-op.
                */
                Sever(new TransportClose(FramePipeline.LivenessCloseCode));
                return default;
            }

            Exception failure = receive.Exception!.InnerException ?? receive.Exception;
            Sever(ToClose(failure));
            return default;
        }

        private static TransportClose ToClose(Exception failure)
        {
            return failure is TransportClosedException closed
                ? closed.Close
                : new TransportClose(FramePipeline.LivenessCloseCode);
        }

        /// <summary>
        /// Ends the current connection exactly once: captures the retained
        /// seat before the session state clears it, discards the dead
        /// connection's queued commands, and prepares the next round. With
        /// a policy the machine resets to a fresh connecting state (the
        /// session continues), and the Disconnected marker is enqueued by
        /// the session loop; without one this is also the session
        /// finalization (the pre-policy behavior, byte for byte). Safe
        /// from any thread.
        /// </summary>
        private void Sever(TransportClose close)
        {
            ITransport dead;
            lock (_gate)
            {
                if (_severed || _terminal)
                {
                    return;
                }

                _severed = true;
                _severClose = close;

                /*
                    The seat outlives a round whose reclaim never went out
                    (a death before the fresh connection authenticated); a
                    fresh capture (a rotated token, a new membership)
                    replaces it, and an issued-but-unanswered reclaim loses
                    it — the documented one-round gap.
                */
                bool captured = ReconnectContext.TryCapture(
                    _machine.CreateSnapshot(),
                    out ReconnectContext seat
                );
                if (captured)
                {
                    _autoSeat = seat;
                }

                _autoSeatPending = captured || _autoSeatPending;

                /*
                    Queued-but-unsent commands are discarded with the dead
                    connection; parked reliable senders unblock with the
                    NotConnected verdict.
                */
                _commands.Complete();
                dead = _transport;

                if (_options.ReconnectPolicy is null)
                {
                    _machine.Apply(SessionEvent.From(SessionEventKind.Disconnected));
                    FinalizeLocked(close);
                }
                else
                {
                    /*
                        The session lives on: a fresh connecting-phase
                        machine keeps Phase/Snapshot truthful between
                        rounds while admission refuses sends (severed).
                        The retained seat rides in _autoSeat, outside the
                        machine.
                    */
                    _machine = new SignalFishStateMachine();
                }
            }

            SignalWake();
            _ = DisposeQuietlyAsync(dead);
        }

        /// <summary>
        /// Marks the session terminal exactly once: applies the final
        /// close, completes the event stream, and reports the actual
        /// connection close when one was observed. Called by the session
        /// orchestration and by disposal; safe from any thread.
        /// </summary>
        private void Finalize(TransportClose close)
        {
            ITransport? dead = null;
            lock (_gate)
            {
                if (_severed)
                {
                    close = _severClose;
                }
                else if (!_terminal)
                {
                    dead = _transport;
                }

                FinalizeLocked(close);
            }

            SignalWake();

            if (dead is not null)
            {
                _ = DisposeQuietlyAsync(dead);
            }
        }

        /// <summary>The terminal mutation; requires the gate, idempotent.</summary>
        private void FinalizeLocked(TransportClose close)
        {
            if (_terminal)
            {
                return;
            }

            if (close.Code == 0 && _severClose.Code != 0)
            {
                // Report the observed death, never a synthetic 0 over it.
                close = _severClose;
            }

            _terminal = true;
            _teardownClose = close;
            _machine.Apply(SessionEvent.From(SessionEventKind.Disconnected));
            _events.Complete();
            _commands.Complete();
        }

        /// <summary>
        /// The one-shot terminal delivery: once the session is terminal and
        /// the completed queue drained, the first consumer to ask receives
        /// the synthesized <c>Disconnected</c> — for a live connection
        /// ended by disposal, or when the dying round's own marker could
        /// not be queued; a delivered round marker already IS the death's
        /// terminal Disconnected. Every later read sees the plain
        /// end-of-stream.
        /// </summary>
        private PollEvent? ConsumeTerminal()
        {
            lock (_gate)
            {
                if (!_terminal || _terminalDelivered || (_disconnectedDelivered && _severed))
                {
                    return null;
                }

                _terminalDelivered = true;
                return PollEvent.Disconnected(_teardownClose);
            }
        }

        private static async Task DisposeQuietlyAsync(ITransport transport)
        {
            try
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Teardown is best-effort; the session is already terminal.
            }
        }
    }
}
