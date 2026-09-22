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
    }
}
