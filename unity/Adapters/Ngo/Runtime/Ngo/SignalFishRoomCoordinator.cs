#if SIGNALFISH_NGO
#nullable enable
namespace SignalFish.Client.Adapters.Ngo
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using SignalFish.Client.Async;
    using SignalFish.Client.Core;
    using SignalFish.Client.Polling;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.Transport;
    using Unity.Netcode;
    using UnityEngine;

    /// <summary>
    /// Coordinates a Netcode for GameObjects (NGO) session with a Signal
    /// Fish room. NGO keeps owning the netcode and its Unity Transport
    /// connection; the Signal Fish room owns matchmaking and membership:
    /// the host allocates a Unity Relay server through a game-supplied
    /// hook and hands the join code to members over the room's game-data
    /// lane, and every NGO connection is approved only when the claimed
    /// Signal Fish player id is a live room member. This is deliberately
    /// not a transport bridge — NGO's transport is Unity Transport, and
    /// binding it to a Relay allocation stays one game-side hook call.
    /// Starts may be awaited from any thread; the engine calls they need
    /// run on the next coordinator tick, and the returned task completes
    /// once they did.
    /// </summary>
    public sealed class SignalFishRoomCoordinator : MonoBehaviour
    {
        private enum StagedStartKind : byte
        {
            [Obsolete(
                "This value only exists so the enum default (0) is not a start. Compare against default(StagedStartKind) instead."
            )]
            None = 0,

            Host = 1,

            Client = 2,
        }

        private readonly struct StagedStart
        {
            public StagedStartKind Kind { get; }

            public int Generation { get; }

            public SignalFishClient Client { get; }

            public RoomMembership Membership { get; }

            public RoomSnapshot Snapshot { get; }

            public string JoinCode { get; }

            public StagedStart(
                StagedStartKind kind,
                int generation,
                SignalFishClient client,
                RoomMembership membership,
                in RoomSnapshot snapshot,
                string joinCode
            )
            {
                Kind = kind;
                Generation = generation;
                Client = client;
                Membership = membership;
                Snapshot = snapshot;
                JoinCode = joinCode;
            }
        }

        private const int WaitPollMilliseconds = 10;

        private const int StagedStartTimeoutMilliseconds = 10_000;

        /// <summary>The room session failed after the engine start; the reason names why.</summary>
        public event Action<string>? CoordinationFailed;

        /// <summary>
        /// A client received a Unity Relay join code over the live room's
        /// game-data lane (the host re-broadcasts whenever a player
        /// joins). The code that starts a client goes to
        /// <see cref="RelayJoinBinder"/> instead.
        /// </summary>
        public event Action<string>? RelayJoinCodeReceived;

        /// <summary>The Signal Fish endpoint (the v2 relay floor; v3 negotiates on top).</summary>
        public string Endpoint
        {
            get { return _endpoint; }
            set { _endpoint = value; }
        }

        /// <summary>The game name the room is created with (required).</summary>
        public string GameName
        {
            get { return _gameName; }
            set { _gameName = value; }
        }

        /// <summary>The local player's display name (required).</summary>
        public string PlayerName
        {
            get { return _playerName; }
            set { _playerName = value; }
        }

        /// <summary>The public app ID for deployments that enforce an allowlist (optional).</summary>
        public string? AppId
        {
            get { return _appId; }
            set { _appId = value; }
        }

        /// <summary>The deployment's tenant credential (optional; a secret — never logged).</summary>
        public string? ConnectToken
        {
            get { return _connectToken; }
            set { _connectToken = value; }
        }

        /// <summary>The inbound per-frame byte bound for the room session.</summary>
        public int MaxFrameBytes
        {
            get { return _maxFrameBytes; }
            set { _maxFrameBytes = value; }
        }

        /// <summary>How long the room handshake may take before the start fails.</summary>
        public float StartTimeoutSeconds
        {
            get { return _startTimeoutSeconds; }
            set { _startTimeoutSeconds = value; }
        }

        /// <summary>
        /// How long a client waits for the relay join code, and how long
        /// either game-side relay hook may take, before the start fails.
        /// </summary>
        public float JoinCodeTimeoutSeconds
        {
            get { return _joinCodeTimeoutSeconds; }
            set { _joinCodeTimeoutSeconds = value; }
        }

        /// <summary>
        /// Whether an approved NGO connection spawns its default player
        /// object; off when the game spawns its own prefabs.
        /// </summary>
        public bool CreatePlayerObject
        {
            get { return _createPlayerObject; }
            set { _createPlayerObject = value; }
        }

        /// <summary>
        /// The host's Unity Relay hook: authenticate with Unity Gaming
        /// Services, allocate a Relay server, bind it to Unity Transport,
        /// and return the allocation's join code. Runs off the main
        /// thread; required before <see cref="StartHostAsync"/>.
        /// </summary>
        public Func<Task<string>>? RelayAllocationRequest { get; set; }

        /// <summary>
        /// The client's Unity Relay hook: bind the join code to Unity
        /// Transport (allocate the join through Unity Gaming Services,
        /// then <c>SetRelayServerData</c>). Runs off the engine main
        /// thread; the coordinator stages the NGO client start only after
        /// the returned task completes. Required before
        /// <see cref="StartClientAsync"/>.
        /// </summary>
        public Func<string, Task>? RelayJoinBinder { get; set; }

        /// <summary>
        /// Replaces the default WebSocket transport (WebGL deployments
        /// inject their reference transport here).
        /// </summary>
        public Func<ITransport>? TransportFactory { get; set; }

        /// <summary>Gets whether a room session is live or starting.</summary>
        public bool IsCoordinating
        {
            get
            {
                lock (_lock)
                {
                    return _client is not null || _session is not null;
                }
            }
        }

        /// <summary>Gets the Signal Fish room code, once joined.</summary>
        public string? JoinedRoomCode
        {
            get { return _joinedRoomCode; }
        }

        /// <summary>Gets the Unity Relay join code, once allocated or received.</summary>
        public string? RelayJoinCode
        {
            get { return _relayJoinCode; }
        }

        /// <summary>Gets the local Signal Fish player id, once joined.</summary>
        public Guid? LocalPlayerId
        {
            get { return _localPlayerId; }
        }

        /// <summary>Gets how many players the roster currently holds.</summary>
        public int RosterCount
        {
            get { return _roster.Count; }
        }

        private int StartTimeoutMilliseconds
        {
            get { return (int)(_startTimeoutSeconds * 1000f); }
        }

        private int JoinCodeTimeoutMilliseconds
        {
            get { return (int)(_joinCodeTimeoutSeconds * 1000f); }
        }

        [SerializeField]
        private string _endpoint = "ws://127.0.0.1:3536/v2/ws";

        [SerializeField]
        private string _gameName = "ngo-game";

        [SerializeField]
        private string _playerName = "player";

        [SerializeField]
        private string? _appId;

        [SerializeField]
        private string? _connectToken;

        [SerializeField]
        private int _maxFrameBytes = 64 * 1024;

        [SerializeField]
        private float _startTimeoutSeconds = 10f;

        [SerializeField]
        private float _joinCodeTimeoutSeconds = 30f;

        [SerializeField]
        private bool _createPlayerObject;

        private readonly object _lock = new object();

        private readonly SignalFishRoomRoster _roster = new SignalFishRoomRoster();

        private int _generation;

        private SignalFishClient? _session;

        private SignalFishClient? _client;

        private StagedStart? _staged;

        private TaskCompletionSource<bool>? _stagedCompletion;

        private bool _isHost;

        private string? _relayJoinCode;

        private string? _joinedRoomCode;

        private Guid? _localPlayerId;

        /// <summary>
        /// Starts the host: joins (or creates) the Signal Fish room,
        /// takes the authority, allocates the Unity Relay server through
        /// <see cref="RelayAllocationRequest"/>, starts the NGO host, and
        /// hands the join code to the room. <paramref name="roomCode"/>
        /// joins an existing room; <c>null</c> creates one.
        /// </summary>
        public Task StartHostAsync(string? roomCode = null, CancellationToken ct = default)
        {
            return RunStartAsync(StagedStartKind.Host, roomCode, ct);
        }

        /// <summary>
        /// Starts a client: joins the Signal Fish room by code, waits for
        /// the Unity Relay join code over the room's game-data lane, binds
        /// it through <see cref="RelayJoinBinder"/>, and starts the NGO
        /// client with its player id as the connection-approval payload.
        /// </summary>
        public Task StartClientAsync(string roomCode, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(roomCode))
            {
                throw new ArgumentException(
                    "The room code cannot be null or empty.",
                    nameof(roomCode)
                );
            }

            return RunStartAsync(StagedStartKind.Client, roomCode, ct);
        }

        /// <summary>Leaves the room, shuts the NGO session down, and releases the client.</summary>
        public void Shutdown()
        {
            SignalFishClient? live = Teardown();
            NetworkManager manager = NetworkManager.Singleton;
            if (manager is not null && (manager.IsServer || manager.IsClient))
            {
                manager.Shutdown();
            }

            if (live is not null)
            {
                _ = DisposeQuietlyAsync(live);
            }
        }

        /// <summary>Drains the live room session and runs any staged engine start.</summary>
        public void Update()
        {
            RunStagedStart();
            DrainSession();
        }

        /// <summary>Shuts the coordination session down with the engine.</summary>
        public void OnDestroy()
        {
            Shutdown();
        }

        private async Task RunStartAsync(
            StagedStartKind kind,
            string? roomCode,
            CancellationToken ct
        )
        {
            ValidateConfigured(kind);
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

                await client.ConnectAsync(new Uri(Endpoint), ct).ConfigureAwait(false);
                SendOrThrow(
                    client.SendAuthenticate(
                        new AuthenticateMessage(appId: _appId, connectToken: _connectToken)
                    ),
                    "Authenticate"
                );
                await WaitForAsync(client, PollEventKind.Authenticated, generation, ct)
                    .ConfigureAwait(false);
                SendOrThrow(
                    client.SendJoinRoom(
                        new JoinRoomMessage(
                            _gameName,
                            _playerName,
                            roomCode,
                            supportsAuthority: true
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
                string joinCode;
                if (kind == StagedStartKind.Host)
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

                    joinCode = await AwaitHook(
                            RelayAllocationRequest!(),
                            generation,
                            "the relay allocation hook"
                        )
                        .ConfigureAwait(false);
                    if (!RelayJoinCodeEnvelope.IsValidJoinCode(joinCode))
                    {
                        throw new InvalidOperationException(
                            "The relay allocation hook returned a join code outside the envelope's charset or length bound."
                        );
                    }
                }
                else
                {
                    joinCode = await WaitForJoinCodeAsync(client, generation, ct)
                        .ConfigureAwait(false);
                    await AwaitHook(RelayJoinBinder!(joinCode), generation, "the relay bind hook")
                        .ConfigureAwait(false);
                }

                Stage(
                    new StagedStart(
                        kind,
                        generation,
                        client,
                        joined.Membership,
                        joined.Snapshot,
                        joinCode
                    ),
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

        /// <summary>
        /// Awaits a game hook under the session's generation and the
        /// join-code deadline, so a torn-down session or a jammed hook
        /// cannot park a start forever.
        /// </summary>
        private async Task AwaitHook(Task hook, int generation, string what)
        {
            Task settled = await Task.WhenAny(hook, Task.Delay(JoinCodeTimeoutMilliseconds))
                .ConfigureAwait(false);
            if (settled != hook)
            {
                EnsureFresh(generation);
                throw new TimeoutException(
                    $"Timed out after {JoinCodeTimeoutSeconds:0.#}s waiting for {what}."
                );
            }

            await hook.ConfigureAwait(false);
            EnsureFresh(generation);
        }

        private async Task<PollEvent> WaitForAsync(
            SignalFishClient client,
            PollEventKind expected,
            int generation,
            CancellationToken ct
        )
        {
            long deadline = Environment.TickCount64 + StartTimeoutMilliseconds;
            while (Environment.TickCount64 < deadline)
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
                    ApplyMembership(current);
                }

                await Task.Delay(WaitPollMilliseconds).ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Timed out after {StartTimeoutSeconds:0.#}s waiting for {expected}."
            );
        }

        private async Task<string> WaitForJoinCodeAsync(
            SignalFishClient client,
            int generation,
            CancellationToken ct
        )
        {
            long deadline = Environment.TickCount64 + JoinCodeTimeoutMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                ct.ThrowIfCancellationRequested();
                EnsureFresh(generation);
                while (client.TryDequeueEvent(out PollEvent current))
                {
                    ThrowForFailure(current);
                    if (current.Kind == PollEventKind.GameData)
                    {
                        if (
                            RelayJoinCodeEnvelope.TryRead(
                                current.GameData.Payload.Span,
                                out string joinCode
                            )
                        )
                        {
                            return joinCode;
                        }

                        continue;
                    }

                    ApplyMembership(current);
                }

                await Task.Delay(WaitPollMilliseconds).ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Timed out after {JoinCodeTimeoutSeconds:0.#}s waiting for the relay join code."
            );
        }

        private async Task AwaitStaged(TaskCompletionSource<bool> completion, int generation)
        {
            Task settled = await Task.WhenAny(
                    completion.Task,
                    Task.Delay(StagedStartTimeoutMilliseconds)
                )
                .ConfigureAwait(false);
            if (settled != completion.Task && !completion.Task.IsCompleted)
            {
                /*
                    The engine start never ran within the budget. Tearing
                    down here (which bumps the generation) makes any
                    not-yet-popped start die at RunStagedStart's entry
                    check — a timeout can never leave a start pending
                    forever, and a start racing the timeout dies through
                    its own freshness check instead of reporting success.
                */
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
                    "The engine start never ran; the coordinator's Update stopped ticking."
                );
            }

            await completion.Task.ConfigureAwait(false);
            EnsureFresh(generation);
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
            bool engineStarted = false;
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
            }

            try
            {
                NetworkManager manager = RequireManager();
                lock (_lock)
                {
                    _client = start.Client;
                    _session = null;
                }

                _joinedRoomCode = start.Membership.RoomCode;
                _localPlayerId = start.Membership.PlayerId;
                _roster.Seed(PlayerIds(start.Snapshot));
                _relayJoinCode = start.JoinCode;

                /*
                    NGO gates the whole approval mechanism on this flag:
                    without it the host auto-approves every connection and
                    the client never sends its ConnectionData payload, so
                    the membership check would be a silent no-op. The
                    host's own connection runs the same callback with its
                    ConnectionData, so the host presents its id too.
                */
                manager.NetworkConfig.ConnectionApproval = true;
                manager.NetworkConfig.ConnectionData = ConnectionApprovalPayload.Write(
                    start.Membership.PlayerId
                );
                if (start.Kind == StagedStartKind.Host)
                {
                    _isHost = true;
                    manager.ConnectionApprovalCallback = ApproveConnection;
                    if (!manager.StartHost())
                    {
                        throw new InvalidOperationException(
                            "NGO refused the host start; see the engine log for the reason."
                        );
                    }

                    engineStarted = true;
                    BroadcastJoinCode(start.Client);
                    EnsureFresh(start.Generation);
                }
                else
                {
                    _isHost = false;
                    if (!manager.StartClient())
                    {
                        throw new InvalidOperationException(
                            "NGO refused the client start; see the engine log for the reason."
                        );
                    }

                    engineStarted = true;
                }

                /*
                    A staged-start timeout may have torn the session down
                    while this engine start was in flight; reporting
                    success for it would orphan a live, uncoordinated NGO
                    session, so the start's own generation wins here and
                    the failure paths below shut the engine down.
                */
                EnsureFresh(start.Generation);
                completion?.SetResult(true);
            }
            catch (Exception failure)
            {
                SignalFishClient? live;
                lock (_lock)
                {
                    live = _client;
                    _client = null;
                }

                if (live is not null)
                {
                    _ = DisposeQuietlyAsync(live);
                }

                Teardown();

                /*
                    An engine start that succeeded before the failure
                    (for example a refused join-code broadcast) leaves a
                    live NGO session nobody coordinates; it must go down
                    with the room session, exactly as Fail does.
                */
                if (engineStarted)
                {
                    NetworkManager engine = NetworkManager.Singleton;
                    if (engine is not null)
                    {
                        engine.Shutdown();
                    }
                }

                completion?.SetException(failure);
            }
        }

        private void DrainSession()
        {
            SignalFishClient? client = RequireLiveClientOrNull();
            if (client is null)
            {
                return;
            }

            /*
                A teardown on a background start-failure path can dispose
                this client mid-drain: TryDequeueEvent keeps serving its
                buffered events, so liveness is re-checked per event and
                a send on the just-disposed client dies quietly here —
                the teardown's own failure path surfaces the reason.
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

                if (
                    pollEvent.Kind == PollEventKind.PlayerJoined
                    || pollEvent.Kind == PollEventKind.PlayerReconnected
                    || pollEvent.Kind == PollEventKind.PlayerLeft
                )
                {
                    ApplyMembership(pollEvent);
                    if (pollEvent.Kind == PollEventKind.PlayerJoined && _isHost)
                    {
                        BroadcastJoinCode(client);
                    }

                    continue;
                }

                if (pollEvent.Kind == PollEventKind.GameData)
                {
                    if (
                        !_isHost
                        && RelayJoinCodeEnvelope.TryRead(
                            pollEvent.GameData.Payload.Span,
                            out string joinCode
                        )
                    )
                    {
                        _relayJoinCode = joinCode;
                        RelayJoinCodeReceived?.Invoke(joinCode);
                    }

                    continue;
                }

                if (pollEvent.Kind == PollEventKind.AuthorityChanged)
                {
                    Fail(
                        "The room's authority moved; an NGO host cannot migrate. Start a new room."
                    );
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

        private void ApproveConnection(
            NetworkManager.ConnectionApprovalRequest request,
            NetworkManager.ConnectionApprovalResponse response
        )
        {
            response.Approved = false;
            response.Reason = "The connection claimed no live Signal Fish room membership.";
            if (
                ConnectionApprovalPayload.TryRead(request.Payload, out Guid playerId)
                && _roster.IsMember(playerId)
            )
            {
                response.Approved = true;
                response.Reason = null;
                response.CreatePlayerObject = _createPlayerObject;
            }
        }

        private void BroadcastJoinCode(SignalFishClient client)
        {
            string joinCode =
                _relayJoinCode
                ?? throw new InvalidOperationException("No relay join code was allocated.");
            Span<byte> scratch = stackalloc byte[RelayJoinCodeEnvelope.MaxEnvelopeLength];
            if (!RelayJoinCodeEnvelope.TryWrite(joinCode, scratch, out int written))
            {
                Fail(
                    "The relay join code no longer fits the envelope; the allocation changed shape."
                );
                return;
            }

            byte[] payload = new byte[written];
            scratch.Slice(0, written).CopyTo(payload);
            CommandSend send = client.SendGameData(new GameDataMessage(payload));
            if (!send.Accepted)
            {
                Fail($"The relay refused the join-code broadcast: {send.Refusal}.");
            }
        }

        private void ApplyMembership(in PollEvent pollEvent)
        {
            if (pollEvent.Kind == PollEventKind.PlayerJoined)
            {
                _roster.Add(pollEvent.PlayerJoined.Player.Id);
                return;
            }

            if (pollEvent.Kind == PollEventKind.PlayerReconnected)
            {
                _roster.Add(pollEvent.LeftPlayerId);
                return;
            }

            if (pollEvent.Kind == PollEventKind.PlayerLeft)
            {
                _roster.Remove(pollEvent.LeftPlayerId);
            }
        }

        private void Fail(string reason)
        {
            SignalFishClient? live = Teardown();
            if (live is not null)
            {
                _ = DisposeQuietlyAsync(live);
            }

            NetworkManager manager = NetworkManager.Singleton;
            if (manager is not null)
            {
                manager.Shutdown();
            }

            CoordinationFailed?.Invoke(reason);
        }

        private SignalFishClient? RequireLiveClientOrNull()
        {
            lock (_lock)
            {
                return _client;
            }
        }

        private bool IsFresh(int generation)
        {
            lock (_lock)
            {
                return _generation == generation;
            }
        }

        private SignalFishClient? Teardown()
        {
            SignalFishClient? live;
            TaskCompletionSource<bool>? completion;
            lock (_lock)
            {
                _generation++;
                live = _client ?? _session;
                _client = null;
                _session = null;
                _staged = null;
                completion = _stagedCompletion;
                _stagedCompletion = null;
            }

            completion?.SetException(
                new OperationCanceledException("The coordination session was torn down.")
            );
            _roster.Clear();
            _isHost = false;
            _relayJoinCode = null;
            _joinedRoomCode = null;
            _localPlayerId = null;
            return live;
        }

        private void Stage(
            StagedStart staged,
            TaskCompletionSource<bool> completion,
            int generation
        )
        {
            lock (_lock)
            {
                if (_generation != generation)
                {
                    throw new OperationCanceledException("The coordination session was torn down.");
                }

                _staged = staged;
                _stagedCompletion = completion;
            }
        }

        private void EnsureFresh(int generation)
        {
            if (_generation != generation)
            {
                throw new OperationCanceledException("The coordination session was torn down.");
            }
        }

        private int BeginSession()
        {
            lock (_lock)
            {
                if (_client is not null || _session is not null)
                {
                    throw new InvalidOperationException(
                        "A coordination session is already live; shut it down first."
                    );
                }

                _generation++;
                return _generation;
            }
        }

        private void ValidateConfigured(StagedStartKind kind)
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

            if (_maxFrameBytes < 1)
            {
                throw new InvalidOperationException("MaxFrameBytes must be positive.");
            }

            if (StartTimeoutMilliseconds < 1)
            {
                throw new InvalidOperationException("StartTimeoutSeconds must be positive.");
            }

            if (JoinCodeTimeoutMilliseconds < 1)
            {
                throw new InvalidOperationException("JoinCodeTimeoutSeconds must be positive.");
            }

            if (kind == StagedStartKind.Host && RelayAllocationRequest is null)
            {
                throw new InvalidOperationException(
                    "RelayAllocationRequest was not configured; a host start allocates the Unity Relay server through it."
                );
            }

            if (kind == StagedStartKind.Client && RelayJoinBinder is null)
            {
                throw new InvalidOperationException(
                    "RelayJoinBinder was not configured; a client start binds the received join code through it."
                );
            }
        }

        private SignalFishClient CreateClient()
        {
            ITransport transport = TransportFactory is not null
                ? TransportFactory()
                : new WebSocketTransport();
            return new SignalFishClient(
                transport,
                SystemClock.Instance,
                new SignalFishClientOptions(maxFrameBytes: _maxFrameBytes)
            );
        }

        private static NetworkManager RequireManager()
        {
            return NetworkManager.Singleton
                ?? throw new InvalidOperationException(
                    "NetworkManager.Singleton is null; add a NetworkManager to the scene before coordinating."
                );
        }

        private static void SendOrThrow(CommandSend send, string what)
        {
            if (!send.Accepted)
            {
                throw new InvalidOperationException($"{what} was refused: {send.Refusal}.");
            }
        }

        private static string? DescribeFailure(in PollEvent pollEvent)
        {
            if (
                pollEvent.Kind == PollEventKind.RoomJoinFailed
                || pollEvent.Kind == PollEventKind.SpectatorJoinFailed
                || pollEvent.Kind == PollEventKind.ReconnectionFailed
                || pollEvent.Kind == PollEventKind.ServerError
            )
            {
                return $"The Signal Fish session failed: {pollEvent.Failure.Reason} ({pollEvent.Failure.ErrorCode}).";
            }

            if (pollEvent.Kind == PollEventKind.Disconnected)
            {
                return "The Signal Fish connection closed.";
            }

            if (
                pollEvent.Kind == PollEventKind.ProtocolViolation
                || pollEvent.Kind == PollEventKind.DecodeFailed
            )
            {
                return "The Signal Fish session broke the wire contract.";
            }

            return null;
        }

        private static void ThrowForFailure(in PollEvent pollEvent)
        {
            string? reason = DescribeFailure(pollEvent);
            if (reason is not null)
            {
                throw new InvalidOperationException(reason);
            }
        }

        private static Guid[] PlayerIds(RoomSnapshot snapshot)
        {
            IReadOnlyList<PlayerInfo> players =
                snapshot.CurrentPlayers ?? Array.Empty<PlayerInfo>();
            Guid[] ids = new Guid[players.Count];
            for (int i = 0; i < players.Count; i++)
            {
                ids[i] = players[i].Id;
            }

            return ids;
        }

        private static async Task DisposeQuietlyAsync(SignalFishClient client)
        {
            /*
                A disposal failure during teardown must never mask the
                failure that caused it; the socket dying quietly here is
                the session ending loudly elsewhere.
            */
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception) { }
        }
    }
}
#endif
