namespace SignalFish.Client.Tests.Polling
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading.Tasks;
    using NUnit.Framework;
    using SignalFish.Client.Core;
    using SignalFish.Client.Polling;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.Tests.Core;
    using SignalFish.Client.Tests.Transport;
    using SignalFish.Client.Transport;

    /// <summary>
    /// Red-green anchors for the M3.4 polling client: one golden frame per
    /// poll event kind, protocol violations, heartbeat liveness, budget
    /// backpressure, teardown exactly once, and the 0 B idle-poll gate.
    /// </summary>
    [TestFixture]
    public class SignalFishPollingClientTests
    {
        private const string RoomCode = "ABC123";

        private static readonly string[] ProtocolCapabilities =
        {
            "reconnection",
            "spectators",
            "authority",
        };
        private static readonly string[] ProtocolFormats = { "json", "message_pack" };
        private static readonly Guid PlayerId = new Guid("00000000-0000-0000-0000-00000000000a");
        private static readonly Guid RoomId = new Guid("11111111-1111-1111-1111-111111111111");

        [Test]
        public async Task ConnectEmitsTransportReadyOnce()
        {
            (SignalFishPollingClient client, FakeTransport _, VirtualClock _) = BuildTimed();

            await client.ConnectAsync(Endpoint());
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.TransportReady));

            List<PollEvent> events = DrainAll(client);
            Assert.That(events, Has.Count.EqualTo(1));
            Assert.That(events[0].Kind, Is.EqualTo(PollEventKind.TransportReady));
            Assert.That(client.PendingEventCount, Is.EqualTo(0));
        }

        [Test]
        public async Task ConnectTwiceIsRejected()
        {
            (SignalFishPollingClient client, FakeTransport _, VirtualClock _) = BuildTimed();
            await client.ConnectAsync(Endpoint());
            Assert.ThrowsAsync<InvalidOperationException>(
                (Func<Task>)(async () => await client.ConnectAsync(Endpoint()))
            );
        }

        [Test]
        public async Task GoldenLobbyFramesSurfaceTypedPayloadsInOrder()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndSettle(client);

            EnqueueGolden(
                transport,
                "Authenticated",
                "ProtocolInfo",
                "LobbyStateChanged",
                "PlayerJoined",
                "PlayerLeft",
                "PlayerReconnected",
                "AuthorityResponse",
                "AuthorityChanged"
            );

            Assert.That(client.Poll(), Is.EqualTo(8));
            List<PollEvent> events = DrainAll(client);
            Assert.That(events, Has.Count.EqualTo(8));

            Assert.That(events[0].Kind, Is.EqualTo(PollEventKind.Authenticated));
            Assert.That(events[0].Authenticated.AppName, Is.EqualTo("my-game"));
            Assert.That(events[0].Authenticated.Organization, Is.EqualTo("Ambiguous Interactive"));
            Assert.That(events[0].Authenticated.RateLimits.PerMinute, Is.EqualTo(60u));
            Assert.That(events[0].Authenticated.RateLimits.PerHour, Is.EqualTo(3600u));
            Assert.That(events[0].Authenticated.RateLimits.PerDay, Is.EqualTo(86400u));

            Assert.That(events[1].Kind, Is.EqualTo(PollEventKind.ProtocolInfo));
            Assert.That(events[1].ProtocolInfo.Capabilities, Is.EqualTo(ProtocolCapabilities));
            Assert.That(events[1].ProtocolInfo.GameDataFormats, Is.EqualTo(ProtocolFormats));

            Assert.That(events[2].Kind, Is.EqualTo(PollEventKind.LobbyStateChanged));
            Assert.That(events[2].Lobby.LobbyState, Is.EqualTo("lobby"));
            Assert.That(events[2].Lobby.AllReady, Is.False);
            Assert.That(events[2].Lobby.ReadyPlayers, Is.EqualTo(new[] { PlayerId }));

            Assert.That(events[3].Kind, Is.EqualTo(PollEventKind.PlayerJoined));
            Assert.That(
                events[3].PlayerJoined.Player.Id,
                Is.EqualTo(new Guid("00000000-0000-0000-0000-00000000000b"))
            );
            Assert.That(events[3].PlayerJoined.Player.Name, Is.EqualTo("Player 2"));
            Assert.That(events[3].PlayerJoined.Player.IsReady, Is.False);

            Assert.That(events[4].Kind, Is.EqualTo(PollEventKind.PlayerLeft));
            Assert.That(
                events[4].LeftPlayerId,
                Is.EqualTo(new Guid("00000000-0000-0000-0000-00000000000b"))
            );

            Assert.That(events[5].Kind, Is.EqualTo(PollEventKind.PlayerReconnected));
            Assert.That(
                events[5].LeftPlayerId,
                Is.EqualTo(new Guid("00000000-0000-0000-0000-00000000000b"))
            );

            Assert.That(events[6].Kind, Is.EqualTo(PollEventKind.AuthorityResponse));
            Assert.That(events[6].AuthorityResponse.Granted, Is.True);
            Assert.That(events[6].AuthorityResponse.Reason, Is.Null);

            Assert.That(events[7].Kind, Is.EqualTo(PollEventKind.AuthorityChanged));
            Assert.That(events[7].AuthorityChanged.AuthorityPlayer, Is.EqualTo(PlayerId));
            Assert.That(events[7].AuthorityChanged.YouAreAuthority, Is.False);
        }

        [Test]
        public async Task RoomJoinedCarriesMembershipAndRoomSnapshot()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndAuthenticate(client, transport);

            EnqueueGolden(transport, "RoomJoined");
            Assert.That(client.Poll(), Is.EqualTo(1));

            List<PollEvent> events = DrainAll(client);
            Assert.That(events, Has.Count.EqualTo(1));
            Assert.That(events[0].Kind, Is.EqualTo(PollEventKind.RoomJoined));
            Assert.That(
                events[0].Membership,
                Is.EqualTo(new RoomMembership(RoomRole.Player, PlayerId, RoomId, RoomCode))
            );
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.InRoom));
            Assert.That(client.Membership.Role, Is.EqualTo(RoomRole.Player));

            RoomSnapshot snapshot = events[0].Snapshot;
            Assert.That(snapshot.GameName, Is.EqualTo("my-game"));
            Assert.That(snapshot.MaxPlayers, Is.EqualTo(8u));
            Assert.That(snapshot.SupportsAuthority, Is.True);
            Assert.That(snapshot.IsAuthority, Is.True);
            Assert.That(snapshot.LobbyState, Is.EqualTo("waiting"));
            Assert.That(snapshot.RelayType, Is.EqualTo("matchbox"));
            Assert.That(snapshot.ReadyPlayers, Is.Empty);
            Assert.That(snapshot.CurrentSpectators, Is.Empty);
            Assert.That(snapshot.CurrentPlayers, Has.Count.EqualTo(1));
            Assert.That(snapshot.CurrentPlayers![0].Id, Is.EqualTo(PlayerId));
            Assert.That(snapshot.CurrentPlayers[0].Name, Is.EqualTo("Player 1"));
            Assert.That(snapshot.CurrentPlayers[0].IsAuthority, Is.True);

            EnqueueGolden(transport, "RoomLeft");
            Assert.That(client.Poll(), Is.EqualTo(1));
            PollEvent left = Single(client);
            Assert.That(left.Kind, Is.EqualTo(PollEventKind.RoomLeft));
            Assert.That(client.Membership.IsPresent, Is.False);
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.Authenticated));
        }

        [Test]
        public async Task SnapshotTracksJoinedSessionAndClearsOnTerminal()
        {
            /*
                M3.5 end-to-end: the coherent snapshot mirrors the machine
                through a token-bearing join and clears whole at teardown —
                the game-loop read never observes a half-applied fact.
            */
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndAuthenticate(client, transport);

            string joinedLine = GoldenFixtures.ReadFirstLineOfType(
                "v2-server-messages.jsonl",
                "RoomJoined"
            );
            string joinedWire = joinedLine.Insert(
                joinedLine.Length - 2,
                ",\"reconnection_token\":\"tok-e2e-1\""
            );
            EnqueueWire(transport, joinedWire);
            Assert.That(client.Poll(), Is.EqualTo(1));
            DrainAll(client);

            ClientSnapshot joined = client.Snapshot;
            Assert.That(joined.Connected, Is.True);
            Assert.That(joined.TransportReady, Is.True);
            Assert.That(joined.Authenticated, Is.True);
            Assert.That(joined.Role, Is.EqualTo(RoomRole.Player));
            Assert.That(joined.PlayerId, Is.EqualTo(PlayerId));
            Assert.That(joined.RoomId, Is.EqualTo(RoomId));
            Assert.That(joined.RoomCode, Is.EqualTo(RoomCode));
            Assert.That(joined.ReconnectionToken, Is.EqualTo("tok-e2e-1"));

            // Server close delivered as a frame: consumed by the next poll.
            transport.Abort(4000);
            Assert.That(client.Poll(), Is.EqualTo(1));
            DrainAll(client);
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.Terminal));

            ClientSnapshot terminal = client.Snapshot;
            Assert.That(terminal.Connected, Is.False);
            Assert.That(terminal.Role, Is.Null);
            Assert.That(terminal.ReconnectionToken, Is.Null);
        }

        [Test]
        public async Task SpectatorJoinedCarriesSnapshotSubset()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndAuthenticate(client, transport);

            EnqueueGolden(transport, "SpectatorJoined");
            Assert.That(client.Poll(), Is.EqualTo(1));

            PollEvent pollEvent = Single(client);
            Assert.That(pollEvent.Kind, Is.EqualTo(PollEventKind.SpectatorJoined));
            Assert.That(
                pollEvent.Membership,
                Is.EqualTo(
                    new RoomMembership(
                        RoomRole.Spectator,
                        new Guid("00000000-0000-0000-0000-00000000000c"),
                        RoomId,
                        RoomCode
                    )
                )
            );
            Assert.That(pollEvent.Snapshot.GameName, Is.EqualTo("my-game"));
            Assert.That(pollEvent.Snapshot.LobbyState, Is.EqualTo("lobby"));
            Assert.That(
                pollEvent.Snapshot.MaxPlayers,
                Is.EqualTo(0u),
                "fields the frame omits stay default"
            );
        }

        [Test]
        public async Task SpectatorLifecycleFramesSurfaceRosterPayloads()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndAuthenticate(client, transport);

            EnqueueGolden(
                transport,
                "SpectatorLeft",
                "NewSpectatorJoined",
                "SpectatorDisconnected"
            );
            Assert.That(client.Poll(), Is.EqualTo(3));

            List<PollEvent> events = DrainAll(client);
            Assert.That(events, Has.Count.EqualTo(3));
            Assert.That(events[0].Kind, Is.EqualTo(PollEventKind.SpectatorLeft));
            Assert.That(events[0].SpectatorLeft.RoomCode, Is.EqualTo(RoomCode));
            Assert.That(events[0].SpectatorLeft.Reason, Is.EqualTo("voluntary_leave"));
            Assert.That(events[0].SpectatorLeft.CurrentSpectators, Is.Empty);

            Assert.That(events[1].Kind, Is.EqualTo(PollEventKind.NewSpectatorJoined));
            Assert.That(events[1].NewSpectator.Spectator.Name, Is.EqualTo("Observer2"));
            Assert.That(events[1].NewSpectator.Reason, Is.EqualTo("joined"));

            Assert.That(events[2].Kind, Is.EqualTo(PollEventKind.SpectatorDisconnected));
            Assert.That(
                events[2].SpectatorDisconnected.SpectatorId,
                Is.EqualTo(new Guid("00000000-0000-0000-0000-00000000000c"))
            );
            Assert.That(events[2].SpectatorDisconnected.Reason, Is.EqualTo("disconnected"));
        }

        [Test]
        public async Task GameStartingCarriesPeerConnectionsWithOptionalEndpoint()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndAuthenticate(client, transport);

            EnqueueGolden(transport, "GameStarting");
            Assert.That(client.Poll(), Is.EqualTo(1));

            PollEvent pollEvent = Single(client);
            Assert.That(pollEvent.Kind, Is.EqualTo(PollEventKind.GameStarting));
            Assert.That(pollEvent.GameStart.PeerConnections, Has.Count.EqualTo(2));

            PeerConnection authority = pollEvent.GameStart.PeerConnections[0];
            Assert.That(authority.PlayerId, Is.EqualTo(PlayerId));
            Assert.That(authority.PlayerName, Is.EqualTo("Player 1"));
            Assert.That(authority.IsAuthority, Is.True);
            Assert.That(authority.RelayType, Is.EqualTo("matchbox"));
            ConnectionEndpoint? endpoint = authority.ConnectionInfo;
            Assert.That(endpoint, Is.Not.Null);
            Assert.That(endpoint.Value.Type, Is.EqualTo("direct"));
            Assert.That(endpoint.Value.Host, Is.EqualTo("192.0.2.10"));
            Assert.That(endpoint.Value.Port, Is.EqualTo(7777u));

            PeerConnection peer = pollEvent.GameStart.PeerConnections[1];
            Assert.That(peer.ConnectionInfo, Is.Null, "connection_info is optional per element");
        }

        [Test]
        public async Task GameDataRelaysPayloadVerbatim()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndAuthenticate(client, transport);

            EnqueueGolden(transport, "GameData");
            Assert.That(client.Poll(), Is.EqualTo(1));

            PollEvent pollEvent = Single(client);
            Assert.That(pollEvent.Kind, Is.EqualTo(PollEventKind.GameData));
            Assert.That(
                pollEvent.GameData.FromPlayer,
                Is.EqualTo(new Guid("00000000-0000-0000-0000-00000000000b"))
            );
            Assert.That(
                Encoding.UTF8.GetString(pollEvent.GameData.Payload.ToArray()),
                Is.EqualTo("{}")
            );
        }

        [Test]
        public async Task FailureFramesCarryReasonAndErrorCode()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndAuthenticate(client, transport);

            EnqueueWire(
                transport,
                @"{""type"":""RoomJoinFailed"",""data"":{""reason"":""Room is full"",""error_code"":""ROOM_FULL""}}"
            );
            EnqueueGolden(
                transport,
                "Error",
                "AuthenticationError",
                "ReconnectionFailed",
                "SpectatorJoinFailed"
            );
            Assert.That(client.Poll(), Is.EqualTo(5));

            List<PollEvent> events = DrainAll(client);
            Assert.That(events, Has.Count.EqualTo(5));
            Assert.That(events[0].Kind, Is.EqualTo(PollEventKind.RoomJoinFailed));
            Assert.That(events[0].Failure.Reason, Is.EqualTo("Room is full"));
            Assert.That(events[0].Failure.ErrorCode, Is.EqualTo("ROOM_FULL"));

            Assert.That(events[1].Kind, Is.EqualTo(PollEventKind.ServerError));
            Assert.That(events[1].Failure.Reason, Is.EqualTo("Room is full"));
            Assert.That(events[1].Failure.ErrorCode, Is.EqualTo("ROOM_FULL"));

            Assert.That(events[2].Kind, Is.EqualTo(PollEventKind.ServerError));
            Assert.That(
                events[2].Failure.Reason,
                Is.EqualTo("Invalid app_id"),
                "the error alias maps to Reason"
            );
            Assert.That(events[2].Failure.ErrorCode, Is.EqualTo("INVALID_APP_ID"));

            Assert.That(events[3].Kind, Is.EqualTo(PollEventKind.ReconnectionFailed));
            Assert.That(events[3].Failure.Reason, Is.EqualTo("Invalid reconnection token"));
            Assert.That(events[3].Failure.ErrorCode, Is.EqualTo("RECONNECTION_TOKEN_INVALID"));

            Assert.That(events[4].Kind, Is.EqualTo(PollEventKind.SpectatorJoinFailed));
            Assert.That(events[4].Failure.Reason, Is.EqualTo("Room not found"));
            Assert.That(events[4].Failure.ErrorCode, Is.EqualTo("ROOM_NOT_FOUND"));
        }

        [Test]
        public async Task MalformedSessionCriticalFrameIsAProtocolViolation()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndSettle(client);

            EnqueueWire(
                transport,
                @"{""type"":""RoomJoined"",""data"":{""room_id"":""11111111-1111-1111-1111-111111111111"",""room_code"":""ABC123"",""player_id"":""not-a-uuid""}}"
            );
            Assert.That(client.Poll(), Is.EqualTo(1));

            PollEvent pollEvent = Single(client);
            Assert.That(pollEvent.Kind, Is.EqualTo(PollEventKind.ProtocolViolation));
            Assert.That(pollEvent.Violation, Is.EqualTo(MessageKind.RoomJoined));
            Assert.That(
                client.Phase,
                Is.EqualTo(ConnectionPhase.TransportReady),
                "fail-closed: no membership applied"
            );
            Assert.That(client.Membership.IsPresent, Is.False);
        }

        [Test]
        public async Task MalformedGameplayPayloadIsAProtocolViolation()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndSettle(client);

            EnqueueWire(
                transport,
                @"{""type"":""LobbyStateChanged"",""data"":{""lobby_state"":""lobby"",""ready_players"":[],""all_ready"":""yes""}}"
            );
            Assert.That(client.Poll(), Is.EqualTo(1));

            PollEvent pollEvent = Single(client);
            Assert.That(pollEvent.Kind, Is.EqualTo(PollEventKind.ProtocolViolation));
            Assert.That(pollEvent.Violation, Is.EqualTo(MessageKind.LobbyStateChanged));
        }

        [Test]
        public async Task BinaryAndOversizedFramesAreProtocolViolations()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndSettle(client);

            transport.Enqueue(new byte[] { 0xde, 0xad }, isText: false);
            transport.Enqueue(Encoding.UTF8.GetBytes(new string(' ', 64 * 1024 + 1)), isText: true);
            Assert.That(client.Poll(), Is.EqualTo(2));

            List<PollEvent> events = DrainAll(client);
            Assert.That(events, Has.Count.EqualTo(2));
            foreach (PollEvent pollEvent in events)
            {
                Assert.That(pollEvent.Kind, Is.EqualTo(PollEventKind.ProtocolViolation));
                Assert.That(pollEvent.Violation, Is.EqualTo(default(MessageKind)));
            }
        }

        [Test]
        public async Task UnknownTypeAndMalformedFramesSurfaceForwardCompatibleEvents()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndSettle(client);

            EnqueueWire(transport, @"{""type"":""Holospace"",""data"":{}}");
            EnqueueWire(transport, "{nope");
            Assert.That(client.Poll(), Is.EqualTo(2));

            List<PollEvent> events = DrainAll(client);
            Assert.That(events, Has.Count.EqualTo(2));
            Assert.That(events[0].Kind, Is.EqualTo(PollEventKind.UnknownMessage));
            Assert.That(events[0].TypeText, Is.EqualTo("Holospace"));

            Assert.That(events[1].Kind, Is.EqualTo(PollEventKind.DecodeFailed));
            Assert.That(events[1].Error, Is.Not.EqualTo(default(DecodeError)));
            Assert.That(events[1].ErrorOffset, Is.GreaterThan(0));
        }

        [Test]
        public async Task PongFramesRefreshLivenessWithoutEvents()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) = BuildTimed(
                new PollingClientOptions(heartbeatIntervalMilliseconds: 1_000)
            );
            await ConnectAndSettle(client);

            EnqueueGolden(transport, "Pong");
            Assert.That(client.Poll(), Is.EqualTo(1));
            Assert.That(DrainAll(client), Has.Count.EqualTo(0), "Pong has no event surface");
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.TransportReady));
        }

        [Test]
        public async Task HeartbeatPingsOnTheCadence()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock clock) =
                BuildTimed();
            await ConnectAndSettle(client);

            clock.Advance(29_999);
            Assert.That(client.Poll(), Is.EqualTo(0));
            Assert.That(CountPings(transport), Is.EqualTo(0), "no ping before the cadence elapses");

            clock.Advance(1);
            Assert.That(client.Poll(), Is.EqualTo(0));
            Assert.That(CountPings(transport), Is.EqualTo(1), "one ping at 30 s");

            EnqueueGolden(transport, "Pong");
            Assert.That(client.Poll(), Is.EqualTo(1), "pong refreshes liveness");

            clock.Advance(29_999);
            Assert.That(client.Poll(), Is.EqualTo(0));
            Assert.That(
                CountPings(transport),
                Is.EqualTo(1),
                "cadence runs from the last ping, not traffic"
            );

            clock.Advance(1);
            Assert.That(client.Poll(), Is.EqualTo(0));
            Assert.That(CountPings(transport), Is.EqualTo(2), "second ping at 60 s");
        }

        [Test]
        public async Task HeartbeatSilenceBeyondTimeoutTearsDownTerminal()
        {
            (SignalFishPollingClient client, FakeTransport _, VirtualClock clock) = BuildTimed();
            await ConnectAndSettle(client);

            clock.Advance(59_999);
            Assert.That(client.Poll(), Is.EqualTo(0));
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.TransportReady));

            clock.Advance(1);
            Assert.That(client.Poll(), Is.EqualTo(0));
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.Terminal));

            PollEvent pollEvent = Single(client);
            Assert.That(pollEvent.Kind, Is.EqualTo(PollEventKind.Disconnected));
            Assert.That(pollEvent.Close.Code, Is.EqualTo(1006));

            Assert.That(client.Poll(), Is.EqualTo(0));
            Assert.That(client.PendingEventCount, Is.EqualTo(0), "terminal polls are inert");
        }

        [Test]
        public async Task CloseFrameTearsDownOnceWithTheWireCode()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndSettle(client);

            transport.Abort(4003);
            Assert.That(client.Poll(), Is.GreaterThanOrEqualTo(1));

            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.Terminal));
            PollEvent pollEvent = Single(client);
            Assert.That(pollEvent.Kind, Is.EqualTo(PollEventKind.Disconnected));
            Assert.That(pollEvent.Close.Code, Is.EqualTo(4003));
            Assert.That(pollEvent.Close.Kind, Is.EqualTo(TransportCloseKind.ActivityTimeout));

            Assert.That(client.Poll(), Is.EqualTo(0));
            Assert.That(
                client.PendingEventCount,
                Is.EqualTo(0),
                "disconnected is delivered exactly once"
            );
        }

        [Test]
        public async Task FaultedReceiveTearsDownWithTheCloseCode()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndSettle(client);

            transport.FailPendingReceive(4002);
            Assert.That(client.Poll(), Is.EqualTo(0));

            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.Terminal));
            PollEvent pollEvent = Single(client);
            Assert.That(pollEvent.Kind, Is.EqualTo(PollEventKind.Disconnected));
            Assert.That(pollEvent.Close.Kind, Is.EqualTo(TransportCloseKind.SlowConsumer));
        }

        [Test]
        public async Task FrameBudgetStopsPollAndNextPollResumes()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) = BuildTimed(
                new PollingClientOptions(maxFramesPerPoll: 8)
            );
            await ConnectAndSettle(client);

            for (int i = 0; i < 10; i++)
            {
                EnqueueGolden(transport, "Pong");
            }

            Assert.That(client.Poll(), Is.EqualTo(8), "first poll stops at the budget");
            Assert.That(client.Poll(), Is.EqualTo(2), "remaining frames wait in the transport");
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.TransportReady));
        }

        [Test]
        public async Task FullRingBackpressuresWithoutFrameLoss()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) = BuildTimed(
                new PollingClientOptions(maxFramesPerPoll: 8, eventCapacity: 3)
            );
            await ConnectAndSettle(client);

            for (int i = 0; i < 5; i++)
            {
                EnqueueGolden(transport, "LobbyStateChanged");
            }

            /*
                Capacity 3 reserves one slot for the terminal event, so at
                most 2 regular events queue before backpressure.
            */
            Assert.That(client.Poll(), Is.EqualTo(2), "stops when the regular capacity fills");
            Assert.That(client.PendingEventCount, Is.EqualTo(2));

            Assert.That(DrainAll(client), Has.Count.EqualTo(2));
            Assert.That(
                client.Poll(),
                Is.EqualTo(2),
                "backpressured frames are consumed next poll"
            );
            Assert.That(DrainAll(client), Has.Count.EqualTo(2));
            Assert.That(client.Poll(), Is.EqualTo(1));
            Assert.That(DrainAll(client), Has.Count.EqualTo(1));
        }

        [Test]
        public async Task IdlePollAllocatesNothing()
        {
            (SignalFishPollingClient client, FakeTransport _, VirtualClock _) = BuildTimed();
            await ConnectAndSettle(client);

            long minDelta = long.MaxValue;
            for (int pass = 0; pass < 4; pass++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int iteration = 0; iteration < 100; iteration++)
                {
                    client.Poll();
                    DrainToNothing(client);
                }

                long after = GC.GetAllocatedBytesForCurrentThread();
                minDelta = Math.Min(minDelta, after - before);
            }

            Assert.That(
                minDelta,
                Is.EqualTo(0),
                "Idle polls (no inbound traffic, no heartbeat due) must not allocate."
            );
        }

        [Test]
        public async Task TeardownOnFullRingStillDeliversDisconnected()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock clock) =
                BuildTimed(new PollingClientOptions(eventCapacity: 3));
            await ConnectAndAuthenticate(client, transport);

            /*
                Two events fill capacity - 1: the frame loop stops before
                the last slot so the terminal event always fits.
            */
            EnqueueGolden(transport, "LobbyStateChanged", "LobbyStateChanged", "LobbyStateChanged");
            Assert.That(client.Poll(), Is.EqualTo(2), "reserved slot stops frame consumption");
            Assert.That(client.PendingEventCount, Is.EqualTo(2));

            clock.Advance(60_000);
            Assert.That(client.Poll(), Is.EqualTo(0));

            List<PollEvent> events = DrainAll(client);
            Assert.That(events, Has.Count.EqualTo(3));
            Assert.That(events[2].Kind, Is.EqualTo(PollEventKind.Disconnected));
            Assert.That(events[2].Close.Code, Is.EqualTo(1006));
        }

        [Test]
        public async Task PingSendFailureFoldsIntoTheNextPoll()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock clock) =
                BuildTimed(new PollingClientOptions(heartbeatIntervalMilliseconds: 1_000));
            await ConnectAndAuthenticate(client, transport);

            /*
                EnqueueClose marks the fake closed without resolving the
                pending receive, so no close frame arrives; the ping send
                then throws synchronously (FakeTransport throws before
                recording) and the failure folds into the next poll.
            */
            transport.EnqueueClose(4000);
            clock.Advance(1_000);
            Assert.That(client.Poll(), Is.EqualTo(0));
            Assert.That(CountPings(transport), Is.EqualTo(0));

            Assert.That(client.Poll(), Is.EqualTo(0));
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.Terminal));
            PollEvent pollEvent = Single(client);
            Assert.That(pollEvent.Kind, Is.EqualTo(PollEventKind.Disconnected));
            Assert.That(pollEvent.Close.Code, Is.EqualTo(1006));
        }

        [Test]
        public async Task DisposeTearsDownIdempotently()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndSettle(client);

            await client.DisposeAsync();
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.Terminal));
            Assert.That(Single(client).Kind, Is.EqualTo(PollEventKind.Disconnected));

            await client.DisposeAsync();
            Assert.That(client.Poll(), Is.EqualTo(0));
            Assert.That(client.PendingEventCount, Is.EqualTo(0));
            Assert.That(transport.IsConnected, Is.False);
        }

        /// <summary>
        /// Drains without allocating: no list, and the ref-struct walk is
        /// contained in a non-async method.
        /// </summary>
        private static void DrainToNothing(SignalFishPollingClient client)
        {
            foreach (PollEvent pollEvent in client.DrainEvents())
            {
                throw new InvalidOperationException("An idle wire must not produce events.");
            }
        }

        private static (
            SignalFishPollingClient Client,
            FakeTransport Transport,
            VirtualClock Clock
        ) BuildTimed(PollingClientOptions? options = null)
        {
            FakeTransport transport = new FakeTransport();
            VirtualClock clock = new VirtualClock();
            SignalFishPollingClient client = new SignalFishPollingClient(transport, clock, options);
            return (client, transport, clock);
        }

        private static Uri Endpoint()
        {
            return new Uri("ws://localhost:8080/v2/client");
        }

        /// <summary>Connects and consumes the transport-ready event.</summary>
        private static async Task ConnectAndSettle(SignalFishPollingClient client)
        {
            await client.ConnectAsync(Endpoint());
            DrainAll(client);
        }

        /// <summary>Connects, then completes the golden handshake so membership facts can apply.</summary>
        private static async Task ConnectAndAuthenticate(
            SignalFishPollingClient client,
            FakeTransport transport
        )
        {
            await ConnectAndSettle(client);
            EnqueueGolden(transport, "Authenticated");
            Assert.That(client.Poll(), Is.EqualTo(1));
            DrainAll(client);
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.Authenticated));
        }

        private static void EnqueueGolden(FakeTransport transport, params string[] wireTypes)
        {
            foreach (string wireType in wireTypes)
            {
                EnqueueWire(
                    transport,
                    GoldenFixtures.ReadFirstLineOfType("v2-server-messages.jsonl", wireType)
                );
            }
        }

        private static void EnqueueWire(FakeTransport transport, string wire)
        {
            transport.Enqueue(Encoding.UTF8.GetBytes(wire), isText: true);
        }

        private static List<PollEvent> DrainAll(SignalFishPollingClient client)
        {
            List<PollEvent> events = new List<PollEvent>();
            foreach (PollEvent pollEvent in client.DrainEvents())
            {
                events.Add(pollEvent);
            }

            return events;
        }

        private static PollEvent Single(SignalFishPollingClient client)
        {
            List<PollEvent> events = DrainAll(client);
            Assert.That(events, Has.Count.EqualTo(1), "exactly one event expected");
            return events[0];
        }

        private static int CountPings(FakeTransport transport)
        {
            int pings = 0;
            foreach (string sent in transport.SentText)
            {
                if (sent == @"{""type"": ""Ping""}")
                {
                    pings++;
                }
            }

            return pings;
        }
    }
}
