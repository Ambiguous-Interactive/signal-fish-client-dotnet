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
    using SignalFish.Client.Tests.Core;
    using SignalFish.Client.Tests.Transport;

    /// <summary>
    /// Red-green anchors for the M4.2 driver loop: event surfacing, golden
    /// wire sends, admission refusals, command-queue backpressure (fail
    /// fast vs reliable), event-queue backpressure (pause, never drop),
    /// virtual-time heartbeat/liveness, and terminal delivery exactly
    /// once.
    /// </summary>
    [TestFixture]
    public class SignalFishClientTests
    {
        private static readonly Guid SenderId = new Guid("00000000-0000-0000-0000-00000000000b");
        private static readonly string[] RelayOnlyTransports = { "relay" };
        private static readonly string[] DirectOnlyTransports = { "direct" };
        private static readonly string[] RelayAndEmptyTransports = { "relay", "" };
        private static readonly string[] CapabilitiesWithNullToken =
        {
            "room_operation_ids",
            null!,
        };

        [Test]
        public async Task ConnectEmitsTransportReadyOnce()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed();

            await client.ConnectAsync(Endpoint());
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.TransportReady));

            PollEvent transportReady = await NextEventAsync(client);
            Assert.That(transportReady.Kind, Is.EqualTo(PollEventKind.TransportReady));
            Assert.That(client.PendingEventCount, Is.EqualTo(0));
            Assert.That(transport.IsConnected, Is.True);

            await client.DisposeAsync();
        }

        [Test]
        public async Task ConnectTwiceIsRejectedByTheClient()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed();
            await client.ConnectAsync(Endpoint());

            InvalidOperationException rejected = Assert.ThrowsAsync<InvalidOperationException>(
                (Func<Task>)(async () => await client.ConnectAsync(Endpoint()))
            );
            Assert.That(
                rejected.Message,
                Does.Contain("ConnectAsync"),
                "the client guard must reject, not the transport"
            );
            Assert.That(transport.ConnectCount, Is.EqualTo(1));

            await client.DisposeAsync();
        }

        [Test]
        public async Task AuthenticateSendsGoldenWireBytes()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed();
            await client.ConnectAsync(Endpoint());

            Assert.That(client.SendAuthenticate(new AuthenticateMessage()).Accepted, Is.True);
            await WaitForAsync(() => transport.SentText.Count >= 1, "handshake on the wire");

            /*
                The v2 floor handshake is payload-less; the vendored
                Authenticate sample is the v3-shaped one (app_id arrives
                with M5.3 credentials).
            */
            Assert.That(transport.SentText[0], Is.EqualTo("{\"type\": \"Authenticate\"}"));
            await client.DisposeAsync();
        }

        [Test]
        public async Task RefusedCommandsNeverTouchTheWire()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed();

            CommandSend beforeConnect = client.SendJoinRoom(new JoinRoomMessage("my-game", "P1"));
            Assert.That(beforeConnect.Accepted, Is.False);
            Assert.That(beforeConnect.Refusal, Is.EqualTo(AdmissionError.NotConnected));

            await client.ConnectAsync(Endpoint());

            CommandSend beforeAuth = client.SendJoinRoom(new JoinRoomMessage("my-game", "P1"));
            Assert.That(beforeAuth.Accepted, Is.False);
            Assert.That(beforeAuth.Refusal, Is.EqualTo(AdmissionError.NotAuthenticated));
            Assert.That(transport.SentText, Is.Empty);
            Assert.That(client.PendingOperation, Is.EqualTo(default(PendingRoomOperation)));

            await client.DisposeAsync();
        }

        [Test]
        public async Task SessionEventsSurfaceInWireOrderThroughTheJoin()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed();
            await client.ConnectAsync(Endpoint());

            PollEvent transportReady = await NextEventAsync(client);
            Assert.That(transportReady.Kind, Is.EqualTo(PollEventKind.TransportReady));

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
            EnqueueGolden(transport, "RoomJoined");
            Assert.That((await NextEventAsync(client)).Kind, Is.EqualTo(PollEventKind.RoomJoined));

            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.InRoom));
            Assert.That(client.IsAuthenticated, Is.True);
            Assert.That(client.Membership.IsPresent, Is.True);
            Assert.That(client.Membership.Role, Is.EqualTo(RoomRole.Player));
            Assert.That(
                client.PendingOperation,
                Is.EqualTo(default(PendingRoomOperation)),
                "the typed result releases the join fence"
            );

            await client.DisposeAsync();
        }

        [Test]
        public async Task StalledWireFillsCommandQueueWithSendBufferFull()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed(
                new SignalFishClientOptions(commandCapacity: 1)
            );
            TaskCompletionSource<bool> wire = new TaskCompletionSource<bool>();
            await ConnectJoinRoom(client, transport);

            /*
                Stall the wire, then put one relay in flight (recorded, then
                parked mid-send) and one in the queue slot. The third relay
                fails fast and names why.
            */
            transport.HoldSendsUntil(wire);
            Assert.That(client.SendGameData(Payload(0)).Accepted, Is.True);
            await WaitForAsync(() => transport.SentText.Count >= 3, "first relay on the wire");
            Assert.That(client.SendGameData(Payload(1)).Accepted, Is.True);

            CommandSend refused = client.SendGameData(Payload(2));
            Assert.That(refused.Accepted, Is.False);
            Assert.That(refused.Refusal, Is.EqualTo(AdmissionError.SendBufferFull));

            wire.TrySetResult(true);
            await WaitForAsync(() => transport.SentText.Count >= 4, "queue drains after recovery");
            Assert.That(client.SendCapacity, Is.EqualTo(client.MaxSendCapacity));

            await client.DisposeAsync();
        }

        [Test]
        public async Task ReliableSendWaitsForAQueueSlotInsteadOfFailing()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed(
                new SignalFishClientOptions(commandCapacity: 1)
            );
            TaskCompletionSource<bool> wire = new TaskCompletionSource<bool>();
            await ConnectJoinRoom(client, transport);

            transport.HoldSendsUntil(wire);
            Assert.That(client.SendGameData(Payload(0)).Accepted, Is.True);
            await WaitForAsync(() => transport.SentText.Count >= 3, "first relay on the wire");
            Assert.That(client.SendGameData(Payload(1)).Accepted, Is.True);

            Task<CommandSend> reliable = client.SendGameDataReliableAsync(Payload(2));
            Assert.That(reliable.IsCompleted, Is.False, "a full queue paces the reliable sender");

            wire.TrySetResult(true);
            CommandSend verdict = await reliable;
            Assert.That(verdict.Accepted, Is.True);
            await WaitForAsync(() => transport.SentText.Count >= 5, "all three relays delivered");

            await client.DisposeAsync();
        }

        [Test]
        public async Task ReliableSendStillReportsAdmissionRefusals()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed();
            await client.ConnectAsync(Endpoint());

            CommandSend verdict = await client.SendGameDataReliableAsync(Payload(0));
            Assert.That(verdict.Accepted, Is.False);
            Assert.That(verdict.Refusal, Is.EqualTo(AdmissionError.NotInRoom));
            Assert.That(transport.SentText, Is.Empty);

            await client.DisposeAsync();
        }

        [Test]
        public async Task FullEventQueuePausesTheLoopWithoutDropping()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed(
                new SignalFishClientOptions(eventCapacity: 8)
            );
            await ConnectJoinRoom(client, transport);

            for (int frame = 0; frame < 50; frame++)
            {
                transport.Enqueue(RelayFrame(frame), isText: true);
            }

            /*
                50 frames against a capacity-8 queue: the loop must pause on
                the full queue and deliver every event, in order.
            */
            for (int expected = 0; expected < 50; expected++)
            {
                PollEvent pollEvent = await NextEventAsync(client);
                Assert.That(pollEvent.Kind, Is.EqualTo(PollEventKind.GameData));
                Assert.That(
                    Encoding.UTF8.GetString(pollEvent.GameData.Payload.Span),
                    Is.EqualTo("{\"n\": " + expected + "}"),
                    $"frame {expected} lost or reordered"
                );
                Assert.That(pollEvent.GameData.FromPlayer, Is.EqualTo(SenderId));
            }

            Assert.That(client.PendingEventCount, Is.EqualTo(0));
            await client.DisposeAsync();
        }

        [Test]
        public async Task ClassifiedGameDataWithoutNegotiatedV3IsRefused()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed();
            await ConnectJoinRoom(client, transport);

            byte[] payload = Encoding.UTF8.GetBytes("{\"tick\": 1}");
            CommandSend latest = client.SendGameData(
                new GameDataMessage(payload, GameDataClass.Latest, key: 7)
            );
            Assert.That(latest.Accepted, Is.False);
            Assert.That(latest.Refusal, Is.EqualTo(AdmissionError.ProtocolUnsupported));
            Assert.That(
                CountTag(transport.SentText, 9),
                Is.EqualTo(0),
                "a refused send never touches the wire"
            );

            /*
                The waiting send carries the same gate: a classified message
                through SendGameDataReliableAsync is refused on a pre-v3
                connection instead of being emitted for the server to
                answer INVALID_DELIVERY_CLASS.
            */
            CommandSend reliableAsync = await client.SendGameDataReliableAsync(
                new GameDataMessage(payload, GameDataClass.Latest, key: 7)
            );
            Assert.That(reliableAsync.Accepted, Is.False);
            Assert.That(reliableAsync.Refusal, Is.EqualTo(AdmissionError.ProtocolUnsupported));

            transport.Enqueue(
                Encoding.UTF8.GetBytes(
                    GoldenFixtures.ReadFirstLineOfType("v3-server-messages.jsonl", "ProtocolInfo")
                ),
                isText: true
            );
            Assert.That(
                (await NextEventAsync(client)).Kind,
                Is.EqualTo(PollEventKind.ProtocolInfo)
            );

            int sentBefore = transport.SentText.Count;
            CommandSend negotiated = client.SendGameData(
                new GameDataMessage(payload, GameDataClass.Latest, key: 7)
            );
            Assert.That(negotiated.Accepted, Is.True);
            await WaitForAsync(
                () => transport.SentText.Count >= sentBefore + 1,
                "classified relay on the wire"
            );
            Assert.That(
                transport.SentText[^1],
                Is.EqualTo(
                    "{\"type\": \"GameData\", \"data\": {\"data\": {\"tick\": 1}, "
                        + "\"class\": \"latest\", \"key\": 7}}"
                )
            );
            await client.DisposeAsync();
        }

        [Test]
        public async Task HeartbeatPingsAndLivenessTimeoutEndsTheSession()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock clock) = BuildTimed(
                new SignalFishClientOptions(
                    heartbeatIntervalMilliseconds: 1_000,
                    heartbeatTimeoutMilliseconds: 5_000
                )
            );
            await ConnectSettledAsync(client);
            await AdvanceUntilAsync(clock, () => CountPings(transport) >= 1, 250);
            Assert.That(CountPings(transport), Is.GreaterThanOrEqualTo(1));

            await AdvanceUntilAsync(clock, () => client.Phase == ConnectionPhase.Terminal, 250);

            PollEvent disconnected = await NextEventAsync(client);
            Assert.That(disconnected.Kind, Is.EqualTo(PollEventKind.Disconnected));
            Assert.That(disconnected.Close.Code, Is.EqualTo(1006));

            CommandSend refused = client.SendGameData(Payload(0));
            Assert.That(refused.Accepted, Is.False);
            Assert.That(refused.Refusal, Is.EqualTo(AdmissionError.NotConnected));

            PollEvent? endOfStream = await client.DequeueEventAsync();
            Assert.That(endOfStream, Is.Null, "the event stream ends after Disconnected");
        }

        [Test]
        public async Task HeartbeatKeepsTheIntervalCadenceAndRespectsTheBoundary()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock clock) = BuildTimed(
                new SignalFishClientOptions(
                    heartbeatIntervalMilliseconds: 1_000,
                    heartbeatTimeoutMilliseconds: 10_000
                )
            );
            await ConnectSettledAsync(client);

            /*
                Deadline-aware virtual time makes every step deterministic:
                an advance that stops short of the next deadline releases
                nothing, so the loop never even wakes.
            */
            clock.Advance(999);
            Assert.That(CountPings(transport), Is.EqualTo(0), "no ping before the interval");

            await AdvanceUntilAsync(clock, () => CountPings(transport) >= 1, 1);
            Assert.That(CountPings(transport), Is.EqualTo(1), "the first ping at the interval");

            clock.Advance(999);
            Assert.That(
                CountPings(transport),
                Is.EqualTo(1),
                "no second ping inside the next interval"
            );

            await AdvanceUntilAsync(clock, () => CountPings(transport) >= 2, 1);
            Assert.That(CountPings(transport), Is.EqualTo(2), "one ping per interval");

            await client.DisposeAsync();
        }

        [Test]
        public async Task TerminalDisconnectedDeliversExactlyOnceEvenUnderFullQueue()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed(
                new SignalFishClientOptions(eventCapacity: 4)
            );
            await ConnectSettledAsync(client);

            /*
                Fill the queue past capacity and never drain: the loop parks
                mid-enqueue. Disposing under that backpressure must still
                yield exactly one terminal Disconnected, last.
            */
            for (int frame = 0; frame < 20; frame++)
            {
                transport.Enqueue(RelayFrame(frame), isText: true);
            }

            await WaitForAsync(
                () => client.PendingEventCount >= 3,
                "the loop parked on the full queue"
            );
            await client.DisposeAsync();

            List<PollEvent> drained = new List<PollEvent>();
            PollEvent? pollEvent;
            while ((pollEvent = await client.DequeueEventAsync()) is not null)
            {
                drained.Add(pollEvent.GetValueOrDefault());
            }

            Assert.That(drained, Is.Not.Empty, "the buffered events survive the teardown");
            Assert.That(
                drained[drained.Count - 1].Kind,
                Is.EqualTo(PollEventKind.Disconnected),
                "the terminal event is delivered last"
            );
            Assert.That(drained[drained.Count - 1].Close.Code, Is.EqualTo(0));

            int disconnects = 0;
            foreach (PollEvent delivered in drained)
            {
                disconnects += delivered.Kind == PollEventKind.Disconnected ? 1 : 0;
            }

            Assert.That(disconnects, Is.EqualTo(1), "exactly one terminal event");
            Assert.That(client.PendingEventCount, Is.EqualTo(0));
        }

        [Test]
        public async Task TeardownUnblocksAParkedReliableSenderWithNotConnected()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed(
                new SignalFishClientOptions(commandCapacity: 1)
            );
            TaskCompletionSource<bool> wire = new TaskCompletionSource<bool>();
            await ConnectJoinRoom(client, transport);

            transport.HoldSendsUntil(wire);
            Assert.That(client.SendGameData(Payload(0)).Accepted, Is.True);
            await WaitForAsync(() => transport.SentText.Count >= 3, "first relay on the wire");
            Assert.That(client.SendGameData(Payload(1)).Accepted, Is.True);

            Task<CommandSend> reliable = client.SendGameDataReliableAsync(Payload(2));
            await WaitForAsync(
                () => client.SendCapacity == 0,
                "the reliable sender is parked on the full queue"
            );

            /*
                A teardown while the sender parks must unblock it with the
                NotConnected verdict. Disposal tears the session down (and
                aborts the stalled wire, like a real transport would), so
                the parked sender, the loop, and the disposal all resolve.
            */
            await client.DisposeAsync();

            CommandSend verdict = await reliable;
            Assert.That(verdict.Accepted, Is.False);
            Assert.That(verdict.Refusal, Is.EqualTo(AdmissionError.NotConnected));
        }

        [Test]
        public async Task ServerCloseFrameDeliversTypedDisconnectedOnce()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed();
            await ConnectSettledAsync(client);

            transport.EnqueueClose(4000);
            transport.Abort();

            PollEvent disconnected = await NextEventAsync(client);
            Assert.That(disconnected.Kind, Is.EqualTo(PollEventKind.Disconnected));
            Assert.That(disconnected.Close.Code, Is.EqualTo(4000));
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.Terminal));

            PollEvent? endOfStream = await client.DequeueEventAsync();
            Assert.That(endOfStream, Is.Null, "exactly one terminal event");
        }

        [Test]
        public async Task ReceiveFaultCarriesTheTypedCloseCode()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed();
            await ConnectSettledAsync(client);

            transport.FailPendingReceive(4003);

            PollEvent disconnected = await NextEventAsync(client);
            Assert.That(disconnected.Kind, Is.EqualTo(PollEventKind.Disconnected));
            Assert.That(disconnected.Close.Code, Is.EqualTo(4003));
            Assert.That(client.IsConnected, Is.False);
        }

        [Test]
        public async Task DisposeStopsTheLoopAndRefusesFurtherWork()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed();
            await ConnectSettledAsync(client);

            await client.DisposeAsync();
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.Terminal));
            Assert.That(transport.IsConnected, Is.False);

            PollEvent disconnected = await NextEventAsync(client);
            Assert.That(disconnected.Kind, Is.EqualTo(PollEventKind.Disconnected));
            Assert.That(disconnected.Close.Code, Is.EqualTo(0));

            Assert.Throws<ObjectDisposedException>((Action)(() => client.SendGameData(Payload(0))));
            Assert.Throws<ObjectDisposedException>(
                (Action)(() => client.SendAuthenticate(new AuthenticateMessage()))
            );

            await client.DisposeAsync();
        }

        [Test]
        public async Task SequentialRelaysReachTheWireInQueueOrder()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed();
            await ConnectJoinRoom(client, transport);

            for (int index = 0; index < 5; index++)
            {
                Assert.That(client.SendGameData(Payload(index)).Accepted, Is.True);
            }

            await WaitForAsync(() => transport.SentText.Count >= 2 + 5, "all relays on the wire");

            for (int index = 0; index < 5; index++)
            {
                Assert.That(
                    transport.SentText[transport.SentText.Count - 5 + index],
                    Is.EqualTo(
                        "{\"type\": \"GameData\", \"data\": {\"data\": {\"n\": " + index + "}}}"
                    ),
                    $"relay {index} must keep its queue position"
                );
            }

            await client.DisposeAsync();
        }

        [Test]
        public async Task ConcurrentRelaysAllReachTheWireExactlyOnce()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed();
            await ConnectJoinRoom(client, transport);

            const int perThread = 25;
            Task first = Task.Run(() =>
            {
                for (int index = 0; index < perThread; index++)
                {
                    SpinUntilAccepted(client, index);
                }
            });
            Task second = Task.Run(() =>
            {
                for (int index = 100; index < 100 + perThread; index++)
                {
                    SpinUntilAccepted(client, index);
                }
            });

            await Task.WhenAll(first, second);
            await WaitForAsync(
                () => transport.SentText.Count >= 2 + perThread * 2,
                "every accepted relay reaches the wire"
            );

            for (int index = 0; index < perThread; index++)
            {
                Assert.That(
                    CountTag(transport.SentText, index),
                    Is.EqualTo(1),
                    $"relay {index} must reach the wire exactly once"
                );
                Assert.That(
                    CountTag(transport.SentText, 100 + index),
                    Is.EqualTo(1),
                    $"relay {100 + index} must reach the wire exactly once"
                );
            }

            await client.DisposeAsync();
        }

        [Test]
        public async Task AuthorityRequestSendsGoldenWireAndTracksTheSeat()
        {
            (SignalFishClient client, FakeTransport transport, VirtualClock _) = BuildTimed();
            await ConnectJoinRoom(client, transport);
            Assert.That(
                client.Snapshot.IsAuthority,
                Is.True,
                "the golden baseline holds authority"
            );

            CommandSend relinquish = client.SendAuthorityRequest(becomeAuthority: false);
            Assert.That(relinquish.Accepted, Is.True);
            await WaitForAsync(
                () =>
                    transport.SentText[^1]
                    == @"{""type"": ""AuthorityRequest"", ""data"": {""become_authority"": false}}",
                "relinquish on the wire"
            );

            EnqueueGolden(transport, "AuthorityChanged");
            Assert.That(
                (await NextEventAsync(client)).Kind,
                Is.EqualTo(PollEventKind.AuthorityChanged)
            );
            Assert.That(client.Snapshot.IsAuthority, Is.False);

            CommandSend refused = client.SendAuthorityRequest(false);
            Assert.That(refused.Refusal, Is.EqualTo(AdmissionError.AuthorityRequired));
            await client.DisposeAsync();
        }

        [Test]
        public void OptionsToStringRedactsTheConnectToken()
        {
            SignalFishClientOptions options = new SignalFishClientOptions(
                appId: "mb_app_abc123",
                connectToken: "sfct_v1.secret-token-value"
            );
            string text = options.ToString();
            Assert.That(text, Does.Contain("AppId=mb_app_abc123"));
            Assert.That(text, Does.Contain("ConnectToken=<redacted>"));
            Assert.That(text, Does.Not.Contain("sfct_v1"));
            Assert.That(
                new SignalFishClientOptions().ToString(),
                Does.Contain("ConnectToken=<none>")
            );
        }

        [Test]
        public void OptionsToStringDescribesTheNegotiationAdvertisement()
        {
            string text = new SignalFishClientOptions(
                protocolVersion: 3,
                supportedTransports: RelayOnlyTransports
            ).ToString();
            Assert.That(text, Does.Contain("ProtocolVersion=3"));
            Assert.That(text, Does.Contain("SupportedTransports=[relay]"));
            Assert.That(
                new SignalFishClientOptions().ToString(),
                Does.Contain("SupportedTransports=<none>")
            );
        }

        // --- Options validation: fail fast, never mid-handshake -------------
        [Test]
        public void OptionsRejectUnfulfillableTransportAdvertisements()
        {
            Assert.That(
                (Action)(
                    () =>
                        _ = new SignalFishClientOptions(supportedTransports: Array.Empty<string>())
                ),
                Throws.ArgumentException
            );
            Assert.That(
                (Action)(
                    () => _ = new SignalFishClientOptions(supportedTransports: DirectOnlyTransports)
                ),
                Throws.ArgumentException
            );
            Assert.That(
                (Action)(
                    () =>
                        _ = new SignalFishClientOptions(
                            supportedTransports: RelayAndEmptyTransports
                        )
                ),
                Throws.ArgumentException
            );
            string[] withNull = { "relay", null! };
            ArgumentException refused = Assert.Throws<ArgumentException>(
                (Action)(() => _ = new SignalFishClientOptions(supportedTransports: withNull))
            );
            Assert.That(refused.ParamName, Is.EqualTo("supportedTransports"));
        }

        [Test]
        public void OptionsRejectEmptyOrTokenlessTopologyAndCapabilityLists()
        {
            Assert.That(
                (Action)(
                    () =>
                        _ = new SignalFishClientOptions(supportedTopologies: Array.Empty<string>())
                ),
                Throws.ArgumentException
            );
            Assert.That(
                (Action)(
                    () =>
                        _ = new SignalFishClientOptions(
                            requestedCapabilities: CapabilitiesWithNullToken
                        )
                ),
                Throws.ArgumentException
            );
        }

        [Test]
        public void OptionsSnapshotTheAdvertisementListsAgainstCallerMutation()
        {
            string[] transports = { "relay", "direct" };
            SignalFishClientOptions options = new SignalFishClientOptions(
                supportedTransports: transports
            );
            transports[1] = "webrtc";
            Assert.That(options.SupportedTransports![1], Is.EqualTo("direct"));
        }

        private static GameDataMessage Payload(int index)
        {
            return new GameDataMessage(Encoding.UTF8.GetBytes("{\"n\": " + index + "}"));
        }

        private static byte[] RelayFrame(int index)
        {
            string wire =
                "{\"type\": \"GameData\", \"data\": {\"from_player\": \""
                + SenderId.ToString()
                + "\", \"data\": {\"n\": "
                + index
                + "}}}";
            return Encoding.UTF8.GetBytes(wire);
        }

        private static int CountTag(IReadOnlyList<string> lines, int tag)
        {
            string needle = "\"n\": " + tag + "}";
            int count = 0;
            foreach (string sent in lines)
            {
                if (sent.Contains(needle, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private static void SpinUntilAccepted(SignalFishClient client, int tag)
        {
            while (!client.SendGameData(Payload(tag)).Accepted)
            {
                Thread.Yield();
            }
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

        private static async Task<PollEvent> NextEventAsync(SignalFishClient client)
        {
            using CancellationTokenSource timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(10)
            );
            PollEvent? pollEvent = await client.DequeueEventAsync(timeout.Token);
            Assert.That(pollEvent, Is.Not.Null, "expected an event before the 10 s timeout");
            return pollEvent.GetValueOrDefault();
        }

        /*
            Readiness polls, not timing asserts: the real 1 ms sleep exists
            only to release the thread-pool thread so the driver loop's
            continuation can run (a Task.Yield hot-spin here starves the
            very task we are waiting for). All protocol timing under test
            moves on the virtual clock.
        */
        private static async Task WaitForAsync(Func<bool> done, string because)
        {
            Stopwatch deadline = Stopwatch.StartNew();
            while (!done() && deadline.ElapsedMilliseconds < 10_000)
            {
                await Task.Delay(1);
            }

            Assert.That(done, Is.True, because);
        }

        private static async Task AdvanceUntilAsync(
            VirtualClock clock,
            Func<bool> done,
            long stepMilliseconds
        )
        {
            for (int attempt = 0; attempt < 400 && !done(); attempt++)
            {
                clock.Advance(stepMilliseconds);
                await Task.Delay(1);
            }

            Assert.That(done, Is.True, "the clock never reached the expected state");
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

        /// <summary>
        /// Connects and drives the golden handshake to a confirmed player
        /// membership; the event queue is drained when this returns.
        /// </summary>
        private static async Task ConnectJoinRoom(SignalFishClient client, FakeTransport transport)
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
            EnqueueGolden(transport, "RoomJoined");
            Assert.That((await NextEventAsync(client)).Kind, Is.EqualTo(PollEventKind.RoomJoined));
            Assert.That(client.Phase, Is.EqualTo(ConnectionPhase.InRoom));
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
    }
}
