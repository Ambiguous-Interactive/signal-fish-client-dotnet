#nullable enable
namespace SignalFish.Client.Adapters.Ngo
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The room membership an NGO connection is approved against: seeded
    /// from a room-joined snapshot, kept current by the room's
    /// player-joined/left/reconnected events, and consulted synchronously
    /// by the engine's connection-approval callback. All members are
    /// thread-safe — the coordinator's bootstrap waits run off the
    /// engine main thread while the approval callback and diagnostics
    /// read from it.
    /// </summary>
    public sealed class SignalFishRoomRoster
    {
        /// <summary>Gets how many players are currently members.</summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _players.Count;
                }
            }
        }

        private readonly object _lock = new object();

        private readonly HashSet<Guid> _players = new HashSet<Guid>();

        /// <summary>
        /// Replaces membership with the snapshot's player ids; the local
        /// player is a member of its own snapshot. Every id must be
        /// non-empty.
        /// </summary>
        public void Seed(IEnumerable<Guid> playerIds)
        {
            HashSet<Guid> seeded = new HashSet<Guid>();
            foreach (
                Guid playerId in playerIds ?? throw new ArgumentNullException(nameof(playerIds))
            )
            {
                if (playerId == Guid.Empty)
                {
                    throw new ArgumentException(
                        "The player id list cannot contain Guid.Empty.",
                        nameof(playerIds)
                    );
                }

                seeded.Add(playerId);
            }

            lock (_lock)
            {
                _players.Clear();
                foreach (Guid playerId in seeded)
                {
                    _players.Add(playerId);
                }
            }
        }

        /// <summary>
        /// Adds a member (a player-joined or player-reconnected event);
        /// <see langword="false"/> when the id is empty or already a
        /// member.
        /// </summary>
        public bool Add(Guid playerId)
        {
            if (playerId == Guid.Empty)
            {
                return false;
            }

            lock (_lock)
            {
                return _players.Add(playerId);
            }
        }

        /// <summary>
        /// Removes a member (a player-left event);
        /// <see langword="false"/> when the id was not a member.
        /// </summary>
        public bool Remove(Guid playerId)
        {
            lock (_lock)
            {
                return _players.Remove(playerId);
            }
        }

        /// <summary>Gets whether the id is a current member.</summary>
        public bool IsMember(Guid playerId)
        {
            lock (_lock)
            {
                return _players.Contains(playerId);
            }
        }

        /// <summary>Forgets every member (session teardown).</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _players.Clear();
            }
        }
    }
}
