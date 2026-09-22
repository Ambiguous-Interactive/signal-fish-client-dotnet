namespace SignalFish.Client.Reconnection
{
    using System;
    using SignalFish.Client.Core;

    /// <summary>
    /// The persisted seat triple for manual reconnection: the player id
    /// and room id issued at join time plus the server-issued
    /// reconnection token — the <c>auth_token</c> a <c>Reconnect</c>
    /// command presents. The token is a bearer seat credential: anyone
    /// holding it can take over the seat for the reconnection window, so
    /// <see cref="ToString"/> redacts it and it must never reach logs.
    /// Capture it from <see cref="ClientSnapshot"/> right after every
    /// <c>RoomJoined</c>/<c>Reconnected</c> (the machine clears the token
    /// at teardown) and replace the persisted copy on every rotation.
    /// </summary>
    public readonly struct ReconnectContext : IEquatable<ReconnectContext>
    {
        /// <summary>Gets the player identity issued at join time.</summary>
        public Guid PlayerId { get; }

        /// <summary>Gets the room identity issued at join time.</summary>
        public Guid RoomId { get; }

        /// <summary>Gets the reconnection token (a seat credential; never log it).</summary>
        public string Token { get; }

        /// <summary>Initializes the triple; the token must be nonempty.</summary>
        public ReconnectContext(Guid playerId, Guid roomId, string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                throw new ArgumentException(
                    "The reconnection token must be nonempty.",
                    nameof(token)
                );
            }

            PlayerId = playerId;
            RoomId = roomId;
            Token = token;
        }

        /// <summary>
        /// Captures the seat triple from a snapshot. Succeeds only for a
        /// confirmed player baseline carrying a token — spectator
        /// baselines have none (spectators cannot reconnect) and
        /// post-teardown snapshots are already cleared, which is why the
        /// capture belongs right after every join or reconnect.
        /// </summary>
        public static bool TryCapture(ClientSnapshot snapshot, out ReconnectContext context)
        {
            context = default;
            if (
                snapshot.Role != RoomRole.Player
                || snapshot.PlayerId is null
                || snapshot.RoomId is null
                || string.IsNullOrEmpty(snapshot.ReconnectionToken)
            )
            {
                return false;
            }

            context = new ReconnectContext(
                snapshot.PlayerId.GetValueOrDefault(),
                snapshot.RoomId.GetValueOrDefault(),
                snapshot.ReconnectionToken!
            );
            return true;
        }

        /// <inheritdoc />
        public bool Equals(ReconnectContext other)
        {
            return PlayerId == other.PlayerId
                && RoomId == other.RoomId
                && string.Equals(Token, other.Token, StringComparison.Ordinal);
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is ReconnectContext other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + PlayerId.GetHashCode();
                hash = (hash * 31) + RoomId.GetHashCode();
                hash = (hash * 31) + Token.GetHashCode(StringComparison.Ordinal);
                return hash;
            }
        }

        /// <summary>
        /// Log-safe form: the token is redacted to presence, never its
        /// value (a bearer seat credential).
        /// </summary>
        public override string ToString()
        {
            return nameof(PlayerId)
                + "="
                + PlayerId
                + ", "
                + nameof(RoomId)
                + "="
                + RoomId
                + ", "
                + nameof(Token)
                + "="
                + (Token is null ? "<none>" : "<redacted>");
        }

        /// <summary>Equality by content.</summary>
        public static bool operator ==(ReconnectContext left, ReconnectContext right)
        {
            return left.Equals(right);
        }

        /// <summary>Inequality by content.</summary>
        public static bool operator !=(ReconnectContext left, ReconnectContext right)
        {
            return !left.Equals(right);
        }
    }
}
