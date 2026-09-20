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
        private bool connected;
        private bool transportReady;
        private bool authenticated;
        private Guid authenticatedPlayerId;
        private RoomMembership membership;
        private PendingRoomOperation pendingOperation;
        private bool terminal;

        /// <summary>Creates the machine in the connecting phase (constructed-live, Rust parity).</summary>
        public SignalFishStateMachine()
        {
            this.connected = true;
        }

        /// <summary>Derived phase: membership &gt; authenticated &gt; transport-ready &gt; connecting.</summary>
        public ConnectionPhase Phase
        {
            get
            {
                if (this.terminal)
                {
                    return ConnectionPhase.Terminal;
                }

                if (this.membership.IsPresent)
                {
                    return ConnectionPhase.InRoom;
                }

                if (this.authenticated)
                {
                    return ConnectionPhase.Authenticated;
                }

                if (this.transportReady)
                {
                    return ConnectionPhase.TransportReady;
                }

                return ConnectionPhase.Connecting;
            }
        }

        /// <summary>True until the session goes terminal.</summary>
        public bool IsConnected
        {
            get { return this.connected; }
        }

        /// <summary>Handshake observed on the current connection (sticky until teardown).</summary>
        public bool IsTransportReady
        {
            get { return this.transportReady; }
        }

        /// <summary>Server confirmed authentication on this connection.</summary>
        public bool IsAuthenticated
        {
            get { return this.authenticated; }
        }

        /// <summary>Player id assigned by the server at authentication.</summary>
        public Guid AuthenticatedPlayerId
        {
            get { return this.authenticatedPlayerId; }
        }

        /// <summary>The four-field membership invariant; absent outside a confirmed room.</summary>
        public RoomMembership Membership
        {
            get { return this.membership; }
        }

        /// <summary>The in-flight directed room operation, if any.</summary>
        public PendingRoomOperation PendingOperation
        {
            get { return this.pendingOperation; }
        }

        /// <summary>
        /// Decides whether <paramref name="command"/> may be sent now. Pure:
        /// arming is a separate step so a failed enqueue never wedges the
        /// fence.
        /// </summary>
        public AdmissionError Admit(ClientCommand command)
        {
            if (!this.connected)
            {
                return AdmissionError.NotConnected;
            }

            if (command == ClientCommand.Ping)
            {
                return AdmissionError.None;
            }

            if (IsDirected(command) && !this.authenticated)
            {
                return AdmissionError.NotAuthenticated;
            }

            if (this.pendingOperation != PendingRoomOperation.None)
            {
                return AdmissionError.RoomOperationPending;
            }

            switch (command)
            {
                case ClientCommand.JoinRoom:
                case ClientCommand.JoinAsSpectator:
                case ClientCommand.Reconnect:
                    return this.membership.IsPresent
                        ? AdmissionError.AlreadyInRoom
                        : AdmissionError.None;
                case ClientCommand.LeaveRoom:
                case ClientCommand.LeaveSpectator:
                    return this.AdmitRoomScoped(command);
                case ClientCommand.SetReady:
                case ClientCommand.StartGame:
                case ClientCommand.SendGameData:
                    if (!this.membership.IsPresent)
                    {
                        return AdmissionError.NotInRoom;
                    }

                    return this.membership.Role == RoomRole.Player
                        ? AdmissionError.None
                        : AdmissionError.WrongRoomRole;
                default:
                    throw new ArgumentException(
                        "Undefined client command value: " + (byte)command,
                        nameof(command)
                    );
            }
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
        /// Arms the fence for a queued directed operation. Call only after
        /// <see cref="Admit"/> returned <see cref="AdmissionError.None"/> and
        /// the send was enqueued; <see cref="PendingRoomOperation.None"/> is
        /// a misuse and throws. The fence is released by typed results or
        /// teardown — never by re-arming.
        /// </summary>
        public void Arm(PendingRoomOperation operation)
        {
            if (operation == PendingRoomOperation.None)
            {
                throw new ArgumentException(
                    "Cannot arm a None fence; arm a directed operation.",
                    nameof(operation)
                );
            }

            this.pendingOperation = operation;
        }

        /// <summary>Applies one session fact. Absorbing after teardown.</summary>
        public void Apply(SessionEvent sessionEvent)
        {
            if (this.terminal)
            {
                return;
            }

            switch (sessionEvent.Kind)
            {
                case SessionEventKind.TransportReady:
                    this.transportReady = true;
                    break;
                case SessionEventKind.Authenticated:
                    // Fail-closed: a repeated or conflicting authentication is
                    // a protocol violation; the first assignment stands.
                    if (!this.authenticated)
                    {
                        this.authenticated = true;
                        this.authenticatedPlayerId = sessionEvent.PlayerId;
                    }

                    break;
                case SessionEventKind.RoomJoined:
                case SessionEventKind.SpectatorJoined:
                case SessionEventKind.Reconnected:
                    // Fail-closed: ignore a membership the server could not
                    // have confirmed — no authentication yet, or a success
                    // kind that does not answer the fenced operation (a
                    // protocol violation). Membership and fence stay put.
                    PendingRoomOperation release = SuccessRelease(sessionEvent.Kind);
                    if (
                        !this.authenticated
                        || (
                            this.pendingOperation != PendingRoomOperation.None
                            && this.pendingOperation != release
                        )
                    )
                    {
                        break;
                    }

                    this.membership = sessionEvent.Membership;
                    this.ReleaseIfPending(release);
                    break;
                case SessionEventKind.RoomLeft:
                case SessionEventKind.SpectatorLeft:
                    // Fail-closed: while fenced, only the leave kind the fence
                    // awaits may clear membership; a mismatched leave is a
                    // protocol violation and is ignored. Unfenced, a leave is
                    // accepted (tolerant server-initiated removal).
                    if (
                        this.pendingOperation != PendingRoomOperation.None
                        && this.pendingOperation != LeaveRelease(sessionEvent.Kind)
                    )
                    {
                        break;
                    }

                    this.membership = default;
                    this.ReleaseIfPending(LeaveRelease(sessionEvent.Kind));
                    break;
                case SessionEventKind.JoinRoomFailed:
                    this.ReleaseIfPending(PendingRoomOperation.JoinPlayer);
                    break;
                case SessionEventKind.JoinSpectatorFailed:
                    this.ReleaseIfPending(PendingRoomOperation.JoinSpectator);
                    break;
                case SessionEventKind.ReconnectFailed:
                    this.ReleaseIfPending(PendingRoomOperation.ReconnectPlayer);
                    break;
                case SessionEventKind.ServerError:
                    // Informational only: the fence stays armed (fail-closed).
                    break;
                case SessionEventKind.Disconnected:
                    this.ClearSession();
                    break;
                case SessionEventKind.None:
                    // Not a session fact; ignored so default events are inert.
                    break;
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
                    return PendingRoomOperation.None;
            }
        }

        private AdmissionError AdmitRoomScoped(ClientCommand command)
        {
            if (!this.membership.IsPresent)
            {
                return AdmissionError.NotInRoom;
            }

            RoomRole required =
                command == ClientCommand.LeaveRoom ? RoomRole.Player : RoomRole.Spectator;
            return this.membership.Role == required
                ? AdmissionError.None
                : AdmissionError.WrongRoomRole;
        }

        private static PendingRoomOperation LeaveRelease(SessionEventKind kind)
        {
            return kind == SessionEventKind.RoomLeft
                ? PendingRoomOperation.LeavePlayer
                : PendingRoomOperation.LeaveSpectator;
        }

        private void ReleaseIfPending(PendingRoomOperation operation)
        {
            if (this.pendingOperation == operation)
            {
                this.pendingOperation = PendingRoomOperation.None;
            }
        }

        private void ClearSession()
        {
            this.terminal = true;
            this.connected = false;
            this.transportReady = false;
            this.authenticated = false;
            this.authenticatedPlayerId = Guid.Empty;
            this.membership = default;
            this.pendingOperation = PendingRoomOperation.None;
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
