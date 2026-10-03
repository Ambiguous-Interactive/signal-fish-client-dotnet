namespace SignalFish.Client.Protocol
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The session topology a v3 <c>SessionPlan</c> selects (S→C).
    /// The server's plan is authoritative: the client obeys it and never
    /// recomputes a topology itself.
    /// </summary>
    public enum SessionTopology : byte
    {
        /// <summary>Sentinel for <c>default(SessionTopology)</c>; not a topology.</summary>
        [Obsolete(
            "This value only exists so the enum default (0) is not a topology. Compare against default(SessionTopology) instead."
        )]
        None = 0,

        /// <summary>Server relay hub — the v2 behavior, always available (the relay floor).</summary>
        Relay = 1,

        /// <summary>Star topology around a single elected host.</summary>
        Host = 2,

        /// <summary>Full mesh: every peer connects to every other peer.</summary>
        Mesh = 3,
    }

    /// <summary>
    /// The data-path transport a v3 <c>SessionPlan</c> selects (S→C). A wire
    /// value describing how peers exchange game data — distinct from the
    /// <c>ITransport</c> byte channel to the signaling server.
    /// </summary>
    public enum SessionTransport : byte
    {
        /// <summary>Sentinel for <c>default(SessionTransport)</c>; not a transport.</summary>
        [Obsolete(
            "This value only exists so the enum default (0) is not a transport. Compare against default(SessionTransport) instead."
        )]
        None = 0,

        /// <summary>Server WebSocket fan-out — the mandatory relay floor.</summary>
        Relay = 1,

        /// <summary>Direct IP:port connection (LAN / routable host).</summary>
        Direct = 2,

        /// <summary>Peer-to-peer WebRTC data channel.</summary>
        WebRtc = 3,
    }

    /// <summary>
    /// One STUN/TURN server from a v3 <c>SessionPlan</c> (or the
    /// <c>RoomJoined</c>/<c>Reconnected</c> ICE pre-gather).
    /// <see cref="Username"/>/<see cref="Credential"/> are present only for
    /// TURN entries; bare STUN servers omit them.
    /// </summary>
    public readonly struct IceServerInfo : IEquatable<IceServerInfo>
    {
        /// <summary>Gets the STUN/TURN URLs (e.g. <c>stun:stun.l.google.com:19302</c>).</summary>
        public IReadOnlyList<string> Urls { get; }

        /// <summary>Gets the TURN username; <see langword="null"/> for credential-less servers.</summary>
        public string? Username { get; }

        /// <summary>Gets the TURN credential; <see langword="null"/> for credential-less servers.</summary>
        public string? Credential { get; }

        /// <summary>Initializes a new <see cref="IceServerInfo"/> value.</summary>
        public IceServerInfo(IReadOnlyList<string> urls, string? username, string? credential)
        {
            Urls = urls;
            Username = username;
            Credential = credential;
        }

        /// <inheritdoc />
        public bool Equals(IceServerInfo other) =>
            AuthenticateMessage.SequenceEquals(Urls, other.Urls)
            && AuthenticateMessage.NullableStringEquals(Username, other.Username)
            && AuthenticateMessage.NullableStringEquals(Credential, other.Credential);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is IceServerInfo other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            if (Urls is not null)
            {
                for (int i = 0; i < Urls.Count; i++)
                {
                    hash.Add(Urls[i]);
                }
            }

            hash.Add(Username);
            hash.Add(Credential);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(IceServerInfo left, IceServerInfo right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(IceServerInfo left, IceServerInfo right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes one <c>ice_servers</c> entry (a sliced sub-object of a
        /// payload). Unknown fields are skipped; a repeated key or a
        /// wrong-typed value is rejected. Returns
        /// <see langword="false"/> for malformed input or a missing
        /// required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out IceServerInfo server)
        {
            server = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            IReadOnlyList<string>? urls = null;
            string? username = null;
            string? credential = null;
            bool usernameSeen = false;
            bool credentialSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "urls"))
                {
                    if (
                        urls is not null
                        || !JsonScanner.TryReadStringArray(data, valueRaw, out urls)
                    )
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "username"))
                {
                    if (usernameSeen)
                    {
                        return false;
                    }

                    usernameSeen = true;
                    if (scanner.TryReadNull(valueRaw))
                    {
                        username = null;
                    }
                    else if (!scanner.TryReadString(valueRaw, out username))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "credential"))
                {
                    if (credentialSeen)
                    {
                        return false;
                    }

                    credentialSeen = true;
                    if (scanner.TryReadNull(valueRaw))
                    {
                        credential = null;
                    }
                    else if (!scanner.TryReadString(valueRaw, out credential))
                    {
                        return false;
                    }
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || urls is null)
            {
                return false;
            }

            server = new IceServerInfo(urls, username, credential);
            return true;
        }
    }

    /// <summary>
    /// A syntactically validated direct endpoint a v3 <c>SessionPlan</c>
    /// repeats for a <c>host</c> + <c>direct</c> session (projected from the
    /// elected host's self-declared connection info). Not proof of
    /// reachability: the relay fallback stays mandatory.
    /// </summary>
    public readonly struct DirectEndpointInfo : IEquatable<DirectEndpointInfo>
    {
        /// <summary>Gets the host name or IP literal to connect to.</summary>
        public string Host { get; }

        /// <summary>Gets the transport port.</summary>
        public uint Port { get; }

        /// <summary>Initializes a new <see cref="DirectEndpointInfo"/> value.</summary>
        public DirectEndpointInfo(string host, uint port)
        {
            Host = host;
            Port = port;
        }

        /// <inheritdoc />
        public bool Equals(DirectEndpointInfo other) =>
            AuthenticateMessage.NullableStringEquals(Host, other.Host) && Port == other.Port;

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is DirectEndpointInfo other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(Host);
            hash.Add(Port);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(DirectEndpointInfo left, DirectEndpointInfo right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(DirectEndpointInfo left, DirectEndpointInfo right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes one <c>direct_endpoint</c> object (a sliced sub-object of
        /// a payload). Unknown fields are skipped; a repeated key or a
        /// wrong-typed value is rejected. Returns
        /// <see langword="false"/> for malformed input or a missing
        /// required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out DirectEndpointInfo endpoint)
        {
            endpoint = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

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

                if (scanner.KeyIs(keyRaw, "host"))
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

            if (state != JsonMemberState.EndObject || host is null || !portSeen)
            {
                return false;
            }

            endpoint = new DirectEndpointInfo(host, port);
            return true;
        }
    }

    /// <summary>
    /// One peer a v3 <c>SessionPlan</c> tells the recipient to connect to.
    /// The plan always excludes the recipient itself, and every
    /// <see cref="Initiate"/> flag is the server's per-recipient glare
    /// decision: obey it verbatim — the client never computes who offers.
    /// </summary>
    public readonly struct SessionPeerInfo : IEquatable<SessionPeerInfo>
    {
        /// <summary>Gets the peer's player identity (required).</summary>
        public Guid PlayerId { get; }

        /// <summary>Gets the peer's display name (required).</summary>
        public string PlayerName { get; }

        /// <summary>Gets a value indicating whether this peer is the session's authority/host (required).</summary>
        public bool IsAuthority { get; }

        /// <summary>Gets a value indicating whether the recipient sends the offer to this peer (required).</summary>
        public bool Initiate { get; }

        /// <summary>Initializes a new <see cref="SessionPeerInfo"/> value.</summary>
        public SessionPeerInfo(Guid playerId, string playerName, bool isAuthority, bool initiate)
        {
            PlayerId = playerId;
            PlayerName = playerName;
            IsAuthority = isAuthority;
            Initiate = initiate;
        }

        /// <inheritdoc />
        public bool Equals(SessionPeerInfo other) =>
            PlayerId == other.PlayerId
            && AuthenticateMessage.NullableStringEquals(PlayerName, other.PlayerName)
            && IsAuthority == other.IsAuthority
            && Initiate == other.Initiate;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is SessionPeerInfo other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(PlayerId);
            hash.Add(PlayerName);
            hash.Add(IsAuthority);
            hash.Add(Initiate);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(SessionPeerInfo left, SessionPeerInfo right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(SessionPeerInfo left, SessionPeerInfo right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes one <c>peers</c> entry (a sliced sub-object of a
        /// payload). Unknown fields are skipped; a repeated key or a
        /// wrong-typed value is rejected. Returns
        /// <see langword="false"/> for malformed input or a missing
        /// required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out SessionPeerInfo peer)
        {
            peer = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            Guid playerId = default;
            string? playerName = null;
            bool isAuthority = false;
            bool initiate = false;
            bool idSeen = false;
            bool authoritySeen = false;
            bool initiateSeen = false;

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
                else if (scanner.KeyIs(keyRaw, "initiate"))
                {
                    if (initiateSeen || !scanner.TryReadBoolean(valueRaw, out initiate))
                    {
                        return false;
                    }

                    initiateSeen = true;
                }

                state = scanner.EndMember();
            }

            if (
                state != JsonMemberState.EndObject
                || !idSeen
                || playerName is null
                || !authoritySeen
                || !initiateSeen
            )
            {
                return false;
            }

            peer = new SessionPeerInfo(playerId, playerName, isAuthority, initiate);
            return true;
        }
    }

    /// <summary>
    /// Payload of the inbound v3 <c>SessionPlan</c> message (S→C): the
    /// per-recipient authoritative session directive, first emitted at game
    /// start and re-issued on late joins, host re-election, and relay
    /// resets. The client contract is <b>the latest plan wins</b>: a plan
    /// fully replaces the peer set and ICE list, a changed generation is a
    /// connection-attempt barrier, and every <see cref="SessionPeerInfo.Initiate"/>
    /// flag is obeyed verbatim.
    /// </summary>
    public readonly struct SessionPlanMessage : IEquatable<SessionPlanMessage>
    {
        /// <summary>
        /// Gets the plan publication's shared generation UUID;
        /// <see langword="null"/> for the legacy Server 0.4 v3 shape (the
        /// key is absent — the client cannot fence signals without it, so
        /// the generation send-fence stands down).
        /// </summary>
        public string? Generation { get; }

        /// <summary>Gets the selected topology.</summary>
        public SessionTopology Topology { get; }

        /// <summary>Gets the selected data-path transport.</summary>
        public SessionTransport Transport { get; }

        /// <summary>
        /// Gets the elected host's player id (host topology only);
        /// <see langword="null"/> otherwise.
        /// </summary>
        public Guid? Host { get; }

        /// <summary>
        /// Gets the validated connect target for a host + direct plan;
        /// <see langword="null"/> for every other plan.
        /// </summary>
        public DirectEndpointInfo? DirectEndpoint { get; }

        /// <summary>Gets the peers this recipient should connect to (required; may be empty).</summary>
        public IReadOnlyList<SessionPeerInfo> Peers { get; }

        /// <summary>
        /// Gets the STUN/TURN servers for WebRTC; empty for non-WebRTC plans
        /// (the key is absent from those frames).
        /// </summary>
        public IReadOnlyList<IceServerInfo> IceServers { get; }

        /// <summary>Gets the universal fallback transport (the relay floor).</summary>
        public SessionTransport Fallback { get; }

        /// <summary>Initializes a new <see cref="SessionPlanMessage"/> payload.</summary>
        public SessionPlanMessage(
            string? generation,
            SessionTopology topology,
            SessionTransport transport,
            Guid? host,
            DirectEndpointInfo? directEndpoint,
            IReadOnlyList<SessionPeerInfo> peers,
            IReadOnlyList<IceServerInfo> iceServers,
            SessionTransport fallback
        )
        {
            Generation = generation;
            Topology = topology;
            Transport = transport;
            Host = host;
            DirectEndpoint = directEndpoint;
            Peers = peers;
            IceServers = iceServers;
            Fallback = fallback;
        }

        /// <inheritdoc />
        public bool Equals(SessionPlanMessage other) =>
            AuthenticateMessage.NullableStringEquals(Generation, other.Generation)
            && Topology == other.Topology
            && Transport == other.Transport
            && Host.Equals(other.Host)
            && DirectEndpoint.Equals(other.DirectEndpoint)
            && SequenceEquals(Peers, other.Peers)
            && SequenceEquals(IceServers, other.IceServers)
            && Fallback == other.Fallback;

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is SessionPlanMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(Generation);
            hash.Add(Topology);
            hash.Add(Transport);
            hash.Add(Host);
            hash.Add(DirectEndpoint);
            hash.Add(SequenceHashCode(Peers));
            hash.Add(SequenceHashCode(IceServers));
            hash.Add(Fallback);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(SessionPlanMessage left, SessionPlanMessage right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(SessionPlanMessage left, SessionPlanMessage right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>SessionPlan</c> envelope
        /// (the <see cref="EnvelopeEvent.Data"/> slice). Unknown fields are
        /// skipped; a repeated key or a wrong-typed value (including a
        /// wrong-typed array element or an unknown topology/transport
        /// token) is rejected. Returns <see langword="false"/> for
        /// malformed input or a missing required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out SessionPlanMessage message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            string? generation = null;
            SessionTopology topology = default(SessionTopology);
            SessionTransport transport = default(SessionTransport);
            Guid? host = null;
            DirectEndpointInfo? directEndpoint = null;
            IReadOnlyList<SessionPeerInfo>? peers = null;
            IReadOnlyList<IceServerInfo>? iceServers = null;
            SessionTransport fallback = default(SessionTransport);
            bool topologySeen = false;
            bool transportSeen = false;
            bool hostSeen = false;
            bool endpointSeen = false;
            bool fallbackSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "generation"))
                {
                    if (generation is not null || !scanner.TryReadString(valueRaw, out generation))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "topology"))
                {
                    if (topologySeen || !TryReadTopology(scanner, valueRaw, out topology))
                    {
                        return false;
                    }

                    topologySeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "transport"))
                {
                    if (transportSeen || !TryReadTransport(scanner, valueRaw, out transport))
                    {
                        return false;
                    }

                    transportSeen = true;
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
                    else if (!scanner.TryReadGuid(valueRaw, out Guid hostId))
                    {
                        return false;
                    }
                    else
                    {
                        host = hostId;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "direct_endpoint"))
                {
                    if (endpointSeen)
                    {
                        return false;
                    }

                    endpointSeen = true;
                    if (scanner.TryReadNull(valueRaw))
                    {
                        directEndpoint = null;
                    }
                    else
                    {
                        if (
                            !JsonScanner.TryReadObjectSlice(
                                data,
                                valueRaw,
                                out ReadOnlyMemory<byte> slice
                            )
                            || !DirectEndpointInfo.TryDecode(slice, out DirectEndpointInfo endpoint)
                        )
                        {
                            return false;
                        }

                        directEndpoint = endpoint;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "peers"))
                {
                    if (
                        peers is not null
                        || !ProtocolArrays.TryReadObjectArray(
                            data,
                            valueRaw,
                            SessionPeerInfo.TryDecode,
                            out peers
                        )
                    )
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "ice_servers"))
                {
                    if (
                        iceServers is not null
                        || !ProtocolArrays.TryReadObjectArray(
                            data,
                            valueRaw,
                            IceServerInfo.TryDecode,
                            out iceServers
                        )
                    )
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "fallback"))
                {
                    if (fallbackSeen || !TryReadTransport(scanner, valueRaw, out fallback))
                    {
                        return false;
                    }

                    fallbackSeen = true;
                }

                state = scanner.EndMember();
            }

            /*
                The relay fallback is the only value the server ever sends
                (the floor is universal); anything else is a noncanonical
                plan the Rust spec rejects as a lifecycle violation.
            */
            if (
                state != JsonMemberState.EndObject
                || !topologySeen
                || !transportSeen
                || peers is null
                || !fallbackSeen
                || fallback != SessionTransport.Relay
            )
            {
                return false;
            }

            message = new SessionPlanMessage(
                generation,
                topology,
                transport,
                host,
                directEndpoint,
                peers,
                iceServers ?? Array.Empty<IceServerInfo>(),
                fallback
            );
            return true;
        }

        private static bool TryReadTopology(
            JsonScanner scanner,
            Range valueRaw,
            out SessionTopology value
        )
        {
            value = default(SessionTopology);
            if (!scanner.TryReadString(valueRaw, out string text))
            {
                return false;
            }

            switch (text)
            {
                case "relay":
                    value = SessionTopology.Relay;
                    return true;
                case "host":
                    value = SessionTopology.Host;
                    return true;
                case "mesh":
                    value = SessionTopology.Mesh;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryReadTransport(
            JsonScanner scanner,
            Range valueRaw,
            out SessionTransport value
        )
        {
            value = default(SessionTransport);
            if (!scanner.TryReadString(valueRaw, out string text))
            {
                return false;
            }

            switch (text)
            {
                case "relay":
                    value = SessionTransport.Relay;
                    return true;
                case "direct":
                    value = SessionTransport.Direct;
                    return true;
                case "webrtc":
                    value = SessionTransport.WebRtc;
                    return true;
                default:
                    return false;
            }
        }

        internal static bool SequenceEquals<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
            where T : IEquatable<T>
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
                if (!left[i].Equals(right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        internal static int SequenceHashCode<T>(IReadOnlyList<T>? values)
            where T : notnull
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
    /// Payload of the inbound v3 <c>NewPeer</c> message (S→C): an additive
    /// WebRTC peer directive whose <see cref="YouInitiate"/> flag avoids
    /// glare. Compatibility shape only — the authoritative rule is always
    /// the latest <c>SessionPlan</c>, which atomically removes stale peers.
    /// </summary>
    public readonly struct NewPeerMessage : IEquatable<NewPeerMessage>
    {
        /// <summary>Gets the new peer's player identity.</summary>
        public Guid PeerId { get; }

        /// <summary>Gets a value indicating whether this client sends the offer.</summary>
        public bool YouInitiate { get; }

        /// <summary>Initializes a new <see cref="NewPeerMessage"/> payload.</summary>
        public NewPeerMessage(Guid peerId, bool youInitiate)
        {
            PeerId = peerId;
            YouInitiate = youInitiate;
        }

        /// <inheritdoc />
        public bool Equals(NewPeerMessage other) =>
            PeerId == other.PeerId && YouInitiate == other.YouInitiate;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is NewPeerMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(PeerId);
            hash.Add(YouInitiate);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(NewPeerMessage left, NewPeerMessage right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(NewPeerMessage left, NewPeerMessage right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>NewPeer</c> envelope (the
        /// <see cref="EnvelopeEvent.Data"/> slice). Unknown fields are
        /// skipped; a repeated key or a wrong-typed value is rejected.
        /// Returns <see langword="false"/> for malformed input or a missing
        /// required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out NewPeerMessage message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            Guid peerId = default;
            bool youInitiate = false;
            bool idSeen = false;
            bool initiateSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "peer_id"))
                {
                    if (idSeen || !scanner.TryReadGuid(valueRaw, out peerId))
                    {
                        return false;
                    }

                    idSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "you_initiate"))
                {
                    if (initiateSeen || !scanner.TryReadBoolean(valueRaw, out youInitiate))
                    {
                        return false;
                    }

                    initiateSeen = true;
                }

                state = scanner.EndMember();
            }

            if (state != JsonMemberState.EndObject || !idSeen || !initiateSeen)
            {
                return false;
            }

            message = new NewPeerMessage(peerId, youInitiate);
            return true;
        }
    }

    /// <summary>
    /// Payload of the inbound v3 <c>PeerTransportStatus</c> message (S→C):
    /// the server's fan-out of an accepted peer <c>TransportStatus</c>
    /// report. Purely informational — it never changes how the server
    /// relays game data.
    /// </summary>
    public readonly struct PeerTransportStatusMessage : IEquatable<PeerTransportStatusMessage>
    {
        /// <summary>Gets the reporting peer's player identity.</summary>
        public Guid PeerId { get; }

        /// <summary>Gets the data-path transport token (e.g. <c>webrtc</c>).</summary>
        public string Transport { get; }

        /// <summary>Gets a value indicating whether the peer's transport is connected.</summary>
        public bool Connected { get; }

        /// <summary>Initializes a new <see cref="PeerTransportStatusMessage"/> payload.</summary>
        public PeerTransportStatusMessage(Guid peerId, string transport, bool connected)
        {
            PeerId = peerId;
            Transport = transport;
            Connected = connected;
        }

        /// <inheritdoc />
        public bool Equals(PeerTransportStatusMessage other) =>
            PeerId == other.PeerId
            && AuthenticateMessage.NullableStringEquals(Transport, other.Transport)
            && Connected == other.Connected;

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is PeerTransportStatusMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(PeerId);
            hash.Add(Transport);
            hash.Add(Connected);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(
            PeerTransportStatusMessage left,
            PeerTransportStatusMessage right
        ) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(
            PeerTransportStatusMessage left,
            PeerTransportStatusMessage right
        ) => !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>PeerTransportStatus</c>
        /// envelope (the <see cref="EnvelopeEvent.Data"/> slice). Unknown
        /// fields are skipped; a repeated key or a wrong-typed value is
        /// rejected. Returns <see langword="false"/> for malformed input or
        /// a missing required field.
        /// </summary>
        internal static bool TryDecode(
            ReadOnlyMemory<byte> data,
            out PeerTransportStatusMessage message
        )
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            Guid peerId = default;
            string? transport = null;
            bool connected = false;
            bool idSeen = false;
            bool connectedSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "peer_id"))
                {
                    if (idSeen || !scanner.TryReadGuid(valueRaw, out peerId))
                    {
                        return false;
                    }

                    idSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "transport"))
                {
                    if (transport is not null || !scanner.TryReadString(valueRaw, out transport))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "connected"))
                {
                    if (connectedSeen || !scanner.TryReadBoolean(valueRaw, out connected))
                    {
                        return false;
                    }

                    connectedSeen = true;
                }

                state = scanner.EndMember();
            }

            if (
                state != JsonMemberState.EndObject
                || !idSeen
                || transport is null
                || !connectedSeen
            )
            {
                return false;
            }

            message = new PeerTransportStatusMessage(peerId, transport, connected);
            return true;
        }
    }

    /// <summary>
    /// Payload of the inbound v3 <c>Signal</c> message (S→C): a peer's
    /// opaque WebRTC signal relayed verbatim. The value is forwarded
    /// without inspection (by convention one of <c>{"Offer": "..."}</c>,
    /// <c>{"Answer": "..."}</c>, or <c>{"IceCandidate": "..."}</c>);
    /// recipients discard it when <see cref="Generation"/> differs from
    /// their current plan generation.
    /// </summary>
    public readonly struct IncomingSignalMessage : IEquatable<IncomingSignalMessage>
    {
        /// <summary>Gets the originating peer's player identity.</summary>
        public Guid From { get; }

        /// <summary>Gets the sender's plan generation UUID.</summary>
        public string Generation { get; }

        /// <summary>Gets the signal JSON value, as UTF-8 bytes (relayed verbatim).</summary>
        public ReadOnlyMemory<byte> Signal => _signal;

        private readonly ReadOnlyMemory<byte> _signal;

        /// <summary>Initializes a new <see cref="IncomingSignalMessage"/> payload.</summary>
        public IncomingSignalMessage(Guid from, string generation, ReadOnlyMemory<byte> signal)
        {
            From = from;
            Generation = generation;
            _signal = signal;
        }

        /// <inheritdoc />
        public bool Equals(IncomingSignalMessage other) =>
            From == other.From
            && AuthenticateMessage.NullableStringEquals(Generation, other.Generation)
            && _signal.Span.SequenceEqual(other._signal.Span);

        /// <inheritdoc />
        public override bool Equals(object? obj) =>
            obj is IncomingSignalMessage other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hash = default;
            hash.Add(From);
            hash.Add(Generation);
            hash.Add(ProtocolHash.Of(_signal.Span));
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public static bool operator ==(IncomingSignalMessage left, IncomingSignalMessage right) =>
            left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(IncomingSignalMessage left, IncomingSignalMessage right) =>
            !left.Equals(right);

        /// <summary>
        /// Decodes the <c>data</c> object of a <c>Signal</c> envelope (the
        /// <see cref="EnvelopeEvent.Data"/> slice) into the originating
        /// peer, plan generation, and verbatim signal value. Unknown fields
        /// are skipped. Returns <see langword="false"/> for malformed input
        /// or a missing required field.
        /// </summary>
        internal static bool TryDecode(ReadOnlyMemory<byte> data, out IncomingSignalMessage message)
        {
            message = default;
            JsonScanner scanner = new JsonScanner(data.Span);
            JsonMemberState state = scanner.BeginObject();

            Guid from = default;
            string? generation = null;
            ReadOnlyMemory<byte> signal = default;
            bool fromSeen = false;
            bool signalSeen = false;

            while (state == JsonMemberState.Member)
            {
                state = scanner.ScanMember(out Range keyRaw, out Range valueRaw);
                if (state != JsonMemberState.Member)
                {
                    return false;
                }

                if (scanner.KeyIs(keyRaw, "from"))
                {
                    if (fromSeen || !scanner.TryReadGuid(valueRaw, out from))
                    {
                        return false;
                    }

                    fromSeen = true;
                }
                else if (scanner.KeyIs(keyRaw, "generation"))
                {
                    if (generation is not null || !scanner.TryReadString(valueRaw, out generation))
                    {
                        return false;
                    }
                }
                else if (scanner.KeyIs(keyRaw, "signal"))
                {
                    (int Offset, int Length) slice = valueRaw.GetOffsetAndLength(data.Length);
                    signal = data.Slice(slice.Offset, slice.Length);
                    signalSeen = true;
                }

                state = scanner.EndMember();
            }

            if (
                state != JsonMemberState.EndObject
                || !fromSeen
                || generation is null
                || !signalSeen
            )
            {
                return false;
            }

            message = new IncomingSignalMessage(from, generation, signal);
            return true;
        }
    }
}
