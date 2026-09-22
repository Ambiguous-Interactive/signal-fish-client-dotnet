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
    using SignalFish.Client.Transport;

    /// <summary>
    /// Red-green anchors for the M4.5 opt-in reconnect policy, mirroring
    /// the Rust client's automatic-reconnection semantics: retryable
    /// edges reconnect with deterministic backoff and
    /// <c>Reconnecting</c>/<c>ReconnectAbandoned</c> markers, the fresh
    /// connection re-authenticates and reclaims a retained player seat,
    /// the attempt budget resets on <c>Authenticated</c>, classified
    /// terminal closes end the session, a voluntary leave discards the
    /// seat context, and queued commands of a dead connection never reach
    /// the new wire.
    /// </summary>
    [TestFixture]
    public class SignalFishClientReconnectTests
    {
        private static readonly Guid GoldenPlayerId = new Guid(
            "00000000-0000-0000-0000-00000000000a"
        );

        private static readonly Guid GoldenRoomId = new Guid(
            "11111111-1111-1111-1111-111111111111"
        );

        private VirtualClock _clock = new VirtualClock();

        [Test]
        public async Task UnexpectedCloseReconnectsAuthenticatesAndReclaimsTheSeat()
        {
            TransportFactory factory = new TransportFactory();
            FakeTransport first = factory.Create();
            SignalFishClient client = BuildPolicyClient(
                first,
                new ReconnectPolicy(
                    factory.Create,
                    initialBackoffMilliseconds: 50,
                    maxBackoffMilliseconds: 50
                )
            );
            await ConnectJoinRoomAsync(client, first);

            first.FailPendingReceive(4003);

            PollEvent disconnected = await NextEventAsync(client);
            Assert.That(disconnected.Kind, Is.EqualTo(PollEventKind.Disconnected));
            Assert.That(disconnected.Close.Code, Is.EqualTo(4003));

            PollEvent reconnecting = await NextEventAsync(client);
            Assert.That(reconnecting.Kind, Is.EqualTo(PollEventKind.Reconnecting));
            Assert.That(reconnecting.Reconnect.Attempt, Is.EqualTo(1));
            Assert.That(reconnecting.Reconnect.BackoffMilliseconds, Is.EqualTo(50));

            /*
                The session is not terminal while it reconnects: the phase
                is the next attempt's connecting phase, so phase-gated
                consumers keep draining.
            */
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.Connecting));
            Assert.That(client.IsConnected, Is.True);
            Assert.That(client.Snapshot.Connected, Is.True);
            Assert.That(client.Snapshot.Role, Is.Null);

            await AdvanceUntilAsync(
                client,
                () => factory.Last is { IsConnected: true },
                "the policy opens a fresh transport after the backoff"
            );
            FakeTransport second = factory.Last!;
            Assert.That(ReferenceEquals(second, first), Is.False);

            PollEvent transportReady = await NextEventAsync(client);
            Assert.That(transportReady.Kind, Is.EqualTo(PollEventKind.TransportReady));
            Assert.That(
                client.Phase,
                Is.EqualTo(ConnectionPhase.TransportReady),
                "the fresh connection starts from a clean machine"
            );

            await WaitForAsync(
                () =>
                    second.SentText.Count >= 1
                    && second.SentText[0] == "{\"type\": \"Authenticate\"}",
                "the driver re-authenticates the fresh connection"
            );
            EnqueueGolden(second, "Authenticated");
            Assert.That(
                (await NextEventAsync(client)).Kind,
                Is.EqualTo(PollEventKind.Authenticated)
            );

            await WaitForAsync(
                () => second.SentText.Count >= 2,
                "the retained seat is reclaimed automatically"
            );
            Assert.That(
                second.SentText[1],
                Is.EqualTo(ExpectedReconnectFrame("seat-token-1")),
                "the automatic reconnect carries the persisted triple"
            );

            second.Enqueue(Encoding.UTF8.GetBytes(ReconnectedFrame("seat-token-2")), isText: true);
            PollEvent reconnected = await NextEventAsync(client);
            Assert.That(reconnected.Kind, Is.EqualTo(PollEventKind.Reconnected));
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.InRoom));
            Assert.That(client.Membership.PlayerId, Is.EqualTo(GoldenPlayerId));
            Assert.That(client.Snapshot.ReconnectionToken, Is.EqualTo("seat-token-2"));

            /*
                Disposing the healthy recovered session reports a clean
                close — the previous death's code must not leak into it.
            */
            await client.DisposeAsync();
            PollEvent terminal = await NextEventAsync(client);
            Assert.That(terminal.Kind, Is.EqualTo(PollEventKind.Disconnected));
            Assert.That(terminal.Close.Code, Is.EqualTo(0));
            Assert.That(await client.DequeueEventAsync(), Is.Null);
        }

        [Test]
        public async Task AttemptBudgetExhaustionEmitsReconnectAbandonedAndEndsTheStream()
        {
            TransportFactory factory = new TransportFactory();
            FakeTransport first = factory.Create();
            SignalFishClient client = BuildPolicyClient(
                first,
                new ReconnectPolicy(
                    () => factory.CreateDoomed(4003),
                    initialBackoffMilliseconds: 0,
                    maxBackoffMilliseconds: 0,
                    maxAttempts: 2
                )
            );
            await ConnectSettledAsync(client, first);

            first.FailPendingReceive(4003);

            List<PollEvent> drained = await DrainUntilEndAsync(
                client,
                clock =>
                {
                    clock.Advance(1);
                    return Task.Delay(1);
                }
            );

            List<PollEventKind> kinds = drained.ConvertAll(delivered => delivered.Kind);
            Assert.That(
                kinds,
                Is.EqualTo(
                    new[]
                    {
                        PollEventKind.Disconnected,
                        PollEventKind.Reconnecting,
                        PollEventKind.TransportReady,
                        PollEventKind.Disconnected,
                        PollEventKind.Reconnecting,
                        PollEventKind.TransportReady,
                        PollEventKind.Disconnected,
                        PollEventKind.ReconnectAbandoned,
                    }
                ),
                "each round delivers Disconnected; the budget ends the stream after the marker"
            );
            Assert.That(drained[1].Reconnect.Attempt, Is.EqualTo(1));
            Assert.That(drained[4].Reconnect.Attempt, Is.EqualTo(2));
            Assert.That(drained[7].Reconnect.Attempt, Is.EqualTo(2), "attempts spent");
            Assert.That(drained[7].Reconnect.LastReason, Is.Not.Null);

            Assert.That(factory.Called, Is.EqualTo(3), "one fresh transport per attempt");
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.Terminal));

            await client.DisposeAsync();
        }

        [Test]
        public async Task AuthenticatedResetsTheAttemptBudget()
        {
            TransportFactory factory = new TransportFactory();
            FakeTransport first = factory.Create();
            int policyRound = 0;
            SignalFishClient client = BuildPolicyClient(
                first,
                new ReconnectPolicy(
                    () =>
                    {
                        policyRound++;
                        return policyRound == 1 ? factory.Create() : factory.CreateDoomed(4003);
                    },
                    initialBackoffMilliseconds: 0,
                    maxBackoffMilliseconds: 0,
                    maxAttempts: 1
                )
            );
            await ConnectJoinRoomAsync(client, first);

            first.FailPendingReceive(4003);

            /*
                Round 2 authenticates (the budget resets) before dying, so
                the next round is attempt 1 again, not 2.
            */
            PollEvent firstDeath = await NextEventAsync(client);
            Assert.That(firstDeath.Kind, Is.EqualTo(PollEventKind.Disconnected));

            PollEvent reconnecting = await NextEventAsync(client);
            Assert.That(reconnecting.Kind, Is.EqualTo(PollEventKind.Reconnecting));
            Assert.That(reconnecting.Reconnect.Attempt, Is.EqualTo(1));

            await AdvanceUntilAsync(client, () => factory.Called >= 2, "round 2 opens");
            FakeTransport second = factory.Last!;
            Assert.That(
                (await NextEventAsync(client)).Kind,
                Is.EqualTo(PollEventKind.TransportReady)
            );
            await WaitForAsync(() => second.SentText.Count >= 1, "auto-authenticate on the wire");
            EnqueueGolden(second, "Authenticated");
            Assert.That(
                (await NextEventAsync(client)).Kind,
                Is.EqualTo(PollEventKind.Authenticated)
            );

            second.EnqueueClose(4003);
            second.Abort();

            PollEvent secondDeath = await NextEventAsync(client);
            Assert.That(secondDeath.Kind, Is.EqualTo(PollEventKind.Disconnected));
            PollEvent secondTry = await NextEventAsync(client);
            Assert.That(secondTry.Kind, Is.EqualTo(PollEventKind.Reconnecting));
            Assert.That(
                secondTry.Reconnect.Attempt,
                Is.EqualTo(1),
                "the budget reset at Authenticated"
            );

            await client.DisposeAsync();
        }

        [Test]
        public async Task TerminalCloseClassificationEndsTheSessionWithoutARound()
        {
            TransportFactory factory = new TransportFactory();
            FakeTransport first = factory.Create();
            ReconnectPolicy policy = new ReconnectPolicy(factory.Create).WithTerminalCloseCodes(
                4007
            );
            SignalFishClient client = BuildPolicyClient(first, policy);
            await ConnectJoinRoomAsync(client, first);

            first.FailPendingReceive(4007);

            PollEvent disconnected = await NextEventAsync(client);
            Assert.That(disconnected.Kind, Is.EqualTo(PollEventKind.Disconnected));
            Assert.That(disconnected.Close.Code, Is.EqualTo(4007));

            PollEvent? endOfStream = await client.DequeueEventAsync();
            Assert.That(endOfStream, Is.Null, "a classified terminal close skips reconnecting");
            Assert.That(factory.Called, Is.EqualTo(1), "no fresh transport is created");

            await client.DisposeAsync();
        }

        [Test]
        public async Task VoluntaryLeaveDiscardsTheSeatContext()
        {
            TransportFactory factory = new TransportFactory();
            FakeTransport first = factory.Create();
            SignalFishClient client = BuildPolicyClient(
                first,
                new ReconnectPolicy(
                    () => factory.CreateDoomed(4003),
                    initialBackoffMilliseconds: 0,
                    maxBackoffMilliseconds: 0,
                    maxAttempts: 1
                )
            );
            await ConnectJoinRoomAsync(client, first);

            Assert.That(client.SendLeaveRoom().Accepted, Is.True);
            EnqueueGolden(first, "RoomLeft");
            Assert.That((await NextEventAsync(client)).Kind, Is.EqualTo(PollEventKind.RoomLeft));

            first.FailPendingReceive(4003);
            List<PollEvent> drained = await DrainToEndAsync(client);

            foreach (PollEvent delivered in drained)
            {
                Assert.That(
                    delivered.Kind,
                    Is.Not.EqualTo(PollEventKind.Reconnected),
                    "a room you chose to leave is never reclaimed"
                );
            }

            FakeTransport second = factory.Last!;
            foreach (string sent in second.SentText)
            {
                Assert.That(
                    sent.StartsWith("{\"type\": \"Reconnect\"", StringComparison.Ordinal),
                    Is.False,
                    "no reconnect command may ride the fresh connection"
                );
            }

            PollEvent abandoned = drained[drained.Count - 1];
            Assert.That(abandoned.Kind, Is.EqualTo(PollEventKind.ReconnectAbandoned));
            Assert.That(abandoned.Reconnect.Attempt, Is.EqualTo(1));

            await client.DisposeAsync();
        }

        [Test]
        public async Task QueuedCommandsOfTheDeadConnectionNeverReachTheNewWire()
        {
            TransportFactory factory = new TransportFactory();
            FakeTransport first = factory.Create();
            SignalFishClient client = BuildPolicyClient(
                first,
                new ReconnectPolicy(
                    () => factory.CreateDoomed(4003),
                    initialBackoffMilliseconds: 0,
                    maxBackoffMilliseconds: 0,
                    maxAttempts: 1
                )
            );
            await ConnectJoinRoomAsync(client, first);

            TaskCompletionSource<bool> wire = new TaskCompletionSource<bool>();
            first.HoldSendsUntil(wire);
            Assert.That(client.SendGameData(Payload(0)).Accepted, Is.True);
            await WaitForAsync(() => first.SentText.Count >= 3, "first relay parks mid-send");
            Assert.That(
                client.SendGameData(Payload(1)).Accepted,
                Is.True,
                "the second relay queues"
            );

            first.FailHeldSends(4003);

            List<PollEvent> drained = await DrainToEndAsync(client);

            foreach (FakeTransport transport in factory.All)
            {
                foreach (string sent in transport.SentText)
                {
                    Assert.That(
                        sent.Contains("\"n\": 1}", StringComparison.Ordinal),
                        Is.False,
                        "a queued command of the dead connection is discarded with it"
                    );
                }
            }

            Assert.That(
                drained[drained.Count - 1].Kind,
                Is.EqualTo(PollEventKind.ReconnectAbandoned)
            );

            await client.DisposeAsync();
        }

        [Test]
        public async Task DisposeDuringBackoffEndsTheSessionPromptly()
        {
            TransportFactory factory = new TransportFactory();
            FakeTransport first = factory.Create();
            SignalFishClient client = BuildPolicyClient(
                first,
                new ReconnectPolicy(
                    factory.Create,
                    initialBackoffMilliseconds: 60_000,
                    maxBackoffMilliseconds: 60_000
                )
            );
            await ConnectJoinRoomAsync(client, first);

            first.FailPendingReceive(4003);

            PollEvent firstDeath = await NextEventAsync(client);
            Assert.That(firstDeath.Kind, Is.EqualTo(PollEventKind.Disconnected));

            PollEvent reconnecting = await NextEventAsync(client);
            Assert.That(reconnecting.Kind, Is.EqualTo(PollEventKind.Reconnecting));

            Stopwatch elapsed = Stopwatch.StartNew();
            await client.DisposeAsync();
            elapsed.Stop();

            Assert.That(
                elapsed.ElapsedMilliseconds,
                Is.LessThan(5_000),
                "disposal bounds the backoff wait"
            );
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.Terminal));
            Assert.That(factory.Called, Is.EqualTo(1), "no round starts after disposal");

            PollEvent? endOfStream = await client.DequeueEventAsync();
            Assert.That(endOfStream, Is.Null);
        }

        [Test]
        public async Task EventOrderHoldsUnderConcurrentRelaysAndBackoff()
        {
            TransportFactory factory = new TransportFactory();
            FakeTransport first = factory.Create();
            SignalFishClient client = BuildPolicyClient(
                first,
                new ReconnectPolicy(
                    () => factory.CreateDoomed(4003),
                    initialBackoffMilliseconds: 0,
                    maxBackoffMilliseconds: 0,
                    maxAttempts: 3
                )
            );
            await ConnectJoinRoomAsync(client, first);

            const int inbound = 20;
            for (int frame = 0; frame < inbound; frame++)
            {
                first.Enqueue(RelayFrame(frame), isText: true);
            }

            const int perThread = 10;
            Task sendA = Task.Run(
                (Action)(
                    () =>
                    {
                        for (int index = 0; index < perThread; index++)
                        {
                            SpinUntilAccepted(client, index);
                        }
                    }
                )
            );
            Task sendB = Task.Run(
                (Action)(
                    () =>
                    {
                        for (int index = 100; index < 100 + perThread; index++)
                        {
                            SpinUntilAccepted(client, index);
                        }
                    }
                )
            );

            await Task.WhenAll(sendA, sendB);

            /*
                All inbound frames must surface before the sever: a real
                transport discards still-buffered inbound data on death.
            */
            await WaitForAsync(
                () => client.PendingEventCount >= inbound,
                "all inbound relays reached the event queue"
            );
            first.FailPendingReceive(4003);

            List<PollEvent> drained = await DrainToEndAsync(client);

            /*
                The inbound relay stream keeps wire order: tags 0..19 exactly
                once each, strictly increasing.
            */
            int lastTag = -1;
            int seen = 0;
            foreach (PollEvent delivered in drained)
            {
                if (delivered.Kind != PollEventKind.GameData)
                {
                    continue;
                }

                int tag = TagOf(delivered);
                Assert.That(
                    tag,
                    Is.GreaterThan(lastTag),
                    "inbound events must keep their wire order across the drain"
                );
                lastTag = tag;
                seen++;
            }

            Assert.That(seen, Is.EqualTo(inbound), "every inbound frame surfaces exactly once");

            /*
                Outbound: each accepted relay reaches a wire at most once
                (commands queued at the sever are discarded with the round).
            */
            Dictionary<int, int> counts = new Dictionary<int, int>();
            foreach (FakeTransport transport in factory.All)
            {
                foreach (string sent in transport.SentText)
                {
                    if (sent.StartsWith("{\"type\": \"GameData\"", StringComparison.Ordinal))
                    {
                        int tag = OutboundTag(sent);
                        counts[tag] = counts.GetValueOrDefault(tag) + 1;
                    }
                }
            }

            foreach (KeyValuePair<int, int> pair in counts)
            {
                Assert.That(
                    pair.Value,
                    Is.EqualTo(1),
                    $"relay {pair.Key} may reach a wire at most once"
                );
            }

            /*
                The reconnect markers stay strictly ordered and the stream
                ends with the abandonment.
            */
            int lastAttempt = 0;
            foreach (PollEvent delivered in drained)
            {
                if (delivered.Kind == PollEventKind.Reconnecting)
                {
                    Assert.That(
                        delivered.Reconnect.Attempt,
                        Is.GreaterThan(lastAttempt),
                        "attempts increase monotonically"
                    );
                    lastAttempt = delivered.Reconnect.Attempt;
                }
            }

            Assert.That(lastAttempt, Is.EqualTo(3), "all three attempts were scheduled");
            Assert.That(
                drained[drained.Count - 1].Kind,
                Is.EqualTo(PollEventKind.ReconnectAbandoned)
            );

            await client.DisposeAsync();
        }

        private static int TagOf(PollEvent delivered)
        {
            string payload = Encoding.UTF8.GetString(delivered.GameData.Payload.Span);
            return OutboundTag(payload);
        }

        private static int OutboundTag(string wire)
        {
            string needle = "\"n\": ";
            int start = wire.IndexOf(needle, StringComparison.Ordinal) + needle.Length;
            int end = wire.IndexOf('}', start);
            return int.Parse(wire[start..end], System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void SpinUntilAccepted(SignalFishClient client, int tag)
        {
            Stopwatch deadline = Stopwatch.StartNew();
            while (!client.SendGameData(Payload(tag)).Accepted)
            {
                Assert.That(
                    deadline.ElapsedMilliseconds,
                    Is.LessThan(10_000),
                    $"relay {tag} was never admitted"
                );
                Thread.Yield();
            }
        }

        private static GameDataMessage Payload(int index)
        {
            return new GameDataMessage(Encoding.UTF8.GetBytes("{\"n\": " + index + "}"));
        }

        private static byte[] RelayFrame(int index)
        {
            string wire =
                "{\"type\": \"GameData\", \"data\": {\"from_player\": \""
                + GoldenPlayerId.ToString()
                + "\", \"data\": {\"n\": "
                + index
                + "}}}";
            return Encoding.UTF8.GetBytes(wire);
        }

        private static string ExpectedReconnectFrame(string token)
        {
            return "{\"type\": \"Reconnect\", \"data\": {\"player_id\": \""
                + GoldenPlayerId.ToString()
                + "\", \"room_id\": \""
                + GoldenRoomId.ToString()
                + "\", \"auth_token\": \""
                + token
                + "\"}}";
        }

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

        private SignalFishClient BuildPolicyClient(
            FakeTransport initial,
            ReconnectPolicy policy,
            int eventCapacity = SignalFishClientOptions.DefaultEventCapacity,
            int commandCapacity = SignalFishClientOptions.DefaultCommandCapacity
        )
        {
            _clock = new VirtualClock();
            return new SignalFishClient(
                initial,
                _clock,
                new SignalFishClientOptions(
                    eventCapacity: eventCapacity,
                    commandCapacity: commandCapacity,
                    shutdownTimeoutMilliseconds: 0,
                    reconnectPolicy: policy
                )
            );
        }

        /// <summary>
        /// Drains events to the end of the stream, advancing the clock
        /// whenever the driver parks in a backoff wait.
        /// </summary>
        private async Task<List<PollEvent>> DrainToEndAsync(SignalFishClient client)
        {
            return await DrainUntilEndAsync(
                client,
                clock =>
                {
                    clock.Advance(1);
                    return Task.Delay(1);
                }
            );
        }

        private async Task<List<PollEvent>> DrainUntilEndAsync(
            SignalFishClient client,
            Func<VirtualClock, Task> tick
        )
        {
            List<PollEvent> drained = new List<PollEvent>();
            Stopwatch deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 10_000)
            {
                try
                {
                    using CancellationTokenSource timeout = new CancellationTokenSource(100);
                    PollEvent? delivered = await client.DequeueEventAsync(timeout.Token);
                    if (delivered is null)
                    {
                        /*
                            A null with a live phase means the driver is parked
                            in a backoff wait: tick the clock and keep
                            draining until the stream truly ends.
                        */
                        if (client.Phase == ConnectionPhase.Terminal)
                        {
                            return drained;
                        }

                        await tick(_clock);
                        continue;
                    }

                    drained.Add(delivered.GetValueOrDefault());
                }
                catch (OperationCanceledException)
                {
                    await tick(_clock);
                }
            }

            Assert.Fail("the event stream never ended within 10 s");
            return drained;
        }

        private async Task AdvanceUntilAsync(
            SignalFishClient client,
            Func<bool> done,
            string because
        )
        {
            Stopwatch deadline = Stopwatch.StartNew();
            while (!done() && deadline.ElapsedMilliseconds < 10_000)
            {
                _clock.Advance(1);
                await Task.Delay(1);
            }

            Assert.That(done, Is.True, because);
        }

        [Test]
        public async Task RefusedReclaimRecoversWhenTheSendQueueFrees()
        {
            TransportFactory factory = new TransportFactory();
            FakeTransport first = factory.Create();
            SignalFishClient client = BuildPolicyClient(
                first,
                new ReconnectPolicy(
                    factory.Create,
                    initialBackoffMilliseconds: 0,
                    maxBackoffMilliseconds: 0
                ),
                commandCapacity: 1
            );
            await ConnectJoinRoomAsync(client, first);

            first.FailPendingReceive(4003);
            Assert.That(
                (await NextEventAsync(client)).Kind,
                Is.EqualTo(PollEventKind.Disconnected)
            );
            Assert.That(
                (await NextEventAsync(client)).Kind,
                Is.EqualTo(PollEventKind.Reconnecting)
            );

            await AdvanceUntilAsync(client, () => factory.Called >= 2, "round 2 opens");
            FakeTransport second = factory.Last!;
            Assert.That(
                (await NextEventAsync(client)).Kind,
                Is.EqualTo(PollEventKind.TransportReady)
            );
            await WaitForAsync(
                () => second.SentText.Count >= 1,
                "auto-authenticate occupies the capacity-1 queue"
            );

            /*
                Refill the queue so the reclaim is refused (SendBufferFull)
                at the Authenticated fact: the driver must keep making
                progress — drain, then retry — instead of spinning on the
                same-condition restart.
            */
            Assert.That(client.SendAuthenticate(new AuthenticateMessage()).Accepted, Is.True);
            EnqueueGolden(second, "Authenticated");
            Assert.That(
                (await NextEventAsync(client)).Kind,
                Is.EqualTo(PollEventKind.Authenticated)
            );

            await WaitForAsync(
                () => second.SentText.Count >= 3,
                "the reclaim retries after the queue drains and reaches the wire"
            );
            Assert.That(
                second.SentText[2],
                Is.EqualTo(ExpectedReconnectFrame("seat-token-1")),
                "the refused reclaim is retried with the retained seat"
            );

            second.Enqueue(Encoding.UTF8.GetBytes(ReconnectedFrame("seat-token-2")), isText: true);
            try
            {
                PollEvent reconnected = await NextEventAsync(client);
                Assert.That(reconnected.Kind, Is.EqualTo(PollEventKind.Reconnected));
            }
            catch (System.Threading.Tasks.TaskCanceledException)
            {
                Assert.Fail(
                    "no Reconnected event. phase="
                        + client.Phase
                        + " pendingEvents="
                        + client.PendingEventCount
                        + " sendCapacity="
                        + client.SendCapacity
                        + " wire2=["
                        + string.Join(" | ", second.SentText)
                        + "]"
                );
            }

            Assert.That(client.Snapshot.ReconnectionToken, Is.EqualTo("seat-token-2"));

            await client.DisposeAsync();
        }

        [Test]
        public async Task SeatSurvivesARoundThatDiesBeforeAuthentication()
        {
            TransportFactory factory = new TransportFactory();
            FakeTransport first = factory.Create();
            int policyRound = 0;
            SignalFishClient client = BuildPolicyClient(
                first,
                new ReconnectPolicy(
                    () =>
                    {
                        policyRound++;
                        return policyRound <= 2 ? factory.Create() : factory.CreateDoomed(4003);
                    },
                    initialBackoffMilliseconds: 0,
                    maxBackoffMilliseconds: 0,
                    maxAttempts: 4
                )
            );
            await ConnectJoinRoomAsync(client, first);

            first.FailPendingReceive(4003);

            PollEvent firstDeath = await NextEventAsync(client);
            Assert.That(firstDeath.Kind, Is.EqualTo(PollEventKind.Disconnected));

            /*
                Round 2 authenticates nowhere: the test kills it before the
                handshake answer, so the round-1 seat was never reclaimed
                and must survive into round 3.
            */
            PollEvent firstTry = await NextEventAsync(client);
            Assert.That(firstTry.Kind, Is.EqualTo(PollEventKind.Reconnecting));

            await AdvanceUntilAsync(client, () => factory.Called >= 2, "round 2 opens");
            FakeTransport second = factory.Last!;
            Assert.That(
                (await NextEventAsync(client)).Kind,
                Is.EqualTo(PollEventKind.TransportReady)
            );
            await WaitForAsync(
                () => second.SentText.Count >= 1,
                "auto-authenticate on the round-2 wire"
            );

            second.EnqueueClose(4003);
            second.Abort();

            PollEvent secondDeath = await NextEventAsync(client);
            Assert.That(secondDeath.Kind, Is.EqualTo(PollEventKind.Disconnected));

            PollEvent secondTry = await NextEventAsync(client);
            Assert.That(secondTry.Kind, Is.EqualTo(PollEventKind.Reconnecting));

            await AdvanceUntilAsync(client, () => factory.Called >= 3, "round 3 opens");
            FakeTransport third = factory.Last!;
            Assert.That(
                (await NextEventAsync(client)).Kind,
                Is.EqualTo(PollEventKind.TransportReady)
            );
            await WaitForAsync(() => third.SentText.Count >= 1, "round 3 auto-authenticates");
            EnqueueGolden(third, "Authenticated");
            Assert.That(
                (await NextEventAsync(client)).Kind,
                Is.EqualTo(PollEventKind.Authenticated)
            );
            await WaitForAsync(
                () => third.SentText.Count >= 2,
                "round 3 reclaims the retained seat once authenticated"
            );
            Assert.That(
                third.SentText[1],
                Is.EqualTo(ExpectedReconnectFrame("seat-token-1")),
                "the seat captured in round 1 is reclaimed after the seatless death"
            );

            third.Enqueue(Encoding.UTF8.GetBytes(ReconnectedFrame("seat-token-2")), isText: true);
            Assert.That((await NextEventAsync(client)).Kind, Is.EqualTo(PollEventKind.Reconnected));

            await client.DisposeAsync();
        }

        [Test]
        public async Task MarkersSurviveAFullEventQueueAcrossARound()
        {
            TransportFactory factory = new TransportFactory();
            FakeTransport first = factory.Create();
            SignalFishClient client = BuildPolicyClient(
                first,
                new ReconnectPolicy(
                    factory.Create,
                    initialBackoffMilliseconds: 0,
                    maxBackoffMilliseconds: 0
                ),
                eventCapacity: 2
            );
            await ConnectJoinRoomAsync(client, first);

            first.FailPendingReceive(4003);

            /*
                Capacity 2 against a reconnecting session: every marker
                (Disconnected, Reconnecting, TransportReady, Authenticated,
                Reconnected) must arrive — the driver parks on the full
                queue, it never drops.
            */
            List<PollEventKind> kinds = new List<PollEventKind>();
            FakeTransport wire = first;
            bool seatReconnectQueued = false;
            Stopwatch deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 10_000)
            {
                PollEvent? delivered;
                try
                {
                    using CancellationTokenSource timeout = new CancellationTokenSource(1_000);
                    delivered = await client.DequeueEventAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    /*
                        Nothing buffered right now: the driver may be parked
                        in a zero-backoff wait, which only the clock releases.
                    */
                    _clock.Advance(1);
                    continue;
                }

                kinds.Add(delivered.GetValueOrDefault().Kind);

                if (delivered.GetValueOrDefault().Kind == PollEventKind.TransportReady)
                {
                    wire = factory.Last!;
                    await WaitForAsync(
                        () => wire.SentText.Count >= 1,
                        "auto-authenticate on the round wire"
                    );
                    EnqueueGolden(wire, "Authenticated");
                }
                else if (
                    delivered.GetValueOrDefault().Kind == PollEventKind.Authenticated
                    && !seatReconnectQueued
                )
                {
                    wire.Enqueue(
                        Encoding.UTF8.GetBytes(ReconnectedFrame("seat-token-2")),
                        isText: true
                    );
                    seatReconnectQueued = true;
                }

                if (kinds.Count > 4 && kinds[4] == PollEventKind.Reconnected)
                {
                    break;
                }
            }

            Assert.That(
                kinds.Count,
                Is.GreaterThanOrEqualTo(5),
                "the full reconnect round surfaced through the capacity-2 queue"
            );
            Assert.That(
                kinds[0],
                Is.EqualTo(PollEventKind.Disconnected),
                "the death marker is never dropped"
            );
            Assert.That(kinds[1], Is.EqualTo(PollEventKind.Reconnecting));
            Assert.That(kinds[2], Is.EqualTo(PollEventKind.TransportReady));
            Assert.That(kinds[3], Is.EqualTo(PollEventKind.Authenticated));
            Assert.That(kinds[4], Is.EqualTo(PollEventKind.Reconnected));

            await client.DisposeAsync();
        }

        [Test]
        public async Task FailedActivationSchedulesTheNextAttemptWithoutATransportReady()
        {
            TransportFactory factory = new TransportFactory();
            FakeTransport first = factory.Create();
            int policyRound = 0;
            SignalFishClient client = BuildPolicyClient(
                first,
                new ReconnectPolicy(
                    () =>
                    {
                        policyRound++;
                        return policyRound == 1
                            ? throw new InvalidOperationException("broken backend")
                            : factory.CreateDoomed(4003);
                    },
                    initialBackoffMilliseconds: 0,
                    maxBackoffMilliseconds: 0,
                    maxAttempts: 2
                )
            );
            await ConnectJoinRoomAsync(client, first);

            first.FailPendingReceive(4003);

            PollEvent disconnected = await NextEventAsync(client);
            Assert.That(disconnected.Kind, Is.EqualTo(PollEventKind.Disconnected));

            /*
                The broken factory consumes attempt 1: the next marker is
                another Reconnecting, with no transport-ready round between.
            */
            PollEvent firstTry = await NextEventWithClockAsync(client);
            Assert.That(firstTry.Kind, Is.EqualTo(PollEventKind.Reconnecting));
            Assert.That(firstTry.Reconnect.Attempt, Is.EqualTo(1));

            PollEvent secondTry = await NextEventWithClockAsync(client);
            Assert.That(secondTry.Kind, Is.EqualTo(PollEventKind.Reconnecting));
            Assert.That(secondTry.Reconnect.Attempt, Is.EqualTo(2));

            PollEvent ready = await NextEventWithClockAsync(client);
            Assert.That(ready.Kind, Is.EqualTo(PollEventKind.TransportReady));

            await client.DisposeAsync();
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

        /// <summary>
        /// Like <see cref="NextEventAsync"/>, but releases zero-backoff
        /// waits by ticking the clock while nothing is buffered.
        /// </summary>
        private async Task<PollEvent> NextEventWithClockAsync(SignalFishClient client)
        {
            Stopwatch deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 10_000)
            {
                try
                {
                    using CancellationTokenSource timeout = new CancellationTokenSource(100);
                    PollEvent? pollEvent = await client.DequeueEventAsync(timeout.Token);
                    if (pollEvent is not null)
                    {
                        return pollEvent.GetValueOrDefault();
                    }
                }
                catch (OperationCanceledException)
                {
                    _clock.Advance(1);
                }
            }

            Assert.Fail("expected an event before the 10 s deadline");
            return default;
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

        private static async Task ConnectSettledAsync(
            SignalFishClient client,
            FakeTransport transport
        )
        {
            await client.ConnectAsync(Endpoint());
            Assert.That(
                (await NextEventAsync(client)).Kind,
                Is.EqualTo(PollEventKind.TransportReady)
            );
            Assert.That(transport.IsConnected, Is.True);
        }

        private static async Task ConnectJoinRoomAsync(
            SignalFishClient client,
            FakeTransport transport
        )
        {
            await ConnectSettledAsync(client, transport);

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

        private static Uri Endpoint()
        {
            return new Uri("ws://localhost:8080/v2/ws");
        }

        /// <summary>Tracks the transports handed to the client by the policy factory.</summary>
        private sealed class TransportFactory
        {
            /// <summary>Gets how many transports the factory produced.</summary>
            public int Called
            {
                get
                {
                    lock (_gate)
                    {
                        return _created.Count;
                    }
                }
            }

            /// <summary>Gets every transport the factory produced, in order.</summary>
            public IReadOnlyList<FakeTransport> All
            {
                get
                {
                    lock (_gate)
                    {
                        return new List<FakeTransport>(_created);
                    }
                }
            }

            /// <summary>Gets the most recent transport (null before the first call).</summary>
            public FakeTransport? Last
            {
                get
                {
                    lock (_gate)
                    {
                        return _created.Count > 0 ? _created[_created.Count - 1] : null;
                    }
                }
            }

            private readonly List<FakeTransport> _created = new List<FakeTransport>();

            private readonly object _gate = new object();

            /// <summary>Produces a fresh, connectable transport (policy factory shape).</summary>
            public FakeTransport Create()
            {
                lock (_gate)
                {
                    FakeTransport transport = new FakeTransport();
                    _created.Add(transport);
                    return transport;
                }
            }

            /// <summary>Produces a transport that dies with the given close on first use.</summary>
            public FakeTransport CreateDoomed(int closeCode)
            {
                lock (_gate)
                {
                    FakeTransport transport = new FakeTransport();
                    transport.DoomWithClose(closeCode);
                    _created.Add(transport);
                    return transport;
                }
            }
        }
    }
}
