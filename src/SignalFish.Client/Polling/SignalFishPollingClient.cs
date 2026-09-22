namespace SignalFish.Client.Polling
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using SignalFish.Client.Core;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.Transport;

    /// <summary>
    /// Frame-driven client for Unity <c>Update()</c>-style loops: every
    /// <see cref="Poll"/> synchronously consumes up to the configured frame
    /// budget from the transport, decodes each frame to at most one
    /// <see cref="PollEvent"/>, applies session facts to the state machine,
    /// and keeps the connection alive on the injected clock (~30 s ping,
    /// 2x-ping liveness timeout). Commands (<c>SendJoinRoom</c> and
    /// siblings) are admitted on the calling thread — a refused command
    /// never touches the wire — encoded, and handed to the transport
    /// fire-and-forget; a failed send folds back into the next poll.
    /// Zero allocation on an idle poll: the single outstanding receive is
    /// issued once and re-issued only when a frame was actually delivered,
    /// so idle polls only observe task completion. Not thread-safe —
    /// construct, connect, send, poll, and drain on one thread (the game
    /// loop's); async sends complete on the thread pool and their failure
    /// is folded back into the next poll. The transport serializes
    /// concurrent sends, so back-to-back commands in one loop tick reach
    /// the wire in dispatch order on runtimes with FIFO wake-ups.
    /// </summary>
    public sealed class SignalFishPollingClient
    {
        /// <summary>Gets the derived connection phase.</summary>
        public ConnectionPhase Phase => _machine.Phase;

        /// <summary>Gets a value indicating whether the session is live (not terminal).</summary>
        public bool IsConnected => _machine.IsConnected;

        /// <summary>Gets a value indicating whether the server authenticated this connection.</summary>
        public bool IsAuthenticated => _machine.IsAuthenticated;

        /// <summary>Gets the confirmed membership; absent outside a confirmed room.</summary>
        public RoomMembership Membership => _machine.Membership;

        /// <summary>
        /// Gets one coherent snapshot of the session state (phase fields,
        /// membership identity, latest reconnection token) — prefer it
        /// whenever multiple fields must describe the same instant.
        /// </summary>
        public ClientSnapshot Snapshot => _machine.CreateSnapshot();

        /// <summary>Gets the in-flight directed room operation, if any.</summary>
        public PendingRoomOperation PendingOperation => _machine.PendingOperation;

        /// <summary>Gets the number of events waiting to be drained.</summary>
        public int PendingEventCount => _events.Count;

        private readonly ITransport _transport;
        private readonly ISignalFishClock _clock;
        private readonly PollingClientOptions _options;
        private readonly SignalFishStateMachine _machine;
        private readonly EventRingBuffer _events;
        private readonly FrameBufferWriter _sendBuffer;

        private Task<TransportFrame>? _pendingReceive;
        private bool _connectCalled;
        private bool _terminal;
        private volatile bool _sendFailed;
        private long _lastServerFrameMs;
        private long _lastPingMs;

        /// <summary>Creates the client over an (unconnected) transport.</summary>
        public SignalFishPollingClient(
            ITransport transport,
            ISignalFishClock clock,
            PollingClientOptions? options = null
        )
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _options = options ?? new PollingClientOptions();
            _machine = new SignalFishStateMachine();
            _events = new EventRingBuffer(_options.EventCapacity);
            _sendBuffer = new FrameBufferWriter();
        }

        /// <summary>
        /// Connects the transport and anchors heartbeat timing. May be
        /// called once per client instance; a failure leaves the client
        /// unusable (construct a fresh client to retry).
        /// </summary>
        public async Task ConnectAsync(Uri endpoint, CancellationToken ct = default)
        {
            if (endpoint is null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            if (_terminal)
            {
                throw new InvalidOperationException(
                    "The polling client session is terminal; construct a new client."
                );
            }

            if (_connectCalled)
            {
                throw new InvalidOperationException("ConnectAsync may be called once per client.");
            }

            _connectCalled = true;
            await _transport.ConnectAsync(endpoint, ct).ConfigureAwait(false);

            _machine.Apply(SessionEvent.From(SessionEventKind.TransportReady));
            _events.TryEnqueue(PollEvent.TransportReady());
            long now = _clock.ElapsedMilliseconds;
            _lastServerFrameMs = now;
            _lastPingMs = now;
            EnsurePendingReceive();
        }

        /// <summary>
        /// Consumes queued transport frames (up to the per-poll budget and
        /// the event-ring capacity), applies their session facts, enqueues
        /// one event per frame, and runs heartbeat/liveness timing.
        /// Returns the number of frames consumed. Inert once terminal.
        /// </summary>
        public int Poll()
        {
            if (_terminal)
            {
                return 0;
            }

            if (_sendFailed)
            {
                Teardown(new TransportClose(FramePipeline.LivenessCloseCode));
                return 0;
            }

            /*
                One ring slot is always reserved for the terminal
                Disconnected event: a teardown on a full ring (heartbeat
                timeout, ping failure, dispose) must never lose the one
                event the game cannot reconstruct from Phase.
            */
            int regularEventCap = _options.EventCapacity - 1;
            int frames = 0;
            while (
                frames < _options.MaxFramesPerPoll
                && _events.Count < regularEventCap
                && TryTakeFrame(out TransportFrame frame)
            )
            {
                frames++;
                ProcessFrame(frame);
                if (_terminal)
                {
                    break;
                }
            }

            if (!_terminal)
            {
                RunHeartbeat();
            }

            return frames;
        }

        /// <summary>
        /// Starts a drain walk over the pending events (oldest first).
        /// Enumerate to consume: <c>foreach (PollEvent ev in
        /// client.DrainEvents()) { ... }</c>; breaking early keeps the
        /// unconsumed tail for the next drain.
        /// </summary>
        public EventDrain DrainEvents()
        {
            return _events.DrainEvents();
        }

        /// <summary>
        /// Tears the session down (idempotent) and disposes the transport.
        /// </summary>
        public ValueTask DisposeAsync()
        {
            Teardown(new TransportClose(0));
            return default;
        }

        /// <summary>
        /// Sends the application handshake. In allowlist mode this must be
        /// the first message; in open mode it is optional but must precede
        /// every application message when used — the server, not this
        /// client, rejects a late or repeated handshake.
        /// </summary>
        public CommandSend SendAuthenticate(in AuthenticateMessage message)
        {
            if (!AdmitForSend(ClientCommand.Authenticate, out AdmissionError refusal))
            {
                return CommandSend.Refused(refusal);
            }

            _sendBuffer.Reset();
            EnvelopeWriter.WriteAuthenticate(_sendBuffer, message);
            DispatchEncodedFrame();
            return CommandSend.Admitted;
        }

        /// <summary>
        /// Joins (or creates) a room as a player and arms the join fence
        /// until <c>RoomJoined</c> or <c>RoomJoinFailed</c>.
        /// </summary>
        public CommandSend SendJoinRoom(in JoinRoomMessage message)
        {
            if (!AdmitForSend(ClientCommand.JoinRoom, out AdmissionError refusal))
            {
                return CommandSend.Refused(refusal);
            }

            _sendBuffer.Reset();
            EnvelopeWriter.WriteJoinRoom(_sendBuffer, message);
            _machine.Arm(PendingRoomOperation.JoinPlayer);
            DispatchEncodedFrame();
            return CommandSend.Admitted;
        }

        /// <summary>
        /// Joins a room as a spectator and arms the spectator-join fence
        /// until <c>SpectatorJoined</c> or <c>SpectatorJoinFailed</c>.
        /// </summary>
        public CommandSend SendJoinAsSpectator(in JoinAsSpectatorMessage message)
        {
            if (!AdmitForSend(ClientCommand.JoinAsSpectator, out AdmissionError refusal))
            {
                return CommandSend.Refused(refusal);
            }

            _sendBuffer.Reset();
            EnvelopeWriter.WriteJoinAsSpectator(_sendBuffer, message);
            _machine.Arm(PendingRoomOperation.JoinSpectator);
            DispatchEncodedFrame();
            return CommandSend.Admitted;
        }

        /// <summary>
        /// Reclaims a prior seat with the server-issued token and arms the
        /// reconnect fence until <c>Reconnected</c> or
        /// <c>ReconnectionFailed</c>.
        /// </summary>
        public CommandSend SendReconnect(in ReconnectMessage message)
        {
            if (!AdmitForSend(ClientCommand.Reconnect, out AdmissionError refusal))
            {
                return CommandSend.Refused(refusal);
            }

            _sendBuffer.Reset();
            EnvelopeWriter.WriteReconnect(_sendBuffer, message);
            _machine.Arm(PendingRoomOperation.ReconnectPlayer);
            DispatchEncodedFrame();
            return CommandSend.Admitted;
        }

        /// <summary>Toggles this player's readiness flag; the answer arrives as <c>LobbyStateChanged</c>.</summary>
        public CommandSend SendPlayerReady()
        {
            return SendPayloadless(ClientCommand.SetReady, EnvelopeWriter.WritePlayerReady);
        }

        /// <summary>Requests the game start (readiness and authority rules apply server-side).</summary>
        public CommandSend SendStartGame()
        {
            return SendPayloadless(ClientCommand.StartGame, EnvelopeWriter.WriteStartGame);
        }

        /// <summary>
        /// Leaves the current room as a player and arms the leave fence
        /// until <c>RoomLeft</c>.
        /// </summary>
        public CommandSend SendLeaveRoom()
        {
            return SendPayloadless(ClientCommand.LeaveRoom, EnvelopeWriter.WriteLeaveRoom);
        }

        /// <summary>
        /// Leaves the current room as a spectator and arms the
        /// spectator-leave fence until <c>SpectatorLeft</c>.
        /// </summary>
        public CommandSend SendLeaveSpectator()
        {
            return SendPayloadless(
                ClientCommand.LeaveSpectator,
                EnvelopeWriter.WriteLeaveSpectator
            );
        }

        /// <summary>Relays a game-data payload to the other players (player role only).</summary>
        public CommandSend SendGameData(in GameDataMessage message)
        {
            if (!AdmitForSend(ClientCommand.SendGameData, out AdmissionError refusal))
            {
                return CommandSend.Refused(refusal);
            }

            _sendBuffer.Reset();
            EnvelopeWriter.WriteGameData(_sendBuffer, message);
            DispatchEncodedFrame();
            return CommandSend.Admitted;
        }

        /// <summary>
        /// Synchronous admission on the poll thread; a refused command never
        /// touches the wire. Sends before <see cref="ConnectAsync"/> are
        /// refused (the machine alone cannot see the connect call).
        /// </summary>
        private bool AdmitForSend(ClientCommand command, out AdmissionError refusal)
        {
            if (!_connectCalled || _terminal)
            {
                refusal = AdmissionError.NotConnected;
                return false;
            }

            return _machine.TryAdmit(command, out refusal);
        }

        /// <summary>Encodes and dispatches a payload-less command.</summary>
        private CommandSend SendPayloadless(ClientCommand command, Action<FrameBufferWriter> write)
        {
            if (!AdmitForSend(command, out AdmissionError refusal))
            {
                return CommandSend.Refused(refusal);
            }

            _sendBuffer.Reset();
            write(_sendBuffer);
            PendingRoomOperation? fence = SignalFishStateMachine.PendingOperationFor(command);
            if (fence is not null)
            {
                _machine.Arm(fence.GetValueOrDefault());
            }

            DispatchEncodedFrame();
            return CommandSend.Admitted;
        }

        /// <summary>
        /// Hands the encoded frame to the transport fire-and-forget: the
        /// copy outlives the next encode on this buffer, and a dead wire
        /// folds into the next poll.
        /// </summary>
        private void DispatchEncodedFrame()
        {
            _ = SendFrameAsync(_sendBuffer.WrittenSpan.ToArray());
        }

        private void ProcessFrame(TransportFrame frame)
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
                _machine.Apply(translated.Fact);
            }

            if (translated.HasEvent)
            {
                _events.TryEnqueue(translated.Event);
            }
        }

        private bool TryTakeFrame(out TransportFrame frame)
        {
            frame = default;
            if (!EnsurePendingReceive())
            {
                return false;
            }

            Task<TransportFrame> pending = _pendingReceive!;
            if (!pending.IsCompleted)
            {
                return false;
            }

            _pendingReceive = null;
            if (!pending.IsCompletedSuccessfully)
            {
                /*
                    Faulted/canceled receive: the close path (or a socket
                    failure). The close frame delivery and the thrown
                    TransportClosedException carry the same code.
                */
                TransportClose close = new TransportClose(FramePipeline.LivenessCloseCode);
                if (pending.Exception?.InnerException is TransportClosedException closed)
                {
                    close = closed.Close;
                }

                Teardown(close);
                return false;
            }

            /*
                Delivered: the next receive is issued lazily (by the next
                take), never eagerly here — the frame may be the close, and
                issuing past it would throw mid-poll.
            */
            frame = pending.Result;
            return true;
        }

        /// <summary>
        /// Issues the single outstanding receive when none is in flight.
        /// A transport that throws synchronously (already closed) tears the
        /// session down with the thrown close code.
        /// </summary>
        private bool EnsurePendingReceive()
        {
            if (_pendingReceive is not null)
            {
                return true;
            }

            try
            {
                _pendingReceive = _transport.ReceiveAsync().AsTask();
                return true;
            }
            catch (TransportClosedException closed)
            {
                Teardown(closed.Close);
                return false;
            }
        }

        private void RunHeartbeat()
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
                SendPing();
            }
        }

        private void SendPing()
        {
            _sendBuffer.Reset();
            EnvelopeWriter.WritePing(_sendBuffer);
            DispatchEncodedFrame();
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
            catch (Exception)
            {
                /*
                    A dead wire surfaces here (close, abort, socket error).
                    Record only: teardown runs on the poll thread so the
                    session state never mutates off-thread.
                */
                _sendFailed = true;
            }
        }

        /// <summary>
        /// Observes the in-flight receive abandoned by teardown so a late
        /// fault can never surface as an unobserved task exception.
        /// </summary>
        private void ObserveAbandonedReceive()
        {
            Task<TransportFrame>? abandoned = Interlocked.Exchange(ref _pendingReceive, null);
            abandoned?.ContinueWith(
                static finished => _ = finished.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default
            );
        }

        private void Teardown(TransportClose close)
        {
            if (_terminal)
            {
                return;
            }

            _terminal = true;
            _machine.Apply(SessionEvent.From(SessionEventKind.Disconnected));
            _events.TryEnqueue(PollEvent.Disconnected(close));
            _ = DisposeTransportQuietlyAsync();
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
