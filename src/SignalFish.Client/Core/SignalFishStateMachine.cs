namespace SignalFish.Client.Core
{
    using System;
    using System.Collections.Generic;
    using SignalFish.Client.Protocol;

    /// <summary>
    /// Connection phase, membership, and membership-fencing state for one
    /// live session — the Rust client's phase table and four-field
    /// invariant as a single allocation-free component. Owned by one
    /// driver/poll loop; not thread-safe. Fencing is fail-closed: only a
    /// typed success, a matching typed failure, or session teardown
    /// releases an armed fence — a generic server error never does.
    /// </summary>
    public sealed class SignalFishStateMachine
    {
        private const int RetiredGenerationFence = 8;

        /// <summary>Derived phase: membership &gt; authenticated &gt; transport-ready &gt; connecting.</summary>
        public ConnectionPhase Phase
        {
            get
            {
                if (_terminal)
                {
                    return ConnectionPhase.Terminal;
                }

                if (_membership.IsPresent)
                {
                    return ConnectionPhase.InRoom;
                }

                if (_authenticated)
                {
                    return ConnectionPhase.Authenticated;
                }

                if (_transportReady)
                {
                    return ConnectionPhase.TransportReady;
                }

                return ConnectionPhase.Connecting;
            }
        }

        /// <summary>True until the session goes terminal.</summary>
        public bool IsConnected
        {
            get { return _connected; }
        }

        /// <summary>Handshake observed on the current connection (sticky until teardown).</summary>
        public bool IsTransportReady
        {
            get { return _transportReady; }
        }

        /// <summary>Server confirmed authentication on this connection.</summary>
        public bool IsAuthenticated
        {
            get { return _authenticated; }
        }

        /// <summary>The four-field membership invariant; absent outside a confirmed room.</summary>
        public RoomMembership Membership
        {
            get { return _membership; }
        }

        /// <summary>The in-flight directed room operation, if any.</summary>
        public PendingRoomOperation PendingOperation
        {
            get { return _pendingOperation; }
        }

        /// <summary>True while this connection is the confirmed room authority.</summary>
        public bool IsAuthority
        {
            get { return _isAuthority; }
        }

        /// <summary>
        /// The negotiated protocol version (the server's cap-down echo);
        /// null before <c>ProtocolInfo</c> arrives or on a v2 negotiation.
        /// Per-connection: cleared at teardown.
        /// </summary>
        public uint? NegotiatedProtocolVersion
        {
            get { return _negotiatedProtocolVersion; }
        }

        /// <summary>
        /// True once a v3 <c>SessionPlan</c> has been observed for the
        /// current membership — the gate every Signal send checks first.
        /// Cleared on leave, reconnection, and teardown (the server
        /// re-plans a fresh seat).
        /// </summary>
        public bool SessionPlanSeen
        {
            get { return _sessionPlanSeen; }
        }

        /// <summary>
        /// The latest plan's generation UUID; null while no plan has been
        /// observed or for the legacy generation-less Server 0.4 v3 shape
        /// (the send-fence stands down either way — nothing to supersede).
        /// Signals must stamp the latest generation, or a re-plan superseded
        /// them.
        /// </summary>
        public string? SessionGeneration
        {
            get { return _sessionGeneration; }
        }

        /// <summary>
        /// The latest plan's selected data-path transport;
        /// <c>default(SessionTransport)</c> while no plan has been observed.
        /// </summary>
        public SessionTransport SessionTransport
        {
            get { return _sessionTransport; }
        }

        private bool _connected;
        private bool _transportReady;
        private bool _authenticated;
        private RoomMembership _membership;
        private PendingRoomOperation _pendingOperation;
        private string? _reconnectionToken;
        private bool _isAuthority;
        private uint? _negotiatedProtocolVersion;
        private bool _terminal;
        private bool _sessionPlanSeen;
        private string? _sessionGeneration;
        private SessionTransport _sessionTransport;
        private readonly List<Guid> _sessionPeers = new List<Guid>();
        private readonly string?[] _retiredGenerations = new string[RetiredGenerationFence];
        private int _retiredGenerationsHead;
        private int _retiredGenerationsCount;
        private readonly List<Guid> _retiredSignalPeers = new List<Guid>();

        /// <summary>Creates the machine in the connecting phase (constructed-live, Rust parity).</summary>
        public SignalFishStateMachine()
        {
            _connected = true;
        }

        /// <summary>
        /// One coherent read of the session state (the Rust client's
        /// snapshot): phase fields, the membership identity, and the
        /// latest reconnection token as of the same instant.
        /// </summary>
        public ClientSnapshot CreateSnapshot()
        {
            return new ClientSnapshot(
                _connected,
                _transportReady,
                _authenticated,
                _membership.IsPresent ? _membership.Role : null,
                _membership.IsPresent ? _membership.PlayerId : null,
                _membership.IsPresent ? _membership.RoomId : null,
                _membership.IsPresent ? _membership.RoomCode : null,
                _reconnectionToken,
                _isAuthority,
                _negotiatedProtocolVersion
            );
        }

        /// <summary>
        /// The fence a command arms once its send is queued, or
        /// <c>null</c> for commands that never fence (ping, in-room game
        /// commands).
        /// </summary>
        public static PendingRoomOperation? PendingOperationFor(ClientCommand command)
        {
            switch (command)
            {
                case ClientCommand.JoinRoom:
                    return PendingRoomOperation.JoinPlayer;
                case ClientCommand.JoinAsSpectator:
                    return PendingRoomOperation.JoinSpectator;
                case ClientCommand.LeaveRoom:
                    return PendingRoomOperation.LeavePlayer;
                case ClientCommand.LeaveSpectator:
                    return PendingRoomOperation.LeaveSpectator;
                case ClientCommand.Reconnect:
                    return PendingRoomOperation.ReconnectPlayer;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Decides whether <paramref name="command"/> may be sent now.
        /// <paramref name="becomeAuthority"/> only matters for
        /// <see cref="ClientCommand.RequestAuthority"/> (a relinquish
        /// additionally requires holding the authority). Returns true when
        /// admitted; otherwise false with <paramref name="error"/>
        /// describing the refusal (assigned just before return). Pure: arming
        /// is a separate step so a failed enqueue never wedges the fence.
        /// </summary>
        public bool TryAdmit(ClientCommand command, out AdmissionError error)
        {
            return TryAdmit(command, becomeAuthority: true, out error);
        }

        /// <summary>The flag-carrying form; see the two-argument overload.</summary>
        public bool TryAdmit(ClientCommand command, bool becomeAuthority, out AdmissionError error)
        {
            return TryAdmit(command, GameDataClass.Reliable, becomeAuthority, out error);
        }

        /// <summary>The delivery-carrying form; see the two-argument overload.</summary>
        public bool TryAdmit(
            ClientCommand command,
            GameDataClass delivery,
            out AdmissionError error
        )
        {
            return TryAdmit(command, delivery, becomeAuthority: true, out error);
        }

        /// <summary>The full form; see the two-argument overload.</summary>
        public bool TryAdmit(
            ClientCommand command,
            GameDataClass delivery,
            bool becomeAuthority,
            out AdmissionError error
        )
        {
            error = Admit(command, becomeAuthority);
            if (
                error == default(AdmissionError)
                && RequiresNegotiatedV3(command, delivery)
                && (_negotiatedProtocolVersion ?? 0) < 3
            )
            {
                /*
                    Rust parity: the protocol refusal is the *last* word —
                    membership and role verdicts win while they apply, and
                    once those are satisfied the version gate describes the
                    remaining failure precisely. Covers both the
                    pre-negotiation and negotiated-v2 cases.
                */
                error = AdmissionError.ProtocolUnsupported;
            }

            return error == default(AdmissionError);
        }

        /// <summary>
        /// Arms the fence for a queued directed operation. Call only after
        /// <see cref="TryAdmit"/> returned true and the send was enqueued;
        /// the default <c>PendingRoomOperation</c> value is a misuse and
        /// throws. The fence is released by typed results or
        /// teardown — never by re-arming.
        /// </summary>
        public void Arm(PendingRoomOperation operation)
        {
            if (operation == default(PendingRoomOperation))
            {
                throw new ArgumentException(
                    "Cannot arm a None fence; arm a directed operation.",
                    nameof(operation)
                );
            }

            _pendingOperation = operation;
        }

        /// <summary>Applies one session fact. Absorbing after teardown.</summary>
        public void Apply(SessionEvent sessionEvent)
        {
            if (_terminal)
            {
                return;
            }

            switch (sessionEvent.Kind)
            {
                case SessionEventKind.TransportReady:
                    _transportReady = true;
                    break;
                case SessionEventKind.Authenticated:
                    /*
                        Idempotent: the v2 Authenticated payload carries no
                        identity, so a repeat has nothing to conflict with;
                        the player identity is confirmed with the membership.
                    */
                    _authenticated = true;
                    break;
                case SessionEventKind.RoomJoined:
                case SessionEventKind.SpectatorJoined:
                case SessionEventKind.Reconnected:
                    /*
                        Fail-closed: ignore a _membership the server could not
                        have confirmed — no authentication yet, or a success
                        kind that does not answer the fenced operation (a
                        protocol violation). Membership and fence stay put.
                    */
                    PendingRoomOperation release = SuccessRelease(sessionEvent.Kind);
                    if (
                        !_authenticated
                        || (
                            _pendingOperation != default(PendingRoomOperation)
                            && _pendingOperation != release
                        )
                    )
                    {
                        break;
                    }

                    _membership = sessionEvent.Membership;
                    /*
                        The baseline's token replaces the retained one whole:
                        player baselines carry the (possibly rotated) token;
                        spectator baselines carry none and clear it.
                    */
                    _reconnectionToken = sessionEvent.ReconnectionToken;
                    /*
                        Player baselines carry the receiving player's
                        is_authority (the room creator holds it); spectator
                        baselines can never hold it.
                    */
                    _isAuthority = sessionEvent.IsAuthority;
                    /*
                        Any confirmed baseline is a hard plan boundary (Rust
                        set_room parity): the reclaim/join re-enters the room
                        without one, and the server re-plans the fresh seat
                        right after. A re-baseline that kept the prior seat's
                        plan would fence signals into a stale room.
                    */
                    ClearSessionPlan();
                    ReleaseIfPending(release);
                    break;
                case SessionEventKind.RoomLeft:
                case SessionEventKind.SpectatorLeft:
                    /*
                        Fail-closed: while fenced, only the leave kind the fence
                        awaits may clear _membership; a mismatched leave is a
                        protocol violation and is ignored. Unfenced, a leave is
                        accepted (tolerant server-initiated removal).
                    */
                    if (
                        _pendingOperation != default(PendingRoomOperation)
                        && _pendingOperation != LeaveRelease(sessionEvent.Kind)
                    )
                    {
                        break;
                    }

                    _membership = default;
                    _reconnectionToken = null;
                    _isAuthority = false;
                    ClearSessionPlan();
                    ReleaseIfPending(LeaveRelease(sessionEvent.Kind));
                    break;
                case SessionEventKind.RoomJoinFailed:
                    ReleaseIfPending(PendingRoomOperation.JoinPlayer);
                    break;
                case SessionEventKind.SpectatorJoinFailed:
                    ReleaseIfPending(PendingRoomOperation.JoinSpectator);
                    break;
                case SessionEventKind.ReconnectionFailed:
                    ReleaseIfPending(PendingRoomOperation.ReconnectPlayer);
                    break;
                case SessionEventKind.ServerError:
                    // Informational only: the fence stays armed (fail-closed).
                    break;
                case SessionEventKind.AuthorityChanged:
                    /*
                        The broadcast is the source of truth, but only for a
                        live seat: applying it outside a confirmed membership
                        would report an authority with no room (fail-closed,
                        matching the membership facts).
                    */
                    if (!_membership.IsPresent)
                    {
                        break;
                    }

                    _isAuthority = sessionEvent.IsAuthority;
                    break;
                case SessionEventKind.ProtocolInfo:
                    /*
                        The cap-down echo is authoritative per connection: a
                        re-echo replaces, and v2 (absent field) negotiates
                        null. Never gates the handshake facts.
                    */
                    _negotiatedProtocolVersion = sessionEvent.NegotiatedProtocolVersion;
                    break;
                case SessionEventKind.SessionPlan:
                    /*
                        The plan is authoritative and latest-wins, but only
                        for a live seat: its peer set names room members, so
                        applying one without a confirmed membership would
                        admit signals into a room the server never confirmed
                        (fail-closed, matching the membership facts).
                    */
                    if (!_membership.IsPresent)
                    {
                        break;
                    }

                    ApplySessionPlan(sessionEvent.Plan);
                    break;
                case SessionEventKind.Disconnected:
                    ClearSession();
                    break;
                default:
                    /*
                        Not a session fact (or an additive future kind);
                        ignored so default events stay inert.
                    */
                    break;
            }
        }

        /// <summary>
        /// Whether <paramref name="playerId"/> is a peer of the latest
        /// session plan — the Signal target check. False while no plan has
        /// been observed (the peer set is empty until a plan names one).
        /// </summary>
        public bool IsSessionPeer(Guid playerId)
        {
            return _sessionPeers.Contains(playerId);
        }

        /// <summary>
        /// Whether a plan with <paramref name="generation"/> was already
        /// superseded: its generation is retired (differs from the current
        /// one and sits inside the bounded fence of recently retired
        /// generations). The frame pipeline rejects such a replay as a
        /// protocol violation instead of letting it overwrite the current
        /// plan — latest-wins by authority, not by arrival. Legacy plans
        /// without a generation can never be fenced.
        /// </summary>
        internal bool IsSessionPlanSuperseded(string? generation)
        {
            return generation is not null
                && !string.Equals(_sessionGeneration, generation, StringComparison.Ordinal)
                && ContainsRetiredGeneration(generation);
        }

        /// <summary>
        /// Whether an inbound <c>Signal</c> is a benign race to absorb
        /// silently: no authoritative plan has arrived yet, the signal is
        /// stamped with a stale (unknown or superseded) generation, or its
        /// sender was retired by the live generation (departed, or dropped
        /// by a same-generation re-plan) — the server protocol discards
        /// such frames at the recipient. Inbound signals always carry a
        /// generation (the decoder rejects the legacy generation-less
        /// shape), so a signal can never match a legacy plan's absent
        /// generation and the retirement fence never needs the
        /// generation-less branch. Not a violation: a signal racing its
        /// plan is normal relay ordering.
        ///
        /// Scope note (deliberate divergence from the Rust client): the
        /// further lifecycle-validation arm — classifying a surviving
        /// signal as a violation when the transport is not WebRTC, or the
        /// sender is not a session peer — is not ported. Every surviving
        /// signal surfaces as a typed event and the consumer filters by
        /// its mesh view (the documented consumer-side contract); no other
        /// .NET mesh kind carries the Rust lifecycle layer either, so a
        /// partial port would classify inconsistently.
        /// </summary>
        internal bool ShouldSuppressInboundSignal(Guid from, string generation)
        {
            if (
                !_sessionPlanSeen
                || !string.Equals(_sessionGeneration, generation, StringComparison.Ordinal)
            )
            {
                return true;
            }

            return _retiredSignalPeers.Contains(from) && !_sessionPeers.Contains(from);
        }

        /// <summary>
        /// Records a compatibility <c>NewPeer</c> directive: the peer is
        /// live again, so its pending signals surface (the authoritative
        /// plan remains the rule; this only undoes a retirement).
        /// </summary>
        internal void OnNewPeer(Guid peerId)
        {
            if (!_sessionPeers.Contains(peerId))
            {
                _sessionPeers.Add(peerId);
            }

            _retiredSignalPeers.Remove(peerId);
        }

        /// <summary>
        /// Records a departed member. On a WebRTC session the departed
        /// peer's final in-flight signals may still arrive stamped with the
        /// still-current generation; retiring the sender keeps those late
        /// frames benign. Outside WebRTC there is no relayed-signal
        /// fallback to race, so this is only a roster change.
        /// </summary>
        internal void OnPlayerLeft(Guid playerId)
        {
            if (_sessionTransport == SessionTransport.WebRtc && _sessionPeers.Remove(playerId))
            {
                _retiredSignalPeers.Add(playerId);
            }
        }

        /// <summary>
        /// Whether a command (with its game-data delivery class) may only
        /// ride a negotiated-v3 connection. The v2 floor is sacred:
        /// <see cref="GameDataClass.Reliable"/> relay reproduces the v2
        /// wire form and is never gated; classified delivery
        /// (<see cref="GameDataClass.Latest"/>,
        /// <see cref="GameDataClass.Volatile"/>) and the mesh signaling
        /// sends (<see cref="ClientCommand.SendSignal"/>,
        /// <see cref="ClientCommand.SendTransportStatus"/>) are v3-only;
        /// <see cref="ClientCommand.ProvideConnectionInfo"/> stays on the
        /// v2-compatible floor.
        /// </summary>
        internal static bool RequiresNegotiatedV3(ClientCommand command, GameDataClass delivery)
        {
            return (
                    command == ClientCommand.SendGameData
                    && (delivery == GameDataClass.Latest || delivery == GameDataClass.Volatile)
                )
                || command == ClientCommand.SendBinaryGameData
                || command == ClientCommand.SendSignal
                || command == ClientCommand.SendTransportStatus;
        }

        private AdmissionError Admit(ClientCommand command, bool becomeAuthority)
        {
            if (!_connected)
            {
                return AdmissionError.NotConnected;
            }

            if (command == ClientCommand.Ping)
            {
                return default;
            }

            /*
                The handshake is admitted in any live phase; whether it may
                still run is the server's call (it must precede application
                messages when used).
            */
            if (command == ClientCommand.Authenticate)
            {
                return default;
            }

            if (IsDirected(command) && !_authenticated)
            {
                return AdmissionError.NotAuthenticated;
            }

            if (_pendingOperation != default(PendingRoomOperation))
            {
                return AdmissionError.RoomOperationPending;
            }

            switch (command)
            {
                case ClientCommand.JoinRoom:
                case ClientCommand.JoinAsSpectator:
                case ClientCommand.Reconnect:
                    return _membership.IsPresent ? AdmissionError.AlreadyInRoom : default;
                case ClientCommand.LeaveRoom:
                case ClientCommand.LeaveSpectator:
                    return AdmitRoomScoped(command);
                case ClientCommand.SetReady:
                case ClientCommand.StartGame:
                case ClientCommand.SendGameData:
                case ClientCommand.SendBinaryGameData:
                case ClientCommand.SendSignal:
                case ClientCommand.SendTransportStatus:
                case ClientCommand.ProvideConnectionInfo:
                    if (!_membership.IsPresent)
                    {
                        return AdmissionError.NotInRoom;
                    }

                    return _membership.Role == RoomRole.Player
                        ? default
                        : AdmissionError.WrongRoomRole;
                case ClientCommand.RequestAuthority:
                    if (!_membership.IsPresent)
                    {
                        return AdmissionError.NotInRoom;
                    }

                    if (_membership.Role != RoomRole.Player)
                    {
                        return AdmissionError.WrongRoomRole;
                    }

                    /*
                        Rust parity: relinquishing without holding the
                        authority is refused locally (the server would deny
                        it anyway, but the local verdict is deterministic).
                    */
                    return becomeAuthority || _isAuthority
                        ? default
                        : AdmissionError.AuthorityRequired;
                default:
                    throw new ArgumentException(
                        "Undefined client command value: " + (byte)command,
                        nameof(command)
                    );
            }
        }

        private static PendingRoomOperation SuccessRelease(SessionEventKind kind)
        {
            switch (kind)
            {
                case SessionEventKind.RoomJoined:
                    return PendingRoomOperation.JoinPlayer;
                case SessionEventKind.SpectatorJoined:
                    return PendingRoomOperation.JoinSpectator;
                case SessionEventKind.Reconnected:
                    return PendingRoomOperation.ReconnectPlayer;
                default:
                    return default(PendingRoomOperation);
            }
        }

        private AdmissionError AdmitRoomScoped(ClientCommand command)
        {
            if (!_membership.IsPresent)
            {
                return AdmissionError.NotInRoom;
            }

            RoomRole required =
                command == ClientCommand.LeaveRoom ? RoomRole.Player : RoomRole.Spectator;
            return _membership.Role == required ? default : AdmissionError.WrongRoomRole;
        }

        private static PendingRoomOperation LeaveRelease(SessionEventKind kind)
        {
            return kind == SessionEventKind.RoomLeft
                ? PendingRoomOperation.LeavePlayer
                : PendingRoomOperation.LeaveSpectator;
        }

        private void ReleaseIfPending(PendingRoomOperation operation)
        {
            if (_pendingOperation == operation)
            {
                _pendingOperation = default(PendingRoomOperation);
            }
        }

        private void ClearSession()
        {
            _terminal = true;
            _connected = false;
            _transportReady = false;
            _authenticated = false;
            _membership = default;
            _pendingOperation = default(PendingRoomOperation);
            _reconnectionToken = null;
            _isAuthority = false;
            _negotiatedProtocolVersion = null;
            ClearSessionPlan();
        }

        private void ClearSessionPlan()
        {
            _sessionPlanSeen = false;
            _sessionGeneration = null;
            _sessionTransport = default(SessionTransport);
            _sessionPeers.Clear();
            _retiredGenerationsHead = 0;
            _retiredGenerationsCount = 0;
            _retiredSignalPeers.Clear();
        }

        private void ApplySessionPlan(in SessionPlanMessage plan)
        {
            bool generationChanged = !string.Equals(
                _sessionGeneration,
                plan.Generation,
                StringComparison.Ordinal
            );
            if (generationChanged)
            {
                /*
                    The superseded generation is fenced against replayed
                    plans re-asserting an already-superseded authoritative
                    view; bounded to the most recent
                    RetiredGenerationFence entries because the realistic
                    replay window is adjacent on one ordered transport. Peer
                    retirements were scoped to the superseded generation;
                    its signals now die in the generation check alone.
                */
                if (_sessionGeneration is not null)
                {
                    _retiredGenerations[_retiredGenerationsHead] = _sessionGeneration;
                    _retiredGenerationsHead =
                        (_retiredGenerationsHead + 1) % RetiredGenerationFence;
                    if (_retiredGenerationsCount < RetiredGenerationFence)
                    {
                        _retiredGenerationsCount++;
                    }
                }

                _retiredSignalPeers.Clear();
            }

            if (
                _sessionPlanSeen
                && !generationChanged
                && _sessionTransport == SessionTransport.WebRtc
            )
            {
                /*
                    Peers dropped by a same-generation replacement may still
                    have signals in flight stamped with that still-live
                    generation; retire them so those final frames stay
                    benign races. A generation change must not carry
                    retirement across: dropped peers never held authority
                    under the new generation.
                */
                for (int i = 0; i < _sessionPeers.Count; i++)
                {
                    if (!PlanNamesPeer(plan, _sessionPeers[i]))
                    {
                        _retiredSignalPeers.Add(_sessionPeers[i]);
                    }
                }
            }

            // Every peer named by the new plan is live again.
            for (int i = 0; i < plan.Peers.Count; i++)
            {
                _retiredSignalPeers.Remove(plan.Peers[i].PlayerId);
            }

            _sessionPlanSeen = true;
            _sessionGeneration = plan.Generation;
            _sessionTransport = plan.Transport;
            _sessionPeers.Clear();
            for (int i = 0; i < plan.Peers.Count; i++)
            {
                _sessionPeers.Add(plan.Peers[i].PlayerId);
            }
        }

        private bool ContainsRetiredGeneration(string generation)
        {
            for (int i = 0; i < _retiredGenerationsCount; i++)
            {
                int index =
                    (_retiredGenerationsHead - 1 - i + RetiredGenerationFence)
                    % RetiredGenerationFence;
                if (string.Equals(_retiredGenerations[index], generation, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool PlanNamesPeer(in SessionPlanMessage plan, Guid playerId)
        {
            for (int i = 0; i < plan.Peers.Count; i++)
            {
                if (plan.Peers[i].PlayerId == playerId)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsDirected(ClientCommand command)
        {
            switch (command)
            {
                case ClientCommand.JoinRoom:
                case ClientCommand.JoinAsSpectator:
                case ClientCommand.LeaveRoom:
                case ClientCommand.LeaveSpectator:
                case ClientCommand.Reconnect:
                    return true;
                default:
                    return false;
            }
        }
    }
}
