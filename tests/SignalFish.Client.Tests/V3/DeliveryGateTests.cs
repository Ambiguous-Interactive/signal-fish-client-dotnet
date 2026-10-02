namespace SignalFish.Client.Tests.V3
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
    /// The delivery gate's policy surface: engine refusals map onto
    /// suppress/violation/teardown verdicts per policy, quarantined state
    /// latches and clears, and the pipeline feeds produce the Rust
    /// client's observable behavior on golden wire samples.
    /// </summary>
    [TestFixture]
    public sealed class DeliveryGateTests
    {
        [Test]
        public void V2RoomJoinedWithAnUnstampedRosterIsAccepted()
        {
            /*
                The v2 wire carries rosters without delivery stamps: the
                roster maps to no senders and the join is accepted.
                Regression for feeding a zeroed baseline to the engine
                (which refuses any non-empty v2 roster).
            */
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            GateVerdict verdict = gate.RebaselineSnapshot(Players(selfWithStamps: false));
            Assert.That(verdict.Suppress, Is.False, verdict.Diagnostic);
            Assert.That(verdict.Diagnostic, Is.Null);

            GateVerdict relayed = gate.RecordGameData(PlainGame(), out _);
            Assert.That(relayed.Suppress, Is.False, relayed.Diagnostic);
        }

        [Test]
        public void V2RoomJoinedWithAStampedRosterIsABaselineExposure()
        {
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            GateVerdict verdict = gate.RebaselineSnapshot(Players(selfWithStamps: true));
            Assert.That(verdict.Suppress, Is.True);
            Assert.That(
                verdict.Diagnostic,
                Does.StartWith("delivery accountability violation: v2 snapshot exposed")
            );
        }

        [Test]
        public void ProtocolInfoReEchoKeepsTheCursors()
        {
            /*
                The state machine treats a ProtocolInfo re-echo as a
                legitimate replacement; the gate must not reset the
                sender cursors on it (a re-swapped engine would blind the
                room until a baseline that never comes).
            */
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            RelayOneStamp(gate, seq: 1);

            gate.OnProtocolInfo(3);
            GateVerdict verdict = gate.RecordGameData(StampedGame(seq: 2, epoch: 1), out _);
            Assert.That(verdict.Suppress, Is.False, verdict.Diagnostic);
        }

        [Test]
        public void ProtocolInfoSwapsTheEngineAndAcceptsTheStampedSnapshot()
        {
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            gate.OnProtocolInfo(3);

            GateVerdict verdict = gate.RebaselineSnapshot(Players(selfWithStamps: true));
            Assert.That(verdict.Suppress, Is.False, verdict.Diagnostic);
            Assert.That(verdict.Diagnostic, Is.Null);
            Assert.That(gate.Quarantined, Is.False);
        }

        [Test]
        public void QuarantinePolicySuppressesDuplicateStampAndLatches()
        {
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            RelayOneStamp(gate, seq: 1);

            /*
                A repeated stamp (seq 1 again) is a duplicate-adjacency
                refusal: the frame is suppressed and the room latches
                quarantined.
            */
            GateVerdict verdict = gate.RecordGameData(
                StampedGame(seq: 1, epoch: 1),
                out GameDataDisposition disposition
            );
            Assert.That(verdict.Suppress, Is.True);
            Assert.That(verdict.Diagnostic, Is.Not.Null);
            Assert.That(gate.Quarantined, Is.True);

            /*
                Healthy room game data is suppressed while quarantined too.
            */
            GateVerdict healthy = gate.RecordGameData(
                StampedGame(seq: 2, epoch: 1),
                out GameDataDisposition healthyDisposition
            );
            Assert.That(healthy.Suppress, Is.True, healthy.Diagnostic);
            Assert.That(healthy.Diagnostic, Is.Null);

            /*
                The next authoritative rebaseline clears the latch.
            */
            GateVerdict rebaseline = gate.RebaselineSnapshot(Players(selfWithStamps: true));
            Assert.That(rebaseline.Suppress, Is.False, rebaseline.Diagnostic);
            Assert.That(gate.Quarantined, Is.False);
        }

        [Test]
        public void QuarantineSuppressesUnexplainedGapAndLatches()
        {
            /*
                A stamp jump with no report announcing the gap refuses
                (unexplained gap): the frame is suppressed and the room
                latches quarantined.
            */
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            gate.OnProtocolInfo(3);
            _ = gate.RebaselineSnapshot(Players(selfWithStamps: true));

            GateVerdict verdict = gate.RecordGameData(StampedGame(seq: 5, epoch: 1), out _);
            Assert.That(verdict.Suppress, Is.True);
            Assert.That(verdict.Diagnostic, Does.Contain("unexplained gap"));
            Assert.That(gate.Quarantined, Is.True);
        }

        [Test]
        public void ObservePolicySurfacesTheViolationAndKeepsApplying()
        {
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Observe);
            RelayOneStamp(gate, seq: 1);

            GateVerdict verdict = gate.RecordGameData(StampedGame(seq: 1, epoch: 1), out _);
            Assert.That(verdict.Suppress, Is.False);
            Assert.That(verdict.Teardown, Is.False);
            Assert.That(verdict.Diagnostic, Is.Not.Null);
            Assert.That(gate.Quarantined, Is.False);
        }

        [Test]
        public void DisconnectPolicyTearsTheSessionDown()
        {
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Disconnect);
            RelayOneStamp(gate, seq: 1);

            GateVerdict verdict = gate.RecordGameData(StampedGame(seq: 1, epoch: 1), out _);
            Assert.That(verdict.Teardown, Is.True);
            Assert.That(verdict.Diagnostic, Is.Not.Null);
        }

        [Test]
        public void RoomLeftResetsTheRoomAndTheLatch()
        {
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            RelayOneStamp(gate, seq: 1);
            _ = gate.RecordGameData(StampedGame(seq: 1, epoch: 1), out _);
            Assert.That(gate.Quarantined, Is.True);

            gate.ResetRoom();
            Assert.That(gate.Quarantined, Is.False);
        }

        [Test]
        public void ObserveTerminalEndsTheSessionLatch()
        {
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            RelayOneStamp(gate, seq: 1);
            _ = gate.RecordGameData(StampedGame(seq: 1, epoch: 1), out _);
            Assert.That(gate.Quarantined, Is.True);

            gate.ObserveTerminal();
            Assert.That(gate.Quarantined, Is.False);
        }

        [Test]
        public void TranslateFeedAllocatesNothing()
        {
            /*
                The wired receive surface this milestone adds: envelope
                decode, gate feed, engine stamp recording, and the frame
                translation together stay at zero bytes on the relay hot
                path. (The polling client's own receive-task machinery is
                measured separately by the idle-poll gate.)
            */
            byte[] negotiation = Encoding.UTF8.GetBytes(
                GoldenFixtures.ReadFirstLineOfType("v3-server-messages.jsonl", "ProtocolInfo")
            );
            byte[] join = Encoding.UTF8.GetBytes(GoldenFixtures.V3JoinWithBothSenders);
            byte[][] frames = new byte[400][];
            for (int i = 0; i < frames.Length; i++)
            {
                frames[i] = Encoding.UTF8.GetBytes(
                    "{\"type\": \"GameData\", \"data\": {\"from_player\": "
                        + "\"00000000-0000-0000-0000-00000000000b\", \"data\": {\"n\": "
                        + (i + 1)
                        + "}, \"seq\": "
                        + (43 + i)
                        + ", \"epoch\": 1}}"
                );
            }

            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            FramePipeline.Translate(
                new TransportFrame(negotiation, isText: true),
                negotiation.Length,
                gate,
                out _
            );
            FramePipeline.Translate(
                new TransportFrame(join, isText: true),
                join.Length,
                gate,
                out FrameTranslation primed
            );
            Assert.That(primed.HasViolation, Is.False, primed.Violation.Diagnostic);

            long minDelta = long.MaxValue;
            int violations = 0;
            for (int pass = 0; pass < 4; pass++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int iteration = 0; iteration < 100; iteration++)
                {
                    byte[] frame = frames[(pass * 100) + iteration];
                    FramePipeline.Translate(
                        new TransportFrame(frame, isText: true),
                        frame.Length,
                        gate,
                        out FrameTranslation translated
                    );
                    if (translated.HasViolation)
                    {
                        violations++;
                    }
                }

                long after = GC.GetAllocatedBytesForCurrentThread();
                minDelta = Math.Min(minDelta, after - before);
            }

            Assert.That(violations, Is.Zero, "the relay stream must stay accepted");
            Assert.That(
                minDelta,
                Is.EqualTo(0),
                "Relayed stamped GameData must flow through decode + gate + engine without allocating."
            );
        }

        [Test]
        public void PipelineSurfacesViolationAheadOfSuppressedGameData()
        {
            /*
                Golden v3 GameData with a stamp gap on a quarantining gate:
                the translation carries the violation (with its diagnostic)
                and no payload event.
            */
            FrameTranslation translated = TranslateGolden(
                "v3-server-messages.jsonl",
                "GameData",
                baselineFirst: true
            );
            Assert.That(translated.HasViolation, Is.True);
            Assert.That(translated.Violation.Diagnostic, Is.Not.Null);
            Assert.That(translated.HasEvent, Is.False);
            Assert.That(translated.HasFact, Is.False);
            Assert.That(translated.IsClose, Is.False);
        }

        [Test]
        public void PipelineSurfacesHealthyStampedGameData()
        {
            /*
                A coherent v3 join (the sender is on the roster), then the
                golden v3 GameData: the payload surfaces with its stamps.
            */
            byte[] negotiation = Encoding.UTF8.GetBytes(
                GoldenFixtures.ReadFirstLineOfType("v3-server-messages.jsonl", "ProtocolInfo")
            );
            byte[] join = Encoding.UTF8.GetBytes(GoldenFixtures.V3JoinWithBothSenders);
            byte[] frame = Encoding.UTF8.GetBytes(
                GoldenFixtures.ReadFirstLineOfType("v3-server-messages.jsonl", "GameData")
            );

            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            FramePipeline.Translate(MakeFrame(negotiation), negotiation.Length, gate, out _);
            FramePipeline.Translate(
                MakeFrame(join),
                join.Length,
                gate,
                out FrameTranslation joined
            );
            Assert.That(joined.HasViolation, Is.False, joined.Violation.Diagnostic);

            FramePipeline.Translate(
                MakeFrame(frame),
                frame.Length,
                gate,
                out FrameTranslation translated
            );
            Assert.That(translated.HasViolation, Is.False, translated.Violation.Diagnostic);
            Assert.That(translated.HasEvent, Is.True);
            Assert.That(translated.Event.Kind, Is.EqualTo(PollEventKind.GameData));
            Assert.That(translated.Event.GameData.Seq, Is.EqualTo(43uL));
            Assert.That(translated.Event.GameData.Epoch, Is.EqualTo(1u));
        }

        [Test]
        public void PipelineDisconnectPolicyTurnsTheRefusalIntoASessionClose()
        {
            FrameTranslation translated = TranslateGolden(
                "v3-server-messages.jsonl",
                "GameData",
                baselineFirst: true,
                policy: DeliveryViolationPolicy.Disconnect
            );
            Assert.That(translated.HasViolation, Is.True);
            Assert.That(translated.IsClose, Is.True);
            Assert.That(translated.PolicyTeardown, Is.True);
            Assert.That(translated.HasEvent, Is.False);
        }

        [Test]
        public void PipelineV2GameDataWithClassMetadataIsAViolation()
        {
            /*
                The v2 floor omits delivery metadata; a class-carrying
                frame on an unmetered connection refuses.
            */
            byte[] frame = Encoding.UTF8.GetBytes(
                "{\"type\": \"GameData\", \"data\": {\"from_player\": \"00000000-0000-0000-0000-00000000000b\", "
                    + "\"data\": {\"x\": 1}, \"class\": \"reliable\"}}"
            );
            FrameTranslation translated = TranslateFrame(frame);
            Assert.That(translated.HasViolation, Is.True, translated.Violation.Diagnostic);
            Assert.That(translated.HasEvent, Is.False);
        }

        [Test]
        public void PipelineV2GameDataWithoutMetadataSurfaces()
        {
            byte[] frame = Encoding.UTF8.GetBytes(
                "{\"type\": \"GameData\", \"data\": {\"from_player\": \"00000000-0000-0000-0000-00000000000b\", "
                    + "\"data\": {\"x\": 1}}}"
            );
            FrameTranslation translated = TranslateFrame(frame);
            Assert.That(translated.HasViolation, Is.False);
            Assert.That(translated.HasEvent, Is.True);
            Assert.That(translated.Event.Kind, Is.EqualTo(PollEventKind.GameData));
        }

        /// <summary>
        /// Negotiates v3, baselines the roster, and relays one accepted
        /// stamp — the healthy session state the refusal tests perturb.
        /// </summary>
        private static void RelayOneStamp(DeliveryGate gate, ulong seq)
        {
            gate.OnProtocolInfo(3);
            GateVerdict baseline = gate.RebaselineSnapshot(Players(selfWithStamps: true));
            Assert.That(baseline.Suppress, Is.False, baseline.Diagnostic);
            GateVerdict relayed = gate.RecordGameData(StampedGame(seq: seq, epoch: 1), out _);
            Assert.That(relayed.Suppress, Is.False, relayed.Diagnostic);
        }

        private static FrameTranslation TranslateGolden(
            string fixture,
            string wireType,
            bool baselineFirst,
            DeliveryViolationPolicy policy = DeliveryViolationPolicy.Quarantine
        )
        {
            byte[] negotiation = Encoding.UTF8.GetBytes(
                GoldenFixtures.ReadFirstLineOfType("v3-server-messages.jsonl", "ProtocolInfo")
            );
            byte[] join = Encoding.UTF8.GetBytes(
                GoldenFixtures.ReadFirstLineOfType("v3-server-messages.jsonl", "RoomJoined")
            );
            byte[] frame = Encoding.UTF8.GetBytes(
                GoldenFixtures.ReadFirstLineOfType(fixture, wireType)
            );

            DeliveryGate gate = new DeliveryGate(policy);
            FrameTranslation translated = default;
            if (baselineFirst)
            {
                FramePipeline.Translate(MakeFrame(negotiation), negotiation.Length, gate, out _);
                FramePipeline.Translate(MakeFrame(join), join.Length, gate, out translated);
                Assert.That(translated.HasViolation, Is.False, translated.Violation.Diagnostic);
            }

            FramePipeline.Translate(MakeFrame(frame), frame.Length, gate, out translated);
            return translated;
        }

        private static FrameTranslation TranslateFrame(byte[] frame)
        {
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            FramePipeline.Translate(
                MakeFrame(frame),
                frame.Length,
                gate,
                out FrameTranslation translated
            );
            return translated;
        }

        private static TransportFrame MakeFrame(byte[] payload)
        {
            return new TransportFrame(payload, isText: true);
        }

        private static List<PlayerInfo> Players(bool selfWithStamps)
        {
            return new List<PlayerInfo>
            {
                new PlayerInfo(
                    new Guid("00000000-0000-0000-0000-00000000000a"),
                    "Alice",
                    isAuthority: true,
                    isReady: false,
                    connectedAt: null,
                    epoch: selfWithStamps ? 1 : null,
                    seq: selfWithStamps ? 0 : null
                ),
            };
        }

        private static IncomingGameData StampedGame(ulong seq, uint epoch)
        {
            /*
                Wire-derived construction: the decode path records the
                metadata presence the engine's v2 floor requires absent.
            */
            return DecodedGame(
                "{\"type\": \"GameData\", \"data\": {\"from_player\": \"00000000-0000-0000-0000-00000000000a\", "
                    + "\"data\": {\"x\": 1}, \"seq\": "
                    + seq
                    + ", \"epoch\": "
                    + epoch
                    + "}}"
            );
        }

        private static IncomingGameData PlainGame()
        {
            return DecodedGame(
                "{\"type\": \"GameData\", \"data\": {\"from_player\": \"00000000-0000-0000-0000-00000000000a\", "
                    + "\"data\": {\"x\": 1}}}"
            );
        }

        private static IncomingGameData DecodedGame(string wire)
        {
            byte[] frame = Encoding.UTF8.GetBytes(wire);
            EnvelopeEvent envelope = EnvelopeReader.Decode(frame);
            Assert.That(
                IncomingGameData.TryDecode(envelope.Data, out IncomingGameData gameData),
                Is.True
            );
            return gameData;
        }
    }
}
