namespace SignalFish.Client.Core
{
    using System;

    /// <summary>
    /// The four-field membership invariant mirrored from the Rust client:
    /// role, player, room, and room code are set together by a confirmed
    /// join and cleared together by a confirmed exit — never partially.
    /// <c>default</c> is the absent membership (role 0 is not a valid role).
    /// </summary>
    public readonly struct RoomMembership : IEquatable<RoomMembership>
    {
        /// <summary>Confirmed role. Zero only in the absent membership.</summary>
        public RoomRole Role { get; }

        /// <summary>This client's player or spectator id.</summary>
        public Guid PlayerId { get; }

        /// <summary>Server-assigned room id.</summary>
        public Guid RoomId { get; }

        /// <summary>Human-shareable room code; null in the absent membership.</summary>
        public string? RoomCode { get; }

        /// <summary>True when a confirmed membership is present.</summary>
        public bool IsPresent
        {
            get { return Role is RoomRole.Player or RoomRole.Spectator; }
        }

        public RoomMembership(RoomRole role, Guid playerId, Guid roomId, string? roomCode)
        {
            Role = role;
            PlayerId = playerId;
            RoomId = roomId;
            RoomCode = roomCode;
        }

        public static bool operator ==(RoomMembership left, RoomMembership right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(RoomMembership left, RoomMembership right)
        {
            return !left.Equals(right);
        }

        public bool Equals(RoomMembership other)
        {
            return Role == other.Role
                && PlayerId == other.PlayerId
                && RoomId == other.RoomId
                && string.Equals(RoomCode, other.RoomCode, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is RoomMembership other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + (int)Role;
                hash = (hash * 31) + PlayerId.GetHashCode();
                hash = (hash * 31) + RoomId.GetHashCode();
                hash = (hash * 31) + (RoomCode?.GetHashCode(StringComparison.Ordinal) ?? 0);
                return hash;
            }
        }
    }
}
