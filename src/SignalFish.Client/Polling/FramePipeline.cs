namespace SignalFish.Client.Polling
{
    using System;
    using SignalFish.Client.Core;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.Transport;

    /// <summary>
    /// One inbound frame's translation: at most one
    /// <see cref="PollEvent"/> to surface plus an optional session fact to
    /// apply (the machine state leads the event stream). A close frame
    /// (server-typed or locally synthesized) instead carries the teardown.
    /// </summary>
    internal struct FrameTranslation
    {
        /// <summary>True when the frame (or a defensive fact) ends the session.</summary>
        internal bool IsClose;

        /// <summary>The teardown close; meaningful only when <see cref="IsClose"/> is true.</summary>
        internal TransportClose Close;

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
    /// Pure: state-machine application and event delivery stay with the
    /// caller.
    /// </summary>
    internal static class FramePipeline
    {
        /// <summary>The local close code when the client declares the session dead itself.</summary>
        internal const int LivenessCloseCode = 1006;

        /// <summary>Translates one inbound transport frame.</summary>
        internal static void Translate(
            in TransportFrame frame,
            int maxFrameBytes,
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

            if (!frame.IsText || frame.Payload.Length > maxFrameBytes)
            {
                /*
                    The v2 floor is JSON text; an oversized or binary frame
                    violates the negotiated contract (binary game data
                    arrives with v3).
                */
                translated.HasEvent = true;
                translated.Event = PollEvent.FromViolation(default(MessageKind), frame.Payload);
                return;
            }

            EnvelopeEvent envelope = EnvelopeReader.Decode(frame.Payload);
            switch (envelope.Kind)
            {
                case EnvelopeEventKind.Message:
                    TranslateMessage(envelope, ref translated);
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

        private static void TranslateMessage(
            in EnvelopeEvent envelope,
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
                v3-only kinds). Null means absorbed.
            */
            if (SessionEventMapper.IsSessionFact(envelope.Message))
            {
                translated.HasEvent = true;
                translated.Event = PollEvent.FromViolation(envelope.Message, envelope.Raw);
                return;
            }

            PollEvent? payloadEvent = TryBuildPayloadEvent(envelope);
            if (payloadEvent is not null)
            {
                translated.HasEvent = true;
                translated.Event = payloadEvent.GetValueOrDefault();
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
    }
}
