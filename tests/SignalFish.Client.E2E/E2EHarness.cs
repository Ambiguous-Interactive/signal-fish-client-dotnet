namespace SignalFish.Client.E2E
{
    using System;
    using System.Diagnostics;
    using System.Text;
    using System.Text.Json.Nodes;
    using System.Threading.Tasks;
    using SignalFish.Client.Core;
    using SignalFish.Client.Polling;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.Transport;

    /// <summary>
    /// Live-server access for the conformance suite: the server URL comes
    /// from <c>SIGNALFISH_E2E_URL</c> (set by scripts/run-e2e.ps1 or the
    /// e2e workflow). Unset, the suite skips — unit CI stays green with no
    /// server present.
    /// </summary>
    internal static class E2EEnvironment
    {
        private const string UrlVariable = "SIGNALFISH_E2E_URL";

        /// <summary>Gets whether a live server was provided.</summary>
        internal static bool IsConfigured
        {
            get
            {
                return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(UrlVariable));
            }
        }

        /// <summary>Gets the live server's base URL without a trailing slash.</summary>
        internal static Uri BaseUrl
        {
            get { return new Uri(Environment.GetEnvironmentVariable(UrlVariable)!.TrimEnd('/')); }
        }

        /// <summary>Gets the v2 relay-floor WebSocket endpoint of the live server.</summary>
        internal static Uri V2Endpoint()
        {
            return new Uri(BaseUrl, "v2/ws");
        }
    }

    /// <summary>Shared driving helpers for the conformance scenarios.</summary>
    internal static class E2EHarness
    {
        internal static readonly TimeSpan DefaultEventTimeout = TimeSpan.FromSeconds(10);

        internal static async Task<SignalFishPollingClient> ConnectClientAsync(
            PollingClientOptions? options = null
        )
        {
            WebSocketTransport transport = new WebSocketTransport();
            SignalFishPollingClient client = new SignalFishPollingClient(
                transport,
                SystemClock.Instance,
                options
            );
            await client.ConnectAsync(E2EEnvironment.V2Endpoint()).ConfigureAwait(false);
            return client;
        }

        /// <summary>
        /// Connects a client whose whole socket path runs through a fresh
        /// in-process partition proxy (the caller disposes the proxy after
        /// the client). The directional-liveness drill severs each proxy
        /// direction to reproduce one-way partitions.
        /// </summary>
        internal static async Task<(
            SignalFishPollingClient Client,
            PartitionProxy Proxy
        )> ConnectProxiedClientAsync(PollingClientOptions? options = null)
        {
            PartitionProxy proxy = PartitionProxy.Start(E2EEnvironment.BaseUrl);
            try
            {
                WebSocketTransport transport = new WebSocketTransport();
                SignalFishPollingClient client = new SignalFishPollingClient(
                    transport,
                    SystemClock.Instance,
                    options
                );
                await client.ConnectAsync(proxy.ClientV2Endpoint()).ConfigureAwait(false);
                return (client, proxy);
            }
            catch
            {
                await proxy.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Connects and completes the handshake: the client's own admission
        /// policy requires a confirmed <c>Authenticated</c> before directed
        /// room operations, so every scenario joins through this path.
        /// </summary>
        internal static async Task<SignalFishPollingClient> ConnectAuthenticatedClientAsync(
            PollingClientOptions? options = null
        )
        {
            SignalFishPollingClient client = await ConnectClientAsync(options)
                .ConfigureAwait(false);
            await HandshakeAsync(client).ConfigureAwait(false);
            return client;
        }

        /// <summary>
        /// The proxied connect plus the handshake (see
        /// <see cref="ConnectAuthenticatedClientAsync"/>): the drill
        /// scenarios join through it, so the client's admission fence is
        /// satisfied before the partition starts.
        /// </summary>
        internal static async Task<(
            SignalFishPollingClient Client,
            PartitionProxy Proxy
        )> ConnectProxiedAuthenticatedClientAsync(PollingClientOptions? options = null)
        {
            (SignalFishPollingClient client, PartitionProxy proxy) =
                await ConnectProxiedClientAsync(options).ConfigureAwait(false);
            try
            {
                await HandshakeAsync(client).ConfigureAwait(false);
                return (client, proxy);
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                await proxy.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>Polls until the matching event arrives; a timeout fails with the phase.</summary>
        internal static async Task<PollEvent> WaitForEventAsync(
            SignalFishPollingClient client,
            Func<PollEvent, bool> match,
            TimeSpan? timeout = null
        )
        {
            TimeSpan budget = timeout ?? DefaultEventTimeout;
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.Elapsed < budget)
            {
                client.Poll();
                PollEvent? matched = TakeMatching(client, match);
                if (matched is not null)
                {
                    return matched.GetValueOrDefault();
                }

                await Task.Delay(10).ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"No matching event within {budget.TotalSeconds:F0}s "
                    + $"(phase {client.Phase}, pending {client.PendingEventCount})."
            );
        }

        /// <summary>Joins (creating on first join) and returns the confirmed membership.</summary>
        internal static async Task<RoomMembership> JoinRoomAsync(
            SignalFishPollingClient client,
            string gameName,
            string playerName,
            string? roomCode = null,
            uint? maxPlayers = null,
            bool? supportsAuthority = null
        )
        {
            CommandSend send = client.SendJoinRoom(
                new JoinRoomMessage(gameName, playerName, roomCode, maxPlayers, supportsAuthority)
            );
            if (!send.Accepted)
            {
                throw new InvalidOperationException($"JoinRoom refused: {send.Refusal}");
            }

            PollEvent joined = await WaitForEventAsync(
                    client,
                    e =>
                        e.Kind == PollEventKind.RoomJoined || e.Kind == PollEventKind.RoomJoinFailed
                )
                .ConfigureAwait(false);
            if (joined.Kind != PollEventKind.RoomJoined)
            {
                throw new InvalidOperationException(
                    $"JoinRoom failed: {joined.Failure.ErrorCode} ({joined.Failure.Reason})"
                );
            }

            return joined.Membership;
        }

        /// <summary>Creates a room and returns its generated code.</summary>
        internal static async Task<string> CreateRoomAsync(SignalFishPollingClient client)
        {
            RoomMembership membership = await JoinRoomAsync(client, GameName(), "creator")
                .ConfigureAwait(false);
            return membership.RoomCode!;
        }

        /// <summary>
        /// Toggles readiness and waits for the reflecting lobby broadcast.
        /// Pass <paramref name="expectAllReady"/> to skip stale broadcasts
        /// that can still be queued from another seat's earlier toggle.
        /// </summary>
        internal static async Task<LobbyStateChangedMessage> SetReadyAsync(
            SignalFishPollingClient client,
            bool? expectAllReady = null
        )
        {
            CommandSend send = client.SendPlayerReady();
            if (!send.Accepted)
            {
                throw new InvalidOperationException($"PlayerReady refused: {send.Refusal}");
            }

            PollEvent lobby = await WaitForEventAsync(
                    client,
                    e =>
                        e.Kind == PollEventKind.LobbyStateChanged
                        && (expectAllReady is null || e.Lobby.AllReady == expectAllReady)
                )
                .ConfigureAwait(false);
            return lobby.Lobby;
        }

        /// <summary>Sends one reliable game-data frame; returns the send verdict.</summary>
        internal static CommandSend SendRelayPayload(
            SignalFishPollingClient client,
            string payloadJson
        )
        {
            return client.SendGameData(new GameDataMessage(Encoding.UTF8.GetBytes(payloadJson)));
        }

        /// <summary>
        /// Joins a room as a spectator (with an optional sealed-room
        /// password) and waits for the typed confirmation.
        /// </summary>
        internal static async Task<RoomMembership> JoinSpectatorAsync(
            SignalFishPollingClient client,
            string gameName,
            string spectatorName,
            string roomCode,
            string? password = null
        )
        {
            CommandSend send = client.SendJoinAsSpectator(
                new JoinAsSpectatorMessage(gameName, roomCode, spectatorName, password)
            );
            if (!send.Accepted)
            {
                throw new InvalidOperationException($"JoinAsSpectator refused: {send.Refusal}");
            }

            PollEvent joined = await WaitForEventAsync(
                    client,
                    e =>
                        e.Kind == PollEventKind.SpectatorJoined
                        || e.Kind == PollEventKind.SpectatorJoinFailed
                )
                .ConfigureAwait(false);
            if (joined.Kind != PollEventKind.SpectatorJoined)
            {
                throw new InvalidOperationException(
                    $"JoinAsSpectator failed: {joined.Failure.ErrorCode} ({joined.Failure.Reason})"
                );
            }

            return joined.Membership;
        }

        /// <summary>Leaves spectator mode and waits for the typed confirmation.</summary>
        internal static async Task LeaveSpectatorAsync(SignalFishPollingClient client)
        {
            CommandSend send = client.SendLeaveSpectator();
            if (!send.Accepted)
            {
                throw new InvalidOperationException($"LeaveSpectator refused: {send.Refusal}");
            }

            await WaitForEventAsync(client, e => e.Kind == PollEventKind.SpectatorLeft)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Compares a relayed game-data payload with the expected JSON by
        /// value: the server relays the JSON value (it re-serializes), not
        /// the sender's exact bytes. Both sides normalize through
        /// <see cref="JsonNode.ToJsonString"/> (compact, same key order).
        /// </summary>
        internal static bool PayloadJsonEquals(ReadOnlySpan<byte> payload, string expectedJson)
        {
            JsonNode? actual = JsonNode.Parse(payload.ToArray());
            JsonNode? expected = JsonNode.Parse(expectedJson);
            return actual is not null
                && expected is not null
                && actual.ToJsonString() == expected.ToJsonString();
        }

        internal static string GameName()
        {
            return "e2e-dotnet-" + new string(Guid.NewGuid().ToString("N").AsSpan(0, 8));
        }

        /// <summary>
        /// One non-async drain walk (the ref-struct enumerator must not
        /// cross an await); breaking early keeps the unconsumed tail queued.
        /// </summary>
        private static PollEvent? TakeMatching(
            SignalFishPollingClient client,
            Func<PollEvent, bool> match
        )
        {
            foreach (PollEvent pollEvent in client.DrainEvents())
            {
                if (match(pollEvent))
                {
                    return pollEvent;
                }
            }

            return null;
        }

        /// <summary>Authenticates and waits for the handshake's two answers.</summary>
        private static async Task HandshakeAsync(SignalFishPollingClient client)
        {
            CommandSend send = client.SendAuthenticate(
                new AuthenticateMessage(appId: "e2e-dotnet-app")
            );
            if (!send.Accepted)
            {
                throw new InvalidOperationException($"Authenticate refused: {send.Refusal}");
            }

            await WaitForEventAsync(client, e => e.Kind == PollEventKind.Authenticated)
                .ConfigureAwait(false);
            await WaitForEventAsync(client, e => e.Kind == PollEventKind.ProtocolInfo)
                .ConfigureAwait(false);
        }
    }
}
