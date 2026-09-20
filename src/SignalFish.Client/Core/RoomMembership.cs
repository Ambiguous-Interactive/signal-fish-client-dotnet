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
        public RoomMembership(RoomRole role, Guid playerId, Guid roomId, string? roomCode)
        {
            this.Role = role;
            this.PlayerId = playerId;
            this.RoomId = roomId;
            this.RoomCode = roomCode;
        }

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
            get { return this.Role != RoomRole.None; }
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
            return this.Role == other.Role
                && this.PlayerId == other.PlayerId
                && this.RoomId == other.RoomId
                && string.Equals(this.RoomCode, other.RoomCode, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is RoomMembership other && this.Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + (int)this.Role;
                hash = (hash * 31) + this.PlayerId.GetHashCode();
                hash = (hash * 31) + this.RoomId.GetHashCode();
                hash = (hash * 31) + (this.RoomCode?.GetHashCode(StringComparison.Ordinal) ?? 0);
                return hash;
            }
        }
    }
}
