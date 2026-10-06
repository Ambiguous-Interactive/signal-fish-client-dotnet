#if SIGNALFISH_FACEPUNCH
#nullable enable
namespace SignalFish.Client.Adapters.Facepunch
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using SignalFish.Client.Async;
    using SignalFish.Client.Core;
    using SignalFish.Client.Polling;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.Transport;
    using Steamworks;
    using Steamworks.Data;
    using UnityEngine;

    /*
        Bootstraps Steam sockets onto a Signal Fish room over the
        Facepunch Steamworks binding. Steam has no matchmaking to lean
        on, so the room itself is the fence: the host opens a P2P relay
        socket and accepts an incoming Steam connection only once that
        peer's SteamId64 was published over the room's game-data lane;
        the client dials the host id published on the same lane. The
        bootstrap requires Facepunch's manual callback mode —
        SteamClient.Init(appId, asyncCallbacks: false) — and pumps
        SteamClient.RunCallbacks on its own tick: in Facepunch's
        default async mode status changes dispatch on a background
        thread, which this component's threading contract (and Unity)
        forbid. With the pump on the tick, every Steamworks call runs
        there too, and nothing here touches the Unity scene. The
        Facepunch bookkeeping fields are shared between the tick, the
        manager callbacks (which dispatch from that pump), and
        teardowns from any thread, so every access takes the lock, and
        teardown closes the Steam state under that same lock so a new
        session's kick can never interleave with an old session's
        close.
    */

    /// <summary>
    /// Bootstraps Steam sockets onto a Signal Fish room through the
    /// Facepunch Steamworks binding: the room owns membership and
    /// timing, Steam owns the transport, and the exchange carries the
    /// two SteamId64s each side needs. The host joins (or creates) the
    /// room, takes the authority, opens a Steam P2P relay socket, and
    /// publishes its SteamId64 over the room's game-data lane
    /// (re-published on every join so late joiners never depend on
    /// timing); a client joins the room by code, marks itself ready,
    /// and dials the host SteamId64 that arrives on that lane. The host
    /// accepts an incoming Steam connection only after that peer's
    /// SteamId64 was published on the lane, within a grace window — the
    /// room's membership is the accept fence. Peers surface as
    /// <c>SteamPeerConnected</c>/<c>SteamPeerDisconnected</c> with the
    /// SteamId64s the game then owns for its own traffic. Starts may be
    /// awaited from any thread; the Steam calls they need run on the
    /// bootstrap's tick (Facepunch's manual callback mode —
    /// <c>SteamClient.Init(appId, asyncCallbacks: false)</c> — is a
    /// requirement: in async mode status changes dispatch on a
    /// background thread, which this component's threading contract and
    /// Unity forbid). This is deliberately not a transport bridge: the
    /// bootstrap establishes and fences the Steam connections, and the
    /// game owns what flows over them — on Facepunch that means the
    /// live <c>SocketManager</c>/<c>ConnectionManager</c> this class
    /// exposes are where the game plugs its message pump (their
    /// <c>Interface</c>) and calls <c>Receive</c>.
    /// </summary>
    public sealed class SignalFishFacepunchSteamIdentityBootstrap : MonoBehaviour
    {
        private enum SteamStartKind : byte
        {
            [Obsolete(
                "This value only exists so the enum default (0) is not a start. Compare against default(SteamStartKind) instead."
            )]
            None = 0,

            Host = 1,

            Client = 2,
        }

        private enum SteamPhase : byte
        {
            [Obsolete(
                "This value only exists so the enum default (0) is not a phase. Compare against default(SteamPhase) instead."
            )]
            None = 0,

            Kicking = 1,

            Connecting = 2,

            Live = 3,
        }

        private readonly struct StagedStart
        {
            public SteamStartKind Kind { get; }

            public int Generation { get; }

            public SignalFishClient Client { get; }

            public RoomMembership Membership { get; }

            public StagedStart(
                SteamStartKind kind,
                int generation,
                SignalFishClient client,
                RoomMembership membership
            )
            {
                Kind = kind;
                Generation = generation;
                Client = client;
                Membership = membership;
            }
        }

        private readonly struct PendingAccept
        {
            public Connection Connection { get; }

            public ulong RemoteSteamId { get; }

            public long DeadlineTicks { get; }

            public PendingAccept(Connection connection, ulong remoteSteamId, long deadlineTicks)
            {
                Connection = connection;
                RemoteSteamId = remoteSteamId;
                DeadlineTicks = deadlineTicks;
            }
        }

        /*
            The Facepunch managers route status changes by socket and
            connection id through static registries that outlive a
            session; the bootstrap forwards only what its own current
            session created, so a stale or recycled dispatch can never
            land in the new session's fence.
        */
        private sealed class FencedSocketManager : SocketManager
        {
            public SignalFishFacepunchSteamIdentityBootstrap? Owner;

            public override void OnConnecting(Connection connection, ConnectionInfo info)
            {
                /*
                    The default SocketManager accepts every connection;
                    the fence owns the accept instead, on the bootstrap's
                    tick, after the peer's id arrived on the lane. The
                    null guard is the defensive backstop for a host that
                    ignored the manual-callback-mode requirement: in
                    async mode a status change could dispatch before the
                    owner assignment landed.
                */
                if (Owner is null)
                {
                    return;
                }

                Owner.OnIncomingConnecting(this, connection, info);
            }

            public override void OnConnected(Connection connection, ConnectionInfo info)
            {
                if (Owner is null)
                {
                    return;
                }

                /*
                    Keep the poll-group assignment: the game pumps its
                    messages through the exposed manager.
                */
                base.OnConnected(connection, info);
                Owner.OnPeerEstablished(this, connection, info);
            }

            public override void OnDisconnected(Connection connection, ConnectionInfo info)
            {
                /*
                    The explicit close after an ended dispatch frees the
                    connection's local resources — but only while this
                    manager is the session's current one: a stale
                    dispatch routed through Facepunch's registry after a
                    teardown can carry a recycled id, and closing that
                    could hit the next session's live dial.
                */
                if (Owner is null || !Owner.OnPeerConnectionEnded(this, connection))
                {
                    return;
                }

                connection.Close();
            }
        }

        private sealed class FencedConnectionManager : ConnectionManager
        {
            public SignalFishFacepunchSteamIdentityBootstrap? Owner;

            public override void OnConnected(ConnectionInfo info)
            {
                if (Owner is null)
                {
                    return;
                }

                Owner.OnHostConnectionEstablished(this, info);
            }

            public override void OnDisconnected(ConnectionInfo info)
            {
                if (Owner is null)
                {
                    return;
                }

                Owner.OnHostConnectionEnded(this, info);
            }
        }

        private const int WaitPollMilliseconds = 10;

        private const int KickSettleMilliseconds = 2000;

        /*
            The app-range connection end reason for a fence refusal
            (NetConnectionEnd.App_Min); app codes keep Steam's own
            diagnostic ranges untouched.
        */
        private const int FenceRefusalEndReason = (int)NetConnectionEnd.App_Min;

        /// <summary>
        /// The host's SteamId64 arrived on the room's game-data lane; the
        /// value is the address the client's Steam connection dials. The
        /// event may fire from the start's await thread; it is
        /// informational.
        /// </summary>
        public event Action<ulong>? SteamHostIdReceived;

        /// <summary>A fenced peer's Steam connection is established; the game owns its side of the traffic from here.</summary>
        public event Action<ulong>? SteamPeerConnected;

        /// <summary>A fenced peer's Steam connection closed.</summary>
        public event Action<ulong>? SteamPeerDisconnected;

        /// <summary>The coordinated session failed; the bootstrap has already torn itself down.</summary>
        public event Action<string>? CoordinationFailed;

        /// <summary>The Signal Fish server's WebSocket endpoint.</summary>
        public string Endpoint
        {
            get => _endpoint;
            set => _endpoint = value;
        }

        public string GameName
        {
            get => _gameName;
            set => _gameName = value;
        }

        public string PlayerName
        {
            get => _playerName;
            set => _playerName = value;
        }

        /// <summary>How long the room handshake may take before the start fails.</summary>
        public float StartTimeoutSeconds
        {
            get => _startTimeoutSeconds;
            set => _startTimeoutSeconds = value;
        }

        /// <summary>How long the client's wait for the host id and the Steam connect may take before the start fails.</summary>
        public float SteamStartTimeoutSeconds
        {
            get => _steamStartTimeoutSeconds;
            set => _steamStartTimeoutSeconds = value;
        }

        /// <summary>
        /// How long the host waits for an incoming peer's SteamId64 to
        /// arrive on the lane before refusing its Steam connection. Zero
        /// refuses everyone who was not already advertised.
        /// </summary>
        public float AcceptGraceSeconds
        {
            get => _acceptGraceSeconds;
            set => _acceptGraceSeconds = value;
        }

        /// <summary>The Steam P2P virtual port the host listens on; 0 is Steam's default.</summary>
        public int ListenVirtualPort
        {
            get => _listenVirtualPort;
            set => _listenVirtualPort = value;
        }

        /// <summary>
        /// Builds the Signal Fish connection's transport. WebGL builds
        /// must return the package's browser-WebSocket transport here.
        /// </summary>
        public Func<ITransport>? TransportFactory { get; set; }

        public bool IsCoordinating
        {
            get
            {
                lock (_lock)
                {
                    return _session is not null || _client is not null;
                }
            }
        }

        /// <summary>Gets the room code the session joined; null while no session is live.</summary>
        public string? JoinedRoomCode
        {
            get
            {
                lock (_lock)
                {
                    return _joinedRoomCode;
                }
            }
        }

        /// <summary>Gets the local SteamId64 once a start passed the Steam readiness check; 0 before that.</summary>
        public ulong LocalSteamId
        {
            get
            {
                lock (_lock)
                {
                    return _localSteamId;
                }
            }
        }

        /// <summary>Gets the host's published SteamId64 once it arrived on the lane; null before that.</summary>
        public ulong? HostSteamId
        {
            get
            {
                lock (_lock)
                {
                    return _hostSteamId == 0 ? null : _hostSteamId;
                }
            }
        }

        /// <summary>
        /// Gets the live host-side manager once a host start is live;
        /// null before that. The game owns the traffic: assign an
        /// <c>ISocketManager</c> to its <c>Interface</c> to receive
        /// messages (<c>OnMessage</c>), and call <c>Receive</c> per
        /// frame to pump them. The bootstrap owns the lifecycle
        /// callbacks; the interface only ever sees messages.
        /// </summary>
        public SocketManager? LiveSocketManager
        {
            get
            {
                lock (_lock)
                {
                    return _socketManager;
                }
            }
        }

        /// <summary>
        /// Gets the live client-side manager once a client start is
        /// live; null before that. The game owns the traffic: assign an
        /// <c>IConnectionManager</c> to its <c>Interface</c> to receive
        /// messages (<c>OnMessage</c>), and call <c>Receive</c> per
        /// frame to pump them. The bootstrap owns the lifecycle
        /// callbacks; the interface only ever sees messages.
        /// </summary>
        public ConnectionManager? LiveConnectionManager
        {
            get
            {
                lock (_lock)
                {
                    return _connectionManager;
                }
            }
        }

        private int StartTimeoutMilliseconds => (int)(_startTimeoutSeconds * 1000f);

        private int SteamStartTimeoutMilliseconds => (int)(_steamStartTimeoutSeconds * 1000f);

        private int AcceptGraceMilliseconds => (int)(_acceptGraceSeconds * 1000f);

        private readonly object _lock = new object();

        private string _endpoint = "ws://127.0.0.1:3536/v2/ws";

        private string _gameName = "facepunch-game";

        private string _playerName = "player";

        private float _startTimeoutSeconds = 10f;

        private float _steamStartTimeoutSeconds = 30f;

        private float _acceptGraceSeconds = 5f;

        private int _listenVirtualPort;

        private int _generation;

        private SignalFishClient? _session;

        private SignalFishClient? _client;

        private StagedStart? _staged;

        private TaskCompletionSource<bool>? _stagedCompletion;

        private bool _isHost;

        private string? _joinedRoomCode;

        private ulong _localSteamId;

        private ulong _hostSteamId;

        /*
            Steam phase state: written by the tick and the manager
            callbacks (both main-thread) and by teardowns from any
            thread, so every access takes the lock.
        */
        private StagedStart? _active;

        private TaskCompletionSource<bool>? _activeCompletion;

        private long _activeDeadline;

        private SteamPhase _phase = default(SteamPhase);

        private bool _published;

        private SocketManager? _socketManager;

        private ConnectionManager? _connectionManager;

        private readonly Dictionary<uint, ulong> _trackedPeers = new Dictionary<uint, ulong>();

        private readonly HashSet<uint> _acceptedConnections = new HashSet<uint>();

        private readonly List<PendingAccept> _pendingIncoming = new List<PendingAccept>();

        private readonly HashSet<ulong> _advertisedPeers = new HashSet<ulong>();

        /// <summary>
        /// Starts the host side: join (or create) the Signal Fish room,
        /// take the authority, publish the local SteamId64 over the
        /// room's game-data lane, open the Steam P2P relay socket, and
        /// start the game. Incoming Steam connections are accepted only
        /// for peers whose SteamId64 was published on the lane.
        /// </summary>
        public Task StartHostAsync(string? roomCode = null, CancellationToken ct = default)
        {
            return CoordinateAsync(SteamStartKind.Host, roomCode, ct);
        }

        /// <summary>
        /// Starts the client side: join the Signal Fish room by code,
        /// mark ready, wait for the host's SteamId64 on the lane, and
        /// dial it over Steam P2P. The task completes once the Steam
        /// connection to the host is established (or fails).
        /// </summary>
        public Task StartClientAsync(string roomCode, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(roomCode))
            {
                throw new ArgumentException("The room code cannot be empty.", nameof(roomCode));
            }

            return CoordinateAsync(SteamStartKind.Client, roomCode, ct);
        }

        /// <summary>Tears the session down: closes the sockets, connections, and managers this bootstrap created.</summary>
        public void Shutdown()
        {
            Teardown();
        }

        private void OnDestroy()
        {
            Teardown();
        }

        private void Update()
        {
            RunStagedStart();
            RunSteamCallbacks();
            DrainSession();
            ProcessPendingAccepts();
            EnforceSteamDeadline();
        }

        private async Task CoordinateAsync(
            SteamStartKind kind,
            string? roomCode,
            CancellationToken ct
        )
        {
            ValidateConfigured();
            int generation = BeginSession();
            SignalFishClient client = CreateClient();
            TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            try
            {
                lock (_lock)
                {
                    if (_generation != generation)
                    {
                        throw new OperationCanceledException(
                            "The coordination session was torn down."
                        );
                    }

                    _session = client;
                }

                await client.ConnectAsync(new Uri(_endpoint), ct).ConfigureAwait(false);
                SendOrThrow(client.SendAuthenticate(new AuthenticateMessage()), "Authenticate");
                await WaitForAsync(client, PollEventKind.Authenticated, generation, ct)
                    .ConfigureAwait(false);
                SendOrThrow(
                    client.SendJoinRoom(
                        new JoinRoomMessage(
                            _gameName,
                            _playerName,
                            roomCode,
                            supportsAuthority: kind == SteamStartKind.Host
                        )
                    ),
                    "JoinRoom"
                );
                PollEvent joined = await WaitForAsync(
                        client,
                        PollEventKind.RoomJoined,
                        generation,
                        ct
                    )
                    .ConfigureAwait(false);

                if (kind == SteamStartKind.Host)
                {
                    SendOrThrow(
                        client.SendAuthorityRequest(becomeAuthority: true),
                        "AuthorityRequest"
                    );
                    PollEvent granted = await WaitForAsync(
                            client,
                            PollEventKind.AuthorityResponse,
                            generation,
                            ct
                        )
                        .ConfigureAwait(false);
                    if (!granted.AuthorityResponse.Granted)
                    {
                        throw new InvalidOperationException(
                            "The room denied the authority request: "
                                + (granted.AuthorityResponse.Reason ?? "unspecified")
                                + "."
                        );
                    }
                }
                else
                {
                    SendOrThrow(client.SendPlayerReady(), "PlayerReady");
                    ulong hostSteamId = await WaitForHostIdAsync(client, generation, ct)
                        .ConfigureAwait(false);
                    lock (_lock)
                    {
                        if (_generation == generation)
                        {
                            _hostSteamId = hostSteamId;
                        }
                    }
                }

                Stage(
                    new StagedStart(kind, generation, client, joined.Membership),
                    completion,
                    generation
                );
                await AwaitStaged(completion, generation).ConfigureAwait(false);
            }
            catch
            {
                await DisposeQuietlyAsync(client).ConfigureAwait(false);
                if (IsFresh(generation))
                {
                    Teardown();
                }

                throw;
            }
        }

        private void ValidateConfigured()
        {
            if (
                string.IsNullOrEmpty(_endpoint)
                || !Uri.IsWellFormedUriString(_endpoint, UriKind.Absolute)
            )
            {
                throw new InvalidOperationException("Endpoint is not a well-formed absolute URI.");
            }

            if (string.IsNullOrEmpty(_gameName))
            {
                throw new InvalidOperationException("GameName cannot be empty.");
            }

            if (string.IsNullOrEmpty(_playerName))
            {
                throw new InvalidOperationException("PlayerName cannot be empty.");
            }

            if (StartTimeoutMilliseconds < 1)
            {
                throw new InvalidOperationException("StartTimeoutSeconds must be positive.");
            }

            if (SteamStartTimeoutMilliseconds < 1)
            {
                throw new InvalidOperationException("SteamStartTimeoutSeconds must be positive.");
            }

            if (AcceptGraceMilliseconds < 0)
            {
                throw new InvalidOperationException("AcceptGraceSeconds cannot be negative.");
            }

            if (_listenVirtualPort < 0)
            {
                throw new InvalidOperationException("ListenVirtualPort cannot be negative.");
            }
        }

        private async Task<PollEvent> WaitForAsync(
            SignalFishClient client,
            PollEventKind expected,
            int generation,
            CancellationToken ct
        )
        {
            long deadline = SystemClock.Instance.ElapsedMilliseconds + StartTimeoutMilliseconds;
            while (SystemClock.Instance.ElapsedMilliseconds < deadline)
            {
                ct.ThrowIfCancellationRequested();
                EnsureFresh(generation);
                while (client.TryDequeueEvent(out PollEvent current))
                {
                    if (current.Kind == expected)
                    {
                        return current;
                    }

                    ThrowForFailure(current);
                }

                await Task.Delay(WaitPollMilliseconds).ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Timed out after {StartTimeoutSeconds:0.#}s waiting for {expected}."
            );
        }

        private async Task<ulong> WaitForHostIdAsync(
            SignalFishClient client,
            int generation,
            CancellationToken ct
        )
        {
            long deadline =
                SystemClock.Instance.ElapsedMilliseconds + SteamStartTimeoutMilliseconds;
            while (SystemClock.Instance.ElapsedMilliseconds < deadline)
            {
                ct.ThrowIfCancellationRequested();
                EnsureFresh(generation);
                while (client.TryDequeueEvent(out PollEvent current))
                {
                    ThrowForFailure(current);
                    if (current.Kind != PollEventKind.GameData)
                    {
                        continue;
                    }

                    if (
                        SteamIdentityEnvelope.TryRead(
                            current.GameData.Payload.Span,
                            SteamIdentityEnvelope.HostPropertyName,
                            out string steamId
                        )
                        && ulong.TryParse(
                            steamId,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out ulong hostSteamId
                        )
                    )
                    {
                        SteamHostIdReceived?.Invoke(hostSteamId);
                        return hostSteamId;
                    }
                }

                await Task.Delay(WaitPollMilliseconds).ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Timed out after {SteamStartTimeoutSeconds:0.#}s waiting for the host's SteamId64."
            );
        }

        private async Task AwaitStaged(TaskCompletionSource<bool> completion, int generation)
        {
            /*
                The staged start is driven by Update() and the manager
                callbacks; a disabled component stops receiving both.
                The watchdog's margin keeps that world bounded too — in a
                ticking world EnforceSteamDeadline fires first with the
                sharper message, so the watchdog only speaks when Update
                really stopped.
            */
            Task settled = await Task.WhenAny(
                    completion.Task,
                    Task.Delay(SteamStartTimeoutMilliseconds + KickSettleMilliseconds)
                )
                .ConfigureAwait(false);
            if (settled != completion.Task && !completion.Task.IsCompleted)
            {
                Teardown();
                try
                {
                    await completion.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Teardown's own verdict; the timeout is the caller-facing reason.
                }

                throw new TimeoutException(
                    "The staged Steam start never settled; the bootstrap's Update stopped ticking."
                );
            }

            await completion.Task.ConfigureAwait(false);
            EnsureFresh(generation);
        }

        private void Stage(StagedStart start, TaskCompletionSource<bool> completion, int generation)
        {
            lock (_lock)
            {
                EnsureFresh(generation);
                if (_staged is not null || _client is not null)
                {
                    throw new InvalidOperationException(
                        "A start is already staged; one bootstrap coordinates one session."
                    );
                }

                _staged = start;
                _stagedCompletion = completion;
                _isHost = start.Kind == SteamStartKind.Host;
                _joinedRoomCode = start.Membership.RoomCode;
            }
        }

        private void RunStagedStart()
        {
            StagedStart? staged;
            TaskCompletionSource<bool>? completion;
            lock (_lock)
            {
                staged = _staged;
                completion = _stagedCompletion;
                _staged = null;
                _stagedCompletion = null;
            }

            if (staged is null)
            {
                return;
            }

            StagedStart start = staged.GetValueOrDefault();
            lock (_lock)
            {
                if (_generation != start.Generation)
                {
                    /*
                        The session was torn down while the start staged
                        (a Shutdown or a failed sibling); the engine start
                        must never run for a dead session.
                    */
                    completion?.SetException(
                        new OperationCanceledException("The coordination session was torn down.")
                    );
                    _ = DisposeQuietlyAsync(start.Client);
                    return;
                }

                /*
                    One atomic region: promote the staged client, arm the
                    phase machine, and kick. A teardown on another thread
                    either fully precedes this region (the generation
                    check above) or fully follows it — it can never
                    interleave between the promotion and the Steam state
                    the kick creates.
                */
                _client = start.Client;
                _session = null;
                _active = start;
                _activeCompletion = completion;
                _activeDeadline =
                    SystemClock.Instance.ElapsedMilliseconds + SteamStartTimeoutMilliseconds;
                _phase = SteamPhase.Kicking;
                try
                {
                    if (_isHost)
                    {
                        KickHost();
                    }
                    else
                    {
                        KickClient();
                    }
                }
                catch (Exception failure)
                {
                    FailStaged(failure);
                }
            }
        }

        /*
            Kick helpers run under the lock, on the main thread, with a
            staged start present.
        */
        private void KickHost()
        {
            StagedStart start = _active.GetValueOrDefault();
            ulong localSteamId = ReadLocalSteamId();
            _localSteamId = localSteamId;

            /*
                The listen socket opens before anything observable
                happens: a socket refusal fails the start before the
                room saw a published id, a ready flag, or a started
                game that would have pointed peers at a socket that
                never existed. No status callback can fire between the
                manager's creation and its owner assignment: the
                bootstrap requires Facepunch's manual callback mode
                (SteamClient.Init(appId, asyncCallbacks: false)), so
                status changes dispatch only from this bootstrap's own
                RunCallbacks pump, which runs on this same tick under
                this same lock.
            */
            FencedSocketManager socketManager;
            try
            {
                socketManager = SteamNetworkingSockets.CreateRelaySocket<FencedSocketManager>(
                    _listenVirtualPort
                );
            }
            catch (ArgumentException failure)
            {
                /*
                    Steam refuses a listen socket by handing back the
                    default handle, which Facepunch's registry setter
                    rejects; surface the intended diagnostic instead of
                    the registry's.
                */
                throw new InvalidOperationException("Steam refused the P2P relay socket.", failure);
            }

            socketManager.Owner = this;
            _socketManager = socketManager;

            if (!TryPublishIdentity(SteamIdentityEnvelope.HostPropertyName, localSteamId))
            {
                throw new InvalidOperationException(
                    "The host's SteamId64 could not be published "
                        + "(envelope bound or refused send)."
                );
            }

            _published = true;
            SendOrThrow(start.Client.SendPlayerReady(), "PlayerReady");
            SendOrThrow(start.Client.SendStartGame(), "StartGame");
            CompleteStaged();
        }

        private void KickClient()
        {
            ulong localSteamId = ReadLocalSteamId();
            _localSteamId = localSteamId;
            if (!TryPublishIdentity(SteamIdentityEnvelope.PeerPropertyName, localSteamId))
            {
                throw new InvalidOperationException(
                    "The local SteamId64 could not be published "
                        + "(envelope bound or refused send)."
                );
            }

            _published = true;
            FencedConnectionManager connectionManager;
            try
            {
                connectionManager = SteamNetworkingSockets.ConnectRelay<FencedConnectionManager>(
                    _hostSteamId,
                    _listenVirtualPort
                );
            }
            catch (ArgumentException failure)
            {
                /*
                    Steam refuses a connect by handing back the default
                    handle, which Facepunch's registry setter rejects;
                    surface the intended diagnostic instead of the
                    registry's.
                */
                throw new InvalidOperationException("Steam refused the P2P connect.", failure);
            }

            connectionManager.Owner = this;
            _connectionManager = connectionManager;

            /*
                Facepunch's manager absorbs the Connecting status of this
                own dial (its Connecting flag starts true, so the state
                machine never forwards it as OnConnecting); the Connecting
                phase waits for the dial's Connected state.
            */
            _phase = SteamPhase.Connecting;
        }

        private void CompleteStaged()
        {
            _phase = SteamPhase.Live;
            TaskCompletionSource<bool>? completion = _activeCompletion;
            _active = null;
            _activeCompletion = null;
            completion?.TrySetResult(true);
        }

        private void RunSteamCallbacks()
        {
            lock (_lock)
            {
                if (_socketManager is null && _connectionManager is null)
                {
                    return;
                }

                /*
                    Steam going away under a live session is silent in
                    Facepunch: after SteamClient.Shutdown the pump is a
                    no-op, and Dispatch swallows callback exceptions
                    into its OnException hook. The validity check is
                    therefore the detector, not the catch below — under
                    the lock, so a cross-thread Shutdown that already
                    tore the session down cannot raise a spurious
                    CoordinationFailed here.
                */
                if (!SteamClient.IsValid)
                {
                    Fail("Steam was shut down under the session.");
                    return;
                }
            }

            try
            {
                /*
                    Status changes — the connection state machine this
                    fence runs on — dispatch from here. Facepunch's
                    message pump (SocketManager.Receive) is deliberately
                    not called: the game owns the traffic.
                */
                SteamClient.RunCallbacks();
            }
            catch (Exception failure)
            {
                /*
                    Backstop only: Facepunch routes callback exceptions
                    into Dispatch.OnException and no-ops the pump after
                    a shutdown, so this rarely speaks — but an
                    unexpected throw must fail the session instead of
                    firing out of Update every tick.
                */
                Fail("Steam callback dispatch failed: " + failure.Message + ".");
            }
        }

        /*
            Manager-callback branches run under the lock, and each one is
            scoped to the manager instance this generation created —
            Facepunch routes status changes by socket and connection id
            through static registries that outlive a session, so a stale
            dispatch or a recycled id can never land in the new
            session's bookkeeping.
        */
        private void OnIncomingConnecting(
            SocketManager manager,
            Connection connection,
            ConnectionInfo info
        )
        {
            lock (_lock)
            {
                if (!ReferenceEquals(_socketManager, manager) || !_isHost)
                {
                    return;
                }

                /*
                    The rendezvous authenticates the identity, so the
                    claim is Steam's, not the peer's; the fence only
                    checks the room advertised it. The accept happens on
                    the tick (ProcessPendingAccepts), within the grace
                    window.
                */
                _pendingIncoming.Add(
                    new PendingAccept(
                        connection,
                        info.Identity.SteamId.Value,
                        SystemClock.Instance.ElapsedMilliseconds + AcceptGraceMilliseconds
                    )
                );
            }
        }

        private void OnPeerEstablished(
            SocketManager manager,
            Connection connection,
            ConnectionInfo info
        )
        {
            lock (_lock)
            {
                if (!ReferenceEquals(_socketManager, manager) || !_isHost)
                {
                    return;
                }

                if (!_acceptedConnections.Remove(connection.Id))
                {
                    /*
                        A Connected state for a connection this session
                        never accepted (a stale dispatch from before a
                        teardown, or a stranger's rendezvous) is not a
                        fenced peer.
                    */
                    connection.Close(
                        false,
                        FenceRefusalEndReason,
                        "Signal Fish: the connection was never accepted by the fence."
                    );
                    return;
                }

                _trackedPeers[connection.Id] = info.Identity.SteamId.Value;
                SteamPeerConnected?.Invoke(info.Identity.SteamId.Value);
            }
        }

        private bool OnPeerConnectionEnded(SocketManager manager, Connection connection)
        {
            lock (_lock)
            {
                if (!ReferenceEquals(_socketManager, manager) || !_isHost)
                {
                    /*
                        A stale dispatch routed through Facepunch's
                        registry after a teardown; the caller skips the
                        explicit close, which could hit a recycled id.
                    */
                    return false;
                }

                for (int i = _pendingIncoming.Count - 1; i >= 0; i--)
                {
                    if (_pendingIncoming[i].Connection == connection)
                    {
                        _pendingIncoming.RemoveAt(i);
                    }
                }

                /*
                    Always retire the accepted id, not only for tracked
                    peers: a connection that dies between its accept and
                    its Connected state is in the accepted set alone, and
                    a stale id there would let a recycled id pose as a
                    fenced peer later.
                */
                _acceptedConnections.Remove(connection.Id);
                if (_trackedPeers.Remove(connection.Id, out ulong remoteSteamId))
                {
                    SteamPeerDisconnected?.Invoke(remoteSteamId);
                }

                return true;
            }
        }

        private void OnHostConnectionEstablished(ConnectionManager manager, ConnectionInfo info)
        {
            lock (_lock)
            {
                if (!ReferenceEquals(_connectionManager, manager))
                {
                    return;
                }

                if (_phase != SteamPhase.Connecting)
                {
                    return;
                }

                ulong remoteSteamId = info.Identity.SteamId.Value;
                if (remoteSteamId != _hostSteamId)
                {
                    manager.Close(
                        false,
                        FenceRefusalEndReason,
                        "Signal Fish: the connected identity is not the published host."
                    );
                    FailStaged(
                        new InvalidOperationException(
                            "The Steam connection's identity "
                                + remoteSteamId.ToString(CultureInfo.InvariantCulture)
                                + " does not match the published host id "
                                + _hostSteamId.ToString(CultureInfo.InvariantCulture)
                                + "."
                        )
                    );
                    return;
                }

                CompleteStaged();
            }
        }

        private void OnHostConnectionEnded(ConnectionManager manager, ConnectionInfo info)
        {
            lock (_lock)
            {
                if (!ReferenceEquals(_connectionManager, manager))
                {
                    return;
                }

                if (_phase == SteamPhase.Connecting)
                {
                    FailStaged(
                        new InvalidOperationException(
                            "The Steam connection to the host closed before it was established "
                                + "(end reason "
                                + ((int)info.EndReason).ToString(CultureInfo.InvariantCulture)
                                + ")."
                        )
                    );
                }
                else if (_phase == SteamPhase.Live)
                {
                    Fail("The Steam connection to the host closed.");
                }
            }
        }

        private void ProcessPendingAccepts()
        {
            lock (_lock)
            {
                if (!_isHost || _pendingIncoming.Count == 0)
                {
                    return;
                }

                long now = SystemClock.Instance.ElapsedMilliseconds;
                for (int i = _pendingIncoming.Count - 1; i >= 0; i--)
                {
                    PendingAccept pending = _pendingIncoming[i];
                    if (_advertisedPeers.Contains(pending.RemoteSteamId))
                    {
                        Result accepted = pending.Connection.Accept();
                        if (accepted == Result.OK)
                        {
                            _acceptedConnections.Add(pending.Connection.Id);
                        }
                        else
                        {
                            pending.Connection.Close(
                                false,
                                FenceRefusalEndReason,
                                "Signal Fish: the accept failed."
                            );
                        }

                        _pendingIncoming.RemoveAt(i);
                        continue;
                    }

                    if (now >= pending.DeadlineTicks)
                    {
                        pending.Connection.Close(
                            false,
                            FenceRefusalEndReason,
                            "Signal Fish: the peer's SteamId64 never arrived on the room's lane."
                        );
                        _pendingIncoming.RemoveAt(i);
                    }
                }
            }
        }

        private void EnforceSteamDeadline()
        {
            lock (_lock)
            {
                if (_active is null || _phase != SteamPhase.Connecting)
                {
                    return;
                }

                if (SystemClock.Instance.ElapsedMilliseconds <= _activeDeadline)
                {
                    return;
                }

                FailStaged(
                    new TimeoutException(
                        $"Timed out after {SteamStartTimeoutSeconds:0.#}s waiting for the Steam connection to the host."
                    )
                );
            }
        }

        private void FailStaged(Exception failure)
        {
            /*
                Caller holds the lock. The failing start takes the whole
                session with it: its client is disposed and Teardown
                re-enters under the same lock (C# locks are reentrant on
                the owning thread).
            */
            StagedStart? start = _active;
            TaskCompletionSource<bool>? completion = _activeCompletion;
            if (start is null)
            {
                return;
            }

            _active = null;
            _activeCompletion = null;
            _phase = default(SteamPhase);
            _ = DisposeQuietlyAsync(start.GetValueOrDefault().Client);
            Teardown();
            completion?.TrySetException(failure);
        }

        private void DrainSession()
        {
            SignalFishClient? client = RequireLiveClientOrNull();
            if (client is null)
            {
                return;
            }

            /*
                A teardown on a background failure path can dispose this
                client mid-drain: TryDequeueEvent keeps serving its
                buffered events, so liveness is re-checked per event.
            */
            try
            {
                DrainEvents(client);
            }
            catch (ObjectDisposedException) { }
        }

        private void DrainEvents(SignalFishClient client)
        {
            while (client.TryDequeueEvent(out PollEvent pollEvent))
            {
                if (!ReferenceEquals(RequireLiveClientOrNull(), client))
                {
                    return;
                }

                if (pollEvent.Kind == PollEventKind.GameData)
                {
                    if (ConsumeIdentity(pollEvent.GameData.Payload.Span))
                    {
                        continue;
                    }
                }

                if (pollEvent.Kind == PollEventKind.PlayerJoined && RequiresRepublish())
                {
                    /*
                        The game-data lane replays nothing: a member that
                        joins after the first publication misses it, so
                        each side re-publishes on every join.
                    */
                    if (!RepublishLocalIdentity())
                    {
                        if (RequireLiveClientOrNull() is null)
                        {
                            /*
                                The session was torn down mid-republish;
                                the drain's next liveness check exits, and
                                a dead session is not a coordination
                                failure.
                            */
                            continue;
                        }

                        Fail("The re-publish of the local SteamId64 was refused.");
                        return;
                    }

                    continue;
                }

                if (
                    pollEvent.Kind == PollEventKind.AuthorityChanged
                    && IsHostAndLive()
                    && !pollEvent.AuthorityChanged.YouAreAuthority
                )
                {
                    /*
                        The grant broadcast that follows this host's own
                        successful authority request is benign — the
                        server answers with AuthorityResponse AND a
                        room-wide AuthorityChanged whose you_are_authority
                        is true for us. Only a move away from this host
                        orphans the fence: fail loudly rather than
                        accepting strangers.
                    */
                    Fail("The room's authority moved; re-host the room.");
                    return;
                }

                string? failure = DescribeFailure(pollEvent);
                if (failure is not null)
                {
                    Fail(failure);
                    return;
                }
            }
        }

        private bool RequiresRepublish()
        {
            lock (_lock)
            {
                return _published && _localSteamId != 0;
            }
        }

        private bool IsHostAndLive()
        {
            lock (_lock)
            {
                return _isHost && _client is not null;
            }
        }

        private bool RepublishLocalIdentity()
        {
            lock (_lock)
            {
                ulong localSteamId = _localSteamId;
                string laneKey = _isHost
                    ? SteamIdentityEnvelope.HostPropertyName
                    : SteamIdentityEnvelope.PeerPropertyName;
                return TryPublishIdentity(laneKey, localSteamId);
            }
        }

        private bool ConsumeIdentity(ReadOnlySpan<byte> payload)
        {
            lock (_lock)
            {
                if (_isHost)
                {
                    if (
                        SteamIdentityEnvelope.TryRead(
                            payload,
                            SteamIdentityEnvelope.PeerPropertyName,
                            out string steamId
                        )
                        && ulong.TryParse(
                            steamId,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out ulong peerSteamId
                        )
                    )
                    {
                        _advertisedPeers.Add(peerSteamId);
                        return true;
                    }

                    return false;
                }

                if (
                    !SteamIdentityEnvelope.TryRead(
                        payload,
                        SteamIdentityEnvelope.HostPropertyName,
                        out string hostId
                    )
                    || !ulong.TryParse(
                        hostId,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out ulong hostSteamId
                    )
                )
                {
                    return false;
                }

                if (_hostSteamId == 0)
                {
                    _hostSteamId = hostSteamId;
                }
                else if (_hostSteamId != hostSteamId)
                {
                    Fail("The host's published Steam identity changed mid-session.");
                }

                return true;
            }
        }

        private static string? DescribeFailure(in PollEvent pollEvent)
        {
            if (
                pollEvent.Kind == PollEventKind.RoomJoinFailed
                || pollEvent.Kind == PollEventKind.ServerError
            )
            {
                return "The Signal Fish session failed: " + pollEvent.Failure.Reason + ".";
            }

            if (pollEvent.Kind == PollEventKind.Disconnected)
            {
                return "The room connection closed.";
            }

            /*
                Both kinds mean the wire just proved broken (a malformed
                frame, a protocol rule the server rejected); the client
                surfaces them fail-closed and so does the coordination.
            */
            if (pollEvent.Kind == PollEventKind.ProtocolViolation)
            {
                return "The room connection broke the protocol: " + pollEvent.Failure.Reason + ".";
            }

            if (pollEvent.Kind == PollEventKind.DecodeFailed)
            {
                return "The room connection carried an undecodable frame.";
            }

            return null;
        }

        private void Fail(string reason)
        {
            /*
                A start still in flight fails through its own task: the
                awaiter gets the real reason, and CoordinationFailed
                stays reserved for failures of a live session. FailStaged
                re-enters under the same lock (C# locks are reentrant on
                the owning thread) and takes the whole session with it,
                exactly like the direct path below.
            */
            lock (_lock)
            {
                if (_active is not null)
                {
                    FailStaged(new InvalidOperationException(reason));
                    return;
                }
            }

            Debug.LogWarning("[SignalFishFacepunchSteamIdentityBootstrap] " + reason);
            CoordinationFailed?.Invoke(reason);
            Teardown();
        }

        private void Teardown()
        {
            /*
                The Steam closes happen under the same lock the next
                session's BeginSession needs: an old teardown can never
                run its Steam-state reset against a new session's
                already-kicked state. Every lock acquisition here is
                reentrant-safe with the Fail/FailStaged callers that
                already hold it.
            */
            TaskCompletionSource<bool>? stagedCompletion;
            TaskCompletionSource<bool>? activeCompletion;
            SignalFishClient? live;
            lock (_lock)
            {
                _generation++;
                live = _client ?? _session;
                _client = null;
                _session = null;
                _staged = null;
                stagedCompletion = _stagedCompletion;
                _stagedCompletion = null;
                _joinedRoomCode = null;
                _hostSteamId = 0;
                _localSteamId = 0;

                activeCompletion = _activeCompletion;
                _active = null;
                _activeCompletion = null;
                _phase = default(SteamPhase);
                _published = false;

                /*
                    Reset the role with the session: a stale drain event
                    landing after this teardown must not be able to feed
                    the next session's accept fence with the old
                    session's advertised peers.
                */
                _isHost = false;

                /*
                    Only bootstrap-created Steam state closes here; the
                    game's own sockets and connections are not in any of
                    these fields. The Steam API may already be shut down
                    (the game's teardown can beat this component's),
                    where every interface call throws — a dead Steam
                    leaves nothing to free, so the closes are guarded and
                    the bookkeeping resets win.
                */
                try
                {
                    if (_connectionManager is not null)
                    {
                        _connectionManager.Close(
                            false,
                            0,
                            "Signal Fish: the coordination session was torn down."
                        );
                        _connectionManager = null;
                    }

                    if (_socketManager is not null)
                    {
                        foreach (PendingAccept pending in _pendingIncoming)
                        {
                            pending.Connection.Close(
                                false,
                                0,
                                "Signal Fish: the coordination session was torn down."
                            );
                        }

                        _pendingIncoming.Clear();
                        foreach (uint handle in _trackedPeers.Keys)
                        {
                            new Connection { Id = handle }.Close(
                                false,
                                0,
                                "Signal Fish: the coordination session was torn down."
                            );
                        }

                        foreach (uint handle in _acceptedConnections)
                        {
                            /*
                                Accepted-but-not-yet-established
                                connections: freed explicitly like their
                                tracked siblings, not left to the listen
                                socket's ungraceful sweep.
                            */
                            new Connection { Id = handle }.Close(
                                false,
                                0,
                                "Signal Fish: the coordination session was torn down."
                            );
                        }

                        _trackedPeers.Clear();
                        _acceptedConnections.Clear();
                        _advertisedPeers.Clear();

                        /*
                            Null the socket's registry entry before the
                            close: Facepunch never removes entries, and
                            a nulled entry turns every stale dispatch to
                            this socket into a no-op instead of a routed
                            callback into a dead session.
                        */
                        Socket socketHandle = _socketManager.Socket;
                        socketHandle.Manager = null!;
                        _socketManager.Close();
                        _socketManager = null;
                    }
                }
                catch (Exception)
                {
                    /*
                        Steam is gone; its resources went with it. The
                        bookkeeping resets below still run so the next
                        session starts from a clean slate.
                    */
                    _pendingIncoming.Clear();
                    _trackedPeers.Clear();
                    _acceptedConnections.Clear();
                    _advertisedPeers.Clear();
                    _connectionManager = null;
                    _socketManager = null;
                }
            }

            /*
                The start tasks are public awaited APIs: a teardown must
                settle them, or Shutdown/OnDestroy (or any room-side
                failure) while a start is staged or in flight hangs the
                awaiter forever.
            */
            if (live is not null)
            {
                _ = DisposeQuietlyAsync(live);
            }

            OperationCanceledException canceled = new("The coordination session was torn down.");
            stagedCompletion?.TrySetException(canceled);
            activeCompletion?.TrySetException(canceled);
        }

        private int BeginSession()
        {
            lock (_lock)
            {
                if (_session is not null || _client is not null || _staged is not null)
                {
                    throw new InvalidOperationException(
                        "This bootstrap is already coordinating a session."
                    );
                }

                _generation++;
                return _generation;
            }
        }

        private bool IsFresh(int generation)
        {
            lock (_lock)
            {
                return _generation == generation;
            }
        }

        private void EnsureFresh(int generation)
        {
            if (!IsFresh(generation))
            {
                throw new OperationCanceledException("The coordination session was torn down.");
            }
        }

        private SignalFishClient CreateClient()
        {
            ITransport transport = TransportFactory?.Invoke() ?? new WebSocketTransport();
            return new SignalFishClient(
                transport,
                SystemClock.Instance,
                new SignalFishClientOptions()
            );
        }

        private SignalFishClient? RequireLiveClientOrNull()
        {
            lock (_lock)
            {
                return _client;
            }
        }

        private static ulong ReadLocalSteamId()
        {
            if (!SteamClient.IsValid)
            {
                throw new InvalidOperationException(
                    "Steam is not available; initialize the Facepunch Steamworks API "
                        + "(SteamClient.Init with asyncCallbacks: false) before coordinating "
                        + "a session."
                );
            }

            ulong steamId;
            try
            {
                steamId = SteamClient.SteamId.Value;
            }
            catch (Exception failure)
            {
                throw new InvalidOperationException(
                    "Steam is not available; initialize the Facepunch Steamworks API "
                        + "(SteamClient.Init with asyncCallbacks: false) before coordinating "
                        + "a session.",
                    failure
                );
            }

            if (steamId == 0)
            {
                /*
                    A nil id means Steam is initialized but no user is
                    logged on; failing here keeps the zero out of the
                    envelope (which would refuse it anyway, with a
                    misleading publish-error diagnosis).
                */
                throw new InvalidOperationException(
                    "Steam has no logged-on user; the SteamId64 exchange needs one."
                );
            }

            return steamId;
        }

        /*
            Caller holds the lock; the client was assigned by the kick's
            caller before this runs.
        */
        private bool TryPublishIdentity(string propertyName, ulong steamId)
        {
            Span<byte> scratch = stackalloc byte[SteamIdentityEnvelope.MaxEnvelopeLength];
            if (
                !SteamIdentityEnvelope.TryWrite(
                    propertyName,
                    steamId.ToString(CultureInfo.InvariantCulture),
                    scratch,
                    out int written
                )
            )
            {
                return false;
            }

            byte[] payload = new byte[written];
            scratch.Slice(0, written).CopyTo(payload);
            SignalFishClient? client = _client;
            return client is not null && client.SendGameData(new GameDataMessage(payload)).Accepted;
        }

        private static void SendOrThrow(CommandSend send, string what)
        {
            if (!send.Accepted)
            {
                throw new InvalidOperationException(
                    "The room refused " + what + ": " + send.Refusal + "."
                );
            }
        }

        private static void ThrowForFailure(in PollEvent pollEvent)
        {
            if (
                pollEvent.Kind == PollEventKind.RoomJoinFailed
                || pollEvent.Kind == PollEventKind.ServerError
            )
            {
                throw new InvalidOperationException(
                    "The Signal Fish session failed: " + pollEvent.Failure.Reason + "."
                );
            }

            if (pollEvent.Kind == PollEventKind.Disconnected)
            {
                throw new InvalidOperationException(
                    "The room connection closed while the start was running."
                );
            }

            if (pollEvent.Kind == PollEventKind.ProtocolViolation)
            {
                throw new InvalidOperationException(
                    "The room connection broke the protocol: " + pollEvent.Failure.Reason + "."
                );
            }

            if (pollEvent.Kind == PollEventKind.DecodeFailed)
            {
                throw new InvalidOperationException(
                    "The room connection carried an undecodable frame."
                );
            }
        }

        private static async Task DisposeQuietlyAsync(SignalFishClient client)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception) { }
        }
    }
}
#endif
