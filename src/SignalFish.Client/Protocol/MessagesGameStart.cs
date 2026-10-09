namespace SignalFish.Client.Protocol
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    /// One peer-advertised connection plan as carried inside a
    /// <c>GameStarting</c> peer connection (and, historically, a
    /// <c>direct</c> endpoint). The wire value is a five-variant tagged
    /// union — <c>direct</c>, <c>unity_relay</c>, <c>relay</c>,
    /// <c>webrtc</c>, <c>custom</c> — that the server stores and forwards
    /// verbatim without authenticating; the consumer owns reachability and
    /// credential validation. Only the fields a variant carries are
    /// populated; everything else stays <see langword="null"/> (and
    /// <see cref="Host"/>/<see cref="Port"/> keep their defaults when the
    /// variant has no endpoint).
    /// </summary>
    public readonly struct ConnectionEndpoint : IEquatable<ConnectionEndpoint>
    {
        /// <summary>Gets the scheme token: <c>direct</c>, <c>unity_relay</c>, <c>relay</c>, <c>webrtc</c>, or <c>custom</c> (required).</summary>
        public string Type { get; }

        /// <summary>
        /// Gets the self-declared host name or IP literal;
        /// <see cref="string.Empty"/> when the variant carries no host.
        /// </summary>
        public string Host { get; }

        /// <summary>Gets the self-declared port; <c>0</c> when the variant carries no port.</summary>
        public uint Port { get; }

        /// <summary>Gets the relay or allocation label (<c>unity_relay</c>, <c>relay</c>).</summary>
        public string? AllocationId { get; }

        /// <summary>Gets the Unity Relay connection data (<c>unity_relay</c>).</summary>
        public string? ConnectionData { get; }

        /// <summary>Gets the Unity Relay key (<c>unity_relay</c>).</summary>
        public string? Key { get; }

        /// <summary>Gets the self-declared relay credential (<c>relay</c>).</summary>
        public string? Token { get; }

        /// <summary>Gets the optional transport label (<c>relay</c>).</summary>
        public string? Transport { get; }

        /// <summary>Gets the optional client label (<c>relay</c>).</summary>
        public uint? ClientId { get; }

        /// <summary>Gets the optional session description (<c>webrtc</c>).</summary>
        public string? Sdp { get; }

        /// <summary>Gets the ICE candidate URLs (<c>webrtc</c>; may be empty).</summary>
        public IReadOnlyList<string>? IceCandidates { get; }

        /// <summary>Gets the raw JSON text of the custom payload (<c>custom</c>).</summary>
        public string? Data { get; }

        /// <summary>Initializes a new direct-style <see cref="ConnectionEndpoint"/> value.</summary>
        public ConnectionEndpoint(string type, string host, uint port)
        {
            Type = type;
            Host = host;
            Port = port;
            AllocationId = null;
            ConnectionData = null;
            Key = null;
            Token = null;
            Transport = null;
            ClientId = null;
            Sdp = null;
            IceCandidates = null;
            Data = null;
        }

        /// <summary>Initializes a new <see cref="ConnectionEndpoint"/> value with every variant field.</summary>
        public ConnectionEndpoint(
            string type,
            string host,
            uint port,
            string? allocationId,
            string? connectionData,
            string? key,
            string? token,
            string? transport,
            uint? clientId,
            string? sdp,
            IReadOnlyList<string>? iceCandidates,
            string? data
        )
        {
            Type = type;
            Host = host;
            Port = port;
            AllocationId = allocationId;
            ConnectionData = connectionData;
            Key = key;
            Token = token;
            Transport = transport;
            ClientId = clientId;
            Sdp = sdp;
            IceCandidates = iceCandidates;
            Data = data;
        }

        /// <inheritdoc />
        public bool Equals(ConnectionEndpoint other) =>
            AuthenticateMessage.NullableStringEquals(Type, other.Type)
            && AuthenticateMessage.NullableStringEquals(Host, other.Host)
            && Port == other.Port
            && AuthenticateMessage.NullableStringEquals(AllocationId, other.AllocationId)
            && AuthenticateMessage.NullableStringEquals(ConnectionData, other.ConnectionData)
            && AuthenticateMessage.NullableStringEquals(Key, other.Key)
            && AuthenticateMessage.NullableStringEquals(Token, other.Token)
            && AuthenticateMessage.NullableStringEquals(Transport, other.Transport)
            && ClientId == other.ClientId
            && AuthenticateMessage.NullableStringEquals(Sdp, other.Sdp)
            && SequenceEquals(IceCandidates, other.IceCandidates)
            && AuthenticateMessage.NullableStringEquals(Data, other.Data);

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
            hash.Add(AllocationId);
            hash.Add(ConnectionData);
            hash.Add(Key);
            hash.Add(Token);
            hash.Add(Transport);
            hash.Add(ClientId);
            hash.Add(Sdp);
            hash.Add(SequenceHashCode(IceCandidates));
            hash.Add(Data);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(ConnectionEndpoint left, ConnectionEndpoint right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(ConnectionEndpoint left, ConnectionEndpoint right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes one <c>connection_info</c> tagged union (a sliced
        /// sub-object of a payload). Unknown fields are skipped; a repeated
        /// key or a wrong-typed value is rejected. A variant missing one of
        /// its required fields, or an unknown type token, is rejected —
        /// matching the Rust client's closed enum. Returns
        /// <see langword="false"/> for malformed input.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out ConnectionEndpoint endpoint)
        {
            endpoint = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            string? type = null;
            string? host = null;
            uint port = 0;
            string? allocationId = null;
            string? connectionData = null;
            string? key = null;
            string? token = null;
            string? transport = null;
            uint? clientId = null;
            string? sdp = null;
            IReadOnlyList<string>? iceCandidates = null;
            string? customData = null;
            bool typeSeen = false;
            bool hostSeen = false;
            bool portSeen = false;
            bool allocationIdSeen = false;
            bool connectionDataSeen = false;
            bool keySeen = false;
            bool tokenSeen = false;
            bool transportSeen = false;
            bool clientIdSeen = false;
            bool sdpSeen = false;
            bool iceCandidatesSeen = false;
            bool customDataSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "type"))
                {
                    if (typeSeen || !scanner.TryReadString(valueRaw, out type))
                    {
                        return false;
                    }

                    typeSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "host"))
                {
                    if (hostSeen)
                    {
                        return false;
                    }

                    hostSeen = true;
                    if (scanner.TryReadNull(valueRaw))
                    {
                        host = null;
                    }
                    else if (!scanner.TryReadString(valueRaw, out host))
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
                else if (scanner.KeyIs(keyRaw, "allocation_id"))
                {
                    if (allocationIdSeen)
                    {
                        return false;
                    }

                    allocationIdSeen = true;
                    if (
                        !scanner.TryReadNull(valueRaw)
                        && !scanner.TryReadString(valueRaw, out allocationId)
                    )
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "connection_data"))
                {
                    if (connectionDataSeen || !scanner.TryReadString(valueRaw, out connectionData))
                    {
                        return false;
                    }

                    connectionDataSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "key"))
                {
                    if (keySeen || !scanner.TryReadString(valueRaw, out key))
                    {
                        return false;
                    }

                    keySeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "token"))
                {
                    if (tokenSeen || !scanner.TryReadString(valueRaw, out token))
                    {
                        return false;
                    }

                    tokenSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "transport"))
                {
                    if (transportSeen)
                    {
                        return false;
                    }

                    transportSeen = true;
                    if (
                        !scanner.TryReadNull(valueRaw)
                        && !scanner.TryReadString(valueRaw, out transport)
                    )
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "client_id"))
                {
                    if (clientIdSeen)
                    {
                        return false;
                    }

                    clientIdSeen = true;
                    if (scanner.TryReadNull(valueRaw))
                    {
                        clientId = null;
                    }
                    else if (!scanner.TryReadUInt32(valueRaw, out uint clientIdValue))
                    {
                        return false;
                    }
                    else
                    {
                        clientId = clientIdValue;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "sdp"))
                {
                    if (sdpSeen)
                    {
                        return false;
                    }

                    sdpSeen = true;
                    if (scanner.TryReadNull(valueRaw))
                    {
                        sdp = null;
                    }
                    else if (!scanner.TryReadString(valueRaw, out sdp))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "ice_candidates"))
                {
                    if (
                        iceCandidatesSeen
                        || !JsonScanner.TryReadStringArray(
                            data,
                            valueRaw,
                            out IReadOnlyList<string>? candidates
                        )
                        || candidates is null
                    )
                    {
                        return false;
                    }

                    iceCandidates = candidates;
                    iceCandidatesSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "data"))
                {
                    if (customDataSeen)
                    {
                        return false;
                    }

                    customDataSeen = true;
                    customData = Encoding.UTF8.GetString(data.Span[valueRaw]);
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || !typeSeen)
            {
                return false;
            }

            bool valid = type switch
            {
                "direct" => hostSeen && host is not null && portSeen,
                "unity_relay" => allocationIdSeen
                    && allocationId is not null
                    && connectionDataSeen
                    && keySeen,
                "relay" => hostSeen
                    && host is not null
                    && portSeen
                    && allocationIdSeen
                    && allocationId is not null
                    && tokenSeen,
                "webrtc" => iceCandidatesSeen,
                "custom" => customDataSeen,
                _ => false,
            };

            if (!valid)
            {
                return false;
            }

            endpoint = new ConnectionEndpoint(
                type!,
                host ?? string.Empty,
                port,
                allocationId,
                connectionData,
                key,
                token,
                transport,
                clientId,
                sdp,
                iceCandidates,
                customData
            );
            return true;
        }

        private static bool SequenceEquals(
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

        private static int SequenceHashCode(IReadOnlyList<string>? values)
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
