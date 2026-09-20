namespace SignalFish.Client.Core
{
    using System;

    /// <summary>A single state-machine input. Struct: applying events never allocates.</summary>
    public readonly struct SessionEvent : IEquatable<SessionEvent>
    {
        private SessionEvent(SessionEventKind kind, Guid playerId, RoomMembership membership)
        {
            this.Kind = kind;
            this.PlayerId = playerId;
            this.Membership = membership;
        }

        /// <summary>The session fact that happened.</summary>
        public SessionEventKind Kind { get; }

        /// <summary>Assigned player id; meaningful only for <see cref="SessionEventKind.Authenticated"/>.</summary>
        public Guid PlayerId { get; }

        /// <summary>Confirmed membership; meaningful only for join/reconnect events.</summary>
        public RoomMembership Membership { get; }

        /// <summary>Creates an event with no payload (liveness, failures, teardown).</summary>
        public static SessionEvent From(SessionEventKind kind)
        {
            return new SessionEvent(kind, Guid.Empty, default);
        }

        /// <summary>Creates a server-authenticated event carrying the player id.</summary>
        public static SessionEvent Authenticated(Guid playerId)
        {
            return new SessionEvent(SessionEventKind.Authenticated, playerId, default);
        }

        /// <summary>Creates a membership-confirming event (join/reconnect kinds).</summary>
        public static SessionEvent Joined(SessionEventKind kind, RoomMembership membership)
        {
            return new SessionEvent(kind, Guid.Empty, membership);
        }

        public static bool operator ==(SessionEvent left, SessionEvent right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(SessionEvent left, SessionEvent right)
        {
            return !left.Equals(right);
        }

        public bool Equals(SessionEvent other)
        {
            return this.Kind == other.Kind
                && this.PlayerId == other.PlayerId
                && this.Membership == other.Membership;
        }

        public override bool Equals(object obj)
        {
            return obj is SessionEvent other && this.Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + (int)this.Kind;
                hash = (hash * 31) + this.PlayerId.GetHashCode();
                hash = (hash * 31) + this.Membership.GetHashCode();
                return hash;
            }
        }
    }
}
