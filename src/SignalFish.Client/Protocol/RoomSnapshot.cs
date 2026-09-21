namespace SignalFish.Client.Protocol
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The room-state snapshot carried by membership-confirming frames
    /// (<c>RoomJoined</c>, <c>SpectatorJoined</c>, <c>Reconnected</c>).
    /// Fields the frame omits stay at their default (empty lists, null
    /// optionals) — the server tailors the snapshot per audience. The
    /// snapshot is advisory room state: it is excluded from its message's
    /// equality, whose identity is the membership triple.
    /// </summary>
    public readonly struct RoomSnapshot : IEquatable<RoomSnapshot>
    {
        /// <summary>Gets the public game name the room was created with.</summary>
        public string? GameName { get; }

        /// <summary>Gets the seat limit (0 when the frame omits it).</summary>
        public uint MaxPlayers { get; }

        /// <summary>Gets a value indicating whether the room accepts authority requests.</summary>
        public bool SupportsAuthority { get; }

        /// <summary>Gets a value indicating whether this client is the current authority.</summary>
        public bool IsAuthority { get; }

        /// <summary>Gets the lobby phase token (e.g. <c>lobby</c>, <c>waiting</c>).</summary>
        public string? LobbyState { get; }

        /// <summary>Gets the relay transport type token (v3; null when absent).</summary>
        public string? RelayType { get; }

        /// <summary>Gets the ready player ids (null when the frame omits the list).</summary>
        public IReadOnlyList<string>? ReadyPlayers { get; }

        /// <summary>Gets the connected players (empty when the frame omits the roster).</summary>
        public IReadOnlyList<PlayerInfo> CurrentPlayers { get; }

        /// <summary>Gets the connected spectators (empty when the frame omits the roster).</summary>
        public IReadOnlyList<SpectatorInfo> CurrentSpectators { get; }

        /// <summary>Initializes a new room snapshot.</summary>
        public RoomSnapshot(
            string? gameName,
            uint maxPlayers,
            bool supportsAuthority,
            bool isAuthority,
            string? lobbyState,
            string? relayType,
            IReadOnlyList<string>? readyPlayers,
            IReadOnlyList<PlayerInfo> currentPlayers,
            IReadOnlyList<SpectatorInfo> currentSpectators
        )
        {
            GameName = gameName;
            MaxPlayers = maxPlayers;
            SupportsAuthority = supportsAuthority;
            IsAuthority = isAuthority;
            LobbyState = lobbyState;
            RelayType = relayType;
            ReadyPlayers = readyPlayers;
            CurrentPlayers = currentPlayers ?? Array.Empty<PlayerInfo>();
            CurrentSpectators = currentSpectators ?? Array.Empty<SpectatorInfo>();
        }

        /// <inheritdoc />
        public bool Equals(RoomSnapshot other) =>
            AuthenticateMessage.NullableStringEquals(GameName, other.GameName)
            && MaxPlayers == other.MaxPlayers
            && SupportsAuthority == other.SupportsAuthority
            && IsAuthority == other.IsAuthority
            && AuthenticateMessage.NullableStringEquals(LobbyState, other.LobbyState)
            && AuthenticateMessage.NullableStringEquals(RelayType, other.RelayType)
            && NullableStringListEquals(ReadyPlayers, other.ReadyPlayers)
            && PlayerInfo.SequenceEquals(CurrentPlayers, other.CurrentPlayers)
            && SpectatorInfo.SequenceEquals(CurrentSpectators, other.CurrentSpectators);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is RoomSnapshot other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(GameName);
            hash.Add(MaxPlayers);
            hash.Add(SupportsAuthority);
            hash.Add(IsAuthority);
            hash.Add(LobbyState);
            hash.Add(RelayType);
            hash.Add(ReadyPlayers?.Count ?? 0);
            hash.Add(CurrentPlayers.Count);
            hash.Add(CurrentSpectators.Count);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(RoomSnapshot left, RoomSnapshot right) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(RoomSnapshot left, RoomSnapshot right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the snapshot fields of a membership-confirming frame.
        /// Keys the frame omits leave their default; an explicit JSON null
        /// counts as omitted (the canonical wire form); a repeated known
        /// key is rejected (fail-closed, matching every payload decoder).
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out RoomSnapshot snapshot)
        {
            snapshot = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            string? gameName = null;
            uint maxPlayers = 0;
            bool supportsAuthority = false;
            bool isAuthority = false;
            string? lobbyState = null;
            string? relayType = null;
            IReadOnlyList<string>? readyPlayers = null;
            IReadOnlyList<PlayerInfo>? currentPlayers = null;
            IReadOnlyList<SpectatorInfo>? currentSpectators = null;
            bool gameSeen = false,
                maxSeen = false,
                supportsSeen = false,
                authoritySeen = false,
                lobbySeen = false,
                relaySeen = false,
                readySeen = false,
                playersSeen = false,
                spectatorsSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "game_name"))
                {
                    if (gameSeen)
                    {
                        return false;
                    }

                    gameSeen = true;
                    if (!scanner.TryReadNull(valueRaw))
                    {
                        if (!scanner.TryReadString(valueRaw, out gameName))
                        {
                            return false;
                        }
                    }
                }
                else if (scanner.KeyIs(keyRaw, "max_players"))
                {
                    if (maxSeen)
                    {
                        return false;
                    }

                    maxSeen = true;
                    if (!scanner.TryReadNull(valueRaw))
                    {
                        if (!scanner.TryReadUInt32(valueRaw, out maxPlayers))
                        {
                            return false;
                        }
                    }
                }
                else if (scanner.KeyIs(keyRaw, "supports_authority"))
                {
                    if (supportsSeen)
                    {
                        return false;
                    }

                    supportsSeen = true;
                    if (!scanner.TryReadNull(valueRaw))
                    {
                        if (!scanner.TryReadBoolean(valueRaw, out supportsAuthority))
                        {
                            return false;
                        }
                    }
                }
                else if (scanner.KeyIs(keyRaw, "is_authority"))
                {
                    if (authoritySeen)
                    {
                        return false;
                    }

                    authoritySeen = true;
                    if (!scanner.TryReadNull(valueRaw))
                    {
                        if (!scanner.TryReadBoolean(valueRaw, out isAuthority))
                        {
                            return false;
                        }
                    }
                }
                else if (scanner.KeyIs(keyRaw, "lobby_state"))
                {
                    if (lobbySeen)
                    {
                        return false;
                    }

                    lobbySeen = true;
                    if (!scanner.TryReadNull(valueRaw))
                    {
                        if (!scanner.TryReadString(valueRaw, out lobbyState))
                        {
                            return false;
                        }
                    }
                }
                else if (scanner.KeyIs(keyRaw, "relay_type"))
                {
                    if (relaySeen)
                    {
                        return false;
                    }

                    relaySeen = true;
                    if (!scanner.TryReadNull(valueRaw))
                    {
                        if (!scanner.TryReadString(valueRaw, out relayType))
                        {
                            return false;
                        }
                    }
                }
                else if (scanner.KeyIs(keyRaw, "ready_players"))
                {
                    if (readySeen)
                    {
                        return false;
                    }

                    readySeen = true;
                    if (!scanner.TryReadNull(valueRaw))
                    {
                        if (!JsonScanner.TryReadStringArray(data, valueRaw, out readyPlayers))
                        {
                            return false;
                        }
                    }
                }
                else if (scanner.KeyIs(keyRaw, "current_players"))
                {
                    if (playersSeen)
                    {
                        return false;
                    }

                    playersSeen = true;
                    if (!scanner.TryReadNull(valueRaw))
                    {
                        if (!PlayerInfo.TryReadArray(data, valueRaw, out currentPlayers))
                        {
                            return false;
                        }
                    }
                }
                else if (scanner.KeyIs(keyRaw, "current_spectators"))
                {
                    if (spectatorsSeen)
                    {
                        return false;
                    }

                    spectatorsSeen = true;
                    if (!scanner.TryReadNull(valueRaw))
                    {
                        if (!SpectatorInfo.TryReadArray(data, valueRaw, out currentSpectators))
                        {
                            return false;
                        }
                    }
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject)
            {
                return false;
            }

            snapshot = new RoomSnapshot(
                gameName,
                maxPlayers,
                supportsAuthority,
                isAuthority,
                lobbyState,
                relayType,
                readyPlayers,
                currentPlayers ?? Array.Empty<PlayerInfo>(),
                currentSpectators ?? Array.Empty<SpectatorInfo>()
            );
            return true;
        }

        private static bool NullableStringListEquals(
            IReadOnlyList<string>? left,
            IReadOnlyList<string>? right
        )
        {
            if (left is null || right is null)
            {
                return left is null && right is null;
            }

            if (left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
