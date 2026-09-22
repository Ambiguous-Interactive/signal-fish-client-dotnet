namespace SignalFish.Client.Core
{
    using System;

    /// <summary>
    /// Coherent synchronous view of the session state — the Rust client's
    /// <c>ClientSnapshot</c> phase table and identity fields at the v2
    /// floor (v3 negotiation and delivery fields land with M6). Every
    /// field is read from one machine state, so multiple reads of one
    /// snapshot always describe the same instant. <c>default</c> is the
    /// disconnected, never-authenticated snapshot.
    /// </summary>
    public readonly struct ClientSnapshot : IEquatable<ClientSnapshot>
    {
        /// <summary>True until the session goes terminal.</summary>
        public bool Connected { get; }

        /// <summary>Handshake observed on the current connection (sticky until teardown).</summary>
        public bool TransportReady { get; }

        /// <summary>Server confirmed authentication on this connection.</summary>
        public bool Authenticated { get; }

        /// <summary>Confirmed room role; null outside a confirmed room.</summary>
        public RoomRole? Role { get; }

        /// <summary>This client's player or spectator id; null outside a confirmed room.</summary>
        public Guid? PlayerId { get; }

        /// <summary>Server-assigned room id; null outside a confirmed room.</summary>
        public Guid? RoomId { get; }

        /// <summary>Human-shareable room code; null outside a confirmed room.</summary>
        public string? RoomCode { get; }

        /// <summary>
        /// Latest server-issued reconnection token (rides
        /// <c>RoomJoined</c>/<c>Reconnected</c> when the deployment sends
        /// it; spectator baselines carry none). A bearer seat credential:
        /// never log it — <see cref="ToString"/> redacts it.
        /// </summary>
        public string? ReconnectionToken { get; }

        /// <summary>True while this connection is the confirmed room authority.</summary>
        public bool IsAuthority { get; }

        /// <summary>Initializes a new snapshot.</summary>
        public ClientSnapshot(
            bool connected,
            bool transportReady,
            bool authenticated,
            RoomRole? role,
            Guid? playerId,
            Guid? roomId,
            string? roomCode,
            string? reconnectionToken,
            bool isAuthority = false
        )
        {
            Connected = connected;
            TransportReady = transportReady;
            Authenticated = authenticated;
            Role = role;
            PlayerId = playerId;
            RoomId = roomId;
            RoomCode = roomCode;
            ReconnectionToken = reconnectionToken;
            IsAuthority = isAuthority;
        }

        public static bool operator ==(ClientSnapshot left, ClientSnapshot right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ClientSnapshot left, ClientSnapshot right)
        {
            return !left.Equals(right);
        }

        public bool Equals(ClientSnapshot other)
        {
            return Connected == other.Connected
                && TransportReady == other.TransportReady
                && Authenticated == other.Authenticated
                && Role == other.Role
                && PlayerId == other.PlayerId
                && RoomId == other.RoomId
                && string.Equals(RoomCode, other.RoomCode, StringComparison.Ordinal)
                && string.Equals(
                    ReconnectionToken,
                    other.ReconnectionToken,
                    StringComparison.Ordinal
                )
                && IsAuthority == other.IsAuthority;
        }

        public override bool Equals(object obj)
        {
            return obj is ClientSnapshot other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + Connected.GetHashCode();
                hash = (hash * 31) + TransportReady.GetHashCode();
                hash = (hash * 31) + Authenticated.GetHashCode();
                hash = (hash * 31) + (Role?.GetHashCode() ?? 0);
                hash = (hash * 31) + (PlayerId?.GetHashCode() ?? 0);
                hash = (hash * 31) + (RoomId?.GetHashCode() ?? 0);
                hash = (hash * 31) + (RoomCode?.GetHashCode(StringComparison.Ordinal) ?? 0);
                hash =
                    (hash * 31) + (ReconnectionToken?.GetHashCode(StringComparison.Ordinal) ?? 0);
                hash = (hash * 31) + IsAuthority.GetHashCode();
                return hash;
            }
        }

        /// <summary>
        /// Log-safe form: the reconnection token is redacted to presence
        /// (<c>&lt;redacted&gt;</c>/<c>&lt;none&gt;</c>), never its value.
        /// </summary>
        public override string ToString()
        {
            return nameof(Connected)
                + "="
                + Connected
                + ", "
                + nameof(TransportReady)
                + "="
                + TransportReady
                + ", "
                + nameof(Authenticated)
                + "="
                + Authenticated
                + ", Role="
                + (Role?.ToString() ?? "<none>")
                + ", "
                + nameof(IsAuthority)
                + "="
                + IsAuthority
                + ", ReconnectionToken="
                + (ReconnectionToken is null ? "<none>" : "<redacted>");
        }
    }
}
