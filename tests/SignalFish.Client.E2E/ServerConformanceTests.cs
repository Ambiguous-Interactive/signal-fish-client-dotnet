namespace SignalFish.Client.E2E
{
    using System;
    using System.Threading.Tasks;
    using NUnit.Framework;
    using SignalFish.Client.Core;
    using SignalFish.Client.Polling;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.Transport;

    /// <summary>
    /// The server's client-author conformance checklist (items 1-7 of
    /// docs/guides/building-a-client.md) exercised by the real client stack
    /// — SignalFishPollingClient over WebSocketTransport — against the live
    /// server. Every scenario runs in open mode (no allowlist): item 1
    /// exercises the optional-first-Authenticate policy, the rest start
    /// with JoinRoom.
    /// </summary>
    [TestFixture]
    public class ServerConformanceTests
    {
        [OneTimeSetUp]
        public void RequireLiveServer()
        {
            if (!E2EEnvironment.IsConfigured)
            {
                Assert.Ignore(
                    "SIGNALFISH_E2E_URL is not set; the conformance suite needs a live server "
                        + "(scripts/run-e2e.ps1)."
                );
            }
        }

        /// <summary>Item 1: handshake policy, join, relay round-trip, clean leave.</summary>
        [Test]
        public async Task HandshakeJoinRelayRoundTripAndLeave()
        {
            /*
                Open-mode optional Authenticate: legal only as the first
                message, answered by Authenticated followed by ProtocolInfo.
                The explicit path doubles as the policy check; every other
                scenario joins through the harness's authenticated helper.
            */
            SignalFishPollingClient alice = await E2EHarness.ConnectClientAsync();
            SignalFishPollingClient bob = await E2EHarness.ConnectAuthenticatedClientAsync();

            Assert.That(
                alice.SendAuthenticate(new AuthenticateMessage(appId: "e2e-dotnet-app")).Accepted,
                Is.True
            );
            await E2EHarness.WaitForEventAsync(alice, e => e.Kind == PollEventKind.Authenticated);
            await E2EHarness.WaitForEventAsync(alice, e => e.Kind == PollEventKind.ProtocolInfo);

            string gameName = E2EHarness.GameName();
            RoomMembership aliceSeat = await E2EHarness.JoinRoomAsync(alice, gameName, "alice");
            RoomMembership bobSeat = await E2EHarness.JoinRoomAsync(
                bob,
                gameName,
                "bob",
                roomCode: aliceSeat.RoomCode
            );
            Assert.That(bobSeat.RoomCode, Is.EqualTo(aliceSeat.RoomCode));

            /*
                Relay floor: alice's payload reaches bob verbatim with the
                sender identity attached, and vice versa.
            */
            Assert.That(E2EHarness.SendRelayPayload(alice, @"{""n"": 1}").Accepted, Is.True);
            PollEvent bobReceived = await E2EHarness.WaitForEventAsync(
                bob,
                e => e.Kind == PollEventKind.GameData
            );
            Assert.That(bobReceived.GameData.FromPlayer, Is.EqualTo(aliceSeat.PlayerId));
            Assert.That(
                E2EHarness.PayloadJsonEquals(bobReceived.GameData.Payload.Span, @"{""n"": 1}"),
                Is.True,
                "the relayed payload must match the sent JSON value"
            );

            Assert.That(E2EHarness.SendRelayPayload(bob, @"{""n"": 2}").Accepted, Is.True);
            PollEvent aliceReceived = await E2EHarness.WaitForEventAsync(
                alice,
                e => e.Kind == PollEventKind.GameData
            );
            Assert.That(aliceReceived.GameData.FromPlayer, Is.EqualTo(bobSeat.PlayerId));

            await LeaveAsync(alice);
            await E2EHarness.WaitForEventAsync(bob, e => e.Kind == PollEventKind.PlayerLeft);
            await LeaveAsync(bob);

            await alice.DisposeAsync();
            await bob.DisposeAsync();
        }

        /// <summary>Item 2: two-player lobby, all-ready, start, GameStarting.</summary>
        [Test]
        public async Task TwoPlayerLobbyReachesGameStarting()
        {
            SignalFishPollingClient alice = await E2EHarness.ConnectAuthenticatedClientAsync();
            SignalFishPollingClient bob = await E2EHarness.ConnectAuthenticatedClientAsync();

            string gameName = E2EHarness.GameName();
            await E2EHarness.JoinRoomAsync(alice, gameName, "alice");
            await E2EHarness.JoinRoomAsync(bob, gameName, "bob", roomCode: alice.Snapshot.RoomCode);

            LobbyStateChangedMessage first = await E2EHarness.SetReadyAsync(
                alice,
                expectAllReady: false
            );
            Assert.That(first.AllReady, Is.False);

            LobbyStateChangedMessage final = await E2EHarness.SetReadyAsync(
                bob,
                expectAllReady: true
            );
            Assert.That(final.AllReady, Is.True);

            Assert.That(alice.SendStartGame().Accepted, Is.True);
            PollEvent aliceStart = await E2EHarness.WaitForEventAsync(
                alice,
                e => e.Kind == PollEventKind.GameStarting
            );

            /*
                Legacy relay metadata: the server lists every current player
                (no ConnectionInfo for relay-only rooms), so a two-player
                room carries two entries.
            */
            Assert.That(aliceStart.GameStart.PeerConnections, Has.Count.EqualTo(2));
            await E2EHarness.WaitForEventAsync(bob, e => e.Kind == PollEventKind.GameStarting);

            await alice.DisposeAsync();
            await bob.DisposeAsync();
        }

        /// <summary>
        /// Item 3: a non-authority start in a ready authority room returns
        /// GAME_START_FORBIDDEN; a premature authority start while the room
        /// is not all-ready returns GAME_START_NOT_READY — both as typed
        /// error_code data.
        /// </summary>
        [Test]
        public async Task StartGameRejectionsCarryTypedErrorCodes()
        {
            /*
                Authority room: the creator is the authority. Both seats
                ready (the room is all-ready), so the only failing rule is
                the authorization one.
            */
            SignalFishPollingClient alice = await E2EHarness.ConnectAuthenticatedClientAsync();
            SignalFishPollingClient bob = await E2EHarness.ConnectAuthenticatedClientAsync();

            string authorityGame = E2EHarness.GameName();
            await E2EHarness.JoinRoomAsync(alice, authorityGame, "alice", supportsAuthority: true);
            await E2EHarness.JoinRoomAsync(
                bob,
                authorityGame,
                "bob",
                roomCode: alice.Snapshot.RoomCode
            );
            await E2EHarness.SetReadyAsync(alice, expectAllReady: false);
            await E2EHarness.SetReadyAsync(bob, expectAllReady: true);

            Assert.That(bob.SendStartGame().Accepted, Is.True);
            PollEvent forbidden = await E2EHarness.WaitForEventAsync(
                bob,
                e => e.Kind == PollEventKind.ServerError
            );
            Assert.That(forbidden.Failure.ErrorCode, Is.EqualTo("GAME_START_FORBIDDEN"));

            /*
                Readiness gate: the authority (ready) starts while the other
                seat is unready, so the room is not all-ready.
            */
            SignalFishPollingClient carol = await E2EHarness.ConnectAuthenticatedClientAsync();
            SignalFishPollingClient dave = await E2EHarness.ConnectAuthenticatedClientAsync();
            string lobbyGame = E2EHarness.GameName();
            await E2EHarness.JoinRoomAsync(carol, lobbyGame, "carol", supportsAuthority: true);
            await E2EHarness.JoinRoomAsync(
                dave,
                lobbyGame,
                "dave",
                roomCode: carol.Snapshot.RoomCode
            );
            await E2EHarness.SetReadyAsync(carol, expectAllReady: false);

            Assert.That(carol.SendStartGame().Accepted, Is.True);
            PollEvent notReady = await E2EHarness.WaitForEventAsync(
                carol,
                e => e.Kind == PollEventKind.ServerError
            );
            Assert.That(notReady.Failure.ErrorCode, Is.EqualTo("GAME_START_NOT_READY"));

            await alice.DisposeAsync();
            await bob.DisposeAsync();
            await carol.DisposeAsync();
            await dave.DisposeAsync();
        }

        /// <summary>
        /// Item 4: a cached all_ready is advisory — a later joiner (always
        /// unready, no corrective broadcast) invalidates it, a start attempt
        /// fails, and the lobby recovers once the joiner readies.
        /// </summary>
        [Test]
        public async Task LateJoinerInvalidatesAllReadyAndLobbyRecovers()
        {
            SignalFishPollingClient alice = await E2EHarness.ConnectAuthenticatedClientAsync();
            SignalFishPollingClient bob = await E2EHarness.ConnectAuthenticatedClientAsync();

            string gameName = E2EHarness.GameName();
            await E2EHarness.JoinRoomAsync(alice, gameName, "alice");
            string roomCode = alice.Snapshot.RoomCode!;

            LobbyStateChangedMessage solo = await E2EHarness.SetReadyAsync(
                alice,
                expectAllReady: true
            );
            Assert.That(solo.AllReady, Is.True, "solo room: alice's readiness is all-ready");

            /*
                The joiner arrives unready and the server sends no corrective
                broadcast: alice's cached all_ready is now stale, and her
                start attempt is answered GAME_START_NOT_READY.
            */
            await E2EHarness.JoinRoomAsync(bob, gameName, "bob", roomCode: roomCode);
            Assert.That(alice.SendStartGame().Accepted, Is.True);
            PollEvent notReady = await E2EHarness.WaitForEventAsync(
                alice,
                e => e.Kind == PollEventKind.ServerError
            );
            Assert.That(notReady.Failure.ErrorCode, Is.EqualTo("GAME_START_NOT_READY"));

            /*
                Recovery: once the joiner readies, the same authority re-issue
                finalizes — the one-shot latch must not stall the lobby.
            */
            await E2EHarness.SetReadyAsync(bob, expectAllReady: true);
            Assert.That(alice.SendStartGame().Accepted, Is.True);
            await E2EHarness.WaitForEventAsync(alice, e => e.Kind == PollEventKind.GameStarting);
            await E2EHarness.WaitForEventAsync(bob, e => e.Kind == PollEventKind.GameStarting);

            await alice.DisposeAsync();
            await bob.DisposeAsync();
        }

        /// <summary>Item 5: refused operations surface typed error_code data.</summary>
        [Test]
        public async Task RefusedJoinsCarryTypedErrorCodes()
        {
            SignalFishPollingClient alice = await E2EHarness.ConnectAuthenticatedClientAsync();
            SignalFishPollingClient bob = await E2EHarness.ConnectAuthenticatedClientAsync();
            SignalFishPollingClient carol = await E2EHarness.ConnectAuthenticatedClientAsync();

            // ROOM_FULL: the room's ceiling refuses the next seat.
            string gameName = E2EHarness.GameName();
            await E2EHarness.JoinRoomAsync(alice, gameName, "alice", maxPlayers: 1);
            string roomCode = alice.Snapshot.RoomCode!;

            CommandSend bobJoin = bob.SendJoinRoom(new JoinRoomMessage(gameName, "bob", roomCode));
            Assert.That(bobJoin.Accepted, Is.True);
            PollEvent full = await E2EHarness.WaitForEventAsync(
                bob,
                e => e.Kind == PollEventKind.RoomJoinFailed
            );
            Assert.That(full.Failure.ErrorCode, Is.EqualTo("ROOM_FULL"));

            // ROOM_NOT_FOUND: spectators join existing rooms only.
            string missingCode = "NF" + new string(Guid.NewGuid().ToString("N").AsSpan(0, 4));
            CommandSend carolSpectate = carol.SendJoinAsSpectator(
                new JoinAsSpectatorMessage(gameName, missingCode, "carol")
            );
            Assert.That(carolSpectate.Accepted, Is.True);
            PollEvent missing = await E2EHarness.WaitForEventAsync(
                carol,
                e => e.Kind == PollEventKind.SpectatorJoinFailed
            );
            Assert.That(missing.Failure.ErrorCode, Is.EqualTo("ROOM_NOT_FOUND"));

            await alice.DisposeAsync();
            await bob.DisposeAsync();
            await carol.DisposeAsync();
        }

        /// <summary>
        /// M5.1: a password-sealed room refuses passwordless and wrong-
        /// password joins with the same PASSWORD_REQUIRED code (missing vs
        /// wrong is indistinguishable to the sender), accepts the correct
        /// one, and the spectator lifecycle runs end to end.
        /// </summary>
        [Test]
        public async Task SealedRoomGateSpectatorFlowByPassword()
        {
            string gameName = E2EHarness.GameName();
            string password = "hunter2-" + new string(Guid.NewGuid().ToString("N").AsSpan(0, 6));

            // Creating the room with a password seals it.
            SignalFishPollingClient alice = await E2EHarness.ConnectAuthenticatedClientAsync();
            CommandSend seal = alice.SendJoinRoom(
                new JoinRoomMessage(gameName, "alice", password: password)
            );
            Assert.That(seal.Accepted, Is.True);
            await E2EHarness.WaitForEventAsync(alice, e => e.Kind == PollEventKind.RoomJoined);

            SignalFishPollingClient watcher = await E2EHarness.ConnectAuthenticatedClientAsync();

            // Passwordless join to the sealed room.
            CommandSend bare = watcher.SendJoinAsSpectator(
                new JoinAsSpectatorMessage(gameName, alice.Snapshot.RoomCode!, "watcher")
            );
            Assert.That(bare.Accepted, Is.True);
            PollEvent required = await E2EHarness.WaitForEventAsync(
                watcher,
                e => e.Kind == PollEventKind.SpectatorJoinFailed
            );
            Assert.That(required.Failure.ErrorCode, Is.EqualTo("PASSWORD_REQUIRED"));

            // Wrong password: same code, indistinguishable.
            CommandSend wrong = watcher.SendJoinAsSpectator(
                new JoinAsSpectatorMessage(
                    gameName,
                    alice.Snapshot.RoomCode!,
                    "watcher",
                    password + "-wrong"
                )
            );
            Assert.That(wrong.Accepted, Is.True);
            PollEvent indistinct = await E2EHarness.WaitForEventAsync(
                watcher,
                e => e.Kind == PollEventKind.SpectatorJoinFailed
            );
            Assert.That(indistinct.Failure.ErrorCode, Is.EqualTo("PASSWORD_REQUIRED"));

            // The correct password admits the spectator; leave confirms.
            RoomMembership seat = await E2EHarness.JoinSpectatorAsync(
                watcher,
                gameName,
                "watcher",
                alice.Snapshot.RoomCode!,
                password
            );
            Assert.That(seat.Role, Is.EqualTo(RoomRole.Spectator));
            await E2EHarness.LeaveSpectatorAsync(watcher);

            await alice.DisposeAsync();
            await watcher.DisposeAsync();
        }

        /// <summary>
        /// M5.2: an authority-enabled room hands the seat to a claiming
        /// player (AuthorityResponse + AuthorityChanged, mirrored in the
        /// snapshots) and only the holder can start the game.
        /// </summary>
        [Test]
        public async Task AuthorityClaimMovesTheSeatAndGatesTheStart()
        {
            SignalFishPollingClient alice = await E2EHarness.ConnectAuthenticatedClientAsync();
            SignalFishPollingClient bob = await E2EHarness.ConnectAuthenticatedClientAsync();

            string gameName = E2EHarness.GameName();
            await E2EHarness.JoinRoomAsync(alice, gameName, "alice", supportsAuthority: true);
            await E2EHarness.JoinRoomAsync(bob, gameName, "bob", roomCode: alice.Snapshot.RoomCode);

            Assert.That(alice.Snapshot.IsAuthority, Is.True, "the creator holds authority");
            Assert.That(bob.Snapshot.IsAuthority, Is.False);

            /*
                Bob claims the seat; the answer and the broadcast may arrive
                in either order, so classify both from one combined wait.
            */
            Assert.That(bob.SendAuthorityRequest(true).Accepted, Is.True);
            List<PollEvent> bobEvents = await E2EHarness.WaitForEventsAsync(
                bob,
                e => e.Kind is PollEventKind.AuthorityResponse or PollEventKind.AuthorityChanged,
                2
            );
            PollEvent bobAnswer = bobEvents.First(e => e.Kind == PollEventKind.AuthorityResponse);
            Assert.That(bobAnswer.AuthorityResponse.Granted, Is.True);
            PollEvent bobMove = bobEvents.First(e => e.Kind == PollEventKind.AuthorityChanged);
            Assert.That(bobMove.AuthorityChanged.YouAreAuthority, Is.True);
            Assert.That(bob.Snapshot.IsAuthority, Is.True);

            // The uniform broadcast reaches the old authority too.
            PollEvent aliceMove = await E2EHarness.WaitForEventAsync(
                alice,
                e => e.Kind == PollEventKind.AuthorityChanged
            );
            Assert.That(aliceMove.AuthorityChanged.YouAreAuthority, Is.False);
            Assert.That(alice.Snapshot.IsAuthority, Is.False);

            // The old authority can no longer start the game...
            await E2EHarness.SetReadyAsync(alice);
            await E2EHarness.SetReadyAsync(bob, expectAllReady: true);
            Assert.That(alice.SendStartGame().Accepted, Is.True);
            PollEvent aliceRefused = await E2EHarness.WaitForEventAsync(
                alice,
                e =>
                    e.Kind == PollEventKind.ServerError
                    && e.Failure.ErrorCode is "GAME_START_FORBIDDEN" or "GAME_START_NOT_READY"
            );
            Assert.That(aliceRefused.Failure.ErrorCode, Is.EqualTo("GAME_START_FORBIDDEN"));

            // ...but the holder can.
            Assert.That(bob.SendStartGame().Accepted, Is.True);
            await E2EHarness.WaitForEventAsync(bob, e => e.Kind == PollEventKind.GameStarting);
            await E2EHarness.WaitForEventAsync(alice, e => e.Kind == PollEventKind.GameStarting);

            await alice.DisposeAsync();
            await bob.DisposeAsync();
        }

        /// <summary>
        /// Item 6: a client that heartbeats survives an idle window longer
        /// than the server's ping timeout and stays usable afterwards.
        /// </summary>
        [Test]
        public async Task HeartbeatKeepsAnIdleConnectionAlive()
        {
            /*
                Ping every second; the CI server reaps silent clients after
                ~3 s, so surviving 6 s of silence proves the heartbeat path.
            */
            SignalFishPollingClient alice = await E2EHarness.ConnectAuthenticatedClientAsync(
                new PollingClientOptions(heartbeatIntervalMilliseconds: 1_000)
            );
            SignalFishPollingClient bob = await E2EHarness.ConnectAuthenticatedClientAsync(
                new PollingClientOptions(heartbeatIntervalMilliseconds: 1_000)
            );

            string gameName = E2EHarness.GameName();
            await E2EHarness.JoinRoomAsync(alice, gameName, "alice");
            await E2EHarness.JoinRoomAsync(bob, gameName, "bob", roomCode: alice.Snapshot.RoomCode);

            /*
                Both seats poll once a second (their heartbeats ride the
                polls) with no game traffic: after 6 s of gameplay silence
                — twice the server's ping timeout — both seats are alive.
            */
            for (int second = 0; second < 6; second++)
            {
                await Task.Delay(1_000);
                alice.Poll();
                bob.Poll();
            }

            Assert.That(
                alice.Phase,
                Is.EqualTo(ConnectionPhase.InRoom),
                "a heartbeating client must outlive the server's ping timeout"
            );
            Assert.That(bob.Phase, Is.EqualTo(ConnectionPhase.InRoom));

            /*
                The idle window did not rot the connection: relay still flows.
            */
            Assert.That(
                E2EHarness.SendRelayPayload(bob, @"{""after"": ""idle""}").Accepted,
                Is.True
            );
            PollEvent received = await E2EHarness.WaitForEventAsync(
                alice,
                e => e.Kind == PollEventKind.GameData
            );
            Assert.That(
                E2EHarness.PayloadJsonEquals(
                    received.GameData.Payload.Span,
                    @"{""after"": ""idle""}"
                ),
                Is.True,
                "relay must still carry payloads after the idle window"
            );

            await alice.DisposeAsync();
            await bob.DisposeAsync();
        }

        /// <summary>
        /// Item 7 (client-side half): a client that stops writing is dropped
        /// by the server's liveness reapers — the client must reach a
        /// terminal session (surfacing the close), never hang half-alive.
        /// The harness runs the server without RFC 6455 Ping probes, so a
        /// silent client cannot survive on automatic Pong replies.
        /// </summary>
        [Test]
        public async Task SilentClientIsDroppedByServerLiveness()
        {
            /*
                Never ping, never declare local death: only the server can
                end this session. CI runs the server with ~3 s liveness
                timers, so the drop lands well inside the 20 s budget.
            */
            SignalFishPollingClient silent = await E2EHarness.ConnectAuthenticatedClientAsync(
                new PollingClientOptions(
                    heartbeatIntervalMilliseconds: int.MaxValue,
                    heartbeatTimeoutMilliseconds: int.MaxValue
                )
            );

            await E2EHarness.JoinRoomAsync(silent, E2EHarness.GameName(), "silent");

            PollEvent dropped = await E2EHarness.WaitForEventAsync(
                silent,
                e =>
                    e.Kind == PollEventKind.Disconnected
                    || (
                        e.Kind == PollEventKind.ServerError
                        && e.Failure.ErrorCode == "CONNECTION_IDLE_TIMEOUT"
                    ),
                TimeSpan.FromSeconds(20)
            );
            if (dropped.Kind == PollEventKind.ServerError)
            {
                dropped = await E2EHarness.WaitForEventAsync(
                    silent,
                    e => e.Kind == PollEventKind.Disconnected,
                    TimeSpan.FromSeconds(20)
                );
            }

            Assert.That(dropped.Kind, Is.EqualTo(PollEventKind.Disconnected));
            Assert.That(silent.Phase, Is.EqualTo(ConnectionPhase.Terminal));
            Assert.That(silent.IsConnected, Is.False);
        }

        /// <summary>
        /// Item 7 (partition drill, client→server severed): with the
        /// outbound direction cut by the proxy, the server's liveness
        /// reaper ends the session and its typed close code reaches the
        /// client through the still-open server→client direction. The
        /// client surfaces that close and reaches terminal — never a
        /// half-alive session. Which reaper fires (ping-timeout vs
        /// idle-timeout) depends on the server build, so either liveness
        /// code proves the point: the server's diagnosis arrived, not a
        /// synthetic local 1006.
        /// </summary>
        [Test]
        public async Task BlockedClientToServerEndsInTypedServerLivenessClose()
        {
            /*
                Only the server can end this session (no pings, no local
                liveness): whatever ends it must arrive over the surviving
                direction. CI runs the server with ~3 s liveness timers.
            */
            (SignalFishPollingClient silent, PartitionProxy proxy) =
                await E2EHarness.ConnectProxiedAuthenticatedClientAsync(
                    new PollingClientOptions(
                        heartbeatIntervalMilliseconds: int.MaxValue,
                        heartbeatTimeoutMilliseconds: int.MaxValue
                    )
                );

            try
            {
                await E2EHarness.JoinRoomAsync(silent, E2EHarness.GameName(), "silent");

                proxy.ClientToServerBlocked = true;

                PollEvent dropped = await E2EHarness.WaitForEventAsync(
                    silent,
                    e => e.Kind == PollEventKind.Disconnected,
                    TimeSpan.FromSeconds(20)
                );
                Assert.That(
                    dropped.Close.Kind,
                    Is.AnyOf(TransportCloseKind.ActivityTimeout, TransportCloseKind.IdleTimeout),
                    $"the server's typed liveness close must arrive through the open "
                        + $"direction (got {dropped.Close})"
                );
                Assert.That(silent.Phase, Is.EqualTo(ConnectionPhase.Terminal));
                Assert.That(silent.IsConnected, Is.False);
            }
            finally
            {
                await silent.DisposeAsync();
                await proxy.DisposeAsync();
            }
        }

        /// <summary>
        /// Item 7 (partition drill, server→client severed): the client's
        /// sends still "succeed" — and genuinely do carry end-to-end, the
        /// server relaying them to a directly-connected peer — but the
        /// client's own liveness clock declares death (local 1006) instead
        /// of trusting one-way outbound progress as healthy.
        /// </summary>
        [Test]
        public async Task BlockedServerToClientIsDeclaredDeadByLocalLiveness()
        {
            /*
                Alice pings every 500 ms (the server never reaps her: the
                outbound direction carries the pings) and declares death
                after 2 s without any server frame. Death must be her own
                verdict — the 1006 liveness close, not a server code.
            */
            (SignalFishPollingClient alice, PartitionProxy proxy) =
                await E2EHarness.ConnectProxiedAuthenticatedClientAsync(
                    new PollingClientOptions(
                        heartbeatIntervalMilliseconds: 500,
                        heartbeatTimeoutMilliseconds: 2_000
                    )
                );
            SignalFishPollingClient bob = await E2EHarness.ConnectAuthenticatedClientAsync();

            try
            {
                string gameName = E2EHarness.GameName();
                RoomMembership aliceSeat = await E2EHarness.JoinRoomAsync(alice, gameName, "alice");
                await E2EHarness.JoinRoomAsync(bob, gameName, "bob", roomCode: aliceSeat.RoomCode);

                proxy.ServerToClientBlocked = true;

                /*
                    The unaffected direction still carries data: alice's
                    relay crosses the partition (outbound only) and reaches
                    bob, who sits outside it.
                */
                Assert.That(
                    E2EHarness.SendRelayPayload(alice, @"{""dir"": ""out""}").Accepted,
                    Is.True
                );
                PollEvent received = await E2EHarness.WaitForEventAsync(
                    bob,
                    e => e.Kind == PollEventKind.GameData
                );
                Assert.That(received.GameData.FromPlayer, Is.EqualTo(aliceSeat.PlayerId));
                Assert.That(
                    E2EHarness.PayloadJsonEquals(
                        received.GameData.Payload.Span,
                        @"{""dir"": ""out""}"
                    ),
                    Is.True
                );

                PollEvent dropped = await E2EHarness.WaitForEventAsync(
                    alice,
                    e => e.Kind == PollEventKind.Disconnected,
                    TimeSpan.FromSeconds(10)
                );
                Assert.That(
                    dropped.Close.Kind,
                    Is.EqualTo(TransportCloseKind.Abnormal),
                    $"a locally-declared liveness death must surface as 1006 "
                        + $"(got {dropped.Close})"
                );
                Assert.That(alice.Phase, Is.EqualTo(ConnectionPhase.Terminal));
            }
            finally
            {
                await alice.DisposeAsync();
                await bob.DisposeAsync();
                await proxy.DisposeAsync();
            }
        }

        private static async Task LeaveAsync(SignalFishPollingClient client)
        {
            CommandSend send = client.SendLeaveRoom();
            if (!send.Accepted)
            {
                throw new InvalidOperationException($"LeaveRoom refused: {send.Refusal}");
            }

            await E2EHarness.WaitForEventAsync(client, e => e.Kind == PollEventKind.RoomLeft);
        }
    }
}
