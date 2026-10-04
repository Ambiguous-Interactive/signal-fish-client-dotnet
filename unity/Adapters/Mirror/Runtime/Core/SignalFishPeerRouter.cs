#nullable enable
namespace SignalFish.Client.Adapters.Mirror
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The bidirectional map between Signal Fish player ids and Mirror
    /// server-side connection ids. Mirror knows only its own integer
    /// connection ids; the relay knows only player UUIDs. The router
    /// assigns ids monotonically from one — never reused while the router
    /// lives — so a peer that leaves and rejoins is a brand-new Mirror
    /// connection exactly as Mirror expects, and zero stays reserved for
    /// the host's local connection. All members are thread-safe.
    /// </summary>
    public sealed class SignalFishPeerRouter
    {
        /// <summary>The connection id reserved for the host's local connection.</summary>
        public const int HostConnectionId = 0;

        /// <summary>Gets how many peers are currently routed.</summary>
        public int PeerCount
        {
            get
            {
                lock (_gate)
                {
                    return _peerToConnection.Count;
                }
            }
        }

        private readonly object _gate = new object();
        private readonly Dictionary<Guid, int> _peerToConnection = new Dictionary<Guid, int>();
        private readonly Dictionary<int, Guid> _connectionToPeer = new Dictionary<int, Guid>();
        private int _nextConnectionId = HostConnectionId + 1;

        /// <summary>
        /// Routes a peer, assigning the next connection id. Succeeds once
        /// per peer lifetime: an already-routed peer keeps its id and the
        /// call fails, so a join/rejoin race can never renumber a peer
        /// Mirror still believes connected.
        /// </summary>
        public bool TryAddPeer(Guid peerId, out int connectionId)
        {
            lock (_gate)
            {
                if (peerId == Guid.Empty || _peerToConnection.ContainsKey(peerId))
                {
                    connectionId = 0;
                    return false;
                }

                if (_nextConnectionId == int.MaxValue)
                {
                    /*
                        Unrealistic in a room, but the failure is loud
                        rather than a silent id reuse decades in.
                    */
                    connectionId = 0;
                    return false;
                }

                connectionId = _nextConnectionId;
                _nextConnectionId++;
                _peerToConnection[peerId] = connectionId;
                _connectionToPeer[connectionId] = peerId;
                return true;
            }
        }

        /// <summary>
        /// Drops a peer's route, reporting the id Mirror saw. Fails for a
        /// peer that was never routed.
        /// </summary>
        public bool TryRemovePeer(Guid peerId, out int connectionId)
        {
            lock (_gate)
            {
                if (!_peerToConnection.TryGetValue(peerId, out connectionId))
                {
                    connectionId = 0;
                    return false;
                }

                _peerToConnection.Remove(peerId);
                _connectionToPeer.Remove(connectionId);
                return true;
            }
        }

        /// <summary>Translates a player id to its connection id.</summary>
        public bool TryGetConnection(Guid peerId, out int connectionId)
        {
            lock (_gate)
            {
                return _peerToConnection.TryGetValue(peerId, out connectionId);
            }
        }

        /// <summary>Translates a connection id to its player id.</summary>
        public bool TryGetPeer(int connectionId, out Guid peerId)
        {
            lock (_gate)
            {
                return _connectionToPeer.TryGetValue(connectionId, out peerId);
            }
        }

        /// <summary>
        /// Copies the current routes (peer id to connection id) so callers
        /// can raise disconnect events outside the router's lock.
        /// </summary>
        public IReadOnlyList<KeyValuePair<Guid, int>> SnapshotRoutes()
        {
            lock (_gate)
            {
                List<KeyValuePair<Guid, int>> routes = new List<KeyValuePair<Guid, int>>(
                    _peerToConnection.Count
                );
                foreach (KeyValuePair<Guid, int> route in _peerToConnection)
                {
                    routes.Add(route);
                }

                return routes;
            }
        }

        /// <summary>Clears every route; the id counter keeps advancing.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _peerToConnection.Clear();
                _connectionToPeer.Clear();
            }
        }
    }
}
