namespace SignalFish.Client.Protocol
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// One direct-connection endpoint as advertised inside a
    /// <c>GameStarting</c> peer connection. All fields are required.
    /// </summary>
    public readonly struct ConnectionEndpoint : IEquatable<ConnectionEndpoint>
    {
        /// <summary>Gets the endpoint scheme token, e.g. <c>direct</c> (required).</summary>
        public string Type { get; }

        /// <summary>Gets the host name or IP literal (required).</summary>
        public string Host { get; }

        /// <summary>Gets the port (required).</summary>
        public uint Port { get; }

        /// <summary>Initializes a new <see cref="ConnectionEndpoint"/> value.</summary>
        public ConnectionEndpoint(string type, string host, uint port)
        {
            Type = type;
            Host = host;
            Port = port;
        }

        /// <inheritdoc />
        public bool Equals(ConnectionEndpoint other) =>
            AuthenticateMessage.NullableStringEquals(Type, other.Type)
            && AuthenticateMessage.NullableStringEquals(Host, other.Host)
            && Port == other.Port;

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is ConnectionEndpoint other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(Type);
            hash.Add(Host);
            hash.Add(Port);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(ConnectionEndpoint left, ConnectionEndpoint right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(ConnectionEndpoint left, ConnectionEndpoint right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes one <c>ConnectionEndpoint</c> object (a sliced sub-object
        /// of a payload). Unknown fields are skipped; a repeated key or a
        /// wrong-typed value is rejected. Returns
        /// <see langword="false"/> for malformed input or a missing
        /// required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out ConnectionEndpoint endpoint)
        {
            endpoint = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            string? type = null;
            string? host = null;
            uint port = 0;
            bool portSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "type"))
                {
                    if (type is not null || !scanner.TryReadString(valueRaw, out type))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "host"))
                {
                    if (host is not null || !scanner.TryReadString(valueRaw, out host))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "port"))
                {
                    if (portSeen || !scanner.TryReadUInt32(valueRaw, out port))
                    {
                        return false;
                    }

                    portSeen = true;
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || type is null || host is null || !portSeen)
            {
                return false;
            }

            endpoint = new ConnectionEndpoint(type, host, port);
            return true;
        }
    }

    /// <summary>
    /// One peer's connection plan inside a <c>GameStarting</c> payload. All
    /// fields except <see cref="ConnectionInfo"/> are required;
    /// <see cref="ConnectionInfo"/> is optional (an absent key or an
    /// explicit JSON <c>null</c> decodes as <see langword="null"/>).
    /// </summary>
    public readonly struct PeerConnection : IEquatable<PeerConnection>
    {
        /// <summary>Gets the peer's player identity (required).</summary>
        public Guid PlayerId { get; }

        /// <summary>Gets the peer's display name (required).</summary>
        public string PlayerName { get; }

        /// <summary>Gets a value indicating whether the peer holds authority (required).</summary>
        public bool IsAuthority { get; }

        /// <summary>Gets the relay transport token (required).</summary>
        public string RelayType { get; }

        /// <summary>
        /// Gets the direct-connection endpoint the peer advertised;
        /// <see langword="null"/> when absent or an explicit JSON
        /// <c>null</c>.
        /// </summary>
        public ConnectionEndpoint? ConnectionInfo { get; }

        /// <summary>Initializes a new <see cref="PeerConnection"/> value.</summary>
        public PeerConnection(
            Guid playerId,
            string playerName,
            bool isAuthority,
            string relayType,
            ConnectionEndpoint? connectionInfo
        )
        {
            PlayerId = playerId;
            PlayerName = playerName;
            IsAuthority = isAuthority;
            RelayType = relayType;
            ConnectionInfo = connectionInfo;
        }

        /// <inheritdoc />
        public bool Equals(PeerConnection other) =>
            PlayerId == other.PlayerId
            && AuthenticateMessage.NullableStringEquals(PlayerName, other.PlayerName)
            && IsAuthority == other.IsAuthority
            && AuthenticateMessage.NullableStringEquals(RelayType, other.RelayType)
            && ConnectionInfo.Equals(other.ConnectionInfo);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is PeerConnection other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(PlayerId);
            hash.Add(PlayerName);
            hash.Add(IsAuthority);
            hash.Add(RelayType);
            hash.Add(ConnectionInfo);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(PeerConnection left, PeerConnection right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(PeerConnection left, PeerConnection right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes one <c>PeerConnection</c> object (a sliced sub-object of
        /// a payload). Unknown fields are skipped; a repeated key or a
        /// wrong-typed value is rejected. Returns
        /// <see langword="false"/> for malformed input or a missing
        /// required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out PeerConnection peer)
        {
            peer = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            Guid playerId = default;
            string? playerName = null;
            bool isAuthority = false;
            string? relayType = null;
            ConnectionEndpoint? connectionInfo = null;
            bool idSeen = false;
            bool authoritySeen = false;
            bool connectionInfoSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "player_id"))
                {
                    if (idSeen || !scanner.TryReadGuid(valueRaw, out playerId))
                    {
                        return false;
                    }

                    idSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "player_name"))
                {
                    if (playerName is not null || !scanner.TryReadString(valueRaw, out playerName))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "is_authority"))
                {
                    if (authoritySeen || !scanner.TryReadBoolean(valueRaw, out isAuthority))
                    {
                        return false;
                    }

                    authoritySeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "relay_type"))
                {
                    if (relayType is not null || !scanner.TryReadString(valueRaw, out relayType))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "connection_info"))
                {
                    /*
                        Optional: an explicit JSON null decodes as absent;
                        a present value must be an endpoint object.
                    */
                    if (connectionInfoSeen)
                    {
                        return false;
                    }

                    if (scanner.TryReadNull(valueRaw))
                    {
                        connectionInfo = null;
                    }
                    else
                    {
                        if (
                            !JsonScanner.TryReadObjectSlice(
                                data,
                                valueRaw,
                                out ReadOnlyMemory<byte> slice
                            )
                            || !ConnectionEndpoint.TryDecode(slice, out ConnectionEndpoint endpoint)
                        )
                        {
                            return false;
                        }

                        connectionInfo = endpoint;
                    }

                    connectionInfoSeen = true;
                }

                state = scanner.EndMember();
            }

            if (
                state != JsonMemberState.EndObject
                || !idSeen
                || playerName is null
                || !authoritySeen
                || relayType is null
            )
            {
                return false;
            }

            peer = new PeerConnection(playerId, playerName, isAuthority, relayType, connectionInfo);
            return true;
        }
    }

    /// <summary>
    /// Payload of the inbound <c>GameStarting</c> message (S→C): the
    /// per-peer connection plans for the starting game. The
    /// <c>peer_connections</c> list is required but may be empty. Wire
    /// order: <c>peer_connections</c>.
    /// </summary>
    public readonly struct GameStartingMessage : IEquatable<GameStartingMessage>
    {
        /// <summary>Gets one connection plan per peer (required; may be empty).</summary>
        public IReadOnlyList<PeerConnection> PeerConnections { get; }

        /// <summary>Initializes a new <see cref="GameStartingMessage"/> payload.</summary>
        public GameStartingMessage(IReadOnlyList<PeerConnection> peerConnections)
        {
            PeerConnections = peerConnections;
        }

        /// <inheritdoc />
        public bool Equals(GameStartingMessage other) =>
            SequenceEquals(PeerConnections, other.PeerConnections);

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is GameStartingMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(SequenceHashCode(PeerConnections));
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(GameStartingMessage left, GameStartingMessage right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(GameStartingMessage left, GameStartingMessage right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>GameStarting</c> envelope
        /// (the <see cref="EnvelopeEvent.Data"/> slice). Unknown fields are
        /// skipped; a repeated key or a wrong-typed value (including a
        /// wrong-typed array element) is rejected. Returns
        /// <see langword="false"/> for malformed input or a missing
        /// required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out GameStartingMessage message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            IReadOnlyList<PeerConnection>? peerConnections = null;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "peer_connections"))
                {
                    if (
                        peerConnections is not null
                        || !TryReadPeerConnections(
                            data,
                            valueRaw,
                            out IReadOnlyList<PeerConnection>? parsed
                        )
                        || parsed is null
                    )
                    {
                        return false;
                    }

                    peerConnections = parsed;
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || peerConnections is null)
            {
                return false;
            }

            message = new GameStartingMessage(peerConnections);
            return true;
        }

        /// <summary>
        /// Reads a scanned value that must be a JSON array of
        /// <c>PeerConnection</c> objects into a list (decode path; allocates
        /// the result).
        /// </summary>
        private static bool TryReadPeerConnections(
            ReadOnlyMemory<byte> data,
            Range valueRaw,
            out IReadOnlyList<PeerConnection>? values
        )
        {
            values = null;
            (int Offset, int Length) s = valueRaw.GetOffsetAndLength(data.Length);
            if (s.Length < 2 || data.Span[s.Offset] != (byte)'[')
            {
                return false;
            }

            JsonScanner scanner = new JsonScanner(data.Span.Slice(s.Offset, s.Length));
            scanner.SkipWhitespace();
            if (scanner.Expect((byte)'[') != default(DecodeError))
            {
                return false;
            }

            List<PeerConnection> list = new List<PeerConnection>();
            scanner.SkipWhitespace();
            if (scanner.Peek == (byte)']')
            {
                if (scanner.Expect((byte)']') != default(DecodeError))
                {
                    return false;
                }
            }
            else
            {
                while (true)
                {
                    scanner.SkipWhitespace();

                    /*
                        Elements validate at member-value depth, the same
                        level JsonScanner.ScanMember uses for payload values.
                    */
                    if (
                        scanner.ScanValueRaw(2, JsonScanner.MaxDepth, out Range element)
                        != default(DecodeError)
                    )
                    {
                        return false;
                    }

                    (int EOffset, int ELength) e = element.GetOffsetAndLength(s.Length);
                    if (
                        !PeerConnection.TryDecode(
                            data.Slice(s.Offset + e.EOffset, e.ELength),
                            out PeerConnection peer
                        )
                    )
                    {
                        return false;
                    }

                    list.Add(peer);
                    scanner.SkipWhitespace();
                    byte next = scanner.Peek;
                    if (next == (byte)',')
                    {
                        if (scanner.Expect((byte)',') != default(DecodeError))
                        {
                            return false;
                        }

                        continue;
                    }

                    if (next == (byte)']')
                    {
                        if (scanner.Expect((byte)']') != default(DecodeError))
                        {
                            return false;
                        }

                        break;
                    }

                    return false;
                }
            }

            scanner.SkipWhitespace();
            if (!scanner.IsEof)
            {
                return false;
            }

            values = list;
            return true;
        }

        private static bool SequenceEquals(
            IReadOnlyList<PeerConnection>? left,
            IReadOnlyList<PeerConnection>? right
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
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static int SequenceHashCode(IReadOnlyList<PeerConnection>? values)
        {
            if (values is null)
            {
                return 0;
            }

            HashCode hash = default;
            for (int i = 0; i < values.Count; i++)
            {
                hash.Add(values[i]);
            }

            return hash.ToHashCode();
        }
    }
}
