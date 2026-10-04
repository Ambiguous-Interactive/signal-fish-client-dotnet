/*
    SIGNALFISH_FISHNET is produced by this package's asmdef versionDefines
    when FishNet's UPM package (com.firstgeargames.fishnet) is installed,
    and the asmdef's defineConstraints skip the whole assembly when it is
    not - the package is inert without FishNet, and no FishNet type is
    ever referenced unguarded.
*/
#nullable enable
#if SIGNALFISH_FISHNET
namespace SignalFish.Client.Adapters.FishNet
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using FishNet.Managing;
    using FishNet.Transporting;
    using SignalFish.Client;
    using SignalFish.Client.Async;
    using SignalFish.Client.Core;
    using SignalFish.Client.Polling;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.Transport;
    using UnityEngine;

    /// <summary>
    /// The FishNet transport bridge (M8.1): one Signal Fish room carries a
    /// FishNet session. The room's authority plays the FishNet server; the
    /// other members play FishNet clients; game traffic rides the v3
    /// binary game-data lane as raw frames wrapped in the adapter's
    /// <see cref="FishNetAdapterWire"/> header. The Signal Fish relay is a
    /// room broadcast, so FishNet's star topology is realized by
    /// <see cref="SignalFishReceiveRules"/>: the authority consumes its
    /// peers' upstream frames, and clients consume only the authority's
    /// downstream frames addressed to them (or to everyone).
    ///
    /// Assign the component under a NetworkManager's transport list, fill
    /// the session fields, and start host or client through FishNet as
    /// usual. The Signal Fish session (connect, authenticate, join, v3
    /// binary negotiation, and the authority grant on the host path) runs
    /// inside StartConnection; the FishNet side reports Started once the
    /// room is live. The host's local client loops back in-process — its
    /// frames never touch the relay.
    ///
    /// Threading: FishNet iterates both sides on one thread, and every
    /// FishNet-facing callback (connection states, receives) is raised
    /// from that thread's iterate, exactly like FishNet's own transports
    /// — the async bootstrap thread only stages facts into queues. Shared
    /// fields read on the tick thread are written under the gate or
    /// ordered by the Starting→Started transition.
    ///
    /// Validation status: authored against the FishNet 4.x transport
    /// contract and the library's conformance suite, but not yet compiled
    /// in Unity — live validation is the M8.7 runbook item (needs a
    /// licensed Unity seat; Unity never runs in CI).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SignalFishFishNetTransport : Transport
    {
        /// <summary>One frame queued for the relay (an owned copy).</summary>
        private readonly struct OutboundFrame
        {
            internal Guid Target { get; }

            internal byte Channel { get; }

            internal byte[] Segment { get; }

            internal OutboundFrame(Guid target, byte channel, byte[] segment)
            {
                Target = target;
                Channel = channel;
                Segment = segment;
            }
        }

        /// <summary>One relayed frame routed to a FishNet side.</summary>
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
        /// Gets or sets the room code to join; empty creates a new room
        /// (the host path). The joined room's code surfaces on
        /// <see cref="JoinedRoomCode"/>.
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

        /// <summary>Gets or sets the host-mode loopback capacity per direction.</summary>
        public int LoopbackCapacity
        {
            get { return _loopbackCapacity; }
            set { _loopbackCapacity = value; }
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

        /// <summary>Gets how many host-loopback frames overflowed.</summary>
        public int LoopbackDropped
        {
            get { return _loopback?.Dropped ?? 0; }
        }

        /// <summary>Gets how many remote peers are currently routed.</summary>
        public int PeerCount
        {
            get { return _router.PeerCount; }
        }

        /// <summary>Gets the last bootstrap failure, for diagnostics.</summary>
        public string? StartupError { get; private set; }
        public override event Action<ClientConnectionStateArgs>? OnClientConnectionState;

        public override event Action<ServerConnectionStateArgs>? OnServerConnectionState;

        public override event Action<RemoteConnectionStateArgs>? OnRemoteConnectionState;

        public override event Action<ClientReceivedDataArgs>? OnClientReceivedData;

        public override event Action<ServerReceivedDataArgs>? OnServerReceivedData;

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

        /// <summary>The frames queued for the relay, flushed in IterateOutgoing.</summary>
        private readonly Queue<OutboundFrame> _outbound = new Queue<OutboundFrame>();

        /// <summary>Relay frames routed for the server feed, awaiting its iterate.</summary>
        private readonly Queue<RelayFrame> _serverPending = new Queue<RelayFrame>();

        /// <summary>Relay frames routed for the client feed, awaiting its iterate.</summary>
        private readonly Queue<RelayFrame> _clientPending = new Queue<RelayFrame>();

        /// <summary>
        /// FishNet-facing connection-state events staged off the tick
        /// thread, drained at the top of every iterate.
        /// </summary>
        private readonly Queue<LocalConnectionState> _pendingServerStates =
            new Queue<LocalConnectionState>();

        private readonly Queue<LocalConnectionState> _pendingClientStates =
            new Queue<LocalConnectionState>();

        private readonly Queue<RemoteConnectionStateArgs> _pendingRemoteStates =
            new Queue<RemoteConnectionStateArgs>();

        private readonly SignalFishPeerRouter _router = new SignalFishPeerRouter();
        private readonly object _gate = new object();

        private SignalFishClient? _client;

        /// <summary>
        /// The bootstrap-owned session: it holds the client until the
        /// session is live, so the tick thread's drain can never steal the
        /// events the bootstrap is itself waiting on (a RoomJoined consumed
        /// by DrainRelay's default branch would time out every start).
        /// </summary>
        private SignalFishClient? _session;

        /// <summary>Whether FishNet has been told the host's local client exists.</summary>
        private bool _hostClientAnnounced;

        private HostLoopback? _loopback;
        private Task? _bootstrap;
        private byte[]? _sendScratch;
        private LocalConnectionState _serverState = LocalConnectionState.Stopped;
        private LocalConnectionState _clientState = LocalConnectionState.Stopped;
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
        private string _gameName = "fishnet-game";

        [SerializeField]
        [Tooltip("This machine's player name inside the room.")]
        private string _playerName = "player";

        [SerializeField]
        [Tooltip("Room code to join; empty creates a new room (the host path).")]
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
            "The client's inbound physical-frame bound in bytes; the FishNet MTU is this minus the adapter wire reserve."
        )]
        private int _maxFrameBytes = 64 * 1024;

        [SerializeField]
        [Tooltip("Host-mode loopback capacity per direction, in frames.")]
        private int _loopbackCapacity = 256;

        [Header("Timing")]
        [SerializeField]
        [Tooltip("Seconds the session bootstrap may take before StartConnection reports failure.")]
        private float _startTimeoutSeconds = 10f;

        /// <inheritdoc />
        public override void Initialize(NetworkManager networkManager, int transportIndex)
        {
            base.Initialize(networkManager, transportIndex);
        }

        /// <inheritdoc />
        public override void HandleClientConnectionState(
            ClientConnectionStateArgs connectionStateArgs
        )
        {
            OnClientConnectionState?.Invoke(connectionStateArgs);
        }

        /// <inheritdoc />
        public override void HandleServerConnectionState(
            ServerConnectionStateArgs connectionStateArgs
        )
        {
            OnServerConnectionState?.Invoke(connectionStateArgs);
        }

        /// <inheritdoc />
        public override void HandleRemoteConnectionState(
            RemoteConnectionStateArgs connectionStateArgs
        )
        {
            OnRemoteConnectionState?.Invoke(connectionStateArgs);
        }

        /// <inheritdoc />
        public override void HandleClientReceivedDataArgs(ClientReceivedDataArgs receivedDataArgs)
        {
            OnClientReceivedData?.Invoke(receivedDataArgs);
        }

        /// <inheritdoc />
        public override void HandleServerReceivedDataArgs(ServerReceivedDataArgs receivedDataArgs)
        {
            OnServerReceivedData?.Invoke(receivedDataArgs);
        }

        /// <inheritdoc />
        public override LocalConnectionState GetConnectionState(bool server)
        {
            return server ? _serverState : _clientState;
        }

        /// <inheritdoc />
        public override RemoteConnectionState GetConnectionState(int connectionId)
        {
            if (connectionId == SignalFishPeerRouter.HostClientConnectionId)
            {
                /*
                    The host's local client is announced, never routed: the
                    router holds remote peers only, so its state lives in
                    the announce flag.
                */
                return _hostClientAnnounced
                    ? RemoteConnectionState.Started
                    : RemoteConnectionState.Stopped;
            }

            return _router.TryGetPeer(connectionId, out _)
                ? RemoteConnectionState.Started
                : RemoteConnectionState.Stopped;
        }

        /// <inheritdoc />
        public override string GetConnectionAddress(int connectionId)
        {
            /*
                The relay exposes player identities, not network addresses;
                the routed player id is the stable thing to show. The host
                client has no route — it is this machine — so its own
                player id answers for it.
            */
            if (connectionId == SignalFishPeerRouter.HostClientConnectionId)
            {
                return _hostClientAnnounced ? $"signal-fish:{_localPlayerId}" : string.Empty;
            }

            return _router.TryGetPeer(connectionId, out Guid peerId)
                ? $"signal-fish:{peerId}"
                : string.Empty;
        }

        /// <inheritdoc />
        public override int GetMTU(byte channel)
        {
            return FishNetAdapterMtu.MaxSegmentBytes(_maxFrameBytes);
        }

        /// <inheritdoc />
        public override bool IsLocalTransport(int connectionid)
        {
            return connectionid == SignalFishPeerRouter.HostClientConnectionId;
        }

        /// <inheritdoc />
        public override bool StartConnection(bool server)
        {
            lock (_gate)
            {
                LocalConnectionState state = server ? _serverState : _clientState;
                if (state == LocalConnectionState.Started || state == LocalConnectionState.Starting)
                {
                    return false;
                }

                if (_terminal)
                {
                    /*
                        A torn-down bridge re-arms from scratch; the dead
                        bootstrap's generation bump keeps it from ever
                        touching this one.
                    */
                    _terminal = false;
                    _bootstrap = null;
                    StartupError = null;
                }

                StageState(server, LocalConnectionState.Starting);
                if (_bootstrap == null)
                {
                    _bootstrap = RunBootstrapAsync();
                }

                return true;
            }
        }

        /// <inheritdoc />
        public override bool StopConnection(bool server)
        {
            bool teardown;
            lock (_gate)
            {
                LocalConnectionState state = server ? _serverState : _clientState;
                if (state == LocalConnectionState.Stopped)
                {
                    return false;
                }

                StageState(server, LocalConnectionState.Stopping);
                teardown =
                    (
                        _serverState == LocalConnectionState.Stopped
                        || _serverState == LocalConnectionState.Stopping
                    )
                    && (
                        _clientState == LocalConnectionState.Stopped
                        || _clientState == LocalConnectionState.Stopping
                    );
            }

            if (server)
            {
                DropAllPeers();
            }
            else
            {
                /*
                    Host mode: stopping the local client disconnects the
                    host connection on the local server, and vice versa —
                    the reserved id 0 is live only while both sides are.
                */
                RetireHostClientIfGone();
            }

            if (teardown)
            {
                Teardown("stopped locally");
                lock (_gate)
                {
                    StageState(server: true, LocalConnectionState.Stopped);
                    StageState(server: false, LocalConnectionState.Stopped);
                }
            }
            else
            {
                lock (_gate)
                {
                    StageState(server, LocalConnectionState.Stopped);
                }
            }

            RetireHostClientIfGone();
            return true;
        }

        /// <inheritdoc />
        public override bool StopConnection(int connectionId, bool immediately)
        {
            /*
                The relay owns peer lifecycles: a kick is the room owner's
                server-side concern, not something this bridge can force.
                Report the disconnect FishNet-side if the peer is real; the
                player stays in the room, so their later frames drop as
                unrouted until they rejoin.
            */
            if (!_router.TryGetPeer(connectionId, out Guid peerId))
            {
                return false;
            }

            DropPeer(peerId);
            return true;
        }

        /// <inheritdoc />
        public override void Shutdown()
        {
            StopConnection(server: true);
            StopConnection(server: false);
        }

        /// <inheritdoc />
        public override void SendToServer(byte channelId, ArraySegment<byte> segment)
        {
            if (!ValidateSend(channelId, segment))
            {
                return;
            }

            if (
                _serverState == LocalConnectionState.Started
                || _serverState == LocalConnectionState.Starting
            )
            {
                /*
                    Host mode: the local server feed takes the frame
                    without a relay round trip; the sender presents as the
                    host's reserved connection id.
                */
                _loopback?.TryEnqueueClientToServer(channelId, segment);
                return;
            }

            EnqueueOutbound(FishNetAdapterWire.BroadcastTarget, channelId, segment);
        }

        /// <inheritdoc />
        public override void SendToClient(
            byte channelId,
            ArraySegment<byte> segment,
            int connectionId
        )
        {
            if (!ValidateSend(channelId, segment))
            {
                return;
            }

            if (connectionId == SignalFishPeerRouter.HostClientConnectionId)
            {
                _loopback?.TryEnqueueServerToClient(channelId, segment);
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

            EnqueueOutbound(peerId, channelId, segment);
        }

        /// <inheritdoc />
        public override void IterateIncoming(bool asServer)
        {
            DrainStagedEvents();
            DrainLoopback(asServer);
            DrainRelay(asServer);
        }

        /// <inheritdoc />
        public override void IterateOutgoing(bool asServer)
        {
            /*
                One flush serves both FishNet sides: the outbound queue is
                the relay's, not a side's, and the Signal Fish client
                serializes sends internally.
            */
            FlushOutbound();
        }

        private static bool ChannelIsKnown(byte channelId)
        {
            return FishNetAdapterWire.IsKnownChannel(channelId);
        }

        /*
            FishNet-facing events are staged, never raised directly from
            the bootstrap thread: FishNet's handlers mutate non-thread-safe
            collections, so they must run on the tick thread, which is the
            only place StageState's queues drain.
        */
        private void StageState(bool server, LocalConnectionState state)
        {
            if (server)
            {
                _serverState = state;
                _pendingServerStates.Enqueue(state);
            }
            else
            {
                _clientState = state;
                _pendingClientStates.Enqueue(state);
            }
        }

        private void StagePeer(int connectionId, RemoteConnectionState state)
        {
            /*
                The bootstrap thread stages peers too (the join snapshot),
                so the enqueue shares the gate with the tick thread's drain.
            */
            lock (_gate)
            {
                _pendingRemoteStates.Enqueue(
                    new RemoteConnectionStateArgs(state, connectionId, Index)
                );
            }
        }

        private void DrainStagedEvents()
        {
            List<LocalConnectionState> serverStates;
            List<LocalConnectionState> clientStates;
            List<RemoteConnectionStateArgs> remoteStates;
            lock (_gate)
            {
                /*
                    Swap the queues out under the gate and raise outside it:
                    FishNet's handlers may call back into this transport,
                    and a callback re-taking the gate would be reentrant-safe
                    but a slow handler would stall every staging thread.
                */
                serverStates = new List<LocalConnectionState>(_pendingServerStates);
                _pendingServerStates.Clear();
                clientStates = new List<LocalConnectionState>(_pendingClientStates);
                _pendingClientStates.Clear();
                remoteStates = new List<RemoteConnectionStateArgs>(_pendingRemoteStates);
                _pendingRemoteStates.Clear();
            }

            foreach (LocalConnectionState state in serverStates)
            {
                OnServerConnectionState?.Invoke(new ServerConnectionStateArgs(state, Index));
            }

            foreach (LocalConnectionState state in clientStates)
            {
                OnClientConnectionState?.Invoke(new ClientConnectionStateArgs(state, Index));
            }

            foreach (RemoteConnectionStateArgs args in remoteStates)
            {
                OnRemoteConnectionState?.Invoke(args);
            }
        }

        private void Teardown(string reason)
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
                _loopback = null;
                _bootstrap = null;
                _hostClientAnnounced = false;
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

        private static async Task DisposeQuietlyAsync(SignalFishClient client)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogException(ex);
            }
        }

        private bool ValidateSend(byte channelId, ArraySegment<byte> segment)
        {
            if (!ChannelIsKnown(channelId))
            {
                LogWarning($"Send on unknown FishNet channel {channelId}; dropped.");
                return false;
            }

            int mtu = FishNetAdapterMtu.MaxSegmentBytes(_maxFrameBytes);
            if (segment.Count > mtu)
            {
                LogWarning(
                    $"Send of {segment.Count} bytes exceeds the reported MTU ({mtu}); dropped."
                );
                return false;
            }

            return true;
        }

        private void EnqueueOutbound(Guid target, byte channelId, ArraySegment<byte> segment)
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
                _outbound.Enqueue(new OutboundFrame(target, channelId, copy));
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

                int wireLength = FishNetAdapterWire.HeaderLength + frame.Segment.Length;
                if (_sendScratch == null || _sendScratch.Length < wireLength)
                {
                    _sendScratch = new byte[
                        Math.Max(
                            wireLength,
                            FishNetAdapterMtu.MaxSegmentBytes(_maxFrameBytes)
                                + FishNetAdapterWire.HeaderLength
                        )
                    ];
                }

                Span<byte> wire = _sendScratch.AsSpan(0, wireLength);
                if (
                    !FishNetAdapterWire.TryEncode(
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
                        FailBothSides("the server never negotiated the binary game-data format");
                        return;
                    }

                    /*
                        SendBufferFull: the frame stays at the queue head so
                        the next iterate retries — relay backpressure reads
                        as FishNet send backpressure, never silent loss.
                    */
                    return;
                }

                lock (_gate)
                {
                    _outbound.Dequeue();
                }
            }
        }

        private void DrainLoopback(bool asServer)
        {
            /*
                Capture the reference: the bootstrap thread can null it
                (teardown, host retirement) while this iterate runs.
            */
            HostLoopback? loopback = _loopback;
            if (loopback == null)
            {
                return;
            }

            if (asServer)
            {
                while (loopback.TryDequeueClientToServer(out HostLoopbackFrame frame))
                {
                    FeedServer(
                        SignalFishPeerRouter.HostClientConnectionId,
                        frame.Channel,
                        frame.Segment.ToArray()
                    );
                }
            }
            else
            {
                while (loopback.TryDequeueServerToClient(out HostLoopbackFrame frame))
                {
                    FeedClient(frame.Channel, frame.Segment.ToArray());
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
                        if (
                            pollEvent.AuthorityChanged.YouAreAuthority
                            && (
                                _serverState != LocalConnectionState.Started
                                && _serverState != LocalConnectionState.Starting
                            )
                        )
                        {
                            /*
                                The room moved the authority here (e.g. the
                                old host left) but no FishNet server side is
                                running to serve it. Serving nothing while
                                everyone routes to us would silently stall
                                the game — tear down loudly instead.
                            */
                            FailBothSides(
                                "this machine became the room authority without a running FishNet server side; re-host and start the server side"
                            );
                            return;
                        }

                        NoteAuthority(pollEvent.AuthorityChanged);
                        break;

                    case PollEventKind.Disconnected:
                        FailBothSides($"the Signal Fish session closed ({pollEvent.Close.Kind})");
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
                    FeedServer(frame.ConnectionId, frame.Channel, frame.Segment);
                }
                else
                {
                    FeedClient(frame.Channel, frame.Segment);
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
                !FishNetAdapterWire.TryDecode(
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

            FishNetFrameRoute route = SignalFishReceiveRules.Route(
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
                connection would misattribute the segment, and the zero
                fallback reads as the host's own client. Drop and count
                here, so neither the direct path nor the cross-side stash
                can ever carry connection id 0.
            */
            int connectionId = 0;
            if (
                route == FishNetFrameRoute.ConsumeAsServer
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
                ? route == FishNetFrameRoute.ConsumeAsServer
                : route == FishNetFrameRoute.ConsumeAsClient;
            if (feedsThisSide)
            {
                mine.Enqueue(relayed);
                FeedPending(mine, asServer);
            }
            else if (
                route == FishNetFrameRoute.ConsumeAsServer
                || route == FishNetFrameRoute.ConsumeAsClient
            )
            {
                theirs.Enqueue(relayed);
            }
        }

        private void FeedServer(int connectionId, byte channel, byte[] segment)
        {
            OnServerReceivedData?.Invoke(
                new ServerReceivedDataArgs(
                    new ArraySegment<byte>(segment),
                    (Channel)channel,
                    connectionId,
                    Index
                )
            );
        }

        private void FeedClient(byte channel, byte[] segment)
        {
            OnClientReceivedData?.Invoke(
                new ClientReceivedDataArgs(new ArraySegment<byte>(segment), (Channel)channel, Index)
            );
        }

        private void AddPeer(Guid playerId)
        {
            if (playerId == Guid.Empty || playerId == _localPlayerId)
            {
                return;
            }

            if (_router.TryAddPeer(playerId, out int connectionId))
            {
                StagePeer(connectionId, RemoteConnectionState.Started);
            }
        }

        private void DropPeer(Guid playerId)
        {
            if (!_router.TryRemovePeer(playerId, out int connectionId))
            {
                return;
            }

            StagePeer(connectionId, RemoteConnectionState.Stopped);
        }

        private void DropAllPeers()
        {
            foreach (KeyValuePair<Guid, int> route in _router.SnapshotRoutes())
            {
                StagePeer(route.Value, RemoteConnectionState.Stopped);
            }

            _router.Clear();
            RetireHostClientIfGone();
        }

        private void RetireHostClientIfGone()
        {
            lock (_gate)
            {
                bool bothSidesLive =
                    (
                        _serverState == LocalConnectionState.Started
                        || _serverState == LocalConnectionState.Starting
                    )
                    && (
                        _clientState == LocalConnectionState.Started
                        || _clientState == LocalConnectionState.Starting
                    );
                if (!_hostClientAnnounced || bothSidesLive)
                {
                    return;
                }

                _hostClientAnnounced = false;
                /*
                    The loopback exists for the host client: with it retired,
                    queued in-process frames would feed a connection FishNet
                    has already removed.
                */
                _loopback = null;
                StagePeer(
                    SignalFishPeerRouter.HostClientConnectionId,
                    RemoteConnectionState.Stopped
                );
            }
        }

        private void NoteAuthority(AuthorityChangedMessage changed)
        {
            _authorityPlayerId = changed.AuthorityPlayer ?? Guid.Empty;
            _localIsAuthority = changed.YouAreAuthority;
        }

        private void FailBothSides(string reason)
        {
            UnityEngine.Debug.LogError($"[SignalFishFishNetTransport] {reason}");
            Teardown(reason);
            lock (_gate)
            {
                StageState(server: true, LocalConnectionState.Stopped);
                StageState(server: false, LocalConnectionState.Stopped);
            }

            DropAllPeers();
        }

        private void LogWarning(string message)
        {
            if (NetworkManager != null)
            {
                NetworkManager.LogWarning($"[SignalFishFishNetTransport] {message}");
            }
            else
            {
                UnityEngine.Debug.LogWarning($"[SignalFishFishNetTransport] {message}");
            }
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
            lock (_gate)
            {
                generation = _generation;
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
                        publishing it to the tick thread now would let
                        DrainRelay consume the very events the waits below
                        are polling for.
                    */
                    _session = client;
                    _loopback = new HostLoopback(_loopbackCapacity);
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
                                NullIfEmpty(_roomCode),
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
                    FailBothSides(
                        "the server did not negotiate protocol v3 (required for the binary lane)"
                    );
                    return;
                }

                /*
                    The host path: the FishNet server side requires the
                    room authority. Non-authority members run client-only
                    and skip the grant round.
                */
                bool wantsServer;
                lock (_gate)
                {
                    wantsServer = _serverState == LocalConnectionState.Starting;
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
                    if (_serverState == LocalConnectionState.Starting)
                    {
                        StageState(server: true, LocalConnectionState.Started);
                    }

                    if (_clientState == LocalConnectionState.Starting)
                    {
                        StageState(server: false, LocalConnectionState.Started);
                    }

                    /*
                        Host mode: FishNet needs a NetworkConnection for the
                        host's own client (its reserved id 0), or nothing
                        looped to it is ever attributable. Announce it after
                        the side states so FishNet sees the server come up
                        first; DropAllPeers retires it with the server.
                    */
                    if (
                        _serverState == LocalConnectionState.Started
                        && _clientState == LocalConnectionState.Started
                    )
                    {
                        StagePeer(
                            SignalFishPeerRouter.HostClientConnectionId,
                            RemoteConnectionState.Started
                        );
                        _hostClientAnnounced = true;
                    }

                    /*
                        The session is live: hand it to the tick thread's
                        drain, which owns the event stream from here on.
                    */
                    _client = client;
                    _session = null;
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

                FailBothSides($"the Signal Fish session failed to start: {ex.Message}");
            }
        }

        private static void Require(bool condition, string failure)
        {
            if (!condition)
            {
                throw new InvalidOperationException(failure);
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

                    FailBothSides($"the session ended while waiting for {what}");
                    return false;
                }

                await Task.Delay(10).ConfigureAwait(false);
            }

            if (IsStale(generation))
            {
                return false;
            }

            FailBothSides($"timed out waiting for {what}");
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

                    FailBothSides("the session ended while waiting for the room join");
                    return default;
                }

                await Task.Delay(10).ConfigureAwait(false);
            }

            if (IsStale(generation))
            {
                return default;
            }

            FailBothSides("timed out waiting for the room join");
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
