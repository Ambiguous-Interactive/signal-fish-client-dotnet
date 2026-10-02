namespace SignalFish.Client.Tests.Polling
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;
    using NUnit.Framework;
    using SignalFish.Client.Core;
    using SignalFish.Client.Polling;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.Tests.Core;
    using SignalFish.Client.Tests.Transport;

    /// <summary>
    /// Red-green anchors for the M3.6 command-send surface: admission
    /// refusals never touch the wire, accepted commands emit golden wire
    /// bytes, directed operations arm their fence until the typed result,
    /// and encode misuse arms nothing.
    /// </summary>
    [TestFixture]
    public class CommandSendTests
    {
        private static readonly Guid PlayerId = new Guid("00000000-0000-0000-0000-00000000000a");
        private static readonly Guid RoomId = new Guid("11111111-1111-1111-1111-111111111111");

        [Test]
        public async Task SendBeforeConnectIsRefusedNotConnected()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();

            CommandSend send = client.SendPlayerReady();
            Assert.That(send.Accepted, Is.False);
            Assert.That(send.Refusal, Is.EqualTo(AdmissionError.NotConnected));
            Assert.That(transport.SentText, Is.Empty);
            Assert.That(client.PendingOperation, Is.EqualTo(default(PendingRoomOperation)));

            await client.ConnectAsync(Endpoint());
            Assert.That(client.SendAuthenticate(new AuthenticateMessage()).Accepted, Is.True);
        }

        [Test]
        public async Task JoinRoomSendsGoldenWireAndArmsJoinFence()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndAuthenticate(client, transport);

            CommandSend send = client.SendJoinRoom(
                new JoinRoomMessage("my-game", "Player1", "ABC123", 8, true)
            );
            Assert.That(send.Accepted, Is.True);
            Assert.That(
                LastSent(transport),
                Is.EqualTo(
                    GoldenFixtures.ReadFirstLineOfType("v2-client-messages.jsonl", "JoinRoom")
                )
            );
            Assert.That(client.PendingOperation, Is.EqualTo(PendingRoomOperation.JoinPlayer));

            /*
                The typed result releases the fence; a generic server error
                would leave it armed (fail-closed).
            */
            EnqueueGolden(transport, "RoomJoined");
            Assert.That(client.Poll(), Is.EqualTo(1));
            Assert.That(client.PendingOperation, Is.EqualTo(default(PendingRoomOperation)));
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.InRoom));

            CommandSend refused = client.SendJoinRoom(new JoinRoomMessage("my-game", "Player1"));
            Assert.That(refused.Accepted, Is.False);
            Assert.That(refused.Refusal, Is.EqualTo(AdmissionError.AlreadyInRoom));
        }

        [Test]
        public async Task DirectedOperationsRequireConfirmedAuthentication()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await client.ConnectAsync(Endpoint());
            DrainAll(client);

            CommandSend join = client.SendJoinRoom(new JoinRoomMessage("my-game", "Player1"));
            Assert.That(join.Accepted, Is.False);
            Assert.That(join.Refusal, Is.EqualTo(AdmissionError.NotAuthenticated));

            CommandSend reconnect = client.SendReconnect(
                new ReconnectMessage(PlayerId.ToString(), RoomId.ToString(), "sf_rt_example_token")
            );
            Assert.That(reconnect.Accepted, Is.False);
            Assert.That(reconnect.Refusal, Is.EqualTo(AdmissionError.NotAuthenticated));
            Assert.That(transport.SentText, Is.Empty);
        }

        [Test]
        public async Task ArmedFenceBlocksSecondDirectedOperation()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndAuthenticate(client, transport);

            Assert.That(
                client.SendJoinRoom(new JoinRoomMessage("my-game", "Player1")).Accepted,
                Is.True
            );
            CommandSend second = client.SendLeaveRoom();
            Assert.That(second.Accepted, Is.False);
            Assert.That(second.Refusal, Is.EqualTo(AdmissionError.RoomOperationPending));
            Assert.That(LastSent(transport), Does.Contain("JoinRoom"));
        }

        [Test]
        public async Task JoinRoomEncodeMisuseThrowsAndArmsNothing()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndAuthenticate(client, transport);

            Assert.Throws<ArgumentException>(
                (Action)(() => client.SendJoinRoom(new JoinRoomMessage(null!, "Player1")))
            );
            Assert.That(client.PendingOperation, Is.EqualTo(default(PendingRoomOperation)));
            Assert.That(transport.SentText, Is.Empty);
        }

        [Test]
        public async Task RoomCommandsSendGoldenWireByRole()
        {
            (SignalFishPollingClient playerClient, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndAuthenticate(playerClient, transport);

            EnqueueGolden(transport, "RoomJoined");
            Assert.That(playerClient.Poll(), Is.EqualTo(1));
            DrainAll(playerClient);

            CommandSend ready = playerClient.SendPlayerReady();
            Assert.That(ready.Accepted, Is.True);
            Assert.That(
                LastSent(transport),
                Is.EqualTo(
                    GoldenFixtures.ReadFirstLineOfType("v2-client-messages.jsonl", "PlayerReady")
                )
            );
            Assert.That(
                playerClient.PendingOperation,
                Is.EqualTo(default(PendingRoomOperation)),
                "payload-less in-room commands never fence"
            );

            Assert.That(playerClient.SendStartGame().Accepted, Is.True);
            Assert.That(
                LastSent(transport),
                Is.EqualTo(
                    GoldenFixtures.ReadFirstLineOfType("v2-client-messages.jsonl", "StartGame")
                )
            );

            Assert.That(playerClient.SendLeaveRoom().Accepted, Is.True);
            Assert.That(
                LastSent(transport),
                Is.EqualTo(
                    GoldenFixtures.ReadFirstLineOfType("v2-client-messages.jsonl", "LeaveRoom")
                )
            );
            Assert.That(
                playerClient.PendingOperation,
                Is.EqualTo(PendingRoomOperation.LeavePlayer)
            );

            EnqueueGolden(transport, "RoomLeft");
            Assert.That(playerClient.Poll(), Is.EqualTo(1));
            Assert.That(playerClient.PendingOperation, Is.EqualTo(default(PendingRoomOperation)));
        }

        [Test]
        public async Task AuthorityRequestSendsGoldenWireAndTracksTheSeat()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndAuthenticate(client, transport);
            JoinGoldenRoom(client, transport);
            Assert.That(
                client.Snapshot.IsAuthority,
                Is.True,
                "the golden baseline holds authority"
            );

            CommandSend relinquish = client.SendAuthorityRequest(becomeAuthority: false);
            Assert.That(relinquish.Accepted, Is.True);
            Assert.That(
                LastSent(transport),
                Is.EqualTo(
                    @"{""type"": ""AuthorityRequest"", ""data"": {""become_authority"": false}}"
                ),
                "relinquish carries become_authority: false"
            );

            /*
                A rival claims the seat; the broadcast is the tracking source
                of truth and the snapshot mirrors it.
            */
            EnqueueGolden(transport, "AuthorityChanged");
            Assert.That(client.Poll(), Is.EqualTo(1));
            DrainAll(client);
            Assert.That(client.Snapshot.IsAuthority, Is.False);

            Assert.That(client.SendAuthorityRequest(false).Accepted, Is.False);
            Assert.That(
                client.SendAuthorityRequest(false).Refusal,
                Is.EqualTo(AdmissionError.AuthorityRequired)
            );
        }

        [Test]
        public async Task AuthorityRequestRefusalsNeverTouchTheWire()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndAuthenticate(client, transport);
            int sentBefore = transport.SentText.Count;

            CommandSend outsideRoom = client.SendAuthorityRequest(true);
            Assert.That(outsideRoom.Refusal, Is.EqualTo(AdmissionError.NotInRoom));

            EnqueueGolden(transport, "SpectatorJoined");
            Assert.That(client.Poll(), Is.EqualTo(1));
            DrainAll(client);

            CommandSend spectator = client.SendAuthorityRequest(true);
            Assert.That(spectator.Refusal, Is.EqualTo(AdmissionError.WrongRoomRole));

            EnqueueWire(
                transport,
                @"{""type"":""RoomJoined"",""data"":{""room_id"":""7c9e6679-7425-40de-944b-e07fc1f90ae7"","
                    + @"""room_code"":""ABC123"",""player_id"":""0f8fad5b-d9cb-469f-a165-70867728950e"","
                    + @"""game_name"":""my-game"",""max_players"":8,""supports_authority"":true,"
                    + @"""current_players"":[],""is_authority"":false,""lobby_state"":""lobby"","
                    + @"""ready_players"":[],""relay_type"":""relay"",""current_spectators"":[]}}"
            );
            Assert.That(client.Poll(), Is.EqualTo(1));
            DrainAll(client);

            CommandSend nonAuthority = client.SendAuthorityRequest(false);
            Assert.That(nonAuthority.Refusal, Is.EqualTo(AdmissionError.AuthorityRequired));

            Assert.That(
                transport.SentText.Count,
                Is.EqualTo(sentBefore),
                "refused commands never reach the wire"
            );

            /*
                The claim form is still admitted for this seat; the wire
                bytes match the golden claim frame.
            */
            CommandSend claim = client.SendAuthorityRequest(true);
            Assert.That(claim.Accepted, Is.True);
            Assert.That(
                LastSent(transport),
                Is.EqualTo(
                    GoldenFixtures.ReadFirstLineOfType(
                        "v2-client-messages.jsonl",
                        "AuthorityRequest"
                    )
                )
            );
        }

        [Test]
        public async Task SpectatorSeatRefusesPlayerCommandsAndLeavesAsSpectator()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndAuthenticate(client, transport);

            EnqueueGolden(transport, "SpectatorJoined");
            Assert.That(client.Poll(), Is.EqualTo(1));
            DrainAll(client);

            CommandSend ready = client.SendPlayerReady();
            Assert.That(ready.Accepted, Is.False);
            Assert.That(ready.Refusal, Is.EqualTo(AdmissionError.WrongRoomRole));

            CommandSend leave = client.SendLeaveRoom();
            Assert.That(leave.Accepted, Is.False);
            Assert.That(leave.Refusal, Is.EqualTo(AdmissionError.WrongRoomRole));

            CommandSend spectatorLeave = client.SendLeaveSpectator();
            Assert.That(spectatorLeave.Accepted, Is.True);
            Assert.That(
                LastSent(transport),
                Is.EqualTo(
                    GoldenFixtures.ReadFirstLineOfType("v2-client-messages.jsonl", "LeaveSpectator")
                )
            );
            Assert.That(client.PendingOperation, Is.EqualTo(PendingRoomOperation.LeaveSpectator));

            EnqueueGolden(transport, "SpectatorLeft");
            Assert.That(client.Poll(), Is.EqualTo(1));
            Assert.That(client.PendingOperation, Is.EqualTo(default(PendingRoomOperation)));
        }

        [Test]
        public async Task GameDataSendsGoldenReliableWire()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndAuthenticate(client, transport);
            JoinGoldenRoom(client, transport);

            byte[] payload = Encoding.UTF8.GetBytes(@"{""action"": ""move"", ""x"": 10}");
            CommandSend send = client.SendGameData(new GameDataMessage(payload));
            Assert.That(send.Accepted, Is.True);
            Assert.That(
                LastSent(transport),
                Is.EqualTo(
                    GoldenFixtures.ReadFirstLineOfType("v2-client-messages.jsonl", "GameData")
                )
            );
            Assert.That(
                client.PendingOperation,
                Is.EqualTo(default(PendingRoomOperation)),
                "relay traffic never fences"
            );
        }

        [Test]
        public async Task ClassifiedGameDataSendsGoldenV3Wire()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndAuthenticate(client, transport);
            JoinGoldenRoom(client, transport);
            NegotiateV3(client, transport);

            byte[] position = Encoding.UTF8.GetBytes("{\"position\": {\"x\": 12, \"y\": 34}}");
            CommandSend latest = client.SendGameData(
                new GameDataMessage(position, GameDataClass.Latest, key: 7)
            );
            Assert.That(latest.Accepted, Is.True);
            Assert.That(LastSent(transport), Is.EqualTo(ReadV3ClientGameData("latest")));

            byte[] effect = Encoding.UTF8.GetBytes("{\"effect\": \"footstep\"}");
            CommandSend volatileSend = client.SendGameData(
                new GameDataMessage(effect, GameDataClass.Volatile)
            );
            Assert.That(volatileSend.Accepted, Is.True);
            Assert.That(LastSent(transport), Is.EqualTo(ReadV3ClientGameData("volatile")));
        }

        [Test]
        public async Task ClassifiedGameDataWithoutNegotiatedV3IsRefused()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndAuthenticate(client, transport);
            JoinGoldenRoom(client, transport);

            byte[] payload = Encoding.UTF8.GetBytes("{\"tick\": 1}");
            Assert.That(
                client.SendGameData(new GameDataMessage(payload, GameDataClass.Latest)).Accepted,
                Is.False,
                "classified delivery needs a negotiated v3 connection"
            );
            Assert.That(
                client.SendGameData(new GameDataMessage(payload, GameDataClass.Volatile)).Accepted,
                Is.False
            );

            NegotiateV3(client, transport);
            Assert.That(
                client.SendGameData(new GameDataMessage(payload, GameDataClass.Latest)).Accepted,
                Is.True
            );
        }

        [Test]
        public async Task JoinAsSpectatorSendsGoldenWireAndArmsSpectatorFence()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndAuthenticate(client, transport);

            CommandSend send = client.SendJoinAsSpectator(
                new JoinAsSpectatorMessage("my-game", "ABC123", "Observer")
            );
            Assert.That(send.Accepted, Is.True);
            Assert.That(
                LastSent(transport),
                Is.EqualTo(
                    GoldenFixtures.ReadFirstLineOfType(
                        "v2-client-messages.jsonl",
                        "JoinAsSpectator"
                    )
                )
            );
            Assert.That(client.PendingOperation, Is.EqualTo(PendingRoomOperation.JoinSpectator));
        }

        [Test]
        public async Task ReconnectSendsGoldenWireAndArmsReconnectFence()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndAuthenticate(client, transport);

            CommandSend send = client.SendReconnect(
                new ReconnectMessage(PlayerId.ToString(), RoomId.ToString(), "sf_rt_example_token")
            );
            Assert.That(send.Accepted, Is.True);
            Assert.That(
                LastSent(transport),
                Is.EqualTo(
                    GoldenFixtures.ReadFirstLineOfType("v2-client-messages.jsonl", "Reconnect")
                )
            );
            Assert.That(client.PendingOperation, Is.EqualTo(PendingRoomOperation.ReconnectPlayer));
        }

        [Test]
        public async Task AuthenticateSendsGoldenWireBeforeAnyMembership()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await client.ConnectAsync(Endpoint());
            DrainAll(client);

            CommandSend send = client.SendAuthenticate(
                new AuthenticateMessage(
                    appId: "mb_app_abc123",
                    sdkVersion: "1.2.3",
                    platform: "unity"
                )
            );
            Assert.That(send.Accepted, Is.True);
            Assert.That(
                LastSent(transport),
                Is.EqualTo(
                    GoldenFixtures.ReadFirstLineOfType("v2-client-messages.jsonl", "Authenticate")
                )
            );
            Assert.That(client.PendingOperation, Is.EqualTo(default(PendingRoomOperation)));
        }

        [Test]
        public async Task SendFailureFoldsIntoTheNextPoll()
        {
            (SignalFishPollingClient client, FakeTransport transport, VirtualClock _) =
                BuildTimed();
            await ConnectAndAuthenticate(client, transport);
            JoinGoldenRoom(client, transport);

            /*
                The fake is closed without a close frame: the send throws,
                the failure folds back, and the next poll tears down.
            */
            transport.EnqueueClose(4000);
            Assert.That(client.SendPlayerReady().Accepted, Is.True);
            Assert.That(client.Poll(), Is.EqualTo(0));
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.Terminal));
            PollEvent pollEvent = Single(client);
            Assert.That(pollEvent.Kind, Is.EqualTo(PollEventKind.Disconnected));
            Assert.That(pollEvent.Close.Code, Is.EqualTo(1006));
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

        private static async Task ConnectAndAuthenticate(
            SignalFishPollingClient client,
            FakeTransport transport
        )
        {
            await client.ConnectAsync(Endpoint());
            DrainAll(client);
            EnqueueGolden(transport, "Authenticated");
            Assert.That(client.Poll(), Is.EqualTo(1));
            DrainAll(client);
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.Authenticated));
        }

        private static void JoinGoldenRoom(SignalFishPollingClient client, FakeTransport transport)
        {
            EnqueueGolden(transport, "RoomJoined");
            Assert.That(client.Poll(), Is.EqualTo(1));
            DrainAll(client);
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

        private static string LastSent(FakeTransport transport)
        {
            Assert.That(transport.SentText, Is.Not.Empty, "the command must reach the wire");
            return transport.SentText[transport.SentText.Count - 1];
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

        /// <summary>Feeds the golden v3 ProtocolInfo so the machine negotiates v3.</summary>
        private static void NegotiateV3(SignalFishPollingClient client, FakeTransport transport)
        {
            EnqueueWire(
                transport,
                GoldenFixtures.ReadFirstLineOfType("v3-server-messages.jsonl", "ProtocolInfo")
            );
            Assert.That(client.Poll(), Is.GreaterThanOrEqualTo(1));
        }

        /// <summary>
        /// The v3 client fixture's GameData line for the given class token,
        /// selected by meaning so the test survives upstream reordering.
        /// </summary>
        private static string ReadV3ClientGameData(string classToken)
        {
            foreach (
                string line in File.ReadAllLines(
                    GoldenFixtures.GoldenDirectory + "/v3-client-messages.jsonl"
                )
            )
            {
                using JsonDocument document = JsonDocument.Parse(line);
                if (
                    document.RootElement.GetProperty("type").GetString() == "GameData"
                    && document.RootElement.GetProperty("data").GetProperty("class").GetString()
                        == classToken
                )
                {
                    return line;
                }
            }

            Assert.Fail($"v3-client-messages.jsonl has no GameData sample of class {classToken}.");
            return string.Empty;
        }
    }
}
