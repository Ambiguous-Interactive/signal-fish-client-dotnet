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
        /// <summary>The canonical v3 server advertisement (the golden ProtocolInfo).</summary>
        private static readonly string[] CanonicalFormats = { "json", "message_pack" };

        private static readonly DeliveryViolationPolicy[] AllPolicies =
        {
            DeliveryViolationPolicy.Quarantine,
            DeliveryViolationPolicy.Observe,
            DeliveryViolationPolicy.Disconnect,
        };

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
        public void ObserveTerminalReconnectsSwapTheEngine()
        {
            /*
                Delivery counters are per physical connection: the fresh
                connection's first RelayStats interval must be accepted
                even though the previous connection pinned a different
                one. With the engine reused across the reconnect, the
                fresh interval refuses and the room quarantines.
            */
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            gate.OnProtocolInfo(3, CanonicalFormats, out _);
            _ = gate.RebaselineSnapshot(Players(selfWithStamps: true));
            GateVerdict first = gate.RecordRelayStats(RelayStats(5_000));
            Assert.That(first.Suppress, Is.False, first.Diagnostic);

            gate.ObserveTerminal();
            gate.OnProtocolInfo(3, CanonicalFormats, out _);
            _ = gate.RebaselineSnapshot(Players(selfWithStamps: true));

            GateVerdict fresh = gate.RecordRelayStats(RelayStats(10_000));
            Assert.That(fresh.Suppress, Is.False, fresh.Diagnostic);
        }

        [Test]
        public void PipelineReconnectedWithMalformedWatermarksIsAViolationNotACrash()
        {
            /*
                The mapper decodes a Reconnected frame with the join
                decoder, which ignores sender_watermarks — so a malformed
                watermark list maps fine and then fails the gate feed's
                typed decode. The translation must surface the routed-fact
                violation (no fact applied), never feed the default struct
                into the gate.
            */
            byte[] negotiation = Encoding.UTF8.GetBytes(
                GoldenFixtures.ReadFirstLineOfType("v3-server-messages.jsonl", "ProtocolInfo")
            );
            byte[] join = Encoding.UTF8.GetBytes(GoldenFixtures.V3JoinWithBothSenders);
            byte[] reconnect = Encoding.UTF8.GetBytes(
                "{\"type\": \"Reconnected\", \"data\": {\"room_id\": "
                    + "\"11111111-1111-1111-1111-111111111111\", "
                    + "\"room_code\": \"ABC123\", \"player_id\": "
                    + "\"00000000-0000-0000-0000-00000000000b\", "
                    + "\"game_name\": \"test_game\", \"max_players\": 4, "
                    + "\"supports_authority\": true, \"current_players\": ["
                    + "{\"id\": \"00000000-0000-0000-0000-00000000000a\", \"name\": \"Alice\", "
                    + "\"is_authority\": true, \"is_ready\": true, \"epoch\": 1, \"seq\": 42}, "
                    + "{\"id\": \"00000000-0000-0000-0000-00000000000b\", \"name\": \"Bob\", "
                    + "\"is_authority\": false, \"is_ready\": true, \"epoch\": 2, \"seq\": 0}"
                    + "], \"is_authority\": false, \"lobby_state\": \"running\", "
                    + "\"ready_players\": [], \"relay_type\": \"matchbox\", "
                    + "\"current_spectators\": [], \"sender_watermarks\": ["
                    + "{\"player_id\": \"00000000-0000-0000-0000-00000000000a\", "
                    + "\"epoch\": true, \"seq\": 42}]}}"
            );

            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            FramePipeline.Translate(
                new TransportFrame(negotiation, isText: true),
                negotiation.Length,
                gate,
                new SignalFishStateMachine(),
                out _
            );
            FramePipeline.Translate(
                new TransportFrame(join, isText: true),
                join.Length,
                gate,
                new SignalFishStateMachine(),
                out FrameTranslation joined
            );
            Assert.That(joined.HasViolation, Is.False, joined.Violation.Diagnostic);

            FramePipeline.Translate(
                new TransportFrame(reconnect, isText: true),
                reconnect.Length,
                gate,
                new SignalFishStateMachine(),
                out FrameTranslation translated
            );
            Assert.That(translated.HasFact, Is.False, "a failed typed decode applies nothing");
            Assert.That(translated.HasEvent, Is.True);
            Assert.That(translated.Event.Kind, Is.EqualTo(PollEventKind.ProtocolViolation));
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

            gate.OnProtocolInfo(3, CanonicalFormats, out _);
            GateVerdict verdict = gate.RecordGameData(StampedGame(seq: 2, epoch: 1), out _);
            Assert.That(verdict.Suppress, Is.False, verdict.Diagnostic);
        }

        [Test]
        public void ProtocolInfoSwapsTheEngineAndAcceptsTheStampedSnapshot()
        {
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            gate.OnProtocolInfo(3, CanonicalFormats, out _);

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
            gate.OnProtocolInfo(3, CanonicalFormats, out _);
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

        [TestCaseSource(nameof(AllPolicies))]
        public void PreNegotiationDowngradeNoticeIsAcceptedNonFatally(
            DeliveryViolationPolicy policy
        )
        {
            /*
                Server-pinned handshake contract: an unsupported requested
                game_data_format is refused with exactly one
                Error(UNSUPPORTED_GAME_DATA_FORMAT) frame before
                Authenticated, and the session downgrades to JSON. The
                notice is a benign advisory, not a delivery violation —
                even under the strictest policy.
            */
            DeliveryGate gate = new DeliveryGate(policy);
            gate.NoteRequestedFormat("rkyv");

            GateVerdict notice = gate.ObserveUnsupportedFormatError();
            Assert.That(notice.Suppress, Is.False, notice.Diagnostic);
            Assert.That(notice.Teardown, Is.False);
            Assert.That(notice.Diagnostic, Is.Null);
        }

        [Test]
        public void DowngradeNoticePinsJsonOverTheRequestedToken()
        {
            /*
                The notice means the session IS JSON ("pinned wire
                order"): a later advertisement naming the refused token
                must not re-select it — the notice outranks the request.
            */
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            gate.NoteRequestedFormat("message_pack");
            _ = gate.ObserveUnsupportedFormatError();

            Assert.That(gate.OnProtocolInfo(3, CanonicalFormats, out _), Is.True);
            Assert.That(gate.NegotiatedEncoding, Is.EqualTo(GameDataFormatToken.Json));
        }

        [Test]
        public void DowngradePinSurvivesAReArmedRequest()
        {
            /*
                The public API admits a late handshake on a live
                connection: a re-armed request must not resurrect the
                refused token — the pin holds for the connection.
            */
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            gate.NoteRequestedFormat("message_pack");
            _ = gate.ObserveUnsupportedFormatError();
            gate.NoteRequestedFormat("message_pack");

            Assert.That(gate.OnProtocolInfo(3, CanonicalFormats, out _), Is.True);
            Assert.That(gate.NegotiatedEncoding, Is.EqualTo(GameDataFormatToken.Json));
        }

        [TestCaseSource(nameof(DowngradeRepeatRefusals))]
        public void RepeatPreNegotiationDowngradeNoticeRefusesPerPolicy(
            DeliveryViolationPolicy policy,
            bool expectSuppress,
            bool expectTeardown
        )
        {
            /*
                The server sends at most one notice per connection (the
                handshake runs once); a repeat is out of contract and
                refuses per the violation policy.
            */
            DeliveryGate gate = new DeliveryGate(policy);
            _ = gate.ObserveUnsupportedFormatError();

            GateVerdict repeat = gate.ObserveUnsupportedFormatError();
            Assert.That(repeat.Suppress, Is.EqualTo(expectSuppress));
            Assert.That(repeat.Teardown, Is.EqualTo(expectTeardown));
            Assert.That(repeat.Diagnostic, Is.Not.Null);
        }

        [Test]
        public void ObserveTerminalClearsTheDowngradeNoticeLatch()
        {
            /*
                The notice is per physical connection: the fresh
                connection's re-handshake may legitimately downgrade
                again, so the latch cannot survive a terminal outcome.
            */
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Disconnect);
            _ = gate.ObserveUnsupportedFormatError();
            gate.ObserveTerminal();

            GateVerdict fresh = gate.ObserveUnsupportedFormatError();
            Assert.That(fresh.Suppress, Is.False, fresh.Diagnostic);
            Assert.That(fresh.Teardown, Is.False);
        }

        [Test]
        public void PipelineHandshakeDowngradeSettlesJsonAndKeepsFlowing()
        {
            /*
                The server's pinned handshake order for an unsupported
                requested game_data_format: the downgrade notice, then
                Authenticated, then ProtocolInfo (the gate is
                Authenticated-blind, so the notice → negotiation pairing
                is what this pins; the notice frame is hand-rolled — no
                golden fixture predates the server's pin). The
                translation must surface the notice as an ordinary
                ServerError (no violation), settle JSON against an
                advertisement that still names the refused token, and
                keep the session facts flowing.
            */
            byte[] notice = Encoding.UTF8.GetBytes(
                "{\"type\": \"Error\", \"data\": {\"message\": \"Requested game data"
                    + " format 'message_pack' is not supported. Server supports:"
                    + " json, message_pack. Falling back to JSON.\", "
                    + "\"error_code\": \"UNSUPPORTED_GAME_DATA_FORMAT\"}}"
            );
            byte[] authenticated = Encoding.UTF8.GetBytes(
                GoldenFixtures.ReadFirstLineOfType("v2-server-messages.jsonl", "Authenticated")
            );
            byte[] negotiation = Encoding.UTF8.GetBytes(
                GoldenFixtures.ReadFirstLineOfType("v3-server-messages.jsonl", "ProtocolInfo")
            );

            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            SignalFishStateMachine machine = new SignalFishStateMachine();
            gate.NoteRequestedFormat("message_pack");
            FramePipeline.Translate(
                MakeFrame(notice),
                notice.Length,
                gate,
                machine,
                out FrameTranslation noticed
            );
            Assert.That(noticed.HasViolation, Is.False, noticed.Violation.Diagnostic);
            Assert.That(noticed.HasEvent, Is.True);
            Assert.That(noticed.Event.Kind, Is.EqualTo(PollEventKind.ServerError));

            FramePipeline.Translate(
                MakeFrame(authenticated),
                authenticated.Length,
                gate,
                machine,
                out FrameTranslation handshake
            );
            Assert.That(handshake.HasViolation, Is.False, handshake.Violation.Diagnostic);

            FramePipeline.Translate(
                MakeFrame(negotiation),
                negotiation.Length,
                gate,
                machine,
                out FrameTranslation negotiated
            );
            Assert.That(negotiated.HasViolation, Is.False, negotiated.Violation.Diagnostic);
            Assert.That(gate.NegotiatedEncoding, Is.EqualTo(GameDataFormatToken.Json));
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
            SignalFishStateMachine machine = new SignalFishStateMachine();
            FramePipeline.Translate(
                new TransportFrame(negotiation, isText: true),
                negotiation.Length,
                gate,
                machine,
                out _
            );
            FramePipeline.Translate(
                new TransportFrame(join, isText: true),
                join.Length,
                gate,
                machine,
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
                        machine,
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
            FramePipeline.Translate(
                MakeFrame(negotiation),
                negotiation.Length,
                gate,
                new SignalFishStateMachine(),
                out _
            );
            FramePipeline.Translate(
                MakeFrame(join),
                join.Length,
                gate,
                new SignalFishStateMachine(),
                out FrameTranslation joined
            );
            Assert.That(joined.HasViolation, Is.False, joined.Violation.Diagnostic);

            FramePipeline.Translate(
                MakeFrame(frame),
                frame.Length,
                gate,
                new SignalFishStateMachine(),
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

        /// <summary>Builds a healthy relay-stats interval.</summary>
        private static RelayStatsMessage RelayStats(ulong intervalMs)
        {
            return new RelayStatsMessage(
                intervalMs,
                sentToYou: 3,
                droppedForYou: 0,
                backpressureEvents: 0
            );
        }

        private static IEnumerable<TestCaseData> DowngradeRepeatRefusals()
        {
            yield return new TestCaseData(DeliveryViolationPolicy.Quarantine, true, false).SetName(
                "RepeatPreNegotiationDowngradeNoticeRefusesPerPolicy.Quarantine.Suppresses"
            );
            yield return new TestCaseData(DeliveryViolationPolicy.Observe, false, false).SetName(
                "RepeatPreNegotiationDowngradeNoticeRefusesPerPolicy.Observe.Surfaces"
            );
            yield return new TestCaseData(DeliveryViolationPolicy.Disconnect, true, true).SetName(
                "RepeatPreNegotiationDowngradeNoticeRefusesPerPolicy.Disconnect.TearsDown"
            );
        }

        /// <summary>
        /// Negotiates v3, baselines the roster, and relays one accepted
        /// stamp — the healthy session state the refusal tests perturb.
        /// </summary>
        private static void RelayOneStamp(DeliveryGate gate, ulong seq)
        {
            gate.OnProtocolInfo(3, CanonicalFormats, out _);
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
                FramePipeline.Translate(
                    MakeFrame(negotiation),
                    negotiation.Length,
                    gate,
                    new SignalFishStateMachine(),
                    out _
                );
                FramePipeline.Translate(
                    MakeFrame(join),
                    join.Length,
                    gate,
                    new SignalFishStateMachine(),
                    out translated
                );
                Assert.That(translated.HasViolation, Is.False, translated.Violation.Diagnostic);
            }

            FramePipeline.Translate(
                MakeFrame(frame),
                frame.Length,
                gate,
                new SignalFishStateMachine(),
                out translated
            );
            return translated;
        }

        private static FrameTranslation TranslateFrame(byte[] frame)
        {
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            FramePipeline.Translate(
                MakeFrame(frame),
                frame.Length,
                gate,
                new SignalFishStateMachine(),
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
