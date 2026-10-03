namespace SignalFish.Client.V3
{
    using System;
    using System.Collections.Generic;
    using SignalFish.Client.Polling;
    using SignalFish.Client.Protocol;

    /// <summary>
    /// A peer within a <see cref="MeshSession"/>, enriched with
    /// selected-path liveness.
    /// </summary>
    public readonly struct MeshPeer : IEquatable<MeshPeer>
    {
        /// <summary>Gets the peer's player identity.</summary>
        public Guid PlayerId { get; }

        /// <summary>Gets the peer's display name (empty until a session plan names it).</summary>
        public string PlayerName { get; }

        /// <summary>Gets a value indicating whether the peer is the session's authoritative host.</summary>
        public bool IsAuthority { get; }

        /// <summary>
        /// Gets a value indicating whether this client sends the WebRTC offer
        /// to this peer (server-assigned glare role; obey verbatim).
        /// </summary>
        public bool Initiate { get; }

        /// <summary>
        /// Gets the last-known liveness reported for the session's selected
        /// transport; status for any other transport is ignored.
        /// </summary>
        public bool Connected { get; }

        internal MeshPeer(
            Guid playerId,
            string playerName,
            bool isAuthority,
            bool initiate,
            bool connected
        )
        {
            PlayerId = playerId;
            PlayerName = playerName;
            IsAuthority = isAuthority;
            Initiate = initiate;
            Connected = connected;
        }

        /// <inheritdoc />
        public bool Equals(MeshPeer other) =>
            PlayerId == other.PlayerId
            && PlayerName == other.PlayerName
            && IsAuthority == other.IsAuthority
            && Initiate == other.Initiate
            && Connected == other.Connected;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is MeshPeer other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(PlayerId);
            hash.Add(PlayerName);
            hash.Add(IsAuthority);
            hash.Add(Initiate);
            hash.Add(Connected);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(MeshPeer left, MeshPeer right) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(MeshPeer left, MeshPeer right) => !left.Equals(right);
    }

    /// <summary>
    /// An always-consistent view of the current mesh/host/relay session,
    /// folded purely from <see cref="PollEvent"/>s: the chosen
    /// topology/transport, the peers this client should connect to (each
    /// with its server-assigned initiate flag and selected-path liveness),
    /// the elected host, and the ICE servers. It does the fiddly
    /// bookkeeping — late joins, host re-election, and reconnect replay —
    /// correctly and idempotently, so consumers don't each re-implement it.
    /// </summary>
    /// <remarks>
    /// No WebRTC, no I/O, and no threads: drive it by calling
    /// <see cref="Apply"/> on every polled event, then read the accessors.
    /// The client still obeys the server — every initiate flag is copied
    /// verbatim from the server, never computed here.
    /// </remarks>
    public sealed class MeshSession
    {
        /// <summary>
        /// Gets the latest authoritative session-plan generation;
        /// <see langword="null"/> before any plan.
        /// </summary>
        public string? Generation => _generation;

        /// <summary>Gets the chosen session topology; <see langword="null"/> before any plan.</summary>
        public SessionTopology? Topology => _topology;

        /// <summary>Gets the chosen data-path transport; <see langword="null"/> before any plan.</summary>
        public SessionTransport? Transport => _transport;

        /// <summary>Gets the universal fallback transport (always relay); <see langword="null"/> before any plan.</summary>
        public SessionTransport? Fallback => _fallback;

        /// <summary>Gets the elected host's player id (host topology only).</summary>
        public Guid? Host => _host;

        /// <summary>Gets the validated connect target for a host + direct plan.</summary>
        public DirectEndpointInfo? DirectEndpoint => _directEndpoint;

        /// <summary>
        /// Gets the peers this client should connect to. Treat the value as
        /// valid only until the next <see cref="Apply"/>: a plan replaces
        /// the list wholesale, while other events mutate it in place.
        /// </summary>
        public IReadOnlyList<MeshPeer> Peers => _peers;

        /// <summary>Gets the ICE (STUN/TURN) servers for WebRTC (pre-gathered or from the plan).</summary>
        public IReadOnlyList<IceServerInfo> IceServers => _iceServers;

        /// <summary>
        /// Gets a value indicating whether a non-relay (host or mesh) plan is
        /// in effect — i.e. the consumer should be establishing peer-to-peer
        /// connections.
        /// </summary>
        public bool IsP2p => _topology is SessionTopology.Host or SessionTopology.Mesh;

        private string? _generation;
        private SessionTopology? _topology;
        private SessionTransport? _transport;
        private SessionTransport? _fallback;
        private Guid? _host;
        private DirectEndpointInfo? _directEndpoint;
        private List<MeshPeer> _peers = new List<MeshPeer>();
        private List<IceServerInfo> _iceServers = new List<IceServerInfo>();

        /// <summary>Initializes an empty session tracker.</summary>
        public MeshSession() { }

        /// <summary>
        /// Folds one polled event into the session. Returns
        /// <see langword="true"/> when the event changed the view (handy for
        /// deciding whether to redraw or re-evaluate connections); a session
        /// plan always returns <see langword="true"/> because it re-asserts
        /// the authoritative plan. Irrelevant events — and no-ops such as a
        /// status for an unknown peer, a redundant
        /// <c>NewPeer</c>/<c>PeerTransportStatus</c>, or a
        /// <c>NewPeer</c> while the selected transport is not WebRTC — return
        /// <see langword="false"/>. Applying the same sequence twice yields
        /// the same state as applying it once (idempotent under reconnect
        /// replay).
        /// </summary>
        public bool Apply(in PollEvent pollEvent)
        {
            switch (pollEvent.Kind)
            {
                case PollEventKind.SessionPlan:
                    return ApplyPlan(pollEvent.SessionPlan);
                case PollEventKind.NewPeer:
                    return ApplyNewPeer(pollEvent.NewPeer);
                case PollEventKind.PeerTransportStatus:
                    return ApplyPeerStatus(pollEvent.PeerTransportStatus);
                case PollEventKind.PlayerLeft:
                    return ApplyPlayerLeft(pollEvent.LeftPlayerId);
                case PollEventKind.RoomJoined:
                    return ApplyPreGather(pollEvent.Snapshot.IceServers);
                case PollEventKind.Reconnected:
                    return ApplyReconnected(pollEvent.Snapshot.IceServers);
                case PollEventKind.RoomLeft:
                case PollEventKind.SpectatorJoined:
                case PollEventKind.SpectatorLeft:
                case PollEventKind.Disconnected:
                    return ApplyTerminalReset();
                default:
                    return false;
            }
        }

        /// <summary>Looks up a peer by id.</summary>
        public MeshPeer? Peer(Guid playerId)
        {
            int index = IndexOfPeer(playerId);
            return index < 0 ? (MeshPeer?)null : _peers[index];
        }

        private bool ApplyPlan(in SessionPlanMessage plan)
        {
            bool selectedPathChanged =
                _generation != plan.Generation || _transport != plan.Transport;
            _generation = plan.Generation;
            _topology = plan.Topology;
            _transport = plan.Transport;
            _fallback = plan.Fallback;
            _host = plan.Host;
            _directEndpoint = plan.DirectEndpoint;

            /*
                A plan fully REPLACES the peer set (host re-election and
                topology change included). Surviving liveness is preserved
                only when generation, selected transport, and offerer role
                are unchanged; peers absent from the new plan are dropped.
            */
            List<MeshPeer> planned = new List<MeshPeer>(plan.Peers.Count);
            for (int i = 0; i < plan.Peers.Count; i++)
            {
                SessionPeerInfo incoming = plan.Peers[i];
                int existing = IndexOfPeer(incoming.PlayerId);
                bool connected =
                    !selectedPathChanged
                    && existing >= 0
                    && _peers[existing].Connected
                    && _peers[existing].Initiate == incoming.Initiate;
                planned.Add(
                    new MeshPeer(
                        incoming.PlayerId,
                        incoming.PlayerName,
                        incoming.IsAuthority,
                        incoming.Initiate,
                        connected
                    )
                );
            }

            _peers = planned;
            // Every plan is authoritative; an explicit relay reset clears stale WebRTC ICE state.
            _iceServers = new List<IceServerInfo>(plan.IceServers);
            return true;
        }

        private bool ApplyNewPeer(in NewPeerMessage directive)
        {
            /*
                The authority scopes NewPeer to WebRTC peer directives: on a
                relay plan (or before any plan) a directive cannot describe a
                connectable peer, so the view stays unchanged — mirroring the
                PeerTransportStatus transport gate below.
            */
            if (_transport != SessionTransport.WebRtc)
            {
                return false;
            }

            // Late joiner: upsert by id (idempotent; the latest flag wins).
            int index = IndexOfPeer(directive.PeerId);
            if (index >= 0)
            {
                MeshPeer existing = _peers[index];
                if (existing.Initiate == directive.YouInitiate)
                {
                    return false;
                }

                /*
                    The controller restarts the handshake when the server
                    changes the offerer role, so prior liveness is stale.
                */
                _peers[index] = new MeshPeer(
                    existing.PlayerId,
                    existing.PlayerName,
                    existing.IsAuthority,
                    directive.YouInitiate,
                    false
                );
                return true;
            }

            _peers.Add(
                new MeshPeer(directive.PeerId, string.Empty, false, directive.YouInitiate, false)
            );
            return true;
        }

        private bool ApplyPeerStatus(in PeerTransportStatusMessage status)
        {
            if (_transport != MapTransport(status.Transport))
            {
                return false;
            }

            /*
                A status carries no generation, so one relayed before a
                re-plan can land just after it and refresh a pre-handshake
                view: inherent last-known-liveness staleness, bounded by the
                next report. Only liveness mutates here; a peer the plan did
                not include is never invented.
            */
            int index = IndexOfPeer(status.PeerId);
            if (index < 0)
            {
                return false;
            }

            MeshPeer existing = _peers[index];
            if (existing.Connected == status.Connected)
            {
                return false;
            }

            _peers[index] = new MeshPeer(
                existing.PlayerId,
                existing.PlayerName,
                existing.IsAuthority,
                existing.Initiate,
                status.Connected
            );
            return true;
        }

        private bool ApplyPlayerLeft(Guid playerId)
        {
            /*
                A departing player is dropped immediately so Peers never
                advertises someone who has left (the server also re-plans on
                membership change, but this closes the window in between).
                Removal needs no server authority to fold.
            */
            int before = _peers.Count;
            for (int i = _peers.Count - 1; i >= 0; i--)
            {
                if (_peers[i].PlayerId == playerId)
                {
                    _peers.RemoveAt(i);
                }
            }

            bool hostDeparted = _host == playerId;
            if (hostDeparted)
            {
                /*
                    The server elects and replans a replacement host on
                    departure, but the next SessionPlan owns that decision.
                    Until it lands, a departed host must not stay reachable
                    through Host/DirectEndpoint.
                */
                _host = null;
                _directEndpoint = null;
            }

            return _peers.Count != before || hostDeparted;
        }

        /*
            Seeds an ICE pre-gather set (RoomJoined and Reconnected). An
            empty set preserves the existing one and an identical set is a
            no-op; either way it reports false so Apply only signals a real
            change.
        */
        private bool ApplyPreGather(IReadOnlyList<IceServerInfo> iceServers)
        {
            if (iceServers.Count == 0 || SessionPlanMessage.SequenceEquals(_iceServers, iceServers))
            {
                return false;
            }

            _iceServers = new List<IceServerInfo>(iceServers);
            return true;
        }

        private bool ApplyReconnected(IReadOnlyList<IceServerInfo> iceServers)
        {
            /*
                Reconnect is a hard plan boundary: mesh controls are not
                valid replay entries. Clear the prior plan and peer set
                immediately so stale topology cannot remain actionable
                during the gap, then seed the refreshed pre-gather ICE.
            */
            bool hadAuthoritativeState =
                _generation is not null
                || _topology.HasValue
                || _transport.HasValue
                || _fallback.HasValue
                || _host.HasValue
                || _directEndpoint.HasValue
                || _peers.Count > 0;
            bool iceChanged = !SessionPlanMessage.SequenceEquals(_iceServers, iceServers);
            Reset();
            ApplyPreGather(iceServers);
            return hadAuthoritativeState || iceChanged;
        }

        private bool ApplyTerminalReset()
        {
            bool hadState = _topology.HasValue || _peers.Count > 0 || _iceServers.Count > 0;
            Reset();
            return hadState;
        }

        private void Reset()
        {
            _generation = null;
            _topology = null;
            _transport = null;
            _fallback = null;
            _host = null;
            _directEndpoint = null;
            _peers.Clear();
            _iceServers.Clear();
        }

        private int IndexOfPeer(Guid playerId)
        {
            for (int i = 0; i < _peers.Count; i++)
            {
                if (_peers[i].PlayerId == playerId)
                {
                    return i;
                }
            }

            return -1;
        }

        private static SessionTransport? MapTransport(string token)
        {
            switch (token)
            {
                case "relay":
                    return SessionTransport.Relay;
                case "direct":
                    return SessionTransport.Direct;
                case "webrtc":
                    return SessionTransport.WebRtc;
                default:
                    return null;
            }
        }
    }
}
