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
    /// 2x-ping liveness timeout). Zero allocation on an idle poll: the
    /// single outstanding receive is issued once and re-issued only when a
    /// frame was actually delivered, so idle polls only observe task
    /// completion. Not thread-safe — construct, connect, poll, and drain
    /// on one thread (the game loop's); the ping send completes on the
    /// thread pool and its failure is folded back into the next poll.
    /// </summary>
    public sealed class SignalFishPollingClient
    {
        private const int LivenessCloseCode = 1006;

        /// <summary>Gets the derived connection phase.</summary>
        public ConnectionPhase Phase => _machine.Phase;

        /// <summary>Gets a value indicating whether the session is live (not terminal).</summary>
        public bool IsConnected => _machine.IsConnected;

        /// <summary>Gets a value indicating whether the server authenticated this connection.</summary>
        public bool IsAuthenticated => _machine.IsAuthenticated;

        /// <summary>Gets the confirmed membership; absent outside a confirmed room.</summary>
        public RoomMembership Membership => _machine.Membership;

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
                Teardown(new TransportClose(LivenessCloseCode));
                return 0;
            }

            int frames = 0;
            while (
                frames < _options.MaxFramesPerPoll
                && !_events.IsFull
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

        private void ProcessFrame(TransportFrame frame)
        {
            _lastServerFrameMs = _clock.ElapsedMilliseconds;
            if (frame.IsClose)
            {
                Teardown(frame.Close);
                return;
            }

            if (!frame.IsText || frame.Payload.Length > _options.MaxFrameBytes)
            {
                /*
                    The v2 floor is JSON text; an oversized or binary frame
                    violates the negotiated contract (binary game data
                    arrives with v3).
                */
                _events.TryEnqueue(PollEvent.FromViolation(default(MessageKind), frame.Payload));
                return;
            }

            EnvelopeEvent envelope = EnvelopeReader.Decode(frame.Payload);
            switch (envelope.Kind)
            {
                case EnvelopeEventKind.Message:
                    ProcessMessage(envelope);
                    break;
                case EnvelopeEventKind.UnknownMessage:
                    _events.TryEnqueue(PollEvent.FromUnknown(envelope.TypeText, envelope.Raw));
                    break;
                case EnvelopeEventKind.DecodeFailed:
                    _events.TryEnqueue(
                        PollEvent.FromDecodeFailed(
                            envelope.Error,
                            envelope.ErrorOffset,
                            envelope.Raw
                        )
                    );
                    break;
                default:
                    // Decode never yields other classifications.
                    break;
            }
        }

        private void ProcessMessage(EnvelopeEvent envelope)
        {
            if (SessionEventMapper.TryMap(envelope, out SessionEvent fact))
            {
                if (fact.Kind == SessionEventKind.Disconnected)
                {
                    // Never produced by a frame; defensive.
                    Teardown(new TransportClose(LivenessCloseCode));
                    return;
                }

                _machine.Apply(fact);
                _events.TryEnqueue(BuildSessionEvent(fact, envelope));
                return;
            }

            /*
                Not a session fact: a gameplay/lifecycle payload (enqueued),
                a protocol violation (enqueued), or an absorbed frame with
                no v2 event surface (Pong, client-to-server echoes,
                v3-only kinds). Null means absorbed.
            */
            if (SessionEventMapper.IsSessionFact(envelope.Message))
            {
                _events.TryEnqueue(PollEvent.FromViolation(envelope.Message, envelope.Raw));
                return;
            }

            PollEvent? payloadEvent = TryBuildPayloadEvent(envelope);
            if (payloadEvent is not null)
            {
                _events.TryEnqueue(payloadEvent.GetValueOrDefault());
            }
        }

        private static PollEvent BuildSessionEvent(SessionEvent fact, in EnvelopeEvent envelope)
        {
            switch (fact.Kind)
            {
                case SessionEventKind.Authenticated:
                {
                    /*
                        The payload is informational (rate budgets); the
                        session fact is already applied, so a malformed
                        payload surfaces as defaults, not a violation.
                    */
                    AuthenticatedMessage.TryDecode(envelope.Data, out AuthenticatedMessage auth);
                    return PollEvent.FromAuthenticated(auth, envelope.Raw);
                }
                case SessionEventKind.RoomJoined:
                {
                    RoomJoinedMessage.TryDecode(envelope.Data, out RoomJoinedMessage joined);
                    return PollEvent.FromMembership(
                        PollEventKind.RoomJoined,
                        fact.Membership,
                        joined.Snapshot,
                        envelope.Raw
                    );
                }
                case SessionEventKind.SpectatorJoined:
                {
                    SpectatorJoinedMessage.TryDecode(
                        envelope.Data,
                        out SpectatorJoinedMessage spectatorJoined
                    );
                    return PollEvent.FromMembership(
                        PollEventKind.SpectatorJoined,
                        fact.Membership,
                        spectatorJoined.Snapshot,
                        envelope.Raw
                    );
                }
                case SessionEventKind.Reconnected:
                {
                    ReconnectedMessage.TryDecode(envelope.Data, out ReconnectedMessage reconnect);
                    return PollEvent.FromMembership(
                        PollEventKind.Reconnected,
                        fact.Membership,
                        reconnect.Snapshot,
                        envelope.Raw
                    );
                }
                case SessionEventKind.RoomLeft:
                    return PollEvent.From(PollEventKind.RoomLeft);
                case SessionEventKind.SpectatorLeft:
                {
                    SpectatorLeftMessage.TryDecode(
                        envelope.Data,
                        out SpectatorLeftMessage spectatorLeft
                    );
                    return PollEvent.FromSpectatorLeft(spectatorLeft, envelope.Raw);
                }
                case SessionEventKind.RoomJoinFailed:
                    return BuildFailure(PollEventKind.RoomJoinFailed, envelope);
                case SessionEventKind.SpectatorJoinFailed:
                    return BuildFailure(PollEventKind.SpectatorJoinFailed, envelope);
                case SessionEventKind.ReconnectionFailed:
                    return BuildFailure(PollEventKind.ReconnectionFailed, envelope);
                case SessionEventKind.ServerError:
                    return BuildFailure(PollEventKind.ServerError, envelope);
                default:
                    // TransportReady and Disconnected are synthetic (not frame-driven).
                    return PollEvent.From(PollEventKind.TransportReady);
            }
        }

        private static PollEvent BuildFailure(PollEventKind kind, in EnvelopeEvent envelope)
        {
            /*
                Failure payloads are informational; a malformed payload
                surfaces as defaults (the session fact itself stands).
            */
            FailureMessage.TryDecode(envelope.Data, out FailureMessage failure);
            return PollEvent.FromFailure(kind, failure, envelope.Raw);
        }

        /// <summary>
        /// Builds the payload event for a routed non-session message. Null
        /// means the frame is absorbed (no v2 event surface). Gameplay and
        /// lifecycle payloads treat a decode failure as a wire violation
        /// (nothing session-critical was applied, so the anomaly must not
        /// be masked by default payloads).
        /// </summary>
        private static PollEvent? TryBuildPayloadEvent(EnvelopeEvent envelope)
        {
            switch (envelope.Message)
            {
                case MessageKind.ProtocolInfo:
                    if (
                        !ProtocolInfoMessage.TryDecode(
                            envelope.Data,
                            out ProtocolInfoMessage protocolInfo
                        )
                    )
                    {
                        return PollEvent.FromViolation(envelope.Message, envelope.Raw);
                    }

                    return PollEvent.FromProtocolInfo(protocolInfo, envelope.Raw);
                case MessageKind.LobbyStateChanged:
                    if (
                        !LobbyStateChangedMessage.TryDecode(
                            envelope.Data,
                            out LobbyStateChangedMessage lobby
                        )
                    )
                    {
                        return PollEvent.FromViolation(envelope.Message, envelope.Raw);
                    }

                    return PollEvent.FromLobby(lobby, envelope.Raw);
                case MessageKind.PlayerJoined:
                    if (
                        !PlayerJoinedMessage.TryDecode(
                            envelope.Data,
                            out PlayerJoinedMessage playerJoined
                        )
                    )
                    {
                        return PollEvent.FromViolation(envelope.Message, envelope.Raw);
                    }

                    return PollEvent.FromPlayerJoined(playerJoined, envelope.Raw);
                case MessageKind.PlayerLeft:
                    if (
                        !PlayerLeftMessage.TryDecode(
                            envelope.Data,
                            out PlayerLeftMessage playerLeft
                        )
                    )
                    {
                        return PollEvent.FromViolation(envelope.Message, envelope.Raw);
                    }

                    return PollEvent.FromPlayerLeft(playerLeft.PlayerId, envelope.Raw);
                case MessageKind.PlayerReconnected:
                    if (
                        !PlayerReconnectedMessage.TryDecode(
                            envelope.Data,
                            out PlayerReconnectedMessage playerReconnected
                        )
                    )
                    {
                        return PollEvent.FromViolation(envelope.Message, envelope.Raw);
                    }

                    return PollEvent.FromPlayerReconnected(
                        playerReconnected.PlayerId,
                        envelope.Raw
                    );
                case MessageKind.GameStarting:
                    if (
                        !GameStartingMessage.TryDecode(
                            envelope.Data,
                            out GameStartingMessage gameStart
                        )
                    )
                    {
                        return PollEvent.FromViolation(envelope.Message, envelope.Raw);
                    }

                    return PollEvent.FromGameStart(gameStart, envelope.Raw);
                case MessageKind.AuthorityResponse:
                    if (
                        !AuthorityResponseMessage.TryDecode(
                            envelope.Data,
                            out AuthorityResponseMessage authorityResponse
                        )
                    )
                    {
                        return PollEvent.FromViolation(envelope.Message, envelope.Raw);
                    }

                    return PollEvent.FromAuthorityResponse(authorityResponse, envelope.Raw);
                case MessageKind.AuthorityChanged:
                    if (
                        !AuthorityChangedMessage.TryDecode(
                            envelope.Data,
                            out AuthorityChangedMessage authorityChanged
                        )
                    )
                    {
                        return PollEvent.FromViolation(envelope.Message, envelope.Raw);
                    }

                    return PollEvent.FromAuthorityChanged(authorityChanged, envelope.Raw);
                case MessageKind.GameData:
                    if (!IncomingGameData.TryDecode(envelope.Data, out IncomingGameData gameData))
                    {
                        return PollEvent.FromViolation(envelope.Message, envelope.Raw);
                    }

                    return PollEvent.FromGameData(gameData, envelope.Raw);
                case MessageKind.AuthenticationError:
                    /*
                        Surfaces as a server error: the reason/code payload
                        carries the diagnosis (the INVALID_APP_ID code
                        distinguishes it from a generic Error).
                    */
                    if (!FailureMessage.TryDecode(envelope.Data, out FailureMessage authFailure))
                    {
                        return PollEvent.FromViolation(envelope.Message, envelope.Raw);
                    }

                    return PollEvent.FromFailure(
                        PollEventKind.ServerError,
                        authFailure,
                        envelope.Raw
                    );
                case MessageKind.NewSpectatorJoined:
                    if (
                        !NewSpectatorJoinedMessage.TryDecode(
                            envelope.Data,
                            out NewSpectatorJoinedMessage newSpectator
                        )
                    )
                    {
                        return PollEvent.FromViolation(envelope.Message, envelope.Raw);
                    }

                    return PollEvent.FromNewSpectator(newSpectator, envelope.Raw);
                case MessageKind.SpectatorDisconnected:
                    if (
                        !SpectatorDisconnectedMessage.TryDecode(
                            envelope.Data,
                            out SpectatorDisconnectedMessage spectatorDisconnected
                        )
                    )
                    {
                        return PollEvent.FromViolation(envelope.Message, envelope.Raw);
                    }

                    return PollEvent.FromSpectatorDisconnected(spectatorDisconnected, envelope.Raw);
                default:
                    /*
                        Routed kinds with no v2 event surface: heartbeat
                        replies (Pong refreshes liveness upstream), client-
                        to-server echoes, and v3-only kinds. Absorbed;
                        still one frame of budget.
                    */
                    return null;
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
                TransportClose close = new TransportClose(LivenessCloseCode);
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
                Teardown(new TransportClose(LivenessCloseCode));
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

            /*
                Copy the frame: the async send may outlive the next send's
                Reset on this buffer. One small array per ping (seconds of
                cadence); failure folds back into the next Poll.
            */
            _ = SendPingAsync(_sendBuffer.WrittenSpan.ToArray());
        }

        private async Task SendPingAsync(byte[] frame)
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
