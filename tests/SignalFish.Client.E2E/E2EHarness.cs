namespace SignalFish.Client.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Text;
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

        /// <summary>Gets the v2 relay-floor WebSocket endpoint of the live server.</summary>
        internal static Uri V2Endpoint()
        {
            string baseUrl = Environment.GetEnvironmentVariable(UrlVariable)!.TrimEnd('/');
            return new Uri(baseUrl + "/v2/ws");
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
            return client;
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
    }
}
