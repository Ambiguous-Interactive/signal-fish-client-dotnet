#if SIGNALFISH_PUN2
#nullable enable
namespace SignalFish.Client.Adapters.Pun2
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Photon.Pun;
    using Photon.Realtime;
    using SignalFish.Client.Async;
    using SignalFish.Client.Core;
    using SignalFish.Client.Polling;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.Transport;
    using UnityEngine;

    /// <summary>
    /// Bootstraps a Photon PUN2 room onto a Signal Fish room: the room
    /// owns matchmaking and membership, PUN2 keeps owning the game
    /// connection, and the only thing exchanged is the room name. The
    /// host joins the Signal Fish room, takes the authority, creates
    /// the PUN room, then publishes the PUN room name over the room's
    /// game-data lane (re-published on every join so late joiners are
    /// independent of timing); a client joins the room by code, marks
    /// itself ready, and joins the PUN room whose name arrives on that
    /// lane. Starts may be awaited from any thread; the PUN calls they
    /// need run on the bootstrap's tick and the returned task completes
    /// once the PUN room was joined (or the start failed). This is
    /// deliberately not a transport bridge — PUN2's transport is
    /// Photon's cloud, and the room's part is the exchange.
    /// </summary>
    public sealed class SignalFishPun2Bootstrap : MonoBehaviourPunCallbacks
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

        private enum PunPhase : byte
        {
            [Obsolete(
                "This value only exists so the enum default (0) is not a phase. Compare against default(PunPhase) instead."
            )]
            None = 0,

            Connecting = 1,

            JoiningRoom = 2,

            InRoom = 3,
        }

        private readonly struct StagedStart
        {
            public StagedStartKind Kind { get; }

            public int Generation { get; }

            public SignalFishClient Client { get; }

            public RoomMembership Membership { get; }

            public string PunRoomName { get; }

            public StagedStart(
                StagedStartKind kind,
                int generation,
                SignalFishClient client,
                RoomMembership membership,
                string punRoomName
            )
            {
                Kind = kind;
                Generation = generation;
                Client = client;
                Membership = membership;
                PunRoomName = punRoomName;
            }
        }

        private const int WaitPollMilliseconds = 10;

        private const long MillisecondsPerSecond = 1000;

        private const int StagedStartTimeoutMilliseconds = 10_000;

        /// <summary>Carries a live-session failure after the start completed.</summary>
        public event Action<string>? CoordinationFailed;

        /// <summary>
        /// Carries the PUN room name the host published, when this
        /// client's wait received it. The bootstrap performs the join
        /// itself; the event is informational.
        /// </summary>
        public event Action<string>? PunRoomNameReceived;

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
        /// The host's PUN room name. Empty derives the name from the
        /// Signal Fish room code, so a host that configures nothing
        /// exchanges a room name both sides can trust. Clients ignore
        /// this: their PUN room name is the one the host published.
        /// </summary>
        public string RoomName
        {
            get => _roomName;
            set => _roomName = value;
        }

        /// <summary>
        /// The host's PUN room capacity (PUN2's <c>RoomOptions.MaxPlayers</c>);
        /// 0 means no limit. Clients ignore the value.
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
        /// How long the client's room-name wait and the PUN connect and
        /// room join may take before the start fails. The PUN wait is
        /// cloud-bound, so this budget is the larger one.
        /// </summary>
        public float PunStartTimeoutSeconds
        {
            get => _punStartTimeoutSeconds;
            set => _punStartTimeoutSeconds = value;
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

        /// <summary>Gets the PUN room both sides joined once the start completed.</summary>
        public string? PhotonRoomName
        {
            get
            {
                lock (_lock)
                {
                    return _photonRoomName;
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

        private int PunStartTimeoutMilliseconds =>
            (int)Math.Ceiling(_punStartTimeoutSeconds * MillisecondsPerSecond);

        private string _endpoint = "ws://127.0.0.1:3536/v2/ws";

        private string _gameName = "pun2-game";

        private string _playerName = "player";

        private string _roomName = string.Empty;

        private byte _maxPlayers;

        private float _startTimeoutSeconds = 10f;

        private float _punStartTimeoutSeconds = 30f;

        private readonly object _lock = new object();

        private int _generation;

        private SignalFishClient? _session;

        private SignalFishClient? _client;

        private StagedStart? _staged;

        private TaskCompletionSource<bool>? _stagedCompletion;

        private bool _isHost;

        private string? _joinedRoomCode;

        private string? _photonRoomName;

        private Guid? _localPlayerId;

        /*
            Main-thread state: the PUN phase machine and the start it is
            working for. PUN raises its callbacks on the main thread and
            Update() kicks the machine, so none of this needs the lock.
        */
        private StagedStart? _active;

        private TaskCompletionSource<bool>? _activeCompletion;

        private long _activeDeadline;

        private PunPhase _phase = default(PunPhase);

        private bool _published;

        private bool _punConnected;

        /// <summary>
        /// Starts the host side: join (or create) the Signal Fish room,
        /// take the authority, create the PUN room, publish its name
        /// over the room's game-data lane, and start the game.
        /// </summary>
        public Task StartHostAsync(string? roomCode = null, CancellationToken ct = default)
        {
            return RunStartAsync(StagedStartKind.Host, roomCode, ct);
        }

        /// <summary>
        /// Starts the client side: join the Signal Fish room by code,
        /// mark ready, and join the PUN room whose name the host
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
        /// PUN connection this bootstrap opened disconnects.
        /// </summary>
        public void Shutdown()
        {
            Teardown();
        }

        public override void OnConnectedToMaster()
        {
            StagedStart? start = LiveStart();
            if (start is null || _phase != PunPhase.Connecting)
            {
                return;
            }

            /*
                The host creates the room; a client only joins it. A
                client that created the same-named room instead (the
                host unreachable or on another region) would sit in a
                lone room reporting success — the split-brain this
                split avoids.
            */
            bool queued =
                start.GetValueOrDefault().Kind == StagedStartKind.Host
                    ? PhotonNetwork.JoinOrCreateRoom(
                        start.GetValueOrDefault().PunRoomName,
                        new RoomOptions { MaxPlayers = _maxPlayers },
                        TypedLobby.Default
                    )
                    : PhotonNetwork.JoinRoom(start.GetValueOrDefault().PunRoomName);
            if (!queued)
            {
                FailStaged(new InvalidOperationException("PUN2 refused the room join operation."));
                return;
            }

            _phase = PunPhase.JoiningRoom;
        }

        public override void OnJoinedRoom()
        {
            StagedStart? start = LiveStart();
            if (start is null || _phase != PunPhase.JoiningRoom)
            {
                return;
            }

            StagedStart staged = start.GetValueOrDefault();
            _phase = PunPhase.InRoom;
            string? roomName = PhotonNetwork.CurrentRoom?.Name;
            lock (_lock)
            {
                _photonRoomName = roomName;
            }

            try
            {
                if (staged.Kind == StagedStartKind.Host)
                {
                    if (!TryPublishRoomName(staged.Client, staged.PunRoomName))
                    {
                        throw new InvalidOperationException(
                            "The PUN room name could not be published "
                                + "(envelope bound or refused send)."
                        );
                    }

                    SendOrThrow(staged.Client.SendPlayerReady(), "PlayerReady");
                    SendOrThrow(staged.Client.SendStartGame(), "StartGame");
                }
            }
            catch (Exception failure)
            {
                FailStaged(failure);
                return;
            }

            TaskCompletionSource<bool>? completion = _activeCompletion;
            _active = null;
            _activeCompletion = null;
            completion?.TrySetResult(true);
        }

        public override void OnCreateRoomFailed(short returnCode, string message)
        {
            FailStaged(
                new InvalidOperationException(
                    $"PUN2 refused the room create ({returnCode}): {message}."
                )
            );
        }

        public override void OnJoinRoomFailed(short returnCode, string message)
        {
            FailStaged(
                new InvalidOperationException(
                    $"PUN2 refused the room join ({returnCode}): {message}."
                )
            );
        }

        public override void OnDisconnected(DisconnectCause cause)
        {
            if (_active is not null)
            {
                FailStaged(new InvalidOperationException($"The PUN2 connection closed: {cause}."));
                return;
            }

            if (_client is not null)
            {
                Fail("The PUN2 connection closed: " + cause + ".");
            }
        }

        public override void OnLeftRoom()
        {
            if (_active is null && _client is not null && _phase == PunPhase.InRoom)
            {
                Fail("The PUN2 room was left.");
            }
        }

        private void Update()
        {
            RunStagedStart();
            EnforcePunDeadline();
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

                string punRoomName;
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

                    punRoomName = ResolveHostRoomName(joined.Membership.RoomCode);
                }
                else
                {
                    SendOrThrow(client.SendPlayerReady(), "PlayerReady");
                    punRoomName = await WaitForRoomNameAsync(client, generation, ct)
                        .ConfigureAwait(false);
                }

                Stage(
                    new StagedStart(kind, generation, client, joined.Membership, punRoomName),
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

            if (PunStartTimeoutMilliseconds < 1)
            {
                throw new InvalidOperationException("PunStartTimeoutSeconds must be positive.");
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

        private async Task<string> WaitForRoomNameAsync(
            SignalFishClient client,
            int generation,
            CancellationToken ct
        )
        {
            long deadline = SystemClock.Instance.ElapsedMilliseconds + PunStartTimeoutMilliseconds;
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
                        Pun2RoomEnvelope.TryRead(current.GameData.Payload.Span, out string roomName)
                    )
                    {
                        PunRoomNameReceived?.Invoke(roomName);
                        return roomName;
                    }
                }

                await Task.Delay(WaitPollMilliseconds).ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Timed out after {PunStartTimeoutSeconds:0.#}s waiting for the host's PUN room name."
            );
        }

        private async Task AwaitStaged(TaskCompletionSource<bool> completion, int generation)
        {
            /*
                The staged start is driven by Update() and the PUN
                callbacks; a disabled component stops receiving both.
                The watchdog's margin keeps that world bounded too — in a
                ticking world EnforcePunDeadline fires first with the
                sharper message, so the watchdog only speaks when Update
                really stopped.
            */
            Task settled = await Task.WhenAny(
                    completion.Task,
                    Task.Delay(PunStartTimeoutMilliseconds + StagedStartTimeoutMilliseconds)
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
                    "The staged PUN start never settled; the bootstrap's Update stopped ticking."
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
                SystemClock.Instance.ElapsedMilliseconds + PunStartTimeoutMilliseconds;
            _published = false;

            /*
                The kick runs on the main thread (Update); PUN raises its
                callbacks there too, so the whole phase machine stays on
                one thread. The room session is live from here: the drain
                watches it while PUN connects.
            */
            if (!PhotonNetwork.ConnectUsingSettings())
            {
                FailStaged(new InvalidOperationException("PUN2 refused the connect operation."));
            }
            else
            {
                _punConnected = true;
                _phase = PunPhase.Connecting;
            }
        }

        private void EnforcePunDeadline()
        {
            if (_active is null || SystemClock.Instance.ElapsedMilliseconds <= _activeDeadline)
            {
                return;
            }

            FailStaged(
                new TimeoutException(
                    $"Timed out after {PunStartTimeoutSeconds:0.#}s waiting for the PUN room."
                )
            );
        }

        private StagedStart? LiveStart()
        {
            if (_active is null)
            {
                return null;
            }

            StagedStart start = _active.GetValueOrDefault();
            lock (_lock)
            {
                if (_generation != start.Generation)
                {
                    return null;
                }
            }

            return start;
        }

        private void FailStaged(Exception failure)
        {
            StagedStart? start = _active;
            TaskCompletionSource<bool>? completion = _activeCompletion;
            if (start is null)
            {
                return;
            }

            _active = null;
            _activeCompletion = null;
            _phase = default(PunPhase);
            QuietDisconnect();
            _ = DisposeQuietlyAsync(start.GetValueOrDefault().Client);
            Teardown();
            completion?.TrySetException(failure);
        }

        private bool TryPublishRoomName(SignalFishClient client, string roomName)
        {
            Span<byte> scratch = stackalloc byte[Pun2RoomEnvelope.MaxEnvelopeLength];
            if (!Pun2RoomEnvelope.TryWrite(roomName, scratch, out int written))
            {
                return false;
            }

            byte[] payload = new byte[written];
            scratch.Slice(0, written).CopyTo(payload);
            if (!client.SendGameData(new GameDataMessage(payload)).Accepted)
            {
                return false;
            }

            _published = true;
            return true;
        }

        private string ResolveHostRoomName(string? roomCode)
        {
            string configured = _roomName;
            if (string.IsNullOrEmpty(configured))
            {
                configured = roomCode ?? string.Empty;
            }

            if (!Pun2RoomEnvelope.IsValidRoomName(configured))
            {
                throw new InvalidOperationException(
                    "The PUN room name does not fit the envelope's charset "
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
                    string? roomName = _photonRoomName;
                    if (roomName is not null && !TryPublishRoomName(client, roomName))
                    {
                        Fail("The host could not re-publish the PUN room name.");
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
                        orphans the published room name: fail loudly
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
            Debug.LogWarning("[SignalFishPun2Bootstrap] " + reason);
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
                _photonRoomName = null;
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
            _phase = default(PunPhase);
            _published = false;
            QuietDisconnect();
            if (live is not null)
            {
                _ = DisposeQuietlyAsync(live);
            }

            OperationCanceledException canceled = new("The coordination session was torn down.");
            stagedCompletion?.TrySetException(canceled);
            activeCompletion?.TrySetException(canceled);
        }

        private void QuietDisconnect()
        {
            /*
                PUN2's client is a process-wide singleton: only a
                connection this bootstrap opened through
                ConnectUsingSettings may be disconnected here, or a
                Shutdown would tear down a connection the game owns.
            */
            if (!_punConnected)
            {
                return;
            }

            _punConnected = false;
            try
            {
                PhotonNetwork.Disconnect();
            }
            catch (Exception) { }
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
    }
}
#endif
