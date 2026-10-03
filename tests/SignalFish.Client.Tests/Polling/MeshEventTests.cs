namespace SignalFish.Client.Tests.Polling
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using NUnit.Framework;
    using SignalFish.Client.Core;
    using SignalFish.Client.Polling;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.Transport;
    using SignalFish.Client.V3;

    /// <summary>
    /// M6.5 mesh anchors: every v3 mesh wire kind routes through the frame
    /// pipeline to exactly one typed event carrying the fully decoded
    /// payload (golden wire samples, inlined), a malformed variant of each
    /// kind surfaces as a protocol violation naming the message kind, the
    /// session plan rides with its plan-admission session fact, and the
    /// payload-only mesh kinds never apply a session fact.
    /// </summary>
    [TestFixture]
    public sealed class MeshEventTests
    {
        private const string Generation = "00000000-0000-0000-0000-00000000000c";
        private const string TurnUsername = "1700003600:00000000-0000-0000-0000-00000000000a";
        private const string TurnCredential = "7/zfauXrL6LSdBbV8YTfnCWafHk=";
        private const string MeshWebrtcPlanWire =
            "{\"type\": \"SessionPlan\", \"data\": {\"generation\": \""
            + Generation
            + "\", "
            + "\"topology\": \"mesh\", \"transport\": \"webrtc\", \"peers\": ["
            + "{\"player_id\": \"00000000-0000-0000-0000-00000000000b\", "
            + "\"player_name\": \"Bob\", \"is_authority\": false, \"initiate\": true}], "
            + "\"ice_servers\": [{\"urls\": [\"stun:stun.l.google.com:19302\"]}, "
            + "{\"urls\": [\"turn:turn.example.com:3478\"], \"username\": \""
            + TurnUsername
            + "\", \"credential\": \""
            + TurnCredential
            + "\"}], \"fallback\": \"relay\"}}";
        private const string RelayResetPlanWire =
            "{\"type\": \"SessionPlan\", \"data\": {\"generation\": \""
            + Generation
            + "\", "
            + "\"topology\": \"relay\", \"transport\": \"relay\", \"peers\": [], "
            + "\"fallback\": \"relay\"}}";
        private const string NonRelayFallbackPlanWire =
            "{\"type\": \"SessionPlan\", \"data\": {\"generation\": \""
            + Generation
            + "\", "
            + "\"topology\": \"relay\", \"transport\": \"relay\", \"peers\": [], "
            + "\"fallback\": \"direct\"}}";
        private const string SignalValue =
            "{\"Answer\": \"v=0\\r\\no=- 0 0 IN IP4 0.0.0.0\\r\\n...\"}";

        private static readonly Guid HostId = new Guid("00000000-0000-0000-0000-00000000000a");
        private static readonly Guid PeerId = new Guid("00000000-0000-0000-0000-00000000000b");
        private static readonly string[] StunUrls = { "stun:stun.l.google.com:19302" };
        private static readonly string[] TurnUrls = { "turn:turn.example.com:3478" };

        private static readonly string HostWebrtcPlanWire =
            "{\"type\": \"SessionPlan\", \"data\": {\"generation\": \""
            + Generation
            + "\", "
            + "\"topology\": \"host\", \"transport\": \"webrtc\", \"host\": \""
            + HostId
            + "\", \"peers\": [{\"player_id\": \""
            + HostId
            + "\", "
            + "\"player_name\": \"Alice\", \"is_authority\": true, \"initiate\": true}], "
            + "\"ice_servers\": [{\"urls\": [\"stun:stun.l.google.com:19302\"]}], "
            + "\"fallback\": \"relay\"}}";
        private static readonly string HostDirectPlanWire =
            "{\"type\": \"SessionPlan\", \"data\": {\"generation\": \""
            + Generation
            + "\", "
            + "\"topology\": \"host\", \"transport\": \"direct\", \"host\": \""
            + HostId
            + "\", \"direct_endpoint\": {\"host\": \"192.0.2.10\", \"port\": 7777}, "
            + "\"peers\": [{\"player_id\": \""
            + HostId
            + "\", "
            + "\"player_name\": \"Alice\", \"is_authority\": true, \"initiate\": true}], "
            + "\"fallback\": \"relay\"}}";
        private static readonly string NewPeerWire =
            "{\"type\": \"NewPeer\", \"data\": {\"peer_id\": \""
            + PeerId
            + "\", "
            + "\"you_initiate\": true}}";
        private static readonly string SignalWire =
            "{\"type\": \"Signal\", \"data\": {\"from\": \""
            + PeerId
            + "\", "
            + "\"generation\": \""
            + Generation
            + "\", "
            + "\"signal\": {\"Answer\": \"v=0\\r\\no=- 0 0 IN IP4 0.0.0.0\\r\\n...\"}}}";
        private static readonly string PeerTransportStatusWire =
            "{\"type\": \"PeerTransportStatus\", \"data\": {\"peer_id\": \""
            + PeerId
            + "\", "
            + "\"transport\": \"webrtc\", \"connected\": true}}";
        private static readonly string LegacyPlanWire =
            "{\"type\": \"SessionPlan\", \"data\": {\"topology\": \"mesh\", "
            + "\"transport\": \"webrtc\", \"peers\": ["
            + "{\"player_id\": \""
            + PeerId
            + "\", \"player_name\": \"Bob\", "
            + "\"is_authority\": false, \"initiate\": true}], \"fallback\": \"relay\"}}";

        [Test]
        public void TranslateSessionPlanMeshWebrtcDecodesPeersAndIce()
        {
            PollEvent ev = TranslateEvent(MeshWebrtcPlanWire);
            Assert.That(ev.Kind, Is.EqualTo(PollEventKind.SessionPlan));

            SessionPlanMessage plan = ev.SessionPlan;
            Assert.That(plan.Generation, Is.EqualTo(Generation));
            Assert.That(plan.Topology, Is.EqualTo(SessionTopology.Mesh));
            Assert.That(plan.Transport, Is.EqualTo(SessionTransport.WebRtc));
            Assert.That(plan.Host, Is.Null);
            Assert.That(plan.DirectEndpoint, Is.Null);

            Assert.That(plan.Peers, Has.Count.EqualTo(1));
            Assert.That(plan.Peers[0].PlayerId, Is.EqualTo(PeerId));
            Assert.That(plan.Peers[0].PlayerName, Is.EqualTo("Bob"));
            Assert.That(plan.Peers[0].IsAuthority, Is.False);
            Assert.That(plan.Peers[0].Initiate, Is.True);

            Assert.That(plan.IceServers, Has.Count.EqualTo(2));
            Assert.That(plan.IceServers[0].Urls, Is.EqualTo(StunUrls));
            Assert.That(plan.IceServers[0].Username, Is.Null);
            Assert.That(plan.IceServers[0].Credential, Is.Null);
            Assert.That(plan.IceServers[1].Urls, Is.EqualTo(TurnUrls));
            Assert.That(plan.IceServers[1].Username, Is.EqualTo(TurnUsername));
            Assert.That(plan.IceServers[1].Credential, Is.EqualTo(TurnCredential));

            Assert.That(plan.Fallback, Is.EqualTo(SessionTransport.Relay));
        }

        [Test]
        public void TranslateSessionPlanHostWebrtcDecodesHost()
        {
            PollEvent ev = TranslateEvent(HostWebrtcPlanWire);
            Assert.That(ev.Kind, Is.EqualTo(PollEventKind.SessionPlan));

            SessionPlanMessage plan = ev.SessionPlan;
            Assert.That(plan.Generation, Is.EqualTo(Generation));
            Assert.That(plan.Topology, Is.EqualTo(SessionTopology.Host));
            Assert.That(plan.Transport, Is.EqualTo(SessionTransport.WebRtc));
            Assert.That(plan.Host, Is.EqualTo(HostId));
            Assert.That(plan.DirectEndpoint, Is.Null);

            Assert.That(plan.Peers, Has.Count.EqualTo(1));
            Assert.That(plan.Peers[0].PlayerId, Is.EqualTo(HostId));
            Assert.That(plan.Peers[0].PlayerName, Is.EqualTo("Alice"));
            Assert.That(plan.Peers[0].IsAuthority, Is.True);
            Assert.That(plan.Peers[0].Initiate, Is.True);

            Assert.That(plan.IceServers, Has.Count.EqualTo(1));
            Assert.That(plan.IceServers[0].Urls, Is.EqualTo(StunUrls));
            Assert.That(plan.IceServers[0].Username, Is.Null);
            Assert.That(plan.IceServers[0].Credential, Is.Null);

            Assert.That(plan.Fallback, Is.EqualTo(SessionTransport.Relay));
        }

        [Test]
        public void TranslateSessionPlanHostDirectDecodesEndpoint()
        {
            PollEvent ev = TranslateEvent(HostDirectPlanWire);
            Assert.That(ev.Kind, Is.EqualTo(PollEventKind.SessionPlan));

            SessionPlanMessage plan = ev.SessionPlan;
            Assert.That(plan.Generation, Is.EqualTo(Generation));
            Assert.That(plan.Topology, Is.EqualTo(SessionTopology.Host));
            Assert.That(plan.Transport, Is.EqualTo(SessionTransport.Direct));
            Assert.That(plan.Host, Is.EqualTo(HostId));

            Assert.That(plan.DirectEndpoint, Is.Not.Null);
            Assert.That(plan.DirectEndpoint.GetValueOrDefault().Host, Is.EqualTo("192.0.2.10"));
            Assert.That(plan.DirectEndpoint.GetValueOrDefault().Port, Is.EqualTo(7777u));

            Assert.That(plan.Peers, Has.Count.EqualTo(1));
            Assert.That(plan.Peers[0].PlayerId, Is.EqualTo(HostId));
            Assert.That(plan.Peers[0].IsAuthority, Is.True);
            Assert.That(plan.Peers[0].Initiate, Is.True);

            Assert.That(plan.IceServers, Is.Empty);
            Assert.That(plan.Fallback, Is.EqualTo(SessionTransport.Relay));
        }

        [Test]
        public void TranslateSessionPlanRelayResetDecodesEmptyPeers()
        {
            PollEvent ev = TranslateEvent(RelayResetPlanWire);
            Assert.That(ev.Kind, Is.EqualTo(PollEventKind.SessionPlan));

            SessionPlanMessage plan = ev.SessionPlan;
            Assert.That(plan.Generation, Is.EqualTo(Generation));
            Assert.That(plan.Topology, Is.EqualTo(SessionTopology.Relay));
            Assert.That(plan.Transport, Is.EqualTo(SessionTransport.Relay));
            Assert.That(plan.Host, Is.Null);
            Assert.That(plan.DirectEndpoint, Is.Null);
            Assert.That(plan.Peers, Is.Empty);
            Assert.That(plan.IceServers, Is.Empty);
            Assert.That(plan.Fallback, Is.EqualTo(SessionTransport.Relay));
        }

        [Test]
        public void TranslateNewPeerDecodesPeerAndInitiate()
        {
            PollEvent ev = TranslateEvent(NewPeerWire);
            Assert.That(ev.Kind, Is.EqualTo(PollEventKind.NewPeer));
            Assert.That(ev.NewPeer.PeerId, Is.EqualTo(PeerId));
            Assert.That(ev.NewPeer.YouInitiate, Is.True);
        }

        [Test]
        public void TranslateSignalDecodesFromGenerationAndVerbatimBytes()
        {
            PollEvent ev = TranslateEvent(SignalWire);
            Assert.That(ev.Kind, Is.EqualTo(PollEventKind.Signal));
            Assert.That(ev.Signal.From, Is.EqualTo(PeerId));
            Assert.That(ev.Signal.Generation, Is.EqualTo(Generation));
            Assert.That(
                ev.Signal.Signal.ToArray(),
                Is.EqualTo(Encoding.UTF8.GetBytes(SignalValue))
            );
        }

        [Test]
        public void TranslatePeerTransportStatusDecodesFields()
        {
            PollEvent ev = TranslateEvent(PeerTransportStatusWire);
            Assert.That(ev.Kind, Is.EqualTo(PollEventKind.PeerTransportStatus));
            Assert.That(ev.PeerTransportStatus.PeerId, Is.EqualTo(PeerId));
            Assert.That(ev.PeerTransportStatus.Transport, Is.EqualTo("webrtc"));
            Assert.That(ev.PeerTransportStatus.Connected, Is.True);
        }

        [Test]
        public void TranslateSessionPlanWithoutGenerationDecodesLegacyShape()
        {
            /*
                Server 0.4 emitted v3 plans without a generation; the decode
                stays tolerant (Rust parity) and the machine's generation
                fence stands down when none arrived.
            */
            PollEvent ev = TranslateEvent(LegacyPlanWire);
            Assert.That(ev.Kind, Is.EqualTo(PollEventKind.SessionPlan));
            Assert.That(ev.SessionPlan.Generation, Is.Null);
            Assert.That(ev.SessionPlan.Topology, Is.EqualTo(SessionTopology.Mesh));
            Assert.That(ev.SessionPlan.Peers, Has.Count.EqualTo(1));
        }

        [TestCaseSource(nameof(MalformedMeshWires))]
        public void MalformedMeshKindSurfacesProtocolViolation(string wire, MessageKind kind)
        {
            byte[] payload = Encoding.UTF8.GetBytes(wire);
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Observe);
            FramePipeline.Translate(
                new TransportFrame(payload, isText: true),
                payload.Length,
                gate,
                out FrameTranslation translated
            );

            Assert.That(translated.HasEvent, Is.True);
            Assert.That(translated.Event.Kind, Is.EqualTo(PollEventKind.ProtocolViolation));
            Assert.That(translated.Event.Violation, Is.EqualTo(kind));
            Assert.That(translated.HasFact, Is.False);
        }

        [Test]
        public void TranslateSessionPlanAppliesThePlanFact()
        {
            /*
                The plan doubles as the signal-admission session fact
                (latest-wins, applied by the state machine); the surfaced
                event still carries the fully decoded typed payload.
            */
            FrameTranslation translated = TranslateWire(MeshWebrtcPlanWire);
            Assert.That(translated.HasFact, Is.True);
            Assert.That(translated.Fact.Kind, Is.EqualTo(SessionEventKind.SessionPlan));
            Assert.That(translated.HasEvent, Is.True);
            Assert.That(translated.Event.Kind, Is.EqualTo(PollEventKind.SessionPlan));
            Assert.That(translated.Event.SessionPlan.Generation, Is.EqualTo(Generation));
            Assert.That(translated.Event.SessionPlan.Topology, Is.EqualTo(SessionTopology.Mesh));
        }

        [Test]
        public void MeshPayloadKindsProduceNoSessionFact()
        {
            string[] wires = { NewPeerWire, SignalWire, PeerTransportStatusWire };

            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Observe);
            foreach (string wire in wires)
            {
                byte[] payload = Encoding.UTF8.GetBytes(wire);
                FramePipeline.Translate(
                    new TransportFrame(payload, isText: true),
                    payload.Length,
                    gate,
                    out FrameTranslation translated
                );

                Assert.That(translated.HasFact, Is.False, wire);
                Assert.That(translated.IsClose, Is.False, wire);
                Assert.That(translated.HasViolation, Is.False, translated.Violation.Diagnostic);
                Assert.That(translated.HasEvent, Is.True, wire);
            }
        }

        private static IEnumerable<TestCaseData> MalformedMeshWires()
        {
            yield return new TestCaseData(NonRelayFallbackPlanWire, MessageKind.SessionPlan);
            yield return new TestCaseData(
                "{\"type\": \"SessionPlan\", \"data\": {\"generation\": \""
                    + Generation
                    + "\", \"topology\": \"mesh\", \"transport\": \"webrtc\", "
                    + "\"fallback\": \"relay\"}}",
                MessageKind.SessionPlan
            );
            yield return new TestCaseData(
                "{\"type\": \"SessionPlan\", \"data\": {\"generation\": \""
                    + Generation
                    + "\", \"topology\": \"diamond\", \"transport\": \"webrtc\", "
                    + "\"peers\": [], \"fallback\": \"relay\"}}",
                MessageKind.SessionPlan
            );
            yield return new TestCaseData(
                "{\"type\": \"NewPeer\", \"data\": {\"peer_id\": \"" + PeerId + "\"}}",
                MessageKind.NewPeer
            );
            yield return new TestCaseData(
                "{\"type\": \"Signal\", \"data\": {\"from\": \""
                    + PeerId
                    + "\", "
                    + "\"generation\": \""
                    + Generation
                    + "\"}}",
                MessageKind.Signal
            );
            yield return new TestCaseData(
                "{\"type\": \"PeerTransportStatus\", \"data\": {\"peer_id\": \""
                    + PeerId
                    + "\", \"transport\": \"webrtc\"}}",
                MessageKind.PeerTransportStatus
            );
        }

        private static PollEvent TranslateEvent(string wire)
        {
            FrameTranslation translated = TranslateWire(wire);
            Assert.That(translated.HasEvent, Is.True);
            Assert.That(translated.HasViolation, Is.False, translated.Violation.Diagnostic);
            return translated.Event;
        }

        private static FrameTranslation TranslateWire(string wire)
        {
            byte[] payload = Encoding.UTF8.GetBytes(wire);
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Observe);
            FramePipeline.Translate(
                new TransportFrame(payload, isText: true),
                payload.Length,
                gate,
                out FrameTranslation translated
            );
            return translated;
        }
    }
}
