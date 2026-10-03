namespace SignalFish.Client.Tests.Polling
{
    using System;
    using System.Text;
    using NUnit.Framework;
    using SignalFish.Client.Core;
    using SignalFish.Client.Polling;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.Transport;
    using SignalFish.Client.V3;

    /// <summary>
    /// The mesh session fences (Rust client parity): a replayed plan whose
    /// generation was already superseded is rejected as a protocol violation
    /// without touching the machine, and an inbound <c>Signal</c> is absorbed
    /// as a benign race when no authoritative plan has arrived, when its
    /// generation is stale, or when its sender was retired by the live
    /// generation. Everything else surfaces untouched. Frames ride the real
    /// pipeline, so the delivery gate's roster stays consistent with the
    /// machine's.
    /// </summary>
    [TestFixture]
    public sealed class MeshFenceTests
    {
        /// <summary>
        /// One live session seat: the frame pipeline feeding both per-session
        /// components exactly as a client loop does (fact applied after the
        /// translation, event surfacing included).
        /// </summary>
        private sealed class LiveSession
        {
            internal const string RoomCode = "CODE01";
            internal static readonly Guid RoomId = new Guid("00000000-0000-0000-0000-00000000000d");

            internal DeliveryGate Gate { get; }

            internal SignalFishStateMachine Machine { get; }

            private LiveSession(DeliveryGate gate, SignalFishStateMachine machine)
            {
                Gate = gate;
                Machine = machine;
            }

            internal static LiveSession Joined()
            {
                LiveSession session = new LiveSession(
                    new DeliveryGate(DeliveryViolationPolicy.Observe),
                    new SignalFishStateMachine()
                );
                session.Machine.Apply(SessionEvent.Authenticated());
                /*
                    The negotiation settles the gate on the v3 contract (a
                    stamped roster is a v3 baseline, not a v2 exposure), then
                    the join baselines the roster.
                */
                session.Feed(
                    "{\"type\": \"ProtocolInfo\", \"data\": {\"capabilities\": [], "
                        + "\"game_data_formats\": [\"json\", \"message_pack\"], "
                        + "\"protocol_version\": 3, \"min_protocol_version\": 2, "
                        + "\"max_protocol_version\": 3}}"
                );
                FrameTranslation join = session.Feed(JoinWire());
                Assert.That(
                    join.HasFact,
                    Is.True,
                    "join fact: violation=" + join.Violation.Diagnostic
                );
                return session;
            }

            /// <summary>Translates one frame and applies its session fact.</summary>
            internal FrameTranslation Feed(string wire)
            {
                byte[] payload = Encoding.UTF8.GetBytes(wire);
                FramePipeline.Translate(
                    new TransportFrame(payload, isText: true),
                    payload.Length,
                    Gate,
                    Machine,
                    out FrameTranslation translated
                );
                if (translated.HasFact)
                {
                    Machine.Apply(translated.Fact);
                }

                return translated;
            }

            private static string JoinWire()
            {
                return "{\"type\": \"RoomJoined\", \"data\": {\"room_id\": \""
                    + WireGuid(RoomId)
                    + "\", \"room_code\": \""
                    + RoomCode
                    + "\", \"player_id\": \""
                    + WireGuid(LocalId)
                    + "\", \"game_name\": \"fence-game\", \"max_players\": 8, "
                    + "\"supports_authority\": false, \"current_players\": ["
                    + RosterEntry(LocalId, "Local")
                    + ", "
                    + RosterEntry(PeerId, "Peer")
                    + ", "
                    + RosterEntry(OtherPeerId, "Other")
                    + "], \"is_authority\": false, \"lobby_state\": \"waiting\", "
                    + "\"ready_players\": [], \"relay_type\": \"matchbox\", "
                    + "\"current_spectators\": []}}";
            }

            private static string RosterEntry(Guid playerId, string name)
            {
                return "{\"id\": \""
                    + WireGuid(playerId)
                    + "\", \"name\": \""
                    + name
                    + "\", \"is_authority\": false, \"is_ready\": false, "
                    + "\"epoch\": 1, \"seq\": 0}";
            }
        }

        private const string GenerationOne = "00000000-0000-0000-0000-000000000001";
        private const string GenerationTwo = "00000000-0000-0000-0000-000000000002";

        private static readonly Guid LocalId = new Guid("00000000-0000-0000-0000-00000000000a");
        private static readonly Guid PeerId = new Guid("00000000-0000-0000-0000-00000000000b");
        private static readonly Guid OtherPeerId = new Guid("00000000-0000-0000-0000-00000000000c");

        [TestCaseSource(nameof(SuppressionCases))]
        public void InboundSignalSuppressionFollowsThePlanState(
            string planWireOne,
            string planWireTwo,
            string rosterWire,
            Guid sender,
            string signalGeneration,
            bool expectSurfaced
        )
        {
            /*
                One table drives the whole suppression face: optional plan
                seeds (applied in order), an optional roster frame (PlayerLeft
                or NewPeer), then the probed signal. Surfaced means the typed
                Signal event arrives; absorbed means no event, fact, or
                violation at all.
            */
            LiveSession session = LiveSession.Joined();
            if (planWireOne is not null)
            {
                session.Feed(planWireOne);
            }

            if (planWireTwo is not null)
            {
                session.Feed(planWireTwo);
            }

            if (rosterWire is not null)
            {
                session.Feed(rosterWire);
            }

            FrameTranslation translated = session.Feed(SignalWire(sender, signalGeneration));
            Assert.That(
                translated.HasEvent,
                Is.EqualTo(expectSurfaced),
                "sender " + sender + " with generation " + signalGeneration
            );
            Assert.That(translated.HasFact, Is.False);
            Assert.That(translated.HasViolation, Is.False, translated.Violation.Diagnostic);
            if (expectSurfaced)
            {
                Assert.That(translated.Event.Kind, Is.EqualTo(PollEventKind.Signal));
                Assert.That(translated.Event.Signal.From, Is.EqualTo(sender));
            }
        }

        [Test]
        public void SupersededPlanReplayIsRejectedWithoutApplying()
        {
            LiveSession session = LiveSession.Joined();
            session.Feed(
                PlanWire(GenerationOne, SessionTopology.Mesh, SessionTransport.WebRtc, PeerId)
            );
            session.Feed(
                PlanWire(GenerationTwo, SessionTopology.Mesh, SessionTransport.WebRtc, PeerId)
            );

            FrameTranslation replay = session.Feed(
                PlanWire(GenerationOne, SessionTopology.Mesh, SessionTransport.WebRtc, PeerId)
            );

            Assert.That(replay.HasFact, Is.False, "a retired plan must never apply");
            Assert.That(replay.HasEvent, Is.True);
            Assert.That(replay.Event.Kind, Is.EqualTo(PollEventKind.ProtocolViolation));
            Assert.That(replay.Event.Violation, Is.EqualTo(MessageKind.SessionPlan));
            Assert.That(replay.Event.Diagnostic, Does.Contain(GenerationOne));
            Assert.That(session.Machine.SessionGeneration, Is.EqualTo(GenerationTwo));
        }

        [Test]
        public void RetiredGenerationFenceBoundsChurn()
        {
            LiveSession session = LiveSession.Joined();
            string current = GenerationOne;
            session.Feed(PlanWire(current, SessionTopology.Mesh, SessionTransport.WebRtc, PeerId));

            /*
                Re-plans retire the outgoing generation; the fence keeps the
                most recent eight. The current generation is never retired,
                the newest retired one still is, and one older than the fence
                degrades to a fresh plan (Rust parity).
            */
            for (int i = 2; i <= 10; i++)
            {
                string next =
                    "00000000-0000-0000-0000-"
                    + i.ToString("D12", System.Globalization.CultureInfo.InvariantCulture);
                session.Feed(PlanWire(next, SessionTopology.Mesh, SessionTransport.WebRtc, PeerId));
                Assert.That(session.Machine.IsSessionPlanSuperseded(current), Is.True, current);
                Assert.That(session.Machine.IsSessionPlanSuperseded(next), Is.False);
                current = next;
            }

            string newestRetired = "00000000-0000-0000-0000-000000000009";
            string fencedOut = "00000000-0000-0000-0000-000000000001";
            Assert.That(session.Machine.IsSessionPlanSuperseded(newestRetired), Is.True);
            Assert.That(session.Machine.IsSessionPlanSuperseded(fencedOut), Is.False);
        }

        [Test]
        public void MembershipBoundaryClearsTheRetirementState()
        {
            LiveSession session = LiveSession.Joined();
            session.Feed(
                PlanWire(GenerationOne, SessionTopology.Mesh, SessionTransport.WebRtc, PeerId)
            );

            /*
                The server-initiated removal retires the departed peer, and a
                re-baseline (leave tolerated, fresh join, fresh plan) clears
                the whole retirement state: a replayed pre-boundary plan is a
                fresh authoritative view, and the re-added peer's live signal
                surfaces.
            */
            FrameTranslation departure = session.Feed(PlayerLeftWire(PeerId));
            Assert.That(departure.HasEvent, Is.True);
            Assert.That(departure.Event.Kind, Is.EqualTo(PollEventKind.PlayerLeft));

            session.Machine.Apply(SessionEvent.From(SessionEventKind.RoomLeft));
            session.Machine.Apply(
                SessionEvent.Joined(
                    SessionEventKind.RoomJoined,
                    new RoomMembership(
                        RoomRole.Player,
                        LocalId,
                        LiveSession.RoomId,
                        LiveSession.RoomCode
                    )
                )
            );
            session.Feed(
                PlanWire(GenerationTwo, SessionTopology.Mesh, SessionTransport.WebRtc, PeerId)
            );

            Assert.That(session.Machine.IsSessionPlanSuperseded(GenerationOne), Is.False);
            FrameTranslation revived = session.Feed(SignalWire(PeerId, GenerationTwo));
            Assert.That(revived.HasEvent, Is.True);
            Assert.That(revived.Event.Kind, Is.EqualTo(PollEventKind.Signal));
        }

        [Test]
        public void DirectTransportDepartureDoesNotRetireThePeer()
        {
            /*
                Peer retirement exists for WebRTC in-flight races; a direct
                session has no relayed-signal fallback, so its departure is
                only a roster change (Rust parity).
            */
            LiveSession session = LiveSession.Joined();
            session.Feed(
                PlanWire(GenerationOne, SessionTopology.Host, SessionTransport.Direct, PeerId)
            );

            session.Feed(PlayerLeftWire(PeerId));
            FrameTranslation late = session.Feed(SignalWire(PeerId, GenerationOne));
            Assert.That(late.HasEvent, Is.True, "no retirement was armed on a direct session");
        }

        private static string PlanWire(
            string generation,
            SessionTopology topology,
            SessionTransport transport,
            params Guid[] peers
        )
        {
            string peerList = "\"peers\": [";
            for (int i = 0; i < peers.Length; i++)
            {
                if (i > 0)
                {
                    peerList += ", ";
                }

                peerList +=
                    "{\"player_id\": \""
                    + WireGuid(peers[i])
                    + "\", "
                    + "\"player_name\": \"P"
                    + i
                    + "\", \"is_authority\": false, "
                    + "\"initiate\": true}";
            }

            peerList += "]";
            string host =
                topology == SessionTopology.Host
                    ? ", \"host\": \"" + WireGuid(PeerId) + "\""
                    : string.Empty;
            return "{\"type\": \"SessionPlan\", \"data\": {\"generation\": \""
                + generation
                + "\", \"topology\": \""
                + TopologyToken(topology)
                + "\", \"transport\": \""
                + TransportToken(transport)
                + "\""
                + host
                + ", "
                + peerList
                + ", \"fallback\": \"relay\"}}";
        }

        private static string SignalWire(Guid from, string generation)
        {
            return "{\"type\": \"Signal\", \"data\": {\"from\": \""
                + WireGuid(from)
                + "\", \"generation\": \""
                + generation
                + "\", \"signal\": {\"Offer\": \"v=0\"}}}";
        }

        private static string PlayerLeftWire(Guid playerId)
        {
            return "{\"type\": \"PlayerLeft\", \"data\": {\"player_id\": \""
                + WireGuid(playerId)
                + "\", \"epoch\": 1, \"final_seq\": 0}}";
        }

        private static string NewPeerWire(Guid peerId)
        {
            return "{\"type\": \"NewPeer\", \"data\": {\"peer_id\": \""
                + WireGuid(peerId)
                + "\", \"you_initiate\": true}}";
        }

        private static string WireGuid(Guid value)
        {
            return value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string TopologyToken(SessionTopology topology)
        {
            return topology switch
            {
                SessionTopology.Relay => "relay",
                SessionTopology.Host => "host",
                _ => "mesh",
            };
        }

        private static string TransportToken(SessionTransport transport)
        {
            return transport switch
            {
                SessionTransport.Relay => "relay",
                SessionTransport.Direct => "direct",
                _ => "webrtc",
            };
        }

        private static System.Collections.Generic.IEnumerable<TestCaseData> SuppressionCases()
        {
            string meshPlan = PlanWire(
                GenerationOne,
                SessionTopology.Mesh,
                SessionTransport.WebRtc,
                PeerId,
                OtherPeerId
            );
            string replan = PlanWire(
                GenerationTwo,
                SessionTopology.Mesh,
                SessionTransport.WebRtc,
                PeerId
            );
            /*
                A same-generation re-plan that drops a peer retires the
                dropped sender on a WebRTC session; on a direct session the
                drop is only a roster change.
            */
            string droppedPeerPlan = PlanWire(
                GenerationOne,
                SessionTopology.Mesh,
                SessionTransport.WebRtc,
                PeerId
            );
            string directPlan = PlanWire(
                GenerationOne,
                SessionTopology.Host,
                SessionTransport.Direct,
                PeerId,
                OtherPeerId
            );
            string directDroppedPeerPlan = PlanWire(
                GenerationOne,
                SessionTopology.Host,
                SessionTransport.Direct,
                PeerId
            );
            yield return new TestCaseData(null, null, null, PeerId, GenerationOne, false).SetName(
                "BeforeAnyPlan.Absorbed"
            );
            yield return new TestCaseData(
                meshPlan,
                null,
                null,
                PeerId,
                GenerationOne,
                true
            ).SetName("LiveGeneration.PlanPeer.Surfaces");
            yield return new TestCaseData(
                meshPlan,
                replan,
                null,
                PeerId,
                GenerationOne,
                false
            ).SetName("RetiredGeneration.Absorbed");
            yield return new TestCaseData(
                meshPlan,
                replan,
                null,
                PeerId,
                GenerationTwo,
                true
            ).SetName("LiveGenerationAfterReplan.Surfaces");
            yield return new TestCaseData(
                meshPlan,
                droppedPeerPlan,
                null,
                OtherPeerId,
                GenerationOne,
                false
            ).SetName("SameGenerationReplanDrop.Absorbed");
            yield return new TestCaseData(
                meshPlan,
                droppedPeerPlan,
                null,
                PeerId,
                GenerationOne,
                true
            ).SetName("SameGenerationReplanSurvivor.Surfaces");
            yield return new TestCaseData(
                directPlan,
                directDroppedPeerPlan,
                null,
                OtherPeerId,
                GenerationOne,
                true
            ).SetName("SameGenerationReplanDropOnDirect.Surfaces");
            yield return new TestCaseData(
                meshPlan,
                replan,
                null,
                OtherPeerId,
                GenerationTwo,
                true
            ).SetName("GenerationChangeDoesNotCarryRetirement.Surfaces");
            yield return new TestCaseData(
                meshPlan,
                null,
                PlayerLeftWire(OtherPeerId),
                OtherPeerId,
                GenerationOne,
                false
            ).SetName("DepartedPeer.Absorbed");
            yield return new TestCaseData(
                meshPlan,
                null,
                PlayerLeftWire(OtherPeerId),
                PeerId,
                GenerationOne,
                true
            ).SetName("Departure.RetiresOnlyDeparted");
            yield return new TestCaseData(
                meshPlan,
                null,
                NewPeerWire(OtherPeerId),
                OtherPeerId,
                GenerationOne,
                true
            ).SetName("ReaddedPeer.Surfaces");
            yield return new TestCaseData(
                PlanWire(GenerationOne, SessionTopology.Mesh, SessionTransport.WebRtc, PeerId),
                null,
                null,
                OtherPeerId,
                GenerationOne,
                true
            ).SetName("UnknownSender.Surfaces");
        }
    }
}
