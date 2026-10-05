#if SIGNALFISH_STEAMWORKSNET
#nullable enable
namespace SignalFish.Client.Adapters.SteamworksNet
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
    using UnityEngine;

    /*
        Bootstraps Steam sockets onto a Signal Fish room. Steam has no
        matchmaking to lean on, so the room itself is the fence: the host
        opens a P2P listen socket and accepts an incoming Steam connection
        only once that peer's SteamId64 was published over the room's
        game-data lane; the client dials the host id published on the same
        lane. Every Steamworks call runs on the bootstrap's tick (or in
        the status callback, which SteamAPI.RunCallbacks dispatches on
        that same tick) — none of it touches the Unity scene. The Steam
        bookkeeping fields are shared between the tick, the callback, and
        teardowns from any thread, so every access takes the lock, and
        teardown closes the Steam state under that same lock so a new
        session's kick can never interleave with an old session's close.
    */

    /// <summary>
    /// Bootstraps Steam sockets onto a Signal Fish room: the room owns
    /// membership and timing, Steam owns the transport, and the exchange
    /// carries the two SteamId64s each side needs. The host joins (or
    /// creates) the room, takes the authority, opens a Steam P2P listen
    /// socket, and publishes its SteamId64 over the room's game-data lane
    /// (re-published on every join so late joiners never depend on
    /// timing); a client joins the room by code, marks itself ready, and
    /// dials the host SteamId64 that arrives on that lane. The host
    /// accepts an incoming Steam connection only after that peer's
    /// SteamId64 was published on the lane, within a grace window — the
    /// room's membership is the accept fence. Peers surface as
    /// <c>SteamPeerConnected</c>/<c>SteamPeerDisconnected</c> with the
    /// SteamId64s the game then owns for its own traffic. Starts may be
    /// awaited from any thread; the Steam calls they need run on the
    /// bootstrap's tick. This is deliberately not a transport bridge:
    /// the bootstrap establishes and fences the Steam connections, and
    /// the game owns what flows over them.
    /// </summary>
    public sealed class SignalFishSteamIdentityBootstrap : MonoBehaviour
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
            public HSteamNetConnection Connection { get; }

            public ulong RemoteSteamId { get; }

            public long DeadlineTicks { get; }

            public PendingAccept(
                HSteamNetConnection connection,
                ulong remoteSteamId,
                long deadlineTicks
            )
            {
                Connection = connection;
                RemoteSteamId = remoteSteamId;
                DeadlineTicks = deadlineTicks;
            }
        }

        private const int WaitPollMilliseconds = 10;

        private const int KickSettleMilliseconds = 2000;

        /*
            The app-range connection end reason for a fence refusal
            (k_ESteamNetConnectionEnd_App_Min); app codes keep Steam's
            own diagnostic ranges untouched.
        */
        private const int FenceRefusalEndReason = 1000;

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

        private int StartTimeoutMilliseconds => (int)(_startTimeoutSeconds * 1000f);

        private int SteamStartTimeoutMilliseconds => (int)(_steamStartTimeoutSeconds * 1000f);

        private int AcceptGraceMilliseconds => (int)(_acceptGraceSeconds * 1000f);

        private readonly object _lock = new object();

        private string _endpoint = "ws://127.0.0.1:3536/v2/ws";

        private string _gameName = "steamworks-game";

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
            Steam phase state: written by the tick and the status
            callback (both main-thread) and by teardowns from any
            thread, so every access takes the lock.
        */
        private StagedStart? _active;

        private TaskCompletionSource<bool>? _activeCompletion;

        private long _activeDeadline;

        private SteamPhase _phase = default(SteamPhase);

        private bool _published;

        private Callback<SteamNetConnectionStatusChangedCallback_t>? _statusCallback;

        private HSteamListenSocket _listenSocket = HSteamListenSocket.Invalid;

        private HSteamNetConnection _hostConnection = HSteamNetConnection.Invalid;

        private readonly Dictionary<uint, ulong> _trackedPeers = new Dictionary<uint, ulong>();

        private readonly HashSet<uint> _acceptedHandles = new HashSet<uint>();

        private readonly List<PendingAccept> _pendingIncoming = new List<PendingAccept>();

        private readonly HashSet<ulong> _advertisedPeers = new HashSet<ulong>();

        /// <summary>
        /// Starts the host side: join (or create) the Signal Fish room,
        /// take the authority, publish the local SteamId64 over the
        /// room's game-data lane, open the Steam P2P listen socket, and
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

        /// <summary>Tears the session down: closes the sockets, connections, and callback this bootstrap created.</summary>
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
                The staged start is driven by Update() and the Steam
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
                    phase machine, register the callback, and kick. A
                    teardown on another thread either fully precedes this
                    region (the generation check above) or fully follows
                    it — it can never interleave between the promotion
                    and the Steam state the kick creates.
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
                    _statusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(
                        OnConnectionStatusChanged
                    );

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
                never existed.
            */
            _listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(
                _listenVirtualPort,
                0,
                null!
            );
            if (_listenSocket == HSteamListenSocket.Invalid)
            {
                throw new InvalidOperationException("Steam refused the P2P listen socket.");
            }

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
            SteamNetworkingIdentity identity = new SteamNetworkingIdentity();
            identity.SetSteamID64(_hostSteamId);
            _hostConnection = SteamNetworkingSockets.ConnectP2P(ref identity, 0, 0, null!);
            if (_hostConnection == HSteamNetConnection.Invalid)
            {
                throw new InvalidOperationException("Steam refused the P2P connect.");
            }

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
                if (_statusCallback is null)
                {
                    return;
                }
            }

            try
            {
                SteamAPI.RunCallbacks();
            }
            catch (Exception failure)
            {
                /*
                    Steam going away under a live session (the API was
                    shut down, the client closed) fails the session
                    instead of throwing out of Update every tick.
                */
                Fail("Steam callback dispatch failed: " + failure.Message + ".");
            }
        }

        private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t callback)
        {
            /*
                Dispatched on the tick's RunCallbacks call. Everything
                this touches is lock-guarded bookkeeping; a teardown on
                another thread either fully precedes this callback (the
                disposed handler is never invoked) or fully follows it.
            */
            lock (_lock)
            {
                if (_statusCallback is null)
                {
                    return;
                }

                HSteamNetConnection connection = callback.m_hConn;
                switch (callback.m_info.m_eState)
                {
                    case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                        OnIncomingConnecting(connection, callback.m_info);
                        break;

                    case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                        OnConnectionEstablished(connection, callback.m_info);
                        break;

                    case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                    case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                        OnConnectionEnded(connection, callback.m_info);
                        break;

                    default:
                        break;
                }
            }
        }

        /*
            Status-callback branches run under the lock. Each one is
            scoped to state this generation created — the current listen
            socket, the accepted-handle set, or this session's own host
            connection — so a recycled handle or a stale dispatch from
            before a teardown can never land in the new session's
            bookkeeping.
        */
        private void OnIncomingConnecting(
            HSteamNetConnection connection,
            SteamNetConnectionInfo_t info
        )
        {
            if (!_isHost)
            {
                if (connection == _hostConnection)
                {
                    /*
                        Our own ConnectP2P posts a Connecting state for its
                        own connection (callbacks are posted for
                        connections created by our own API calls); the
                        Connecting phase waits for that dial's Connected
                        state, so there is nothing to fence here.
                    */
                    return;
                }

                SteamNetworkingSockets.CloseConnection(
                    connection,
                    FenceRefusalEndReason,
                    "Signal Fish: not accepting connections here.",
                    false
                );
                return;
            }

            if (
                _listenSocket == HSteamListenSocket.Invalid
                || info.m_hListenSocket != _listenSocket
            )
            {
                /*
                    An incoming connection on a socket this session never
                    opened; there is nothing to fence, so refuse it
                    politely.
                */
                SteamNetworkingSockets.CloseConnection(
                    connection,
                    FenceRefusalEndReason,
                    "Signal Fish: not accepting connections here.",
                    false
                );
                return;
            }

            /*
                The rendezvous authenticates the identity, so the claim
                is Steam's, not the peer's; the fence only checks the
                room advertised it.
            */
            _pendingIncoming.Add(
                new PendingAccept(
                    connection,
                    info.m_identityRemote.GetSteamID64(),
                    SystemClock.Instance.ElapsedMilliseconds + AcceptGraceMilliseconds
                )
            );
        }

        private void OnConnectionEstablished(
            HSteamNetConnection connection,
            SteamNetConnectionInfo_t info
        )
        {
            ulong remoteSteamId = info.m_identityRemote.GetSteamID64();
            if (!_isHost)
            {
                if (_phase != SteamPhase.Connecting || connection != _hostConnection)
                {
                    return;
                }

                if (remoteSteamId != _hostSteamId)
                {
                    SteamNetworkingSockets.CloseConnection(
                        connection,
                        FenceRefusalEndReason,
                        "Signal Fish: the connected identity is not the published host.",
                        false
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
                return;
            }

            if (!_acceptedHandles.Remove(connection.m_HSteamNetConnection))
            {
                /*
                    A Connected state for a connection this session never
                    accepted (a stale dispatch from before a teardown, or
                    a stranger's rendezvous) is not a fenced peer.
                */
                SteamNetworkingSockets.CloseConnection(
                    connection,
                    FenceRefusalEndReason,
                    "Signal Fish: the connection was never accepted by the fence.",
                    false
                );
                return;
            }

            _trackedPeers[connection.m_HSteamNetConnection] = remoteSteamId;
            SteamPeerConnected?.Invoke(remoteSteamId);
        }

        private void OnConnectionEnded(
            HSteamNetConnection connection,
            SteamNetConnectionInfo_t info
        )
        {
            if (!_isHost)
            {
                if (_phase == SteamPhase.Connecting && connection == _hostConnection)
                {
                    string detail = string.IsNullOrEmpty(info.m_szEndDebug)
                        ? "no detail"
                        : info.m_szEndDebug;
                    FailStaged(
                        new InvalidOperationException(
                            "The Steam connection to the host closed before it was established: "
                                + detail
                                + "."
                        )
                    );
                }
                else if (_phase == SteamPhase.Live && connection == _hostConnection)
                {
                    Fail("The Steam connection to the host closed.");
                }

                return;
            }

            for (int i = _pendingIncoming.Count - 1; i >= 0; i--)
            {
                if (_pendingIncoming[i].Connection == connection)
                {
                    _pendingIncoming.RemoveAt(i);
                }
            }

            /*
                Always retire the accepted handle, not only for tracked
                peers: a connection that dies between its accept and its
                Connected state is in the accepted set alone, and a
                stale handle there would let a recycled handle pose as a
                fenced peer later.
            */
            _acceptedHandles.Remove(connection.m_HSteamNetConnection);
            if (_trackedPeers.Remove(connection.m_HSteamNetConnection, out ulong remoteSteamId))
            {
                SteamPeerDisconnected?.Invoke(remoteSteamId);
            }

            /*
                The SDK requires an explicit CloseConnection after a
                ClosedByPeer/ProblemDetectedLocally dispatch to free the
                connection's local resources; closing an already-ended
                connection is exactly that free.
            */
            SteamNetworkingSockets.CloseConnection(connection, 0, null!, false);
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
                        EResult accepted = SteamNetworkingSockets.AcceptConnection(
                            pending.Connection
                        );
                        if (accepted == EResult.k_EResultOK)
                        {
                            _acceptedHandles.Add(pending.Connection.m_HSteamNetConnection);
                        }
                        else
                        {
                            SteamNetworkingSockets.CloseConnection(
                                pending.Connection,
                                FenceRefusalEndReason,
                                "Signal Fish: the accept failed.",
                                false
                            );
                        }

                        _pendingIncoming.RemoveAt(i);
                        continue;
                    }

                    if (now >= pending.DeadlineTicks)
                    {
                        SteamNetworkingSockets.CloseConnection(
                            pending.Connection,
                            FenceRefusalEndReason,
                            "Signal Fish: the peer's SteamId64 never arrived on the room's lane.",
                            false
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
            Debug.LogWarning("[SignalFishSteamIdentityBootstrap] " + reason);
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
                    game's own sockets, connections, and callbacks are
                    not in any of these fields. The Steam API may already
                    be shut down (the game's SteamManager.OnDestroy can
                    beat this component's), where every interface call
                    throws — a dead Steam leaves nothing to free, so the
                    closes are guarded and the bookkeeping resets win.
                */
                try
                {
                    _statusCallback?.Dispose();
                    _statusCallback = null;
                    if (_hostConnection != HSteamNetConnection.Invalid)
                    {
                        SteamNetworkingSockets.CloseConnection(_hostConnection, 0, null!, false);
                        _hostConnection = HSteamNetConnection.Invalid;
                    }

                    foreach (PendingAccept pending in _pendingIncoming)
                    {
                        SteamNetworkingSockets.CloseConnection(pending.Connection, 0, null!, false);
                    }

                    _pendingIncoming.Clear();
                    foreach (uint handle in _trackedPeers.Keys)
                    {
                        SteamNetworkingSockets.CloseConnection(
                            new HSteamNetConnection(handle),
                            0,
                            null!,
                            false
                        );
                    }

                    _trackedPeers.Clear();
                    _acceptedHandles.Clear();
                    _advertisedPeers.Clear();
                    if (_listenSocket != HSteamListenSocket.Invalid)
                    {
                        SteamNetworkingSockets.CloseListenSocket(_listenSocket);
                        _listenSocket = HSteamListenSocket.Invalid;
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
                    _acceptedHandles.Clear();
                    _advertisedPeers.Clear();
                    _statusCallback = null;
                    _hostConnection = HSteamNetConnection.Invalid;
                    _listenSocket = HSteamListenSocket.Invalid;
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
            try
            {
                return SteamUser.GetSteamID().m_SteamID;
            }
            catch (Exception failure)
            {
                throw new InvalidOperationException(
                    "Steam is not available; initialize the Steamworks API before coordinating a session.",
                    failure
                );
            }
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
