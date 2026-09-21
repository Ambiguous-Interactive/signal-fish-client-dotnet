namespace SignalFish.Client.Core
{
    using SignalFish.Client.Protocol;

    /// <summary>
    /// Maps decoded server envelopes to session facts — the single wire
    /// (Protocol) to session (Core) bridge both drivers consume. Pure and
    /// allocation-free: decoding the membership subset scans the frame in
    /// place; every non-session message maps to <c>false</c> so gameplay
    /// events stay with the event surface (M3.4). Unknown wire types never
    /// map (forward compatibility).
    /// </summary>
    internal static class SessionEventMapper
    {
        /// <summary>
        /// Maps one decoded envelope to its session fact. Returns true when
        /// <paramref name="envelope"/> is a session fact, with
        /// <paramref name="sessionEvent"/> assigned just before return;
        /// false for every other message (and for a session-fact frame whose
        /// session-critical payload fields are missing or malformed).
        /// </summary>
        internal static bool TryMap(EnvelopeEvent envelope, out SessionEvent sessionEvent)
        {
            if (envelope.Kind != EnvelopeEventKind.Message)
            {
                sessionEvent = default;
                return false;
            }

            switch (envelope.Message)
            {
                case MessageKind.Authenticated:
                    sessionEvent = SessionEvent.Authenticated();
                    return true;
                case MessageKind.RoomJoined:
                    return TryMapJoined(
                        envelope,
                        SessionEventKind.RoomJoined,
                        RoomRole.Player,
                        out sessionEvent
                    );
                case MessageKind.SpectatorJoined:
                    return TryMapJoined(
                        envelope,
                        SessionEventKind.SpectatorJoined,
                        RoomRole.Spectator,
                        out sessionEvent
                    );
                case MessageKind.Reconnected:
                    return TryMapJoined(
                        envelope,
                        SessionEventKind.Reconnected,
                        RoomRole.Player,
                        out sessionEvent
                    );
                case MessageKind.RoomLeft:
                    sessionEvent = SessionEvent.From(SessionEventKind.RoomLeft);
                    return true;
                case MessageKind.SpectatorLeft:
                    sessionEvent = SessionEvent.From(SessionEventKind.SpectatorLeft);
                    return true;
                case MessageKind.RoomJoinFailed:
                    sessionEvent = SessionEvent.From(SessionEventKind.RoomJoinFailed);
                    return true;
                case MessageKind.SpectatorJoinFailed:
                    sessionEvent = SessionEvent.From(SessionEventKind.SpectatorJoinFailed);
                    return true;
                case MessageKind.ReconnectionFailed:
                    sessionEvent = SessionEvent.From(SessionEventKind.ReconnectionFailed);
                    return true;
                case MessageKind.Error:
                    // Informational only: the fence stays armed (fail-closed).
                    sessionEvent = SessionEvent.From(SessionEventKind.ServerError);
                    return true;
                default:
                    sessionEvent = default;
                    return false;
            }
        }

        private static bool TryMapJoined(
            in EnvelopeEvent envelope,
            SessionEventKind kind,
            RoomRole role,
            out SessionEvent sessionEvent
        )
        {
            sessionEvent = default;
            if (role == RoomRole.Player)
            {
                if (!RoomJoinedMessage.TryDecode(envelope.Data, out RoomJoinedMessage joined))
                {
                    return false;
                }

                sessionEvent = SessionEvent.Joined(
                    kind,
                    new RoomMembership(role, joined.PlayerId, joined.RoomId, joined.RoomCode)
                );
                return true;
            }

            if (
                !SpectatorJoinedMessage.TryDecode(
                    envelope.Data,
                    out SpectatorJoinedMessage spectatorJoined
                )
            )
            {
                return false;
            }

            sessionEvent = SessionEvent.Joined(
                kind,
                new RoomMembership(
                    role,
                    spectatorJoined.SpectatorId,
                    spectatorJoined.RoomId,
                    spectatorJoined.RoomCode
                )
            );
            return true;
        }
    }
}
