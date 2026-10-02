namespace SignalFish.Client.Tests.Async
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using NUnit.Framework;
    using SignalFish.Client.Async;
    using SignalFish.Client.Core;
    using SignalFish.Client.Polling;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.Reconnection;
    using SignalFish.Client.Tests.Core;
    using SignalFish.Client.Tests.Transport;

    /// <summary>
    /// Red-green anchors for the M4.3 staged graceful shutdown and the
    /// M4.4 manual seat-recovery procedure: disposing in a room sends the
    /// role's leave first and closes after the typed confirmation (with a
    /// bounded abort fallback when the server never answers, and an
    /// immediate teardown at a zero budget), and the documented recovery
    /// flow — persist the seat triple, fresh client, authenticate,
    /// reconnect — restores the seat with a rotated token.
    /// </summary>
    [TestFixture]
    public class SignalFishClientShutdownTests
    {
        private static readonly Guid GoldenPlayerId = new Guid(
            "00000000-0000-0000-0000-00000000000a"
        );

        private static readonly Guid GoldenRoomId = new Guid(
            "11111111-1111-1111-1111-111111111111"
        );

        [TestCase(RoomRole.Player, "{\"type\": \"LeaveRoom\"}", PollEventKind.RoomLeft)]
        [TestCase(
            RoomRole.Spectator,
            "{\"type\": \"LeaveSpectator\"}",
            PollEventKind.SpectatorLeft
        )]
        public async Task DisposeInRoomSendsTheLeaveAndClosesOnConfirmation(
            RoomRole role,
            string expectedLeaveFrame,
            PollEventKind confirmationKind
        )
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed(
                new SignalFishClientOptions(shutdownTimeoutMilliseconds: 30_000)
            );
            await ConnectInRoomAsync(client, transport, role);

            ValueTask dispose = client.DisposeAsync();
            await WaitForAsync(
                () => Contains(transport.SentText, expectedLeaveFrame),
                "the graceful shutdown sends the role's leave first"
            );

            EnqueueGolden(
                transport,
                confirmationKind == PollEventKind.RoomLeft ? "RoomLeft" : "SpectatorLeft"
            );

            PollEvent confirmation = await NextEventAsync(client);
            Assert.That(confirmation.Kind, Is.EqualTo(confirmationKind));

            PollEvent disconnected = await NextEventAsync(client);
            Assert.That(disconnected.Kind, Is.EqualTo(PollEventKind.Disconnected));
            Assert.That(disconnected.Close.Code, Is.EqualTo(0));

            await dispose;
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.Terminal));
            Assert.That(client.Membership.IsPresent, Is.False);

            PollEvent? endOfStream = await client.DequeueEventAsync();
            Assert.That(endOfStream, Is.Null, "the terminal event is delivered exactly once");
        }

        [Test]
        public async Task DisposeWithoutServerAnswerAbortsAtTheShutdownBudget()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed(
                new SignalFishClientOptions(shutdownTimeoutMilliseconds: 100)
            );
            await ConnectJoinRoomAsync(client, transport);

            /*
                A stalled wire: the leave is recorded but never completes,
                so no confirmation can arrive. The bounded shutdown must
                still finish — abort fallback, terminal event delivered.
            */
            TaskCompletionSource<bool> wire = new TaskCompletionSource<bool>();
            transport.HoldSendsUntil(wire);

            Stopwatch elapsed = Stopwatch.StartNew();
            await client.DisposeAsync();
            elapsed.Stop();

            Assert.That(
                elapsed.ElapsedMilliseconds,
                Is.LessThan(5_000),
                "the shutdown budget bounds DisposeAsync even on a dead wire"
            );
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.Terminal));

            PollEvent disconnected = await NextEventAsync(client);
            Assert.That(disconnected.Kind, Is.EqualTo(PollEventKind.Disconnected));
            Assert.That(disconnected.Close.Code, Is.EqualTo(0));

            PollEvent? endOfStream = await client.DequeueEventAsync();
            Assert.That(endOfStream, Is.Null);
        }

        [Test]
        public async Task IdleLoopClosesItselfAtTheVirtualShutdownDeadline()
        {
            /*
                The real-time fallback in DisposeAsync and the loop-side
                budget back each other up; this pins the loop side alone.
                The heartbeat park would fire at 60 s, so with a 30 s
                budget only the capped park can wake the loop when the
                clock advances 30 s — and the 30 s REAL fallback cannot
                fire inside the 10 s event timeout either.
            */
            (SignalFishClient client, FakeTransport transport, VirtualClock clock) = BuildTimed(
                new SignalFishClientOptions(
                    heartbeatIntervalMilliseconds: 60_000,
                    heartbeatTimeoutMilliseconds: 120_000,
                    shutdownTimeoutMilliseconds: 30_000
                )
            );
            await ConnectJoinRoomAsync(client, transport);

            ValueTask dispose = client.DisposeAsync();
            await WaitForAsync(
                () => Contains(transport.SentText, "{\"type\": \"LeaveRoom\"}"),
                "the leave stage is in flight"
            );

            Stopwatch elapsed = Stopwatch.StartNew();
            clock.Advance(30_000);

            PollEvent disconnected = await NextEventAsync(client);
            elapsed.Stop();
            Assert.That(disconnected.Kind, Is.EqualTo(PollEventKind.Disconnected));
            Assert.That(disconnected.Close.Code, Is.EqualTo(0));
            Assert.That(
                elapsed.ElapsedMilliseconds,
                Is.LessThan(5_000),
                "the loop-side deadline ended the session without the real-time fallback"
            );

            await dispose;
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.Terminal));
        }

        [Test]
        public async Task DisposeWithZeroBudgetSkipsTheLeaveStage()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed(
                new SignalFishClientOptions(shutdownTimeoutMilliseconds: 0)
            );
            await ConnectJoinRoomAsync(client, transport);

            await client.DisposeAsync();

            Assert.That(
                Contains(transport.SentText, "{\"type\": \"LeaveRoom\"}"),
                Is.False,
                "a zero budget aborts immediately without the leave stage"
            );
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.Terminal));

            PollEvent disconnected = await NextEventAsync(client);
            Assert.That(disconnected.Kind, Is.EqualTo(PollEventKind.Disconnected));
        }

        [Test]
        public async Task DisposeWithAPendingLeaveSkipsTheStageAndClosesPromptly()
        {
            /*
                The app's own leave is in flight (fenced): staging a second
                one would clobber the armed fence — disposal must skip the
                stage and end the session without wedging the machine.
            */
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed(
                new SignalFishClientOptions(shutdownTimeoutMilliseconds: 5_000)
            );
            await ConnectJoinRoomAsync(client, transport);
            Assert.That(client.SendLeaveRoom().Accepted, Is.True);
            Assert.That(client.PendingOperation, Is.EqualTo(PendingRoomOperation.LeavePlayer));

            await client.DisposeAsync();

            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.Terminal));
            Assert.That(
                client.PendingOperation,
                Is.EqualTo(default(PendingRoomOperation)),
                "teardown released the app's own leave fence"
            );

            PollEvent disconnected = await NextEventAsync(client);
            Assert.That(disconnected.Kind, Is.EqualTo(PollEventKind.Disconnected));
            Assert.That(disconnected.Close.Code, Is.EqualTo(0));

            PollEvent? endOfStream = await client.DequeueEventAsync();
            Assert.That(endOfStream, Is.Null);
        }

        [Test]
        public async Task DisposeWithAFullCommandQueueSkipsTheStageAndClosesPromptly()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed(
                new SignalFishClientOptions(commandCapacity: 1, shutdownTimeoutMilliseconds: 5_000)
            );
            await ConnectJoinRoomAsync(client, transport);

            TaskCompletionSource<bool> wire = new TaskCompletionSource<bool>();
            transport.HoldSendsUntil(wire);
            Assert.That(client.SendGameData(Payload(0)).Accepted, Is.True);
            await WaitForAsync(() => transport.SentText.Count >= 3, "the relay is parked mid-send");
            Assert.That(client.SendGameData(Payload(1)).Accepted, Is.True);

            await client.DisposeAsync();

            Assert.That(
                Contains(transport.SentText, "{\"type\": \"LeaveRoom\"}"),
                Is.False,
                "a full command queue skips the leave stage"
            );
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.Terminal));

            PollEvent disconnected = await NextEventAsync(client);
            Assert.That(disconnected.Kind, Is.EqualTo(PollEventKind.Disconnected));
        }

        [Test]
        public async Task DisposeOutsideRoomClosesImmediatelyWithoutLeave()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed(
                new SignalFishClientOptions(shutdownTimeoutMilliseconds: 5_000)
            );
            await ConnectSettledAsync(client);

            await client.DisposeAsync();

            Assert.That(Contains(transport.SentText, "{\"type\": \"LeaveRoom\"}"), Is.False);
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.Terminal));

            PollEvent disconnected = await NextEventAsync(client);
            Assert.That(disconnected.Kind, Is.EqualTo(PollEventKind.Disconnected));
            Assert.That(disconnected.Close.Code, Is.EqualTo(0));
        }

        [Test]
        public async Task SendsRefuseDuringTheGracefulWindow()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed(
                new SignalFishClientOptions(shutdownTimeoutMilliseconds: 5_000)
            );
            await ConnectJoinRoomAsync(client, transport);

            ValueTask dispose = client.DisposeAsync();
            await WaitForAsync(
                () => Contains(transport.SentText, "{\"type\": \"LeaveRoom\"}"),
                "the leave stage is in flight"
            );

            /*
                Post-dispose semantics hold the moment disposal starts —
                even while the graceful leave is still on the wire.
            */
            Assert.Throws<ObjectDisposedException>((Action)(() => client.SendGameData(Payload(0))));
            Assert.ThrowsAsync<ObjectDisposedException>(
                (Func<Task>)(async () => await client.SendGameDataReliableAsync(Payload(1)))
            );

            EnqueueGolden(transport, "RoomLeft");
            await dispose;
        }

        [Test]
        public async Task ManualSeatRecoveryRestoresTheSeatWithARotatedToken()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed();
            await ConnectJoinRoomAsync(
                client,
                transport,
                joined => ReconnectContext.TryCapture(joined, out _),
                "the first baseline must carry a capturable seat"
            );
            ReconnectContext.TryCapture(client.Snapshot, out ReconnectContext seat);
            Assert.That(seat.PlayerId, Is.EqualTo(GoldenPlayerId));
            Assert.That(seat.RoomId, Is.EqualTo(GoldenRoomId));
            Assert.That(seat.Token, Is.EqualTo("seat-token-1"));

            // The connection dies unexpectedly (server-typed activity close).
            transport.EnqueueClose(4003);
            transport.Abort();
            PollEvent death = await NextEventAsync(client);
            Assert.That(death.Kind, Is.EqualTo(PollEventKind.Disconnected));
            Assert.That(death.Close.Code, Is.EqualTo(4003));
            await client.DisposeAsync();

            /*
                Fresh transport + client, fresh handshake, then the persisted
                triple reclaims the seat; the rotated token replaces the old.
            */
            (SignalFishClient recovery, FakeTransport wire, VirtualClock _) = BuildTimed(
                new SignalFishClientOptions(shutdownTimeoutMilliseconds: 0)
            );
            await ConnectSettledAsync(recovery);
            Assert.That(recovery.IsAuthenticated, Is.False);

            Assert.That(recovery.SendAuthenticate(new AuthenticateMessage()).Accepted, Is.True);
            EnqueueGolden(wire, "Authenticated");
            Assert.That(
                (await NextEventAsync(recovery)).Kind,
                Is.EqualTo(PollEventKind.Authenticated)
            );

            Assert.That(
                recovery
                    .SendReconnect(
                        new ReconnectMessage(
                            seat.PlayerId.ToString(),
                            seat.RoomId.ToString(),
                            seat.Token
                        )
                    )
                    .Accepted,
                Is.True
            );
            await WaitForAsync(
                () => Contains(wire.SentText, ExpectedReconnectFrame(seat)),
                "the reconnect command carries the persisted triple"
            );

            wire.Enqueue(Encoding.UTF8.GetBytes(ReconnectedFrame("seat-token-2")), isText: true);
            PollEvent reconnected = await NextEventAsync(recovery);
            Assert.That(reconnected.Kind, Is.EqualTo(PollEventKind.Reconnected));
            Assert.That(recovery.Phase, Is.EqualTo(ConnectionPhase.InRoom));
            Assert.That(recovery.Membership.PlayerId, Is.EqualTo(GoldenPlayerId));
            Assert.That(recovery.Snapshot.ReconnectionToken, Is.EqualTo("seat-token-2"));

            Assert.That(
                ReconnectContext.TryCapture(recovery.Snapshot, out ReconnectContext rotated),
                Is.True
            );
            Assert.That(rotated.Token, Is.EqualTo("seat-token-2"), "the token rotated");
            await recovery.DisposeAsync();
        }

        private static string ExpectedReconnectFrame(ReconnectContext seat)
        {
            return "{\"type\": \"Reconnect\", \"data\": {\"player_id\": \""
                + seat.PlayerId.ToString()
                + "\", \"room_id\": \""
                + seat.RoomId.ToString()
                + "\", \"auth_token\": \""
                + seat.Token
                + "\"}}";
        }

        /// <summary>The golden player baseline with a reconnection token injected.</summary>
        private static string JoinedFrameWithToken(string token)
        {
            string golden = GoldenFixtures.ReadFirstLineOfType(
                "v2-server-messages.jsonl",
                "RoomJoined"
            );
            return golden.Replace(
                "{\"type\": \"RoomJoined\", \"data\": {",
                "{\"type\": \"RoomJoined\", \"data\": {\"reconnection_token\": \"" + token + "\", ",
                StringComparison.Ordinal
            );
        }

        /// <summary>The golden Reconnected baseline with the rotated token injected.</summary>
        private static string ReconnectedFrame(string token)
        {
            string golden = GoldenFixtures.ReadFirstLineOfType(
                "v2-server-messages.jsonl",
                "Reconnected"
            );
            return golden.Replace(
                "{\"type\": \"Reconnected\", \"data\": {",
                "{\"type\": \"Reconnected\", \"data\": {\"reconnection_token\": \""
                    + token
                    + "\", ",
                StringComparison.Ordinal
            );
        }

        private static GameDataMessage Payload(int index)
        {
            return new GameDataMessage(Encoding.UTF8.GetBytes("{\"n\": " + index + "}"));
        }

        private static bool Contains(IReadOnlyList<string> lines, string expected)
        {
            foreach (string line in lines)
            {
                if (line == expected)
                {
                    return true;
                }
            }

            return false;
        }

        private static async Task<PollEvent> NextEventAsync(SignalFishClient client)
        {
            using CancellationTokenSource timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(10)
            );
            PollEvent? pollEvent = await client.DequeueEventAsync(timeout.Token);
            Assert.That(pollEvent, Is.Not.Null, "expected an event before the 10 s timeout");
            return pollEvent.GetValueOrDefault();
        }

        private static async Task WaitForAsync(Func<bool> done, string because)
        {
            Stopwatch deadline = Stopwatch.StartNew();
            while (!done() && deadline.ElapsedMilliseconds < 10_000)
            {
                await Task.Delay(1);
            }

            Assert.That(done, Is.True, because);
        }

        private static (
            SignalFishClient Client,
            FakeTransport Transport,
            VirtualClock Clock
        ) BuildTimed(SignalFishClientOptions? options = null)
        {
            FakeTransport transport = new FakeTransport();
            VirtualClock clock = new VirtualClock();
            SignalFishClient client = new SignalFishClient(transport, clock, options);
            return (client, transport, clock);
        }

        private static Uri Endpoint()
        {
            return new Uri("ws://localhost:8080/v2/ws");
        }

        private static void EnqueueGolden(FakeTransport transport, params string[] wireTypes)
        {
            foreach (string wireType in wireTypes)
            {
                transport.Enqueue(
                    Encoding.UTF8.GetBytes(
                        GoldenFixtures.ReadFirstLineOfType("v2-server-messages.jsonl", wireType)
                    ),
                    isText: true
                );
            }
        }

        /// <summary>Connects and consumes the transport-ready event.</summary>
        private static async Task ConnectSettledAsync(SignalFishClient client)
        {
            await client.ConnectAsync(Endpoint());
            Assert.That(
                (await NextEventAsync(client)).Kind,
                Is.EqualTo(PollEventKind.TransportReady)
            );
        }

        /// <summary>
        /// Connects, authenticates, and completes the join fence over a
        /// token-carrying baseline; the snapshot is handed to
        /// <paramref name="verify"/> once membership is confirmed.
        /// </summary>
        private static async Task ConnectJoinRoomAsync(
            SignalFishClient client,
            FakeTransport transport,
            Func<ClientSnapshot, bool>? verify = null,
            string? because = null
        )
        {
            await ConnectSettledAsync(client);

            Assert.That(client.SendAuthenticate(new AuthenticateMessage()).Accepted, Is.True);
            EnqueueGolden(transport, "Authenticated");
            Assert.That(
                (await NextEventAsync(client)).Kind,
                Is.EqualTo(PollEventKind.Authenticated)
            );

            Assert.That(
                client
                    .SendJoinRoom(new JoinRoomMessage("my-game", "P1", "ABC123", 8, true))
                    .Accepted,
                Is.True
            );
            transport.Enqueue(
                Encoding.UTF8.GetBytes(JoinedFrameWithToken("seat-token-1")),
                isText: true
            );
            Assert.That((await NextEventAsync(client)).Kind, Is.EqualTo(PollEventKind.RoomJoined));
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.InRoom));
            if (verify is not null)
            {
                Assert.That(verify(client.Snapshot), Is.True, because);
            }
        }

        /// <summary>
        /// Connects and reaches a confirmed membership as a player or
        /// spectator (the spectated join uses the golden spectator flow).
        /// </summary>
        private static async Task ConnectInRoomAsync(
            SignalFishClient client,
            FakeTransport transport,
            RoomRole role
        )
        {
            if (role == RoomRole.Player)
            {
                await ConnectJoinRoomAsync(client, transport);
                return;
            }

            await ConnectSettledAsync(client);
            Assert.That(client.SendAuthenticate(new AuthenticateMessage()).Accepted, Is.True);
            EnqueueGolden(transport, "Authenticated");
            Assert.That(
                (await NextEventAsync(client)).Kind,
                Is.EqualTo(PollEventKind.Authenticated)
            );

            Assert.That(
                client
                    .SendJoinAsSpectator(new JoinAsSpectatorMessage("my-game", "ABC123", "Obs"))
                    .Accepted,
                Is.True
            );
            EnqueueGolden(transport, "SpectatorJoined");
            Assert.That(
                (await NextEventAsync(client)).Kind,
                Is.EqualTo(PollEventKind.SpectatorJoined)
            );
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.InRoom));
            Assert.That(client.Membership.Role, Is.EqualTo(RoomRole.Spectator));
        }
    }
}
