namespace SignalFish.Client.Core
{
    using System;

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

        private bool _connected;
        private bool _transportReady;
        private bool _authenticated;
        private RoomMembership _membership;
        private PendingRoomOperation _pendingOperation;
        private string? _reconnectionToken;
        private bool _isAuthority;
        private uint? _negotiatedProtocolVersion;
        private bool _terminal;

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
            error = Admit(command, becomeAuthority);
            if (
                error == default(AdmissionError)
                && RequiresNegotiatedV3(command)
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
        /// Whether <paramref name="command"/> may only ride a negotiated-v3
        /// connection. The v2 floor is sacred: every command defined so far
        /// is v2 — the first v3-only send (classified delivery, mesh
        /// signaling) adds itself here together with the
        /// <see cref="AdmissionError.ProtocolUnsupported"/> gate coverage.
        /// </summary>
        internal static bool RequiresNegotiatedV3(ClientCommand command)
        {
            return false;
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
