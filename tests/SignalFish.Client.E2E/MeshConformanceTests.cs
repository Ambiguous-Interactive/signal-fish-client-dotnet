namespace SignalFish.Client.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using NUnit.Framework;
    using SignalFish.Client.Core;
    using SignalFish.Client.Polling;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.V3;

    /// <summary>
    /// The server's worked v3 sessions (docs/scenarios: mesh + WebRTC, host
    /// topology, host failover) driven by the real client stack —
    /// SignalFishPollingClient over WebSocketTransport on the /v3 endpoint,
    /// every member advertising the WebRTC capabilities the scenario needs.
    /// The server brokers the signaling only; the data-path states are
    /// reported, not simulated, so every assertion lands on plan obedience,
    /// signal relay, status fan-out, and the relay floor that never closes.
    /// Requires the deployment's desired topology to admit the non-relay
    /// rungs (the e2e workflow sets the mesh ceiling).
    /// </summary>
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class MeshConformanceTests
    {
        private static readonly string[] WebrtcTransports = { "relay", "direct", "webrtc" };
        private static readonly string[] MeshTopologies = { "relay", "host", "mesh" };
        private static readonly string[] HostTopologies = { "relay", "host" };
        private static readonly string[] RelayOnlyTransports = { "relay" };
        private static readonly string[] RelayOnlyTopologies = { "relay" };

        [OneTimeSetUp]
        public void RequireLiveServer()
        {
            if (!E2EEnvironment.IsConfigured)
            {
                Assert.Ignore(
                    "SIGNALFISH_E2E_URL is not set; the mesh scenarios need a live server "
                        + "(scripts/run-e2e.ps1)."
                );
            }
        }

        /// <summary>
        /// Mesh + WebRTC: two v3 clients finalize to a mesh plan whose
        /// per-recipient glare flags follow the lesser-UUID offerer rule,
        /// signals relay verbatim both ways under the shared generation,
        /// transport status fans out to the peer, and the relay floor
        /// carries game data before, through, and after the fallback
        /// report.
        /// </summary>
        [Test]
        public async Task MeshWebrtcSessionSignalsThenFallsBackToTheRelayFloor()
        {
            SignalFishPollingClient alice = await E2EHarness.ConnectV3ClientAsync(
                WebrtcTransports,
                MeshTopologies
            );
            SignalFishPollingClient bob = await E2EHarness.ConnectV3ClientAsync(
                WebrtcTransports,
                MeshTopologies
            );

            try
            {
                string gameName = E2EHarness.GameName();
                RoomMembership aliceSeat = await E2EHarness.JoinRoomAsync(alice, gameName, "alice");
                RoomMembership bobSeat = await E2EHarness.JoinRoomAsync(
                    bob,
                    gameName,
                    "bob",
                    roomCode: aliceSeat.RoomCode
                );
                await E2EHarness.SetReadyAsync(alice);
                await E2EHarness.SetReadyAsync(bob, expectAllReady: true);
                Assert.That(alice.SendStartGame().Accepted, Is.True);

                /*
                    Finalize emits GameStarting plus the per-recipient plan;
                    one combined wait per client classifies both frames
                    without betting on socket-write interleaving.
                */
                List<PollEvent> aliceStart = await E2EHarness.WaitForEventsAsync(
                    alice,
                    e => e.Kind is PollEventKind.GameStarting or PollEventKind.SessionPlan,
                    2
                );
                List<PollEvent> bobStart = await E2EHarness.WaitForEventsAsync(
                    bob,
                    e => e.Kind is PollEventKind.GameStarting or PollEventKind.SessionPlan,
                    2
                );
                SessionPlanMessage alicePlan = SinglePlan(aliceStart);
                SessionPlanMessage bobPlan = SinglePlan(bobStart);

                Assert.That(alicePlan.Topology, Is.EqualTo(SessionTopology.Mesh));
                Assert.That(alicePlan.Transport, Is.EqualTo(SessionTransport.WebRtc));
                Assert.That(alicePlan.Host, Is.Null);
                Assert.That(alicePlan.Fallback, Is.EqualTo(SessionTransport.Relay));
                Assert.That(
                    alicePlan.Generation,
                    Is.Not.Null.And.Not.Empty,
                    "the plan publication carries the generation the signals stamp"
                );
                Assert.That(
                    bobPlan.Generation,
                    Is.EqualTo(alicePlan.Generation),
                    "the room-wide publication shares one generation"
                );

                AssertPlanPeer(
                    alicePlan,
                    bobSeat.PlayerId,
                    expectInitiate: InitiatesFirst(bobSeat.PlayerId, aliceSeat.PlayerId)
                );
                AssertPlanPeer(
                    bobPlan,
                    aliceSeat.PlayerId,
                    expectInitiate: InitiatesFirst(aliceSeat.PlayerId, bobSeat.PlayerId)
                );

                MeshSession aliceView = Track(aliceStart);
                MeshSession bobView = Track(bobStart);
                Assert.That(aliceView.IsP2p, Is.True);
                Assert.That(aliceView.Peers.Single().PlayerId, Is.EqualTo(bobSeat.PlayerId));

                /*
                    The offerer relays an offer, the answerer relays an
                    answer, and one ICE candidate trickles each way — every
                    payload verbatim, every frame stamped with the live
                    generation.
                */
                Guid offererSeat = InitiatesFirst(aliceSeat.PlayerId, bobSeat.PlayerId)
                    ? aliceSeat.PlayerId
                    : bobSeat.PlayerId;
                SignalFishPollingClient offerer = offererSeat == aliceSeat.PlayerId ? alice : bob;
                SignalFishPollingClient answerer = offerer == alice ? bob : alice;
                Guid offererId = offererSeat;
                Guid answererId = offerer == alice ? bobSeat.PlayerId : aliceSeat.PlayerId;

                Assert.That(
                    offerer
                        .SendSignal(
                            new SignalMessage(
                                Wire(answererId),
                                alicePlan.Generation!,
                                Encoding.UTF8.GetBytes("{\"Offer\": \"v=0 offer\"}")
                            )
                        )
                        .Accepted,
                    Is.True
                );
                await AssertSignalArrivesAsync(
                    answerer,
                    offererId,
                    alicePlan.Generation!,
                    "{\"Offer\": \"v=0 offer\"}"
                );

                Assert.That(
                    answerer
                        .SendSignal(
                            new SignalMessage(
                                Wire(offererId),
                                bobPlan.Generation!,
                                Encoding.UTF8.GetBytes("{\"Answer\": \"v=0 answer\"}")
                            )
                        )
                        .Accepted,
                    Is.True
                );
                await AssertSignalArrivesAsync(
                    offerer,
                    answererId,
                    bobPlan.Generation!,
                    "{\"Answer\": \"v=0 answer\"}"
                );

                Assert.That(
                    offerer
                        .SendSignal(
                            new SignalMessage(
                                Wire(answererId),
                                alicePlan.Generation!,
                                Encoding.UTF8.GetBytes(
                                    "{\"IceCandidate\": \"candidate:1 1 UDP host\"}"
                                )
                            )
                        )
                        .Accepted,
                    Is.True
                );
                await AssertSignalArrivesAsync(
                    answerer,
                    offererId,
                    alicePlan.Generation!,
                    "{\"IceCandidate\": \"candidate:1 1 UDP host\"}"
                );

                /*
                    The data path "opens": both report connected webrtc, and
                    each server fans the report out to the other member.
                */
                Assert.That(
                    alice.SendTransportStatus(new TransportStatusMessage("webrtc", true)).Accepted,
                    Is.True
                );
                Assert.That(
                    bob.SendTransportStatus(new TransportStatusMessage("webrtc", true)).Accepted,
                    Is.True
                );
                await E2EHarness.WaitForEventAsync(
                    alice,
                    e =>
                        e.Kind == PollEventKind.PeerTransportStatus
                        && e.PeerTransportStatus.PeerId == bobSeat.PlayerId
                        && e.PeerTransportStatus.Connected
                );
                await E2EHarness.WaitForEventAsync(
                    bob,
                    e =>
                        e.Kind == PollEventKind.PeerTransportStatus
                        && e.PeerTransportStatus.PeerId == aliceSeat.PlayerId
                        && e.PeerTransportStatus.Connected
                );

                /*
                    The relay floor never closed: game data still crosses the
                    server after the peer-to-peer path "dies" (the fallback
                    report), unchanged in shape.
                */
                Assert.That(
                    bob.SendTransportStatus(new TransportStatusMessage("webrtc", false)).Accepted,
                    Is.True
                );
                await E2EHarness.WaitForEventAsync(
                    alice,
                    e =>
                        e.Kind == PollEventKind.PeerTransportStatus
                        && e.PeerTransportStatus.PeerId == bobSeat.PlayerId
                        && !e.PeerTransportStatus.Connected
                );
                Assert.That(
                    E2EHarness.SendRelayPayload(bob, @"{""after"": ""fallback""}").Accepted,
                    Is.True
                );
                PollEvent relayed = await E2EHarness.WaitForEventAsync(
                    alice,
                    e => e.Kind == PollEventKind.GameData
                );
                Assert.That(relayed.GameData.FromPlayer, Is.EqualTo(bobSeat.PlayerId));
                Assert.That(
                    E2EHarness.PayloadJsonEquals(
                        relayed.GameData.Payload.Span,
                        @"{""after"": ""fallback""}"
                    ),
                    Is.True
                );

                await LeaveAsync(alice);
                await LeaveAsync(bob);
            }
            finally
            {
                await alice.DisposeAsync();
                await bob.DisposeAsync();
            }
        }

        /// <summary>
        /// Host + WebRTC: a three-member authority room finalizes to a star
        /// around the elected host (the room authority). Each client's plan
        /// names only the host with initiate true, the host's plan lists
        /// every client with initiate false, the shared generation is
        /// identical, and clients signal the host — never each other.
        /// </summary>
        [Test]
        public async Task HostTopologyStarsEveryClientAroundTheElectedHost()
        {
            SignalFishPollingClient alice = await E2EHarness.ConnectV3ClientAsync(
                WebrtcTransports,
                HostTopologies
            );
            SignalFishPollingClient bob = await E2EHarness.ConnectV3ClientAsync(
                WebrtcTransports,
                HostTopologies
            );
            SignalFishPollingClient carol = await E2EHarness.ConnectV3ClientAsync(
                WebrtcTransports,
                HostTopologies
            );

            try
            {
                string gameName = E2EHarness.GameName();
                RoomMembership aliceSeat = await E2EHarness.JoinRoomAsync(
                    alice,
                    gameName,
                    "alice",
                    supportsAuthority: true
                );
                RoomMembership bobSeat = await E2EHarness.JoinRoomAsync(
                    bob,
                    gameName,
                    "bob",
                    roomCode: aliceSeat.RoomCode
                );
                RoomMembership carolSeat = await E2EHarness.JoinRoomAsync(
                    carol,
                    gameName,
                    "carol",
                    roomCode: aliceSeat.RoomCode
                );
                await E2EHarness.SetReadyAsync(alice);
                await E2EHarness.SetReadyAsync(bob);
                await E2EHarness.SetReadyAsync(carol, expectAllReady: true);
                Assert.That(alice.SendStartGame().Accepted, Is.True);

                List<PollEvent> aliceStart = await E2EHarness.WaitForEventsAsync(
                    alice,
                    e => e.Kind is PollEventKind.GameStarting or PollEventKind.SessionPlan,
                    2
                );
                List<PollEvent> bobStart = await E2EHarness.WaitForEventsAsync(
                    bob,
                    e => e.Kind is PollEventKind.GameStarting or PollEventKind.SessionPlan,
                    2
                );
                List<PollEvent> carolStart = await E2EHarness.WaitForEventsAsync(
                    carol,
                    e => e.Kind is PollEventKind.GameStarting or PollEventKind.SessionPlan,
                    2
                );
                SessionPlanMessage hostPlan = SinglePlan(aliceStart);
                SessionPlanMessage bobPlan = SinglePlan(bobStart);
                SessionPlanMessage carolPlan = SinglePlan(carolStart);

                Assert.That(hostPlan.Topology, Is.EqualTo(SessionTopology.Host));
                Assert.That(hostPlan.Transport, Is.EqualTo(SessionTransport.WebRtc));
                Assert.That(hostPlan.Host, Is.EqualTo(aliceSeat.PlayerId));
                Assert.That(
                    hostPlan.Generation,
                    Is.EqualTo(bobPlan.Generation).And.EqualTo(carolPlan.Generation)
                );

                /*
                    The star: the host answers every client; every client
                    offers to the host and names it is_authority.
                */
                Assert.That(hostPlan.Peers, Has.Count.EqualTo(2));
                Assert.That(
                    hostPlan.Peers.Any(p => p.PlayerId == bobSeat.PlayerId && !p.Initiate),
                    Is.True
                );
                Assert.That(
                    hostPlan.Peers.Any(p => p.PlayerId == carolSeat.PlayerId && !p.Initiate),
                    Is.True
                );
                AssertPlanPeer(bobPlan, aliceSeat.PlayerId, expectInitiate: true);
                Assert.That(bobPlan.Peers[0].IsAuthority, Is.True);
                AssertPlanPeer(carolPlan, aliceSeat.PlayerId, expectInitiate: true);
                Assert.That(carolPlan.Peers[0].IsAuthority, Is.True);

                MeshSession hostView = Track(aliceStart);
                Assert.That(hostView.Host, Is.EqualTo(aliceSeat.PlayerId));
                Assert.That(hostView.Peers, Has.Count.EqualTo(2));

                /*
                    Star signaling: each client offers to the host and the
                    host answers — two disjoint client/host edges.
                */
                foreach (
                    (SignalFishPollingClient client, RoomMembership seat, string name) in new[]
                    {
                        (bob, bobSeat, "bob"),
                        (carol, carolSeat, "carol"),
                    }
                )
                {
                    Assert.That(
                        client
                            .SendSignal(
                                new SignalMessage(
                                    Wire(aliceSeat.PlayerId),
                                    hostPlan.Generation!,
                                    Encoding.UTF8.GetBytes("{\"Offer\": \"v=0 from " + name + "\"}")
                                )
                            )
                            .Accepted,
                        Is.True
                    );
                    await AssertSignalArrivesAsync(
                        alice,
                        seat.PlayerId,
                        hostPlan.Generation!,
                        "{\"Offer\": \"v=0 from " + name + "\"}"
                    );
                    Assert.That(
                        alice
                            .SendSignal(
                                new SignalMessage(
                                    Wire(seat.PlayerId),
                                    hostPlan.Generation!,
                                    Encoding.UTF8.GetBytes("{\"Answer\": \"v=0 to " + name + "\"}")
                                )
                            )
                            .Accepted,
                        Is.True
                    );
                    await AssertSignalArrivesAsync(
                        client,
                        aliceSeat.PlayerId,
                        hostPlan.Generation!,
                        "{\"Answer\": \"v=0 to " + name + "\"}"
                    );
                }

                await LeaveAsync(alice);
                await LeaveAsync(bob);
                await LeaveAsync(carol);
            }
            finally
            {
                await alice.DisposeAsync();
                await bob.DisposeAsync();
                await carol.DisposeAsync();
            }
        }

        /// <summary>
        /// Host failover: the elected host departs, the survivors receive
        /// the departure plus a fresh per-recipient plan (sticky host +
        /// webrtc, re-elected host, new generation), and the new star edge
        /// carries signals while the relay floor stays continuous.
        /// </summary>
        [Test]
        public async Task HostFailoverReelectsAndReplansTheSurvivors()
        {
            SignalFishPollingClient alice = await E2EHarness.ConnectV3ClientAsync(
                WebrtcTransports,
                HostTopologies
            );
            SignalFishPollingClient bob = await E2EHarness.ConnectV3ClientAsync(
                WebrtcTransports,
                HostTopologies
            );
            SignalFishPollingClient carol = await E2EHarness.ConnectV3ClientAsync(
                WebrtcTransports,
                HostTopologies
            );

            try
            {
                string gameName = E2EHarness.GameName();
                RoomMembership aliceSeat = await E2EHarness.JoinRoomAsync(
                    alice,
                    gameName,
                    "alice",
                    supportsAuthority: true
                );
                RoomMembership bobSeat = await E2EHarness.JoinRoomAsync(
                    bob,
                    gameName,
                    "bob",
                    roomCode: aliceSeat.RoomCode
                );
                RoomMembership carolSeat = await E2EHarness.JoinRoomAsync(
                    carol,
                    gameName,
                    "carol",
                    roomCode: aliceSeat.RoomCode
                );
                await E2EHarness.SetReadyAsync(alice);
                await E2EHarness.SetReadyAsync(bob);
                await E2EHarness.SetReadyAsync(carol, expectAllReady: true);
                Assert.That(alice.SendStartGame().Accepted, Is.True);

                SessionPlanMessage firstPlan = await E2EHarness.WaitForPlanAsync(alice);
                await E2EHarness.WaitForPlanAsync(bob);
                await E2EHarness.WaitForPlanAsync(carol);

                /*
                    The host departs cleanly; the server re-elects over the
                    remaining electable members (authority preferred, then
                    earliest joiner — bob joined before carol).
                */
                await LeaveAsync(alice);
                List<PollEvent> bobFollow = await E2EHarness.WaitForEventsAsync(
                    bob,
                    e => e.Kind is PollEventKind.PlayerLeft or PollEventKind.SessionPlan,
                    2
                );
                List<PollEvent> carolFollow = await E2EHarness.WaitForEventsAsync(
                    carol,
                    e => e.Kind is PollEventKind.PlayerLeft or PollEventKind.SessionPlan,
                    2
                );

                Assert.That(
                    bobFollow.Any(e =>
                        e.Kind == PollEventKind.PlayerLeft && e.LeftPlayerId == aliceSeat.PlayerId
                    ),
                    Is.True
                );
                SessionPlanMessage bobPlan = SinglePlan(bobFollow);
                SessionPlanMessage carolPlan = SinglePlan(carolFollow);

                Assert.That(bobPlan.Topology, Is.EqualTo(SessionTopology.Host));
                Assert.That(bobPlan.Transport, Is.EqualTo(SessionTransport.WebRtc));
                Assert.That(bobPlan.Host, Is.EqualTo(bobSeat.PlayerId));
                Assert.That(
                    bobPlan.Generation,
                    Is.Not.EqualTo(firstPlan.Generation),
                    "the re-plan supersedes the departed session"
                );
                Assert.That(bobPlan.Generation, Is.EqualTo(carolPlan.Generation));
                AssertPlanPeer(bobPlan, carolSeat.PlayerId, expectInitiate: false);
                AssertPlanPeer(carolPlan, bobSeat.PlayerId, expectInitiate: true);

                /*
                    The fresh star edge carries signals under the new
                    generation, and the relay floor stayed continuous
                    throughout the failover.
                */
                Assert.That(
                    carol
                        .SendSignal(
                            new SignalMessage(
                                Wire(bobSeat.PlayerId),
                                carolPlan.Generation!,
                                Encoding.UTF8.GetBytes("{\"Offer\": \"v=0 reelect\"}")
                            )
                        )
                        .Accepted,
                    Is.True
                );
                await AssertSignalArrivesAsync(
                    bob,
                    carolSeat.PlayerId,
                    bobPlan.Generation!,
                    "{\"Offer\": \"v=0 reelect\"}"
                );

                Assert.That(
                    E2EHarness.SendRelayPayload(carol, @"{""after"": ""failover""}").Accepted,
                    Is.True
                );
                PollEvent relayed = await E2EHarness.WaitForEventAsync(
                    bob,
                    e => e.Kind == PollEventKind.GameData
                );
                Assert.That(relayed.GameData.FromPlayer, Is.EqualTo(carolSeat.PlayerId));

                await LeaveAsync(bob);
                await LeaveAsync(carol);
            }
            finally
            {
                await alice.DisposeAsync();
                await bob.DisposeAsync();
                await carol.DisposeAsync();
            }
        }

        /// <summary>
        /// A relay-only room still publishes one authoritative result at
        /// finalize: every v3 member receives the explicit relay-floor plan
        /// (no peers, fallback relay), so finalization always has a pairing
        /// verdict.
        /// </summary>
        [Test]
        public async Task RelayFloorRoomPublishesAnExplicitRelayPlan()
        {
            SignalFishPollingClient alice = await E2EHarness.ConnectV3ClientAsync(
                RelayOnlyTransports,
                RelayOnlyTopologies
            );
            SignalFishPollingClient bob = await E2EHarness.ConnectV3ClientAsync(
                RelayOnlyTransports,
                RelayOnlyTopologies
            );

            try
            {
                string gameName = E2EHarness.GameName();
                RoomMembership aliceSeat = await E2EHarness.JoinRoomAsync(alice, gameName, "alice");
                await E2EHarness.JoinRoomAsync(bob, gameName, "bob", roomCode: aliceSeat.RoomCode);
                await E2EHarness.SetReadyAsync(alice);
                await E2EHarness.SetReadyAsync(bob, expectAllReady: true);
                Assert.That(alice.SendStartGame().Accepted, Is.True);

                SessionPlanMessage alicePlan = await E2EHarness.WaitForPlanAsync(alice);
                SessionPlanMessage bobPlan = await E2EHarness.WaitForPlanAsync(bob);

                Assert.That(alicePlan.Topology, Is.EqualTo(SessionTopology.Relay));
                Assert.That(alicePlan.Transport, Is.EqualTo(SessionTransport.Relay));
                Assert.That(alicePlan.Peers, Is.Empty);
                Assert.That(alicePlan.Fallback, Is.EqualTo(SessionTransport.Relay));
                Assert.That(bobPlan.Generation, Is.EqualTo(alicePlan.Generation));
            }
            finally
            {
                await alice.DisposeAsync();
                await bob.DisposeAsync();
            }
        }

        /// <summary>The canonical wire form of a player id (Signal targets).</summary>
        private static string Wire(Guid playerId)
        {
            return playerId.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>The deterministic offerer rule: the lesser UUID offers.</summary>
        private static bool InitiatesFirst(Guid left, Guid right)
        {
            return string.CompareOrdinal(Wire(left), Wire(right)) < 0;
        }

        private static SessionPlanMessage SinglePlan(List<PollEvent> events)
        {
            return events.Single(e => e.Kind == PollEventKind.SessionPlan).SessionPlan;
        }

        private static void AssertPlanPeer(
            SessionPlanMessage plan,
            Guid peerId,
            bool expectInitiate
        )
        {
            Assert.That(plan.Peers, Has.Count.EqualTo(1));
            Assert.That(plan.Peers[0].PlayerId, Is.EqualTo(peerId));
            Assert.That(
                plan.Peers[0].Initiate,
                Is.EqualTo(expectInitiate),
                "peer " + peerId + " initiate flag"
            );
        }

        /// <summary>Folds the finalize events into the tracker and returns it.</summary>
        private static MeshSession Track(List<PollEvent> events)
        {
            MeshSession view = new MeshSession();
            PollEvent plan = events.Single(e => e.Kind == PollEventKind.SessionPlan);
            Assert.That(view.Apply(plan), Is.True, "a plan always re-asserts the view");
            return view;
        }

        private static async Task AssertSignalArrivesAsync(
            SignalFishPollingClient recipient,
            Guid from,
            string generation,
            string expectedSignalJson
        )
        {
            PollEvent signal = await E2EHarness.WaitForEventAsync(
                recipient,
                e => e.Kind == PollEventKind.Signal && e.Signal.From == from
            );
            Assert.That(signal.Signal.Generation, Is.EqualTo(generation));
            Assert.That(
                E2EHarness.PayloadJsonEquals(signal.Signal.Signal.Span, expectedSignalJson),
                Is.True,
                "the relayed signal must arrive verbatim"
            );
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
