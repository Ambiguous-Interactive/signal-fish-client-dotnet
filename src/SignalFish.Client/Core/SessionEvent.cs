namespace SignalFish.Client.Core
{
    using System;

    /// <summary>A single state-machine input. Struct: applying events never allocates.</summary>
    public readonly struct SessionEvent : IEquatable<SessionEvent>
    {
        /// <summary>The session fact that happened.</summary>
        public SessionEventKind Kind { get; }

        /// <summary>Confirmed membership; meaningful only for join/reconnect events.</summary>
        public RoomMembership Membership { get; }

        /// <summary>
        /// Reconnection token the baseline frame carried (null for
        /// spectator baselines and frames without one).
        /// </summary>
        public string? ReconnectionToken { get; }

        /// <summary>
        /// Whether this connection holds the room authority: the baseline's
        /// <c>is_authority</c> for join/reconnect kinds, the broadcast's
        /// <c>you_are_authority</c> for <see cref="SessionEventKind.AuthorityChanged"/>.
        /// </summary>
        public bool IsAuthority { get; }

        private SessionEvent(
            SessionEventKind kind,
            RoomMembership membership,
            string? reconnectionToken,
            bool isAuthority
        )
        {
            Kind = kind;
            Membership = membership;
            ReconnectionToken = reconnectionToken;
            IsAuthority = isAuthority;
        }

        /// <summary>Creates an event with no payload (liveness, failures, teardown).</summary>
        public static SessionEvent From(SessionEventKind kind)
        {
            return new SessionEvent(kind, default, null, false);
        }

        /// <summary>Creates the server-authenticated event (payload-less on the v2 wire).</summary>
        public static SessionEvent Authenticated()
        {
            return new SessionEvent(SessionEventKind.Authenticated, default, null, false);
        }

        /// <summary>Creates a membership-confirming event (join/reconnect kinds).</summary>
        public static SessionEvent Joined(
            SessionEventKind kind,
            RoomMembership membership,
            string? reconnectionToken = null,
            bool isAuthority = false
        )
        {
            return new SessionEvent(kind, membership, reconnectionToken, isAuthority);
        }

        /// <summary>Creates the authority-moved event.</summary>
        public static SessionEvent AuthorityChanged(bool youAreAuthority)
        {
            return new SessionEvent(
                SessionEventKind.AuthorityChanged,
                default,
                null,
                youAreAuthority
            );
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
            return Kind == other.Kind
                && Membership == other.Membership
                && IsAuthority == other.IsAuthority
                && string.Equals(
                    ReconnectionToken,
                    other.ReconnectionToken,
                    StringComparison.Ordinal
                );
        }

        public override bool Equals(object obj)
        {
            return obj is SessionEvent other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + (int)Kind;
                hash = (hash * 31) + Membership.GetHashCode();
                hash = (hash * 31) + IsAuthority.GetHashCode();
                hash =
                    (hash * 31) + (ReconnectionToken?.GetHashCode(StringComparison.Ordinal) ?? 0);
                return hash;
            }
        }
    }
}
