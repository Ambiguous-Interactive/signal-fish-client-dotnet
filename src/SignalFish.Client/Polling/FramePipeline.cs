namespace SignalFish.Client.Polling
{
    using System;
    using SignalFish.Client.Core;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.Transport;
    using SignalFish.Client.V3;

    /// <summary>
    /// One inbound frame's translation: at most one
    /// <see cref="PollEvent"/> to surface plus an optional session fact to
    /// apply (the machine state leads the event stream), plus an optional
    /// delivery-accountability violation that surfaces ahead of both. A
    /// close frame (server-typed or locally synthesized) instead carries
    /// the teardown.
    /// </summary>
    internal struct FrameTranslation
    {
        /// <summary>True when the frame (or a defensive fact) ends the session.</summary>
        internal bool IsClose;

        /// <summary>The teardown close; meaningful only when <see cref="IsClose"/> is true.</summary>
        internal TransportClose Close;

        /// <summary>
        /// True when <see cref="IsClose"/> is the violation policy's
        /// session-terminal teardown (never reconnected).
        /// </summary>
        internal bool PolicyTeardown;

        /// <summary>True when a delivery violation must surface ahead of the fact/event.</summary>
        internal bool HasViolation;

        /// <summary>The violation event; meaningful only when <see cref="HasViolation"/> is true.</summary>
        internal PollEvent Violation;

        /// <summary>True when a session fact must be applied to the state machine.</summary>
        internal bool HasFact;

        /// <summary>The session fact; meaningful only when <see cref="HasFact"/> is true.</summary>
        internal SessionEvent Fact;

        /// <summary>True when an event must be surfaced to the consumer.</summary>
        internal bool HasEvent;

        /// <summary>The event to surface; meaningful only when <see cref="HasEvent"/> is true.</summary>
        internal PollEvent Event;
    }

    /// <summary>
    /// The one inbound frame → event translation shared by both clients
    /// (async driver and polling loop), so decode routing, violation
    /// policy, and payload surfacing have exactly one implementation.
    /// Pure in the frame → translation sense: session-event application
    /// and event delivery stay with the caller; the delivery gate and the
    /// state machine are the two per-session components fed here, and only
    /// their own state moves.
    /// </summary>
    internal static class FramePipeline
    {
        /// <summary>The local close code when the client declares the session dead itself.</summary>
        internal const int LivenessCloseCode = 1006;

        /// <summary>
        /// The <c>error_code</c> token that arms the unsupported-format
        /// advisory causality check (v3 delivery accounting).
        /// </summary>
        private const string UnsupportedGameDataFormatToken = "UNSUPPORTED_GAME_DATA_FORMAT";

        /// <summary>Translates one inbound transport frame.</summary>
        internal static void Translate(
            in TransportFrame frame,
            int maxFrameBytes,
            DeliveryGate gate,
            SignalFishStateMachine machine,
            out FrameTranslation translated
        )
        {
            translated = default;
            if (frame.IsClose)
            {
                translated.IsClose = true;
                translated.Close = frame.Close;
                return;
            }

            if (frame.Payload.Length > maxFrameBytes)
            {
                /*
                    The v2 floor is JSON text under a per-frame byte bound;
                    an oversized frame violates the negotiated contract
                    before any decode.
                */
                translated.HasEvent = true;
                translated.Event = PollEvent.FromViolation(default(MessageKind), frame.Payload);
                return;
            }

            if (!frame.IsText)
            {
                /*
                    Binary game data rides v3's negotiated non-JSON
                    encoding; without it a binary frame violates the
                    negotiated contract.
                */
                TranslateBinaryGameData(frame, gate, ref translated);
                return;
            }

            EnvelopeEvent envelope = EnvelopeReader.Decode(frame.Payload);
            switch (envelope.Kind)
            {
                case EnvelopeEventKind.Message:
                    TranslateMessage(envelope, gate, machine, ref translated);
                    break;
                case EnvelopeEventKind.UnknownMessage:
                    translated.HasEvent = true;
                    translated.Event = PollEvent.FromUnknown(envelope.TypeText, envelope.Raw);
                    break;
                case EnvelopeEventKind.DecodeFailed:
                    translated.HasEvent = true;
                    translated.Event = PollEvent.FromDecodeFailed(
                        envelope.Error,
                        envelope.ErrorOffset,
                        envelope.Raw
                    );
                    break;
                default:
                    // Decode never yields other classifications.
                    break;
            }
        }

        /// <summary>
        /// Translates one physical binary frame. Admission runs first
        /// (negotiation settled, non-JSON encoding negotiated); then the
        /// strict MessagePack decode (failure → bounded
        /// <see cref="PollEventKind.DecodeFailed"/>, never a policy
        /// teardown by itself); then the representation check (the
        /// envelope's embedded encoding must equal the negotiated token);
        /// finally the game-data gate — stamps, cursors, quarantine —
        /// exactly like a JSON <c>GameData</c>.
        /// </summary>
        private static void TranslateBinaryGameData(
            in TransportFrame frame,
            DeliveryGate gate,
            ref FrameTranslation translated
        )
        {
            if (!gate.TryAdmitBinaryFrame(out GateVerdict admission, out _))
            {
                /*
                    The refusal applies per policy; under Observe the
                    violation surfaces but the frame still stops here — an
                    out-of-contract frame is never decoded into game data,
                    and its bytes can never move the cursors. The session
                    keeps flowing either way.
                */
                ApplyVerdict(default(MessageKind), frame.Payload, admission, ref translated);
                return;
            }

            if (
                !BinaryGameDataFrame.TryDecode(
                    frame.Payload,
                    gate.IsProtocolV3,
                    out BinaryGameDataFrame decoded,
                    out DecodeError error,
                    out int errorOffset
                )
            )
            {
                translated.HasEvent = true;
                translated.Event = PollEvent.FromDecodeFailed(error, errorOffset, frame.Payload);
                return;
            }

            /*
                Binary carries no class metadata: reliable by definition,
                gated through the same engine (paired stamps on v3, the
                bare shape on v2).
            */
            bool protocolV3 = gate.IsProtocolV3;
            IncomingGameData gameData = new IncomingGameData(
                decoded.FromPlayer,
                decoded.Payload,
                GameDataClass.Reliable,
                classPresent: false,
                key: 0,
                keyPresent: false,
                protocolV3 ? decoded.Seq : null,
                protocolV3 ? decoded.Epoch : null
            );

            if (decoded.Format != gate.NegotiatedEncoding)
            {
                /*
                    The envelope names a different encoding than the
                    negotiation settled: the refusal applies, and under
                    Observe the payload still surfaces — informational,
                    never advancing the cursors (Rust parity).
                */
                GateVerdict representation = gate.RefuseRepresentation(decoded.Format);
                if (
                    ApplyVerdict(
                        default(MessageKind),
                        frame.Payload,
                        representation,
                        ref translated
                    )
                )
                {
                    return;
                }

                translated.HasEvent = true;
                translated.Event = PollEvent.FromGameData(gameData, frame.Payload);
                return;
            }

            GateVerdict verdict = gate.RecordGameData(gameData, out _);
            if (ApplyVerdict(default(MessageKind), frame.Payload, verdict, ref translated))
            {
                return;
            }

            translated.HasEvent = true;
            translated.Event = PollEvent.FromGameData(gameData, frame.Payload);
        }

        private static void TranslateMessage(
            in EnvelopeEvent envelope,
            DeliveryGate gate,
            SignalFishStateMachine machine,
            ref FrameTranslation translated
        )
        {
            if (SessionEventMapper.TryMap(envelope, out SessionEvent fact))
            {
                if (fact.Kind == SessionEventKind.Disconnected)
                {
                    // Never produced by a frame; defensive.
                    translated.IsClose = true;
                    translated.Close = new TransportClose(LivenessCloseCode);
                    return;
                }

                /*
                    A replayed plan whose generation was already superseded
                    must never overwrite the current plan (latest-wins by
                    authority, not by arrival). Deliberately outside the
                    violation policy, like the routed-fact wire violation
                    below: the plan is rejected as session input, the
                    machine stays put, and the anomaly surfaces.
                */
                if (
                    fact.Kind == SessionEventKind.SessionPlan
                    && machine.IsSessionPlanSuperseded(fact.Plan.Generation)
                )
                {
                    translated.HasEvent = true;
                    translated.Event = PollEvent.FromViolation(
                        envelope.Message,
                        "SessionPlan generation "
                            + fact.Plan.Generation
                            + " was already superseded",
                        envelope.Raw
                    );
                    return;
                }

                /*
                    The mapper validates membership-bearing payloads with
                    its own decoder; the gate feed re-decodes snapshot
                    bearers with the full typed decoder, which can fail
                    where the mapper's pass cannot (a Reconnected frame
                    with malformed sender_watermarks maps fine but fails
                    the typed decode). A default struct must never reach
                    the gate as authoritative input: a failed re-decode is
                    the routed-fact wire violation.
                */
                if (
                    !FeedSessionFact(
                        gate,
                        fact,
                        envelope,
                        out GateVerdict factVerdict,
                        out string? factRefusal
                    )
                )
                {
                    /*
                        Deliberately outside the violation policy: a frame
                        that cannot be trusted as session input is rejected
                        outright (the fact never applies), whatever the
                        configured reaction — matching the routed-fact
                        wire-violation precedent.
                    */
                    translated.HasEvent = true;
                    translated.Event = PollEvent.FromViolation(
                        envelope.Message,
                        factRefusal,
                        envelope.Raw
                    );
                    return;
                }

                if (ApplyVerdict(envelope, factVerdict, ref translated))
                {
                    return;
                }

                translated.HasFact = true;
                translated.Fact = fact;
                translated.HasEvent = true;
                translated.Event = BuildSessionEvent(fact, envelope);
                return;
            }

            /*
                Not a session fact: a gameplay/lifecycle payload (enqueued),
                a protocol violation (enqueued), or an absorbed frame with
                no v2 event surface (Pong, client-to-server echoes,
                operation results). Null means absorbed.
            */
            if (SessionEventMapper.IsSessionFact(envelope.Message))
            {
                translated.HasEvent = true;
                translated.Event = PollEvent.FromViolation(envelope.Message, envelope.Raw);
                return;
            }

            PollEvent? payloadEvent = TryBuildPayloadEvent(
                envelope,
                gate,
                machine,
                out GateVerdict verdict
            );
            if (ApplyVerdict(envelope, verdict, ref translated))
            {
                return;
            }

            if (payloadEvent is not null)
            {
                translated.HasEvent = true;
                translated.Event = payloadEvent.GetValueOrDefault();
            }
        }

        /// <summary>
        /// Feeds the frames the delivery gate validates at the session-fact
        /// layer. <c>ProtocolInfo</c> settles the negotiated version (the
        /// per-connection engine swap); join/reconnect snapshots are the
        /// authoritative rebaselines; room exits reset the cursors; the
        /// unsupported-format error arms its causality check. False means
        /// the frame's payload failed the typed re-decode — the caller
        /// surfaces the routed-fact wire violation instead (<paramref
        /// name="verdict"/> is then a default, proceed verdict).
        /// </summary>
        private static bool FeedSessionFact(
            DeliveryGate gate,
            in SessionEvent fact,
            in EnvelopeEvent envelope,
            out GateVerdict verdict,
            out string? refusal
        )
        {
            verdict = default;
            refusal = null;
            switch (fact.Kind)
            {
                case SessionEventKind.ProtocolInfo:
                    if (
                        !ProtocolInfoMessage.TryDecode(
                            envelope.Data,
                            out ProtocolInfoMessage protocolInfo
                        )
                    )
                    {
                        return false;
                    }

                    return gate.OnProtocolInfo(
                        protocolInfo.ProtocolVersion,
                        protocolInfo.GameDataFormats,
                        out refusal
                    );
                case SessionEventKind.RoomJoined:
                    if (!RoomJoinedMessage.TryDecode(envelope.Data, out RoomJoinedMessage joined))
                    {
                        return false;
                    }

                    verdict = gate.RebaselineSnapshot(joined.Snapshot.CurrentPlayers);
                    return true;
                case SessionEventKind.SpectatorJoined:
                    if (
                        !SpectatorJoinedMessage.TryDecode(
                            envelope.Data,
                            out SpectatorJoinedMessage spectatorJoined
                        )
                    )
                    {
                        return false;
                    }

                    verdict = gate.RebaselineSnapshot(spectatorJoined.Snapshot.CurrentPlayers);
                    return true;
                case SessionEventKind.Reconnected:
                    if (
                        !ReconnectedMessage.TryDecode(
                            envelope.Data,
                            out ReconnectedMessage reconnect
                        )
                    )
                    {
                        return false;
                    }

                    verdict = gate.RebaselineReconnected(
                        reconnect.Snapshot.CurrentPlayers,
                        reconnect.SenderWatermarks
                    );
                    return true;
                case SessionEventKind.RoomLeft:
                case SessionEventKind.SpectatorLeft:
                    gate.ResetRoom();
                    return true;
                case SessionEventKind.ServerError:
                    if (
                        FailureMessage.TryDecode(envelope.Data, out FailureMessage failure)
                        && failure.ErrorCode == UnsupportedGameDataFormatToken
                    )
                    {
                        verdict = gate.ObserveUnsupportedFormatError();
                    }

                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Applies a gate verdict to the translation: surfaces the
        /// violation (when one was raised), converts a policy teardown
        /// into the close, and reports whether the frame is suppressed.
        /// </summary>
        private static bool ApplyVerdict(
            in EnvelopeEvent envelope,
            in GateVerdict verdict,
            ref FrameTranslation translated
        )
        {
            return ApplyVerdict(envelope.Message, envelope.Raw, verdict, ref translated);
        }

        /// <summary>
        /// Applies a gate verdict to the translation: surfaces the
        /// violation (when one was raised), converts a policy teardown
        /// into the close, and reports whether the frame is suppressed.
        /// The envelope-free form serves binary frames (no envelope:
        /// default message kind, the frame bytes as the raw payload).
        /// </summary>
        private static bool ApplyVerdict(
            MessageKind messageKind,
            ReadOnlyMemory<byte> raw,
            in GateVerdict verdict,
            ref FrameTranslation translated
        )
        {
            if (verdict.Diagnostic != null)
            {
                translated.HasViolation = true;
                translated.Violation = PollEvent.FromViolation(
                    messageKind,
                    verdict.Diagnostic,
                    raw
                );
            }

            if (verdict.Teardown)
            {
                translated.IsClose = true;
                translated.PolicyTeardown = true;
                translated.Close = new TransportClose(LivenessCloseCode);
                return true;
            }

            return verdict.Suppress;
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
                case SessionEventKind.AuthorityChanged:
                {
                    /*
                        The session fact already validated the decode, so the
                        informational re-decode cannot fail.
                    */
                    AuthorityChangedMessage.TryDecode(
                        envelope.Data,
                        out AuthorityChangedMessage authorityChanged
                    );
                    return PollEvent.FromAuthorityChanged(authorityChanged, envelope.Raw);
                }
                case SessionEventKind.ProtocolInfo:
                {
                    /*
                        Same as above: the fact's decode validated the
                        payload, so the surfaced re-decode cannot fail.
                    */
                    ProtocolInfoMessage.TryDecode(
                        envelope.Data,
                        out ProtocolInfoMessage protocolInfo
                    );
                    return PollEvent.FromProtocolInfo(protocolInfo, envelope.Raw);
                }
                case SessionEventKind.SessionPlan:
                {
                    /*
                        Same as above: the fact's decode validated the
                        payload, so the surfaced re-decode cannot fail.
                    */
                    SessionPlanMessage.TryDecode(envelope.Data, out SessionPlanMessage plan);
                    return PollEvent.FromSessionPlan(plan, envelope.Raw);
                }
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
        /// be masked by default payloads). The machine's mesh roster is fed
        /// here (the departed/re-added peer bookkeeping behind the inbound
        /// signal fence), and a <c>Signal</c> racing its plan is absorbed
        /// as a benign relay-ordering race.
        /// </summary>
        private static PollEvent? TryBuildPayloadEvent(
            EnvelopeEvent envelope,
            DeliveryGate gate,
            SignalFishStateMachine machine,
            out GateVerdict verdict
        )
        {
            verdict = default;
            switch (envelope.Message)
            {
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

                    verdict = gate.NotePlayerJoined(playerJoined.Player);
                    if (verdict.Suppress)
                    {
                        return null;
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

                    verdict = gate.NotePlayerLeft(
                        playerLeft.PlayerId,
                        playerLeft.Epoch,
                        playerLeft.FinalSeq
                    );
                    if (verdict.Suppress)
                    {
                        return null;
                    }

                    /*
                        The roster feed rides the accepted frame (Rust parity:
                        a gate-refused departure updates no client state —
                        under Observe the violation surfaces and the frame
                        still proceeds).
                    */
                    machine.OnPlayerLeft(playerLeft.PlayerId);
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

                    verdict = gate.NotePlayerReconnected(
                        playerReconnected.PlayerId,
                        playerReconnected.Epoch
                    );
                    if (verdict.Suppress)
                    {
                        return null;
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
                case MessageKind.GameData:
                    if (!IncomingGameData.TryDecode(envelope.Data, out IncomingGameData gameData))
                    {
                        return PollEvent.FromViolation(envelope.Message, envelope.Raw);
                    }

                    verdict = gate.RecordGameData(gameData, out _);
                    if (verdict.Suppress)
                    {
                        return null;
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
                case MessageKind.DeliveryReport:
                    if (
                        !DeliveryReportMessage.TryDecode(
                            envelope.Data,
                            out DeliveryReportMessage deliveryReport
                        )
                    )
                    {
                        return PollEvent.FromViolation(envelope.Message, envelope.Raw);
                    }

                    verdict = gate.RecordReport(deliveryReport);
                    if (verdict.Suppress)
                    {
                        return null;
                    }

                    return PollEvent.FromDeliveryReport(deliveryReport, envelope.Raw);
                case MessageKind.RelayStats:
                    if (
                        !RelayStatsMessage.TryDecode(
                            envelope.Data,
                            out RelayStatsMessage relayStats
                        )
                    )
                    {
                        return PollEvent.FromViolation(envelope.Message, envelope.Raw);
                    }

                    verdict = gate.RecordRelayStats(relayStats);
                    if (verdict.Suppress)
                    {
                        return null;
                    }

                    return PollEvent.FromRelayStats(relayStats, envelope.Raw);
                case MessageKind.GoingAway:
                    if (!GoingAwayMessage.TryDecode(envelope.Data, out GoingAwayMessage goingAway))
                    {
                        return PollEvent.FromViolation(envelope.Message, envelope.Raw);
                    }

                    return PollEvent.FromGoingAway(goingAway, envelope.Raw);
                case MessageKind.NewPeer:
                    if (!NewPeerMessage.TryDecode(envelope.Data, out NewPeerMessage newPeer))
                    {
                        return PollEvent.FromViolation(envelope.Message, envelope.Raw);
                    }

                    machine.OnNewPeer(newPeer.PeerId);
                    return PollEvent.FromNewPeer(newPeer, envelope.Raw);
                case MessageKind.PeerTransportStatus:
                    if (
                        !PeerTransportStatusMessage.TryDecode(
                            envelope.Data,
                            out PeerTransportStatusMessage peerTransportStatus
                        )
                    )
                    {
                        return PollEvent.FromViolation(envelope.Message, envelope.Raw);
                    }

                    return PollEvent.FromPeerTransportStatus(peerTransportStatus, envelope.Raw);
                case MessageKind.Signal:
                    if (
                        !IncomingSignalMessage.TryDecode(
                            envelope.Data,
                            out IncomingSignalMessage signal
                        )
                    )
                    {
                        return PollEvent.FromViolation(envelope.Message, envelope.Raw);
                    }

                    if (machine.ShouldSuppressInboundSignal(signal.From, signal.Generation))
                    {
                        return null;
                    }

                    return PollEvent.FromSignal(signal, envelope.Raw);
                default:
                    /*
                        Routed kinds with no v2 event surface: heartbeat
                        replies (Pong refreshes liveness upstream),
                        client-to-server echoes, and server operation
                        results. Absorbed; still one frame of budget.
                    */
                    return null;
            }
        }
    }
}
