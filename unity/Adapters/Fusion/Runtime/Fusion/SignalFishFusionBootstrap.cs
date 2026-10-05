#if SIGNALFISH_FUSION
#nullable enable
namespace SignalFish.Client.Adapters.Fusion
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    /*
        global:: is load-bearing: the adapter namespace
        (SignalFish.Client.Adapters.Fusion) is a member of the enclosing
        SignalFish.Client.Adapters namespace, and enclosing-namespace
        members win over using directives - plain `using Fusion;` would
        resolve to this package and import nothing (in Unity too; the
        lint's shape-stub lane catches it).
    */
    using global::Fusion;
    using global::Fusion.Sockets;
    using SignalFish.Client.Async;
    using SignalFish.Client.Core;
    using SignalFish.Client.Polling;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.Transport;
    using UnityEngine;

    /// <summary>
    /// Bootstraps a Fusion session onto a Signal Fish room: the room
    /// owns matchmaking and membership, Fusion and Photon's cloud keep
    /// owning the game connection, and the only thing exchanged is the
    /// session name. The host joins the Signal Fish room, takes the
    /// authority, starts the Fusion session (<c>GameMode.Host</c>) with
    /// the chosen name, then publishes that name over the room's
    /// game-data lane (re-published on every join so late joiners are
    /// independent of timing); a client joins the room by code, marks
    /// itself ready, and starts its runner as <c>GameMode.Client</c>
    /// with the name that arrived on that lane — a mode that joins a
    /// named session and never creates one. Starts may be awaited from
    /// any thread; the Unity calls they need are staged onto the
    /// bootstrap's tick, and the returned task completes once the
    /// session was started (or the start failed). This is deliberately
    /// not a transport bridge — Fusion's transport is Photon's cloud,
    /// and the room's part is the exchange.
    /// </summary>
    public sealed class SignalFishFusionBootstrap : MonoBehaviour, INetworkRunnerCallbacks
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

            public string SessionName { get; }

            public StagedStart(
                StagedStartKind kind,
                int generation,
                SignalFishClient client,
                RoomMembership membership,
                string sessionName
            )
            {
                Kind = kind;
                Generation = generation;
                Client = client;
                Membership = membership;
                SessionName = sessionName;
            }
        }

        private const int WaitPollMilliseconds = 10;

        private const long MillisecondsPerSecond = 1000;

        private const int StagedStartTimeoutMilliseconds = 10_000;

        private const string RunnerObjectName = "SignalFishFusionRunner";

        /// <summary>Carries a live-session failure after the start completed.</summary>
        public event Action<string>? CoordinationFailed;

        /// <summary>
        /// Carries the session name the host published, when this
        /// client's wait received it. The bootstrap performs the start
        /// itself; the event is informational.
        /// </summary>
        public event Action<string>? SessionNameReceived;

        /// <summary>The Signal Fish session identity and exchange tuning.</summary>
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

        /// <summary>
        /// The host's Fusion session name. Empty derives the name from
        /// the Signal Fish room code, so a host that configures nothing
        /// exchanges a session name both sides can trust. Clients ignore
        /// this: their session name is the one the host published.
        /// </summary>
        public string SessionName
        {
            get => _sessionName;
            set => _sessionName = value;
        }

        /// <summary>
        /// The host's session capacity (Fusion's <c>PlayerCount</c>);
        /// 0 means no explicit limit. Clients ignore the value.
        /// </summary>
        public byte MaxPlayers
        {
            get => _maxPlayers;
            set => _maxPlayers = value;
        }

        /// <summary>How long the room handshake may take before the start fails.</summary>
        public float StartTimeoutSeconds
        {
            get => _startTimeoutSeconds;
            set => _startTimeoutSeconds = value;
        }

        /// <summary>
        /// How long the client's session-name wait and the runner's
        /// session start may take before the start fails. The start is
        /// cloud-bound, so this budget is the larger one.
        /// </summary>
        public float FusionStartTimeoutSeconds
        {
            get => _fusionStartTimeoutSeconds;
            set => _fusionStartTimeoutSeconds = value;
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
                    return _session is not null || _client is not null || _staged is not null;
                }
            }
        }

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

        /// <summary>Gets the Fusion session both sides started once the start completed.</summary>
        public string? FusionSessionName
        {
            get
            {
                lock (_lock)
                {
                    return _fusionSessionName;
                }
            }
        }

        public Guid? LocalPlayerId
        {
            get
            {
                lock (_lock)
                {
                    return _localPlayerId;
                }
            }
        }

        private int StartTimeoutMilliseconds =>
            (int)Math.Ceiling(_startTimeoutSeconds * MillisecondsPerSecond);

        private int FusionStartTimeoutMilliseconds =>
            (int)Math.Ceiling(_fusionStartTimeoutSeconds * MillisecondsPerSecond);

        private string _endpoint = "ws://127.0.0.1:3536/v2/ws";

        private string _gameName = "fusion-game";

        private string _playerName = "player";

        private string _sessionName = string.Empty;

        private byte _maxPlayers;

        private float _startTimeoutSeconds = 10f;

        private float _fusionStartTimeoutSeconds = 30f;

        private readonly object _lock = new object();

        private int _generation;

        private SignalFishClient? _session;

        private SignalFishClient? _client;

        private StagedStart? _staged;

        private TaskCompletionSource<bool>? _stagedCompletion;

        private bool _isHost;

        private string? _joinedRoomCode;

        private string? _fusionSessionName;

        private Guid? _localPlayerId;

        private bool _published;

        private SynchronizationContext? _mainContext;

        /*
            Main-thread state: the staged start's Unity half and the
            runner it started. Fusion raises its callbacks on the main
            thread and Update() kicks the staging, so none of this needs
            the lock.
        */
        private StagedStart? _active;

        private TaskCompletionSource<bool>? _activeCompletion;

        private long _activeDeadline;

        private NetworkRunner? _runner;

        private bool _runnerOwned;

        /// <summary>
        /// Starts the host side: join (or create) the Signal Fish room,
        /// take the authority, start the Fusion session with the chosen
        /// name, publish that name over the room's game-data lane, and
        /// start the game.
        /// </summary>
        public Task StartHostAsync(string? roomCode = null, CancellationToken ct = default)
        {
            return RunStartAsync(StagedStartKind.Host, roomCode, ct);
        }

        /// <summary>
        /// Starts the client side: join the Signal Fish room by code,
        /// mark ready, and start the Fusion session whose name the host
        /// publishes over the room's game-data lane.
        /// </summary>
        public Task StartClientAsync(string roomCode, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(roomCode))
            {
                throw new ArgumentException(
                    "A room code is required to join a room.",
                    nameof(roomCode)
                );
            }

            return RunStartAsync(StagedStartKind.Client, roomCode, ct);
        }

        /// <summary>
        /// Tears the coordination down: the room session closes and a
        /// Fusion runner this bootstrap created shuts down.
        /// </summary>
        public void Shutdown()
        {
            Teardown();
        }

        void INetworkRunnerCallbacks.OnObjectExitAOI(
            NetworkRunner runner,
            NetworkObject obj,
            PlayerRef player
        )
        {
            /*
                The bootstrap coordinates the session start, not the
                simulation; interest management is the game's business.
            */
        }

        void INetworkRunnerCallbacks.OnObjectEnterAOI(
            NetworkRunner runner,
            NetworkObject obj,
            PlayerRef player
        )
        {
            // Interest management is the game's business.
        }

        void INetworkRunnerCallbacks.OnPlayerJoined(NetworkRunner runner, PlayerRef player)
        {
            /*
                Room membership is the Signal Fish room's job; the
                session-name re-publish keys on the room's own join
                event.
            */
        }

        void INetworkRunnerCallbacks.OnPlayerLeft(NetworkRunner runner, PlayerRef player)
        {
            // Room membership is the Signal Fish room's job.
        }

        void INetworkRunnerCallbacks.OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
        {
            if (_active is not null)
            {
                FailStaged(
                    new InvalidOperationException(
                        $"The Fusion runner shut down mid-start: {shutdownReason}."
                    )
                );
                return;
            }

            if (_client is not null && ReferenceEquals(runner, _runner))
            {
                Fail($"The Fusion runner shut down: {shutdownReason}.");
            }
        }

        void INetworkRunnerCallbacks.OnDisconnectedFromServer(
            NetworkRunner runner,
            NetDisconnectReason reason
        )
        {
            if (_active is not null)
            {
                FailStaged(
                    new InvalidOperationException($"The Fusion server connection closed: {reason}.")
                );
                return;
            }

            if (_client is not null && ReferenceEquals(runner, _runner))
            {
                Fail($"The Fusion server connection closed: {reason}.");
            }
        }

        void INetworkRunnerCallbacks.OnConnectRequest(
            NetworkRunner runner,
            NetworkRunnerCallbackArgs.ConnectRequest request,
            byte[] token
        )
        {
            /*
                Fusion's accept path is the session itself; the room's
                membership is the Signal Fish room's job.
            */
        }

        void INetworkRunnerCallbacks.OnConnectFailed(
            NetworkRunner runner,
            NetAddress remoteAddress,
            NetConnectFailedReason reason
        )
        {
            if (_active is not null)
            {
                FailStaged(
                    new InvalidOperationException(
                        $"The Fusion session start was refused: {reason}."
                    )
                );
                return;
            }

            if (_client is not null && ReferenceEquals(runner, _runner))
            {
                Fail($"The Fusion session connect failed: {reason}.");
            }
        }

        void INetworkRunnerCallbacks.OnUserSimulationMessage(
            NetworkRunner runner,
            SimulationMessagePtr message
        )
        {
            // Simulation diagnostics are the game's business.
        }

        void INetworkRunnerCallbacks.OnReliableDataReceived(
            NetworkRunner runner,
            PlayerRef player,
            ReliableKey key,
            ArraySegment<byte> data
        )
        {
            /*
                Fusion's reliable-data lane is the game's business; the
                exchange rides the Signal Fish room's game-data lane.
            */
        }

        void INetworkRunnerCallbacks.OnReliableDataProgress(
            NetworkRunner runner,
            PlayerRef player,
            ReliableKey key,
            float progress
        )
        {
            // Delivery progress is the game's business.
        }

        void INetworkRunnerCallbacks.OnInput(NetworkRunner runner, NetworkInput input)
        {
            // Input is the game's business.
        }

        void INetworkRunnerCallbacks.OnInputMissing(
            NetworkRunner runner,
            PlayerRef player,
            NetworkInput input
        )
        {
            // Input is the game's business.
        }

        void INetworkRunnerCallbacks.OnConnectedToServer(NetworkRunner runner)
        {
            /*
                The awaited StartGame result carries the verdict; this
                callback needs no coordination action.
            */
        }

        void INetworkRunnerCallbacks.OnSessionListUpdated(
            NetworkRunner runner,
            List<SessionInfo> sessionList
        )
        {
            // Lobby browsing is the game's business.
        }

        void INetworkRunnerCallbacks.OnCustomAuthenticationResponse(
            NetworkRunner runner,
            Dictionary<string, object> data
        )
        {
            // Authentication is the game's business.
        }

        void INetworkRunnerCallbacks.OnHostMigration(
            NetworkRunner runner,
            HostMigrationToken hostMigrationToken
        )
        {
            /*
                A host migration hands the session to another peer —
                including the published session-name authority. Fail the
                coordination loudly rather than coordinate a session
                someone else now names.
            */
            if (_client is not null && ReferenceEquals(runner, _runner))
            {
                Fail("The Fusion session began a host migration; re-host the room.");
            }
        }

        void INetworkRunnerCallbacks.OnSceneLoadDone(NetworkRunner runner)
        {
            // Scenes are the game's business.
        }

        void INetworkRunnerCallbacks.OnSceneLoadStart(NetworkRunner runner)
        {
            // Scenes are the game's business.
        }

        private void Update()
        {
            RunStagedStart();
            EnforceFusionDeadline();
            DrainSession();
        }

        private void OnDestroy()
        {
            Teardown();
        }

        private async Task RunStartAsync(
            StagedStartKind kind,
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
                            supportsAuthority: kind == StagedStartKind.Host
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

                string sessionName;
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

                    sessionName = ResolveHostSessionName(joined.Membership.RoomCode);
                }
                else
                {
                    SendOrThrow(client.SendPlayerReady(), "PlayerReady");
                    sessionName = await WaitForSessionNameAsync(client, generation, ct)
                        .ConfigureAwait(false);
                }

                Stage(
                    new StagedStart(kind, generation, client, joined.Membership, sessionName),
                    completion,
                    generation
                );
                await AwaitStaged(completion, generation).ConfigureAwait(false);

                lock (_lock)
                {
                    _fusionSessionName = sessionName;
                }

                if (kind == StagedStartKind.Host)
                {
                    if (!TryPublishSessionName(client, sessionName))
                    {
                        throw new InvalidOperationException(
                            "The Fusion session name could not be published "
                                + "(envelope bound or refused send)."
                        );
                    }

                    SendOrThrow(client.SendPlayerReady(), "PlayerReady");
                    SendOrThrow(client.SendStartGame(), "StartGame");

                    lock (_lock)
                    {
                        _published = true;
                    }
                }
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

            if (FusionStartTimeoutMilliseconds < 1)
            {
                throw new InvalidOperationException("FusionStartTimeoutSeconds must be positive.");
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

        private async Task<string> WaitForSessionNameAsync(
            SignalFishClient client,
            int generation,
            CancellationToken ct
        )
        {
            long deadline =
                SystemClock.Instance.ElapsedMilliseconds + FusionStartTimeoutMilliseconds;
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
                        FusionSessionEnvelope.TryRead(
                            current.GameData.Payload.Span,
                            out string sessionName
                        )
                    )
                    {
                        SessionNameReceived?.Invoke(sessionName);
                        return sessionName;
                    }
                }

                await Task.Delay(WaitPollMilliseconds).ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Timed out after {FusionStartTimeoutSeconds:0.#}s waiting for the host's session name."
            );
        }

        private async Task AwaitStaged(TaskCompletionSource<bool> completion, int generation)
        {
            /*
                The staged start is driven by Update(); a disabled
                component stops receiving it. The watchdog's margin keeps
                that world bounded too — in a ticking world
                EnforceFusionDeadline fires first with the sharper
                message, so the watchdog only speaks when Update really
                stopped.
            */
            Task settled = await Task.WhenAny(
                    completion.Task,
                    Task.Delay(FusionStartTimeoutMilliseconds + StagedStartTimeoutMilliseconds)
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
                    "The staged Fusion start never settled; the bootstrap's Update stopped ticking."
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
                if (_staged is not null || _active is not null)
                {
                    throw new InvalidOperationException(
                        "A start is already staged; one bootstrap coordinates one session."
                    );
                }

                _staged = start;
                _stagedCompletion = completion;
                _isHost = start.Kind == StagedStartKind.Host;
                _joinedRoomCode = start.Membership.RoomCode;
                _localPlayerId = start.Membership.PlayerId;
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

                _client = start.Client;
                _session = null;
            }

            _active = start;
            _activeCompletion = completion;
            _activeDeadline =
                SystemClock.Instance.ElapsedMilliseconds + FusionStartTimeoutMilliseconds;
            _mainContext = SynchronizationContext.Current;

            /*
                The kick runs on the main thread (Update); Fusion raises
                its callbacks there too. The runner lives on its own
                GameObject this bootstrap owns, so teardown can shut the
                session down without touching the game's scene objects.
                No scene manager is passed: StartGame falls back to
                Fusion's default provider itself. The room session is
                live from here: the drain watches it while Fusion
                starts.
            */
            NetworkRunner runner = new GameObject(RunnerObjectName).AddComponent<NetworkRunner>();
            _runner = runner;
            _runnerOwned = true;
            runner.AddCallbacks(this);
            Task<StartGameResult> started = runner.StartGame(BuildStartArgs(start));
            _ = started.ContinueWith(SettleStaged, TaskContinuationOptions.ExecuteSynchronously);
        }

        private StartGameArgs BuildStartArgs(StagedStart start)
        {
            if (start.Kind == StagedStartKind.Host)
            {
                return new StartGameArgs
                {
                    GameMode = GameMode.Host,
                    SessionName = start.SessionName,
                    PlayerCount = _maxPlayers > 0 ? _maxPlayers : null,
                };
            }

            /*
                A client that created the same-named session instead (the
                host unreachable or on another region) would sit in a
                lone session reporting success — the split-brain this
                split avoids. Fusion's own default already refuses
                client-made sessions; naming it keeps the guard honest
                if that default ever moves.
            */
            return new StartGameArgs
            {
                GameMode = GameMode.Client,
                SessionName = start.SessionName,
                EnableClientSessionCreation = false,
            };
        }

        private void SettleStaged(Task<StartGameResult> started)
        {
            /*
                The start task settles on a Fusion worker thread as often
                as not, but _active is main-thread state: the settle is
                marshaled onto the context the kick was captured on, so
                every active-start transition happens on one thread. A
                torn-down session's posted settle finds no pending start
                and becomes a no-op.
            */
            SynchronizationContext? main = _mainContext;
            if (main is null || main == SynchronizationContext.Current)
            {
                SettleStagedOnMain(started);
                return;
            }

            main.Post(_ => SettleStagedOnMain(started), null);
        }

        private void SettleStagedOnMain(Task<StartGameResult> started)
        {
            TaskCompletionSource<bool>? completion = _activeCompletion;
            if (completion is null)
            {
                return;
            }

            string? failure = DescribeStagedOutcome(started);
            if (failure is null)
            {
                /*
                    Clear the active start before settling: the deadline,
                    the Fusion callbacks, and this settle all run on the
                    main thread, so a later tick can never tear a session
                    whose start already succeeded down.
                */
                _active = null;
                _activeCompletion = null;
                completion.TrySetResult(true);
                return;
            }

            FailStaged(new InvalidOperationException(failure));
        }

        private static string? DescribeStagedOutcome(Task<StartGameResult> started)
        {
            if (started.IsCanceled)
            {
                return "The Fusion start was canceled.";
            }

            if (started.IsFaulted)
            {
                Exception inner =
                    started.Exception?.GetBaseException()
                    ?? new InvalidOperationException("The Fusion start failed.");
                return "The Fusion start failed: " + inner.Message + ".";
            }

            StartGameResult result = started.Result;
            return result.Ok ? null : "The Fusion start failed: " + result.ErrorMessage + ".";
        }

        private void EnforceFusionDeadline()
        {
            if (_active is null || SystemClock.Instance.ElapsedMilliseconds <= _activeDeadline)
            {
                return;
            }

            FailStaged(
                new TimeoutException(
                    $"Timed out after {FusionStartTimeoutSeconds:0.#}s waiting for the Fusion session."
                )
            );
        }

        private void FailStaged(Exception failure)
        {
            /*
                The pending completion is the start's liveness marker: a
                start that already settled (success or failure) makes
                every later FailStaged a no-op, so a healthy session is
                never torn down by a stale verdict.
            */
            TaskCompletionSource<bool>? completion = _activeCompletion;
            if (completion is null)
            {
                return;
            }

            StagedStart? start = _active;
            _active = null;
            _activeCompletion = null;
            QuietShutdownRunner();
            if (start is not null)
            {
                _ = DisposeQuietlyAsync(start.GetValueOrDefault().Client);
            }

            Teardown();
            completion.TrySetException(failure);
        }

        private bool TryPublishSessionName(SignalFishClient client, string sessionName)
        {
            Span<byte> scratch = stackalloc byte[FusionSessionEnvelope.MaxEnvelopeLength];
            if (!FusionSessionEnvelope.TryWrite(sessionName, scratch, out int written))
            {
                return false;
            }

            byte[] payload = new byte[written];
            scratch.Slice(0, written).CopyTo(payload);
            return client.SendGameData(new GameDataMessage(payload)).Accepted;
        }

        private string ResolveHostSessionName(string? roomCode)
        {
            string configured = _sessionName;
            if (string.IsNullOrEmpty(configured))
            {
                configured = roomCode ?? string.Empty;
            }

            if (!FusionSessionEnvelope.IsValidSessionName(configured))
            {
                throw new InvalidOperationException(
                    "The Fusion session name does not fit the envelope's charset "
                        + "([A-Za-z0-9_-], at most 128 chars); the exchange could never carry it."
                );
            }

            return configured;
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

                if (pollEvent.Kind == PollEventKind.PlayerJoined && _isHost && _published)
                {
                    /*
                        The game-data lane replays nothing: a member that
                        joins after the first publication misses it, so
                        the host re-publishes on every join. A refusal
                        here (the room socket died under us) fails the
                        session instead of throwing out of Update.
                    */
                    string? sessionName = _fusionSessionName;
                    if (sessionName is not null && !TryPublishSessionName(client, sessionName))
                    {
                        Fail("The host could not re-publish the Fusion session name.");
                        return;
                    }

                    continue;
                }

                if (
                    pollEvent.Kind == PollEventKind.AuthorityChanged
                    && _isHost
                    && !pollEvent.AuthorityChanged.YouAreAuthority
                )
                {
                    /*
                        The grant broadcast that follows this host's own
                        successful authority request is benign — the
                        server answers with AuthorityResponse AND a
                        room-wide AuthorityChanged whose you_are_authority
                        is true for us. Only a move away from this host
                        orphans the published session name: fail loudly
                        rather than coordinating a session someone else
                        now gates.
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
            Debug.LogWarning("[SignalFishFusionBootstrap] " + reason);
            CoordinationFailed?.Invoke(reason);
            Teardown();
        }

        private void Teardown()
        {
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
                _fusionSessionName = null;
                _localPlayerId = null;
            }

            /*
                The start tasks are public awaited APIs: a teardown must
                settle them, or Shutdown/OnDestroy (or any room-side
                failure) while a start is staged or in flight hangs the
                awaiter forever.
            */
            activeCompletion = _activeCompletion;
            _active = null;
            _activeCompletion = null;
            _published = false;
            _mainContext = null;
            QuietShutdownRunner();
            if (live is not null)
            {
                _ = DisposeQuietlyAsync(live);
            }

            OperationCanceledException canceled = new("The coordination session was torn down.");
            stagedCompletion?.TrySetException(canceled);
            activeCompletion?.TrySetException(canceled);
        }

        private void QuietShutdownRunner()
        {
            /*
                Only a runner this bootstrap created on its own GameObject
                is shut down (and its GameObject destroyed) here; a
                runner the game handed over or started elsewhere is not
                ours to tear down.
            */
            if (!_runnerOwned)
            {
                return;
            }

            NetworkRunner? runner = _runner;
            _runner = null;
            _runnerOwned = false;
            if (runner is null)
            {
                return;
            }

            _ = ShutdownRunnerQuietlyAsync(runner);
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

        private static async Task ShutdownRunnerQuietlyAsync(NetworkRunner runner)
        {
            try
            {
                await runner.Shutdown(destroyGameObject: true).ConfigureAwait(false);
            }
            catch (Exception) { }
        }
    }
}
#endif
