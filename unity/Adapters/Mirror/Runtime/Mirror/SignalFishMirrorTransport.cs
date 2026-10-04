/*
    SIGNALFISH_MIRROR is produced by this package's editor define
    detector (Editor/SignalFishMirrorDefineDetector.cs) whenever the
    Mirror assembly is in the project — Mirror ships as an asset (no UPM
    package), so a detector, not versionDefines, owns the define. The
    asmdef's defineConstraints skip the whole assembly when the define is
    absent: the package is inert without Mirror, and no Mirror type is
    ever referenced unguarded.
*/
#nullable enable
#if SIGNALFISH_MIRROR
namespace SignalFish.Client.Adapters.Mirror
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Mirror;
    using SignalFish.Client;
    using SignalFish.Client.Async;
    using SignalFish.Client.Core;
    using SignalFish.Client.Polling;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.Transport;
    using UnityEngine;

    /// <summary>
    /// The Mirror transport bridge (M8.2): one Signal Fish room carries a
    /// Mirror session. The room's authority plays the Mirror server; the
    /// other members play Mirror clients; game traffic rides the v3
    /// binary game-data lane as raw frames wrapped in the adapter's
    /// <see cref="AdapterWire"/> header. The Signal Fish relay is a
    /// room broadcast, so Mirror's star topology is realized by
    /// <see cref="SignalFishReceiveRules"/>: the authority consumes its
    /// peers' upstream frames, and clients consume only the authority's
    /// downstream frames addressed to them (or to everyone).
    ///
    /// Assign the component under a NetworkManager (Mirror picks it as
    /// <c>Transport.active</c>), fill the session fields, and start host
    /// or client through Mirror as usual — for a client, the
    /// NetworkManager's address is the Signal Fish room code. The Signal
    /// Fish session (connect, authenticate, join, v3 binary negotiation,
    /// and the authority grant on the host path) runs inside
    /// ServerStart/ClientConnect; Mirror sees connections only once the
    /// room is live. Host mode needs no loopback: Mirror's local
    /// connection delivers the host's own client traffic in-process,
    /// never through this transport.
    ///
    /// Threading: Mirror iterates the transport on the main thread, and
    /// every Mirror-facing callback (connections, receives) is raised
    /// from those iterate points — the async bootstrap thread only
    /// stages facts into queues. Shared fields read on the main thread
    /// are written under the gate or ordered by the Starting→Started
    /// transition.
    ///
    /// Validation status: authored against the Mirror 96.9.x transport
    /// contract and the library's conformance suite, but not yet compiled
    /// in Unity — live validation is the M8.7 runbook item (needs a
    /// licensed Unity seat; Unity never runs in CI).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SignalFishMirrorTransport : Transport
    {
        /// <summary>One frame queued for the relay (an owned copy).</summary>
        private readonly struct OutboundFrame
        {
            internal Guid Target { get; }

            internal byte Channel { get; }

            internal int ConnectionId { get; }

            internal byte[] Segment { get; }

            internal OutboundFrame(Guid target, byte channel, int connectionId, byte[] segment)
            {
                Target = target;
                Channel = channel;
                ConnectionId = connectionId;
                Segment = segment;
            }
        }

        /// <summary>One relayed frame routed to a Mirror side.</summary>
        private readonly struct RelayFrame
        {
            internal byte Channel { get; }

            internal byte[] Segment { get; }

            internal int ConnectionId { get; }

            internal RelayFrame(byte channel, byte[] segment, int connectionId)
            {
                Channel = channel;
                Segment = segment;
                ConnectionId = connectionId;
            }
        }

        /// <summary>The facts the bootstrap needs from the RoomJoined event.</summary>
        private readonly struct RoomJoinedReceipt
        {
            internal Guid MembershipPlayerId { get; }

            internal RoomSnapshot Snapshot { get; }

            internal RoomJoinedReceipt(Guid membershipPlayerId, RoomSnapshot snapshot)
            {
                MembershipPlayerId = membershipPlayerId;
                Snapshot = snapshot;
            }
        }

        /// <summary>One Mirror-facing fact staged off the main thread.</summary>
        private readonly struct StagedEvent
        {
            internal StagedKind Kind { get; }

            internal int ConnectionId { get; }

            internal TransportError Error { get; }

            internal string Reason { get; }

            internal Exception? TransportException { get; }

            internal StagedEvent(
                StagedKind kind,
                int connectionId = 0,
                TransportError error = TransportError.Unexpected,
                string reason = "",
                Exception? transportException = null
            )
            {
                Kind = kind;
                ConnectionId = connectionId;
                Error = error;
                Reason = reason;
                TransportException = transportException;
            }
        }

        /// <summary>The kind of Mirror-facing fact staged for the next early update.</summary>
        private enum StagedKind : byte
        {
            PeerStarted = 0,
            PeerStopped = 1,
            ClientConnected = 2,
            ClientFailed = 3,
            ClientDropped = 4,
            ServerError = 5,
            ClientTransportException = 6,
        }

        /// <summary>Per-side Mirror session state.</summary>
        private enum SessionSideState : byte
        {
            Stopped = 0,
            Starting = 1,
            Started = 2,
        }

        /// <summary>The outbound queue bound, in frames.</summary>
        private const int OutboundCapacity = 1024;

        /// <summary>Gets or sets the Signal Fish WebSocket endpoint.</summary>
        public string Endpoint
        {
            get { return _endpoint; }
            set { _endpoint = value; }
        }

        /// <summary>Gets or sets the public game name the room is created with.</summary>
        public string GameName
        {
            get { return _gameName; }
            set { _gameName = value; }
        }

        /// <summary>Gets or sets this machine's player name inside the room.</summary>
        public string PlayerName
        {
            get { return _playerName; }
            set { _playerName = value; }
        }

        /// <summary>
        /// Gets or sets the room code the server side creates-or-joins;
        /// empty creates a new room. Client joins take the room code from
        /// <see cref="ClientConnect(string)"/> (the NetworkManager's
        /// address), not from this field.
        /// </summary>
        public string RoomCode
        {
            get { return _roomCode; }
            set { _roomCode = value; }
        }

        /// <summary>Gets or sets the public app id sent on every Authenticate.</summary>
        public string AppId
        {
            get { return _appId; }
            set { _appId = value; }
        }

        /// <summary>
        /// Gets or sets the optional tenant credential sent on every
        /// Authenticate. A secret: never log it.
        /// </summary>
        public string ConnectToken
        {
            get { return _connectToken; }
            set { _connectToken = value; }
        }

        /// <summary>Gets or sets the inbound physical-frame bound (the MTU basis).</summary>
        public int MaxFrameBytes
        {
            get { return _maxFrameBytes; }
            set { _maxFrameBytes = value; }
        }

        /// <summary>
        /// Gets or sets the transport factory for the Signal Fish
        /// connection. The default builds the .NET WebSocket transport;
        /// WebGL builds must inject the package's browser-WebSocket
        /// transport here.
        /// </summary>
        public Func<ITransport>? TransportFactory { get; set; }

        /// <summary>Gets the room code of the joined room, when one is joined.</summary>
        public string? JoinedRoomCode
        {
            get
            {
                lock (_gate)
                {
                    return _session?.Snapshot.RoomCode ?? _client?.Snapshot.RoomCode;
                }
            }
        }

        /// <summary>Gets how many outbound frames overflowed the queue.</summary>
        public int OutboundDropped
        {
            get
            {
                lock (_gate)
                {
                    return _outboundDropped;
                }
            }
        }

        /// <summary>
        /// Gets how many relay frames were dropped because their sender had
        /// no route (a peer that left, or was kicked, while frames were in
        /// flight).
        /// </summary>
        public int RoutedDropped
        {
            get
            {
                lock (_gate)
                {
                    return _routedDropped;
                }
            }
        }

        /// <summary>Gets how many remote peers are currently routed.</summary>
        public int PeerCount
        {
            get { return _router.PeerCount; }
        }

        /// <summary>Gets the last session failure, for diagnostics.</summary>
        public string? StartupError { get; private set; }

        private readonly object _gate = new object();

        /// <summary>
        /// Bumped on every teardown; an in-flight bootstrap that observes a
        /// new generation disposes its own client and stops.
        /// </summary>
        private int _generation;

        /// <summary>How many outbound frames overflowed the queue.</summary>
        private int _outboundDropped;

        /// <summary>How many relay frames were dropped for having no route.</summary>
        private int _routedDropped;

        /// <summary>The session went terminal; every state change is final.</summary>
        private bool _terminal;

        /// <summary>Whether the Mirror server side has been started and not stopped.</summary>
        private bool _serverActive;

        private SessionSideState _clientState = SessionSideState.Stopped;
        private SessionSideState _serverState = SessionSideState.Stopped;

        /// <summary>The frames queued for the relay, flushed in the late updates.</summary>
        private readonly Queue<OutboundFrame> _outbound = new Queue<OutboundFrame>();

        /// <summary>Relay frames routed for the server feed, awaiting its early update.</summary>
        private readonly Queue<RelayFrame> _serverPending = new Queue<RelayFrame>();

        /// <summary>Relay frames routed for the client feed, awaiting its early update.</summary>
        private readonly Queue<RelayFrame> _clientPending = new Queue<RelayFrame>();

        /// <summary>
        /// Mirror-facing facts staged off the main thread, drained at the
        /// top of every early update.
        /// </summary>
        private readonly Queue<StagedEvent> _staged = new Queue<StagedEvent>();

        private readonly SignalFishPeerRouter _router = new SignalFishPeerRouter();

        private SignalFishClient? _client;

        /// <summary>
        /// The bootstrap-owned session: it holds the client until the
        /// session is live, so the main thread's drain can never steal the
        /// events the bootstrap is itself waiting on (a RoomJoined consumed
        /// by DrainRelay's default branch would time out every start).
        /// </summary>
        private SignalFishClient? _session;

        /// <summary>The room code the next bootstrap joins (or creates when empty).</summary>
        private string _pendingRoomCode = "";

        private Task? _bootstrap;
        private byte[]? _sendScratch;
        private Guid _localPlayerId;
        private Guid _authorityPlayerId;
        private bool _localIsAuthority;

        [Header("Signal Fish session")]
        [SerializeField]
        [Tooltip(
            "The Signal Fish WebSocket endpoint (the v2 relay floor; v3 is negotiated on top)."
        )]
        private string _endpoint = "ws://127.0.0.1:8080/v2/ws";

        [SerializeField]
        [Tooltip("Public game name the room is created with.")]
        private string _gameName = "mirror-game";

        [SerializeField]
        [Tooltip("This machine's player name inside the room.")]
        private string _playerName = "player";

        [SerializeField]
        [Tooltip(
            "Room code the server side creates-or-joins; empty creates a new room. Client joins take the code from the NetworkManager address instead."
        )]
        private string _roomCode = "";

        [SerializeField]
        [Tooltip(
            "Public app id sent on every Authenticate; empty omits it (open deployments accept the handshake)."
        )]
        private string _appId = "";

        [SerializeField]
        [Tooltip("Optional tenant credential sent on every Authenticate. A secret: never log it.")]
        private string _connectToken = "";

        [Header("Transport budgets")]
        [SerializeField]
        [Tooltip(
            "The client's inbound physical-frame bound in bytes; the Mirror packet size is this minus the adapter wire reserve."
        )]
        private int _maxFrameBytes = 64 * 1024;

        [Header("Timing")]
        [SerializeField]
        [Tooltip("Seconds the session bootstrap may take before the start fails.")]
        private float _startTimeoutSeconds = 10f;

        /// <inheritdoc />
        public override bool Available()
        {
            return true;
        }

        /// <inheritdoc />
        public override Uri ServerUri()
        {
            string roomCode = JoinedRoomCode ?? "unjoined";
            return new Uri($"signalfish://{roomCode}");
        }

        /// <inheritdoc />
        public override bool ServerActive()
        {
            /*
                Starting counts: Mirror must not conclude the server is
                gone while the room bootstrap is still connecting.
            */
            lock (_gate)
            {
                return _serverActive;
            }
        }

        /// <inheritdoc />
        public override bool ClientConnected()
        {
            lock (_gate)
            {
                return _clientState == SessionSideState.Started;
            }
        }

        /// <inheritdoc />
        public override void ClientConnect(string address)
        {
            string roomCode = (address ?? string.Empty).Trim();
            lock (_gate)
            {
                if (_clientState == SessionSideState.Starting)
                {
                    LogWarning("ClientConnect ignored: the client side is already connecting.");
                    return;
                }

                if (_clientState == SessionSideState.Started)
                {
                    LogWarning("ClientConnect ignored: the client side is already connected.");
                    return;
                }

                if (_terminal)
                {
                    _terminal = false;
                    _bootstrap = null;
                    StartupError = null;
                }

                if (roomCode.Length == 0)
                {
                    Stage(StagedKind.ClientFailed, error: TransportError.InvalidSend);
                    UnityEngine.Debug.LogError(
                        "[SignalFishMirrorTransport] the NetworkManager address is the Signal Fish room code; it cannot be empty"
                    );
                    return;
                }

                if (_maxFrameBytes <= AdapterMtu.WireReserve)
                {
                    Stage(
                        StagedKind.ClientFailed,
                        error: TransportError.InvalidSend,
                        reason: $"MaxFrameBytes ({_maxFrameBytes}) must exceed the adapter wire reserve ({AdapterMtu.WireReserve})"
                    );
                    return;
                }

                /*
                    A live session already has the room answer: riding it
                    needs no relay round trip, and a different code cannot
                    silently strand Mirror's client in limbo.
                */
                if (_client != null)
                {
                    if (_client.Snapshot.RoomCode == roomCode)
                    {
                        _clientState = SessionSideState.Started;
                        Stage(StagedKind.ClientConnected);
                    }
                    else
                    {
                        Stage(
                            StagedKind.ClientFailed,
                            error: TransportError.InvalidSend,
                            reason: $"this machine is already in room {_client.Snapshot.RoomCode}"
                        );
                    }

                    return;
                }

                /*
                    A running bootstrap has already fixed the room (the
                    server side started first): the client side rides it.
                */
                _clientState = SessionSideState.Starting;
                if (_bootstrap == null || _bootstrap.IsCompleted)
                {
                    _pendingRoomCode = roomCode;
                    _bootstrap = RunBootstrapAsync();
                }
            }
        }

        /// <inheritdoc />
        public override void ClientSend(
            ArraySegment<byte> segment,
            int channelId = Channels.Reliable
        )
        {
            if (!ValidateSend(channelId, segment))
            {
                return;
            }

            EnqueueOutbound(AdapterWire.BroadcastTarget, channelId, connectionId: 0, segment);
        }

        /// <inheritdoc />
        public override void ClientDisconnect()
        {
            bool teardown;
            lock (_gate)
            {
                if (_clientState == SessionSideState.Stopped)
                {
                    return;
                }

                _clientState = SessionSideState.Stopped;
                teardown = _serverState == SessionSideState.Stopped;
            }

            /*
                Mirror holds the client in Disconnecting until the
                transport answers with OnClientDisconnected — starting or
                connected, a voluntary stop always owes the callback.
            */
            Stage(StagedKind.ClientDropped);

            if (teardown)
            {
                Teardown("stopped locally");
            }
        }

        /// <inheritdoc />
        public override void ServerStart()
        {
            lock (_gate)
            {
                if (_serverActive)
                {
                    LogWarning("ServerStart ignored: the server side is already started.");
                    return;
                }

                if (_terminal)
                {
                    _terminal = false;
                    _bootstrap = null;
                    StartupError = null;
                }

                if (_maxFrameBytes <= AdapterMtu.WireReserve)
                {
                    UnityEngine.Debug.LogError(
                        $"[SignalFishMirrorTransport] MaxFrameBytes ({_maxFrameBytes}) must exceed the adapter wire reserve ({AdapterMtu.WireReserve}); the server side cannot start."
                    );
                    StartupError =
                        StartupError ?? "MaxFrameBytes is below the adapter wire reserve";
                    return;
                }

                _serverActive = true;
                _serverState = SessionSideState.Starting;

                if (_client != null)
                {
                    /*
                        Late server start on a live session (client-only
                        until now): the room is joined, so only the
                        authority grant is missing.
                    */
                    _ = RunAuthorityUpgradeAsync();
                    return;
                }

                if (_bootstrap == null || _bootstrap.IsCompleted)
                {
                    _pendingRoomCode = _roomCode;
                    _bootstrap = RunBootstrapAsync();
                }
            }
        }

        /// <inheritdoc />
        public override void ServerSend(
            int connectionId,
            ArraySegment<byte> segment,
            int channelId = Channels.Reliable
        )
        {
            if (!ValidateSend(channelId, segment))
            {
                return;
            }

            if (connectionId == SignalFishPeerRouter.HostConnectionId)
            {
                /*
                    Mirror's local connection never routes through the
                    transport; a send to id 0 here is Mirror-internal
                    misuse, and looping it to the relay would echo the
                    host's own frames back as foreign traffic.
                */
                LogWarning(
                    $"ServerSend to the reserved local connection id {connectionId}; dropped."
                );
                return;
            }

            if (!_router.TryGetPeer(connectionId, out Guid peerId))
            {
                lock (_gate)
                {
                    _routedDropped++;
                }

                return;
            }

            EnqueueOutbound(peerId, channelId, connectionId, segment);
        }

        /// <inheritdoc />
        public override void ServerDisconnect(int connectionId)
        {
            /*
                The relay owns peer lifecycles: a kick is the room owner's
                server-side concern, not something this bridge can force.
                Report the disconnect Mirror-side if the peer is real; the
                player stays in the room, so their later frames drop as
                unrouted until they rejoin.
            */
            if (!_router.TryGetPeer(connectionId, out Guid peerId))
            {
                return;
            }

            _router.TryRemovePeer(peerId, out _);
            Stage(StagedKind.PeerStopped, connectionId);
        }

        /// <inheritdoc />
        public override string ServerGetClientAddress(int connectionId)
        {
            return _router.TryGetPeer(connectionId, out Guid peerId)
                ? $"signal-fish:{peerId}"
                : string.Empty;
        }

        /// <inheritdoc />
        public override void ServerStop()
        {
            lock (_gate)
            {
                if (!_serverActive)
                {
                    return;
                }

                _serverActive = false;
                _serverState = SessionSideState.Stopped;
            }

            foreach (KeyValuePair<Guid, int> route in _router.SnapshotRoutes())
            {
                Stage(StagedKind.PeerStopped, route.Value);
            }

            bool teardown;
            lock (_gate)
            {
                teardown = _clientState == SessionSideState.Stopped;
            }

            if (teardown)
            {
                Teardown("stopped locally");
            }

            /*
                With a live session the routes stay: the room membership is
                the router's truth, and a later late server start
                re-announces it (see MarkServerStarted). Mirror already
                tore its connections down with the server, so these
                disconnects are bookkeeping, not a session change.
            */
        }

        /// <inheritdoc />
        public override int GetMaxPacketSize(int channelId = Channels.Reliable)
        {
            return AdapterMtu.MaxSegmentBytes(_maxFrameBytes);
        }

        /// <inheritdoc />
        public override void Shutdown()
        {
            ClientDisconnect();
            ServerStop();
        }

        /// <inheritdoc />
        public override void ServerEarlyUpdate()
        {
            DrainStaged();
            DrainRelay(asServer: true);
        }

        /// <inheritdoc />
        public override void ClientEarlyUpdate()
        {
            DrainStaged();
            DrainRelay(asServer: false);
        }

        /// <inheritdoc />
        public override void ServerLateUpdate()
        {
            FlushOutbound();
        }

        /// <inheritdoc />
        public override void ClientLateUpdate()
        {
            FlushOutbound();
        }

        /*
            Mirror-facing facts are staged, never raised directly from the
            bootstrap thread: Mirror's handlers mutate non-thread-safe
            collections, so they must run on the main thread, which is the
            only place the staging queue drains.
        */
        private void Stage(
            StagedKind kind,
            int connectionId = 0,
            TransportError error = TransportError.Unexpected,
            string reason = "",
            Exception? transportException = null
        )
        {
            lock (_gate)
            {
                _staged.Enqueue(
                    new StagedEvent(kind, connectionId, error, reason, transportException)
                );
            }
        }

        private void DrainStaged()
        {
            StagedEvent[] events;
            lock (_gate)
            {
                if (_staged.Count == 0)
                {
                    return;
                }

                events = _staged.ToArray();
                _staged.Clear();
            }

            foreach (StagedEvent staged in events)
            {
                switch (staged.Kind)
                {
                    case StagedKind.PeerStarted:
                        OnServerConnectedWithAddress?.Invoke(
                            staged.ConnectionId,
                            ServerGetClientAddress(staged.ConnectionId)
                        );
                        break;

                    case StagedKind.PeerStopped:
                        OnServerDisconnected?.Invoke(staged.ConnectionId);
                        break;

                    case StagedKind.ClientConnected:
                        OnClientConnected?.Invoke();
                        break;

                    case StagedKind.ClientFailed:
                        OnClientError?.Invoke(staged.Error, staged.Reason);
                        OnClientDisconnected?.Invoke();
                        break;

                    case StagedKind.ClientDropped:
                        OnClientDisconnected?.Invoke();
                        break;

                    case StagedKind.ServerError:
                        OnServerError?.Invoke(staged.ConnectionId, staged.Error, staged.Reason);
                        break;

                    case StagedKind.ClientTransportException:
                        OnClientTransportException?.Invoke(staged.TransportException!);
                        break;
                }
            }
        }

        /*
            Session-fatal failures tear the whole bridge down. Mirror has
            no "server failed to start" callback, so the server side's
            active flag drops and the staged peers drain as disconnects;
            the client side reports the failure through
            OnClientError/OnClientDisconnected; StartupError carries the
            reason for the game to surface.
        */
        private void FailSession(TransportError error, string reason)
        {
            UnityEngine.Debug.LogError($"[SignalFishMirrorTransport] {reason}");
            bool clientWasStarting;
            bool clientWasStarted;
            lock (_gate)
            {
                clientWasStarting = _clientState == SessionSideState.Starting;
                clientWasStarted = _clientState == SessionSideState.Started;
                Teardown(reason, error);
            }

            if (clientWasStarting)
            {
                Stage(StagedKind.ClientFailed, error: error, reason: reason);
            }
            else if (clientWasStarted)
            {
                Stage(StagedKind.ClientDropped);
            }
        }

        private void Teardown(string reason, TransportError? error = null)
        {
            SignalFishClient? client;
            SignalFishClient? session;
            lock (_gate)
            {
                _terminal = true;
                _generation++;
                client = _client;
                session = _session;
                _client = null;
                _session = null;
                _bootstrap = null;
                _serverActive = false;
                _serverState = SessionSideState.Stopped;
                _clientState = SessionSideState.Stopped;
                /*
                    Queued work belongs to the dead session: stale outbound
                    frames would flush onto a successor before its v3
                    negotiation and trip BinaryFormatNotNegotiated against
                    the fresh session.
                */
                _outbound.Clear();
                _serverPending.Clear();
                _clientPending.Clear();
                StartupError = StartupError ?? reason;
            }

            foreach (KeyValuePair<Guid, int> route in _router.SnapshotRoutes())
            {
                /*
                    Mirror's rule: when an error precedes a disconnect,
                    raise the error first, per connection.
                */
                if (error.HasValue)
                {
                    Stage(StagedKind.ServerError, route.Value, error.Value, reason);
                }

                Stage(StagedKind.PeerStopped, route.Value);
            }

            _router.Clear();

            if (client != null)
            {
                /*
                    The client's dispose is graceful: inside a room it
                    sends the role's leave and waits the shutdown budget
                    for the typed confirmation before ending the session.
                */
                _ = DisposeQuietlyAsync(client);
            }

            if (session != null && !ReferenceEquals(session, client))
            {
                _ = DisposeQuietlyAsync(session);
            }
        }

        private async Task DisposeQuietlyAsync(SignalFishClient client)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogException(ex);
                Stage(StagedKind.ClientTransportException, transportException: ex);
            }
        }

        private bool ValidateSend(int channelId, ArraySegment<byte> segment)
        {
            if (!ChannelIsKnown(channelId))
            {
                LogWarning($"Send on unknown Mirror channel {channelId}; dropped.");
                return false;
            }

            int maxPacketSize = AdapterMtu.MaxSegmentBytes(_maxFrameBytes);
            if (segment.Count > maxPacketSize)
            {
                LogWarning(
                    $"Send of {segment.Count} bytes exceeds the reported packet size ({maxPacketSize}); dropped."
                );
                return false;
            }

            return true;
        }

        /*
            The engine's delivery ids double as the shared header's
            channel bytes: Channels.Reliable = 0, Channels.Unreliable = 1
            (Mirror v96.9.23; pinned by lint-unity-adapter).
        */
        private static bool ChannelIsKnown(int channelId)
        {
            return channelId == AdapterWire.ReliableChannel
                || channelId == AdapterWire.UnreliableChannel;
        }

        private void EnqueueOutbound(
            Guid target,
            int channelId,
            int connectionId,
            ArraySegment<byte> segment
        )
        {
            if (_client == null)
            {
                return;
            }

            lock (_gate)
            {
                if (_outbound.Count >= OutboundCapacity)
                {
                    _outboundDropped++;
                    LogWarning("The outbound queue is full; frame dropped.");
                    return;
                }

                byte[] copy = new byte[segment.Count];
                Array.Copy(segment.Array!, segment.Offset, copy, 0, segment.Count);
                _outbound.Enqueue(new OutboundFrame(target, (byte)channelId, connectionId, copy));
            }
        }

        private void FlushOutbound()
        {
            SignalFishClient? client = _client;
            if (client == null)
            {
                return;
            }

            while (true)
            {
                OutboundFrame frame;
                lock (_gate)
                {
                    if (_outbound.Count == 0)
                    {
                        return;
                    }

                    frame = _outbound.Peek();
                }

                int wireLength = AdapterWire.HeaderLength + frame.Segment.Length;
                if (_sendScratch == null || _sendScratch.Length < wireLength)
                {
                    _sendScratch = new byte[
                        Math.Max(
                            wireLength,
                            AdapterMtu.MaxSegmentBytes(_maxFrameBytes) + AdapterWire.HeaderLength
                        )
                    ];
                }

                Span<byte> wire = _sendScratch.AsSpan(0, wireLength);
                if (
                    !AdapterWire.TryEncode(
                        frame.Channel,
                        frame.Target,
                        frame.Segment,
                        wire,
                        out int written
                    )
                )
                {
                    lock (_gate)
                    {
                        _outbound.Dequeue();
                    }

                    continue;
                }

                CommandSend send = client.SendBinaryGameData(_sendScratch.AsMemory(0, written));
                if (!send.Accepted)
                {
                    if (send.Refusal == AdmissionError.BinaryFormatNotNegotiated)
                    {
                        /*
                            The binary lane needs a negotiated v3 format;
                            without it nothing can ever flow — fail loudly
                            instead of queuing forever.
                        */
                        FailSession(
                            TransportError.Unexpected,
                            "the server never negotiated the binary game-data format"
                        );
                        return;
                    }

                    /*
                        SendBufferFull: the frame stays at the queue head so
                        the next late update retries — relay backpressure
                        reads as Mirror send backpressure, never silent loss.
                    */
                    return;
                }

                lock (_gate)
                {
                    _outbound.Dequeue();
                }

                if (frame.Target == AdapterWire.BroadcastTarget)
                {
                    OnClientDataSent?.Invoke(new ArraySegment<byte>(frame.Segment), frame.Channel);
                }
                else
                {
                    OnServerDataSent?.Invoke(
                        frame.ConnectionId,
                        new ArraySegment<byte>(frame.Segment),
                        frame.Channel
                    );
                }
            }
        }

        private void DrainRelay(bool asServer)
        {
            SignalFishClient? client = _client;
            if (client == null)
            {
                return;
            }

            Queue<RelayFrame> mine = asServer ? _serverPending : _clientPending;
            Queue<RelayFrame> theirs = asServer ? _clientPending : _serverPending;

            FeedPending(mine, asServer);

            while (client.TryDequeueEvent(out PollEvent pollEvent))
            {
                switch (pollEvent.Kind)
                {
                    case PollEventKind.GameData:
                        RouteRelayFrame(pollEvent.GameData, mine, theirs, asServer);
                        break;

                    case PollEventKind.PlayerJoined:
                        AddPeer(pollEvent.PlayerJoined.Player.Id);
                        break;

                    case PollEventKind.PlayerReconnected:
                        AddPeer(pollEvent.LeftPlayerId);
                        break;

                    case PollEventKind.PlayerLeft:
                        DropPeer(pollEvent.LeftPlayerId);
                        break;

                    case PollEventKind.AuthorityChanged:
                        if (pollEvent.AuthorityChanged.YouAreAuthority && !_serverActive)
                        {
                            /*
                                The room moved the authority here (e.g. the
                                old host left) but no Mirror server side is
                                running to serve it. Serving nothing while
                                everyone routes to us would silently stall
                                the game — tear down loudly instead.
                            */
                            FailSession(
                                TransportError.Unexpected,
                                "this machine became the room authority without a running Mirror server side; re-host to become the hub again"
                            );
                            return;
                        }

                        NoteAuthority(pollEvent.AuthorityChanged);
                        break;

                    case PollEventKind.Disconnected:
                        FailSession(
                            TransportError.ConnectionClosed,
                            $"the Signal Fish session closed ({pollEvent.Close.Kind})"
                        );
                        return;

                    default:
                        /*
                            Lobby, delivery reports, and spectator traffic
                            are the game's business, not the bridge's.
                        */
                        break;
                }
            }
        }

        private void FeedPending(Queue<RelayFrame> mine, bool asServer)
        {
            while (mine.Count > 0)
            {
                RelayFrame frame = mine.Dequeue();
                if (asServer)
                {
                    OnServerDataReceived?.Invoke(
                        frame.ConnectionId,
                        new ArraySegment<byte>(frame.Segment),
                        frame.Channel
                    );
                }
                else
                {
                    OnClientDataReceived?.Invoke(
                        new ArraySegment<byte>(frame.Segment),
                        frame.Channel
                    );
                }
            }
        }

        private void RouteRelayFrame(
            IncomingGameData gameData,
            Queue<RelayFrame> mine,
            Queue<RelayFrame> theirs,
            bool asServer
        )
        {
            if (
                !AdapterWire.TryDecode(
                    gameData.Payload.Span,
                    out byte channel,
                    out Guid target,
                    out ReadOnlySpan<byte> segment
                )
            )
            {
                /*
                    Not ours, or a version we cannot read: another consumer
                    of the same room's game-data lane (the room's own JSON
                    relay traffic), never a reason to tear down.
                */
                return;
            }

            AdapterFrameRoute route = SignalFishReceiveRules.Route(
                _localIsAuthority,
                _localPlayerId,
                _authorityPlayerId,
                gameData.FromPlayer,
                target
            );

            /*
                Server-bound frames need a real route before they can go
                anywhere: presenting an unrouted sender (a peer that left
                or was kicked while its frames were in flight) as any real
                connection would misattribute the segment, and the reserved
                id 0 reads as Mirror's local connection. Drop and count
                here, so neither the direct path nor the cross-side stash
                can ever carry connection id 0.
            */
            int connectionId = SignalFishPeerRouter.HostConnectionId;
            if (
                route == AdapterFrameRoute.ConsumeAsServer
                && !_router.TryGetConnection(gameData.FromPlayer, out connectionId)
            )
            {
                lock (_gate)
                {
                    _routedDropped++;
                }

                return;
            }

            RelayFrame relayed = new RelayFrame(channel, segment.ToArray(), connectionId);

            bool feedsThisSide = asServer
                ? route == AdapterFrameRoute.ConsumeAsServer
                : route == AdapterFrameRoute.ConsumeAsClient;
            if (feedsThisSide)
            {
                mine.Enqueue(relayed);
                FeedPending(mine, asServer);
            }
            else if (
                route == AdapterFrameRoute.ConsumeAsServer
                || route == AdapterFrameRoute.ConsumeAsClient
            )
            {
                theirs.Enqueue(relayed);
            }
        }

        private void AddPeer(Guid playerId)
        {
            if (playerId == Guid.Empty || playerId == _localPlayerId)
            {
                return;
            }

            if (_router.TryAddPeer(playerId, out int connectionId))
            {
                Stage(StagedKind.PeerStarted, connectionId);
            }
        }

        private void DropPeer(Guid playerId)
        {
            if (!_router.TryRemovePeer(playerId, out int connectionId))
            {
                return;
            }

            Stage(StagedKind.PeerStopped, connectionId);
        }

        private void NoteAuthority(AuthorityChangedMessage changed)
        {
            _authorityPlayerId = changed.AuthorityPlayer ?? Guid.Empty;
            _localIsAuthority = changed.YouAreAuthority;
        }

        private void LogWarning(string message)
        {
            UnityEngine.Debug.LogWarning($"[SignalFishMirrorTransport] {message}");
        }

        private bool IsStale(int generation)
        {
            lock (_gate)
            {
                return _generation != generation;
            }
        }

        private async Task RunBootstrapAsync()
        {
            int generation;
            string roomCode;
            lock (_gate)
            {
                generation = _generation;
                roomCode = _pendingRoomCode;
            }

            SignalFishClient? client = null;
            try
            {
                ITransport transport =
                    TransportFactory != null ? TransportFactory() : new WebSocketTransport();
                SignalFishClientOptions options = new SignalFishClientOptions(
                    maxFrameBytes: _maxFrameBytes
                );
                client = new SignalFishClient(transport, SystemClock.Instance, options);
                lock (_gate)
                {
                    if (_generation != generation)
                    {
                        _ = DisposeQuietlyAsync(client);
                        return;
                    }

                    /*
                        The session is bootstrap-private until it is live:
                        publishing it to the main thread now would let
                        DrainRelay consume the very events the waits below
                        are polling for.
                    */
                    _session = client;
                }

                await client.ConnectAsync(new Uri(_endpoint)).ConfigureAwait(false);
                if (IsStale(generation))
                {
                    await DisposeQuietlyAsync(client).ConfigureAwait(false);
                    return;
                }

                Require(
                    client
                        .SendAuthenticate(
                            new AuthenticateMessage(
                                appId: NullIfEmpty(_appId),
                                gameDataFormat: "message_pack",
                                protocolVersion: 3,
                                connectToken: NullIfEmpty(_connectToken)
                            )
                        )
                        .Accepted,
                    "the authenticate was refused"
                );
                if (
                    !await WaitForAsync(
                            client,
                            () => client.IsAuthenticated,
                            "authentication",
                            generation
                        )
                        .ConfigureAwait(false)
                )
                {
                    return;
                }

                Require(
                    client
                        .SendJoinRoom(
                            new JoinRoomMessage(
                                _gameName,
                                _playerName,
                                NullIfEmpty(roomCode),
                                supportsAuthority: true
                            )
                        )
                        .Accepted,
                    "the room join was refused"
                );
                RoomJoinedReceipt joined = await WaitForRoomJoinedAsync(client, generation)
                    .ConfigureAwait(false);
                if (joined.MembershipPlayerId == Guid.Empty)
                {
                    return;
                }

                lock (_gate)
                {
                    if (_generation != generation)
                    {
                        _ = DisposeQuietlyAsync(client);
                        return;
                    }

                    _localPlayerId = joined.MembershipPlayerId;
                    SeedPeersFromSnapshot(joined.Snapshot);
                }

                if (
                    client.Snapshot.NegotiatedProtocolVersion is not uint negotiated
                    || negotiated < 3
                )
                {
                    FailSession(
                        TransportError.Unexpected,
                        "the server did not negotiate protocol v3 (required for the binary lane)"
                    );
                    return;
                }

                /*
                    The host path: the Mirror server side requires the
                    room authority. Non-authority members run client-only
                    and skip the grant round.
                */
                bool wantsServer;
                lock (_gate)
                {
                    wantsServer = _serverActive;
                }

                if (wantsServer && !client.Snapshot.IsAuthority)
                {
                    Require(
                        client.SendAuthorityRequest(becomeAuthority: true).Accepted,
                        "the authority request was refused"
                    );
                    if (
                        !await WaitForAsync(
                                client,
                                () => client.Snapshot.IsAuthority,
                                "the authority grant",
                                generation
                            )
                            .ConfigureAwait(false)
                    )
                    {
                        return;
                    }
                }

                bool clientWasStarting;
                lock (_gate)
                {
                    if (_generation != generation)
                    {
                        _ = DisposeQuietlyAsync(client);
                        return;
                    }

                    _localIsAuthority = client.Snapshot.IsAuthority;
                    _authorityPlayerId = _localIsAuthority
                        ? _localPlayerId
                        : ResolveAuthorityPlayerId(joined.Snapshot);
                    if (_serverActive)
                    {
                        _serverState = SessionSideState.Started;
                    }

                    clientWasStarting = _clientState == SessionSideState.Starting;
                    if (clientWasStarting)
                    {
                        _clientState = SessionSideState.Started;
                    }

                    /*
                        The session is live: hand it to the main thread's
                        drain, which owns the event stream from here on.
                        The bootstrap is done - a later start rides the
                        session, never this dead task.
                    */
                    _client = client;
                    _session = null;
                    _bootstrap = null;
                    if (clientWasStarting)
                    {
                        Stage(StagedKind.ClientConnected);
                    }
                }
            }
            catch (Exception ex)
            {
                if (IsStale(generation))
                {
                    /*
                        A newer bootstrap owns the bridge; this one only
                        buries its own client.
                    */
                    if (client != null)
                    {
                        await DisposeQuietlyAsync(client).ConfigureAwait(false);
                    }

                    return;
                }

                FailSession(
                    TransportError.Unexpected,
                    $"the Signal Fish session failed to start: {ex.Message}"
                );
            }
        }

        private static void Require(bool condition, string failure)
        {
            if (!condition)
            {
                throw new InvalidOperationException(failure);
            }
        }

        /*
            The late server start on a live client-only session: the room
            is joined, so only the authority grant is missing. Failures
            are session-fatal exactly like a bootstrap failure — a server
            side that cannot lead the room it just joined serves nothing.
        */
        private async Task RunAuthorityUpgradeAsync()
        {
            int generation;
            SignalFishClient? client;
            lock (_gate)
            {
                generation = _generation;
                client = _client;
            }

            if (client == null)
            {
                return;
            }

            try
            {
                if (client.Snapshot.IsAuthority)
                {
                    MarkServerStarted(generation);
                    return;
                }

                Require(
                    client.SendAuthorityRequest(becomeAuthority: true).Accepted,
                    "the authority request was refused"
                );
                if (
                    !await WaitForAsync(
                            client,
                            () => client.Snapshot.IsAuthority,
                            "the authority grant",
                            generation
                        )
                        .ConfigureAwait(false)
                )
                {
                    return;
                }

                MarkServerStarted(generation);
            }
            catch (Exception ex)
            {
                if (IsStale(generation))
                {
                    return;
                }

                FailSession(
                    TransportError.Unexpected,
                    $"the authority upgrade failed: {ex.Message}"
                );
            }
        }

        private void MarkServerStarted(int generation)
        {
            lock (_gate)
            {
                if (IsStale(generation) || !_serverActive)
                {
                    return;
                }

                _serverState = SessionSideState.Started;

                /*
                    Mirror wired its server callbacks in Listen (this is
                    the late start), so every announce before now drained
                    into nothing: re-announce the router's membership —
                    the live room's truth — now that the handler is live.
                */
                foreach (KeyValuePair<Guid, int> route in _router.SnapshotRoutes())
                {
                    Stage(StagedKind.PeerStarted, route.Value);
                }
            }
        }

        private async Task<bool> WaitForAsync(
            SignalFishClient client,
            Func<bool> condition,
            string what,
            int generation
        )
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(_startTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (IsStale(generation))
                {
                    return false;
                }

                if (condition())
                {
                    return true;
                }

                if (!client.IsConnected)
                {
                    if (IsStale(generation))
                    {
                        return false;
                    }

                    FailSession(
                        TransportError.ConnectionClosed,
                        $"the session ended while waiting for {what}"
                    );
                    return false;
                }

                await Task.Delay(10).ConfigureAwait(false);
            }

            if (IsStale(generation))
            {
                return false;
            }

            FailSession(TransportError.Timeout, $"timed out waiting for {what}");
            return false;
        }

        private async Task<RoomJoinedReceipt> WaitForRoomJoinedAsync(
            SignalFishClient client,
            int generation
        )
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(_startTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (IsStale(generation))
                {
                    return default;
                }

                while (client.TryDequeueEvent(out PollEvent pollEvent))
                {
                    if (pollEvent.Kind == PollEventKind.RoomJoined)
                    {
                        return new RoomJoinedReceipt(
                            pollEvent.Membership.PlayerId,
                            pollEvent.Snapshot
                        );
                    }
                }

                if (!client.IsConnected)
                {
                    if (IsStale(generation))
                    {
                        return default;
                    }

                    FailSession(
                        TransportError.ConnectionClosed,
                        "the session ended while waiting for the room join"
                    );
                    return default;
                }

                await Task.Delay(10).ConfigureAwait(false);
            }

            if (IsStale(generation))
            {
                return default;
            }

            FailSession(TransportError.Timeout, "timed out waiting for the room join");
            return default;
        }

        private void SeedPeersFromSnapshot(RoomSnapshot snapshot)
        {
            foreach (PlayerInfo player in snapshot.CurrentPlayers)
            {
                AddPeer(player.Id);
            }
        }

        private static Guid ResolveAuthorityPlayerId(RoomSnapshot snapshot)
        {
            foreach (PlayerInfo player in snapshot.CurrentPlayers)
            {
                if (player.IsAuthority)
                {
                    return player.Id;
                }
            }

            return Guid.Empty;
        }

        private static string? NullIfEmpty(string value)
        {
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
#endif
