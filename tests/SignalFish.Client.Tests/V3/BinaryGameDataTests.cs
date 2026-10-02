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
    /// The v3 binary game-data surface: the strict MessagePack envelope
    /// decode, the format negotiation that admits binary frames, the
    /// representation checks, and the shared delivery-accountability gate
    /// — all on wire-shaped bytes, with the hot path pinned allocation-
    /// free like the JSON lane.
    /// </summary>
    [TestFixture]
    public sealed class BinaryGameDataTests
    {
        private static readonly Guid SenderB = new Guid("00000000-0000-0000-0000-00000000000b");
        private static readonly byte[] PayloadBytes = { 0x81, 0xa1, (byte)'n', 0x01 };

        /// <summary>The canonical v3 server advertisement (the golden ProtocolInfo).</summary>
        private static readonly string[] CanonicalFormats = { "json", "message_pack" };

        private static readonly string[] JsonOnlyFormats = { "json" };

        private static readonly string[] NonCanonicalFormats = { "message_pack", "json" };

        /*
            ------------------------------------------------------------------
            Envelope decode
            ------------------------------------------------------------------
        */

        [Test]
        public void V3FrameDecodesToTheRosterGuidAndVerbatimPayload()
        {
            byte[] frame = V3Frame(seq: 43, epoch: 1);

            bool decoded = BinaryGameDataFrame.TryDecode(
                frame,
                protocolV3: true,
                out BinaryGameDataFrame gameData,
                out DecodeError error,
                out int errorOffset
            );

            Assert.That(decoded, Is.True, $"{error} at {errorOffset}");
            Assert.That(gameData.FromPlayer, Is.EqualTo(SenderB));
            Assert.That(gameData.Format, Is.EqualTo(GameDataFormatToken.MessagePack));
            Assert.That(gameData.Payload.ToArray(), Is.EqualTo(PayloadBytes));
            Assert.That(gameData.Seq, Is.EqualTo(43uL));
            Assert.That(gameData.Epoch, Is.EqualTo(1u));
        }

        [Test]
        public void AnyKeyOrderDecodesTheSameFrame()
        {
            /*
                MessagePack maps are unordered: a strict decoder must bind
                each value to its key, not to its position — the payload
                must stay the payload even when from_player follows it.
            */
            List<byte> frame = new List<byte> { 0x85 };
            WriteKey(frame, "payload");
            WriteBin(frame, PayloadBytes);
            WriteKey(frame, "encoding");
            WriteStr(frame, "message_pack");
            WriteKey(frame, "from_player");
            WriteBin(frame, SenderB.ToNetworkOrderBytes());
            WriteKey(frame, "seq");
            WriteUInt(frame, 43);
            WriteKey(frame, "epoch");
            WriteUInt(frame, 1);

            Assert.That(
                BinaryGameDataFrame.TryDecode(
                    frame.ToArray(),
                    protocolV3: true,
                    out BinaryGameDataFrame gameData,
                    out DecodeError error,
                    out int errorOffset
                ),
                Is.True,
                $"{error} at {errorOffset}"
            );
            Assert.That(gameData.FromPlayer, Is.EqualTo(SenderB));
            Assert.That(gameData.Payload.ToArray(), Is.EqualTo(PayloadBytes));
            Assert.That(gameData.Seq, Is.EqualTo(43uL));
        }

        [Test]
        public void ContainerAndStringWidthsDecodeOnTheSuccessPath()
        {
            /*
                The server's encoder is not minimal-length on containers
                either: a map16 root with str8 keys and bin16 values is the
                same frame as the fix* spelling.
            */
            List<byte> frame = new List<byte> { 0xde, 0x00, 0x05 };
            WriteWideStr(frame, "from_player");
            WriteWideBin(frame, SenderB.ToNetworkOrderBytes());
            WriteWideStr(frame, "encoding");
            WriteWideStr(frame, "message_pack");
            WriteWideStr(frame, "payload");
            WriteWideBin(frame, PayloadBytes);
            WriteKey(frame, "seq");
            WriteUInt(frame, 43);
            WriteKey(frame, "epoch");
            WriteUInt(frame, 1);

            Assert.That(
                BinaryGameDataFrame.TryDecode(
                    frame.ToArray(),
                    protocolV3: true,
                    out BinaryGameDataFrame gameData,
                    out DecodeError error,
                    out int errorOffset
                ),
                Is.True,
                $"{error} at {errorOffset}"
            );
            Assert.That(gameData.FromPlayer, Is.EqualTo(SenderB));
            Assert.That(gameData.Payload.ToArray(), Is.EqualTo(PayloadBytes));
            Assert.That(gameData.Seq, Is.EqualTo(43uL));
        }

        [Test]
        public void EpochBeyondTheUintRangeIsRejected()
        {
            List<byte> frame = new List<byte> { 0x85 };
            WriteKey(frame, "from_player");
            WriteBin(frame, SenderB.ToNetworkOrderBytes());
            WriteKey(frame, "encoding");
            WriteStr(frame, "message_pack");
            WriteKey(frame, "payload");
            WriteBin(frame, PayloadBytes);
            WriteKey(frame, "seq");
            WriteUInt(frame, 43);
            WriteKey(frame, "epoch");
            frame.Add(0xcf);
            frame.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 });

            Assert.That(
                BinaryGameDataFrame.TryDecode(
                    frame.ToArray(),
                    protocolV3: true,
                    out _,
                    out DecodeError error,
                    out int errorOffset
                ),
                Is.False
            );
            Assert.That(error, Is.EqualTo(DecodeError.InvalidFieldValue));
            Assert.That(errorOffset, Is.GreaterThan(0));
        }

        [Test]
        public void UuidBytesReadInTheNetworkOrderTheJsonRosterSpells()
        {
            /*
                from_player rides as the 16 RFC-4122 bytes; the decoded
                Guid must equal the hyphenated spelling the JSON roster
                carries (the sender match depends on it).
            */
            Guid roster = new Guid("a5f3c9e2-4b1d-4c8a-9f2e-7d6c5b4a3f2e");
            byte[] frame = V3Frame(seq: 1, epoch: 1, fromPlayer: roster);

            Assert.That(
                BinaryGameDataFrame.TryDecode(
                    frame,
                    protocolV3: true,
                    out BinaryGameDataFrame gameData,
                    out _,
                    out _
                ),
                Is.True
            );
            Assert.That(gameData.FromPlayer, Is.EqualTo(roster));
        }

        [TestCase(5ul, new byte[] { 0x05 }, TestName = "fixint")]
        [TestCase(5ul, new byte[] { 0xcc, 0x05 }, TestName = "uint8-width")]
        [TestCase(5ul, new byte[] { 0xcd, 0x00, 0x05 }, TestName = "uint16-width")]
        [TestCase(70_000ul, new byte[] { 0xce, 0x00, 0x01, 0x11, 0x70 }, TestName = "uint32-width")]
        [TestCase(
            5_000_000_000ul,
            new byte[] { 0xcf, 0x00, 0x00, 0x00, 0x01, 0x2a, 0x05, 0xf2, 0x00 },
            TestName = "uint64-width"
        )]
        public void StampsDecodeAtAnyIntegerWidth(ulong expectedSeq, byte[] seqValueBytes)
        {
            /*
                The server's encoder is not minimal-length: any integer
                width carrying the value decodes (epoch rides fixint 1).
            */
            List<byte> frame = new List<byte> { 0x85 };
            WriteKey(frame, "from_player");
            WriteBin(frame, SenderB.ToNetworkOrderBytes());
            WriteKey(frame, "encoding");
            WriteStr(frame, "message_pack");
            WriteKey(frame, "payload");
            WriteBin(frame, PayloadBytes);
            WriteKey(frame, "seq");
            frame.AddRange(seqValueBytes);
            WriteKey(frame, "epoch");
            frame.Add(0x01);

            Assert.That(
                BinaryGameDataFrame.TryDecode(
                    frame.ToArray(),
                    protocolV3: true,
                    out BinaryGameDataFrame gameData,
                    out DecodeError error,
                    out int errorOffset
                ),
                Is.True,
                $"{error} at {errorOffset}"
            );
            Assert.That(gameData.Seq, Is.EqualTo(expectedSeq));
            Assert.That(gameData.Epoch, Is.EqualTo(1u));
        }

        [TestCase("unknown-key", DecodeError.UnknownField, TestName = "unknown key")]
        [TestCase("duplicate-key", DecodeError.DuplicateField, TestName = "duplicate key")]
        [TestCase("missing-key", DecodeError.UnknownField, TestName = "short map on v3")]
        [TestCase("zero-seq", DecodeError.InvalidFieldValue, TestName = "zero seq")]
        [TestCase("zero-epoch", DecodeError.InvalidFieldValue, TestName = "zero epoch")]
        [TestCase("short-uuid", DecodeError.InvalidFieldValue, TestName = "uuid not 16 bytes")]
        [TestCase("string-payload", DecodeError.InvalidFieldValue, TestName = "payload not bin")]
        [TestCase("root-array", DecodeError.NotAMap, TestName = "root not a map")]
        [TestCase("truncated", DecodeError.Truncated, TestName = "truncated")]
        [TestCase("trailing", DecodeError.TrailingContent, TestName = "trailing bytes")]
        [TestCase(
            "unknown-encoding",
            DecodeError.InvalidFieldValue,
            TestName = "unknown encoding token"
        )]
        [TestCase("seq-on-v2", DecodeError.UnknownField, TestName = "v3 key on the v2 shape")]
        public void StrictDecodeRejectsMalformedShapes(string shape, DecodeError expectedError)
        {
            byte[] frame = Malformed(shape);

            bool decoded = BinaryGameDataFrame.TryDecode(
                frame,
                protocolV3: shape != "seq-on-v2",
                out _,
                out DecodeError error,
                out int errorOffset
            );

            Assert.That(decoded, Is.False, $"{shape} must not decode");
            Assert.That(error, Is.EqualTo(expectedError), $"{shape} at {errorOffset}");
        }

        [Test]
        public void DeclaredLengthsBeyondTheBufferTruncateInsteadOfThrowing()
        {
            /*
                A bin32/str32 length prefix is attacker-controlled bytes:
                0x7FFFFFFF wraps the bounded remaining-bytes check and
                walked the cursor out of the buffer — a malformed frame
                faulted the receive loop instead of surfacing as
                DecodeFailed. The negative spelling (all ones) must stay
                truncation too.
            */
            byte[] FrameWithDeclaredBin32(byte[] declaredLength)
            {
                List<byte> frame = new List<byte> { 0x85 };
                WriteKey(frame, "from_player");
                WriteBin(frame, SenderB.ToNetworkOrderBytes());
                WriteKey(frame, "encoding");
                WriteStr(frame, "message_pack");
                WriteKey(frame, "payload");
                frame.Add(0xc6);
                frame.AddRange(declaredLength);
                frame.Add(0x01);
                WriteKey(frame, "seq");
                WriteUInt(frame, 43);
                WriteKey(frame, "epoch");
                WriteUInt(frame, 1);
                return frame.ToArray();
            }

            foreach (
                byte[] declared in new[]
                {
                    new byte[] { 0x7f, 0xff, 0xff, 0xff },
                    new byte[] { 0xff, 0xff, 0xff, 0xff },
                }
            )
            {
                Assert.That(
                    BinaryGameDataFrame.TryDecode(
                        FrameWithDeclaredBin32(declared),
                        protocolV3: true,
                        out _,
                        out DecodeError error,
                        out int errorOffset
                    ),
                    Is.False
                );
                Assert.That(
                    error,
                    Is.EqualTo(DecodeError.Truncated),
                    $"declared {BitConverter.ToString(declared)} at {errorOffset}"
                );
            }
        }

        [Test]
        public void V2ShapeDecodesWithoutStamps()
        {
            List<byte> frame = new List<byte> { 0x83 };
            WriteKey(frame, "from_player");
            WriteBin(frame, SenderB.ToNetworkOrderBytes());
            WriteKey(frame, "encoding");
            WriteStr(frame, "message_pack");
            WriteKey(frame, "payload");
            WriteBin(frame, PayloadBytes);

            Assert.That(
                BinaryGameDataFrame.TryDecode(
                    frame.ToArray(),
                    protocolV3: false,
                    out BinaryGameDataFrame gameData,
                    out DecodeError error,
                    out int errorOffset
                ),
                Is.True,
                $"{error} at {errorOffset}"
            );
            Assert.That(gameData.Seq, Is.EqualTo(0uL));
            Assert.That(gameData.Epoch, Is.EqualTo(0u));
        }

        /*
            ------------------------------------------------------------------
            Negotiation
            ------------------------------------------------------------------
        */

        [Test]
        public void NonCanonicalAdvertisementRejectsNegotiation()
        {
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            gate.NoteRequestedFormat("message_pack");

            Assert.That(gate.OnProtocolInfo(3, NonCanonicalFormats, out string? refusal), Is.False);
            Assert.That(refusal, Does.Contain("canonical Server 0.8 negotiation order"));
            Assert.That(gate.IsProtocolV3, Is.False, "a rejected frame negotiates nothing");
        }

        [Test]
        public void RequestedFormatSurvivesOnlyWhenAdvertised()
        {
            DeliveryGate refused = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            refused.NoteRequestedFormat("message_pack");
            Assert.That(refused.OnProtocolInfo(3, JsonOnlyFormats, out _), Is.True);
            Assert.That(refused.NegotiatedEncoding, Is.EqualTo(GameDataFormatToken.Json));

            DeliveryGate kept = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            kept.NoteRequestedFormat("message_pack");
            Assert.That(kept.OnProtocolInfo(3, CanonicalFormats, out _), Is.True);
            Assert.That(kept.NegotiatedEncoding, Is.EqualTo(GameDataFormatToken.MessagePack));
        }

        [Test]
        public void LegacyAndUnknownRequestsStayJson()
        {
            /*
                A legacy server advertises no list; an unknown token cannot
                map onto a client encoding — both keep the JSON default.
            */
            DeliveryGate legacy = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            legacy.NoteRequestedFormat("message_pack");
            Assert.That(legacy.OnProtocolInfo(3, Array.Empty<string>(), out _), Is.True);
            Assert.That(legacy.NegotiatedEncoding, Is.EqualTo(GameDataFormatToken.Json));

            DeliveryGate unknown = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            unknown.NoteRequestedFormat("cbor");
            Assert.That(unknown.OnProtocolInfo(3, CanonicalFormats, out _), Is.True);
            Assert.That(unknown.NegotiatedEncoding, Is.EqualTo(GameDataFormatToken.Json));
        }

        [Test]
        public void RejectedReEchoKeepsTheSettledEncoding()
        {
            /*
                A version-changing ProtocolInfo re-echo with a non-canonical
                advertisement is rejected — and rejection must not clobber
                the encoding the connection already settled: state mutates
                only after validation succeeds.
            */
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            gate.NoteRequestedFormat("message_pack");
            Assert.That(gate.OnProtocolInfo(3, CanonicalFormats, out _), Is.True);
            Assert.That(gate.NegotiatedEncoding, Is.EqualTo(GameDataFormatToken.MessagePack));

            Assert.That(gate.OnProtocolInfo(2, NonCanonicalFormats, out string? refusal), Is.False);
            Assert.That(refusal, Does.Contain("canonical Server 0.8 negotiation order"));
            Assert.That(
                gate.NegotiatedEncoding,
                Is.EqualTo(GameDataFormatToken.MessagePack),
                "a rejected frame leaves the settled negotiation alone"
            );
            Assert.That(gate.IsProtocolV3, Is.True, "the prior negotiation still stands");
        }

        [Test]
        public void LateRequestAndReEchoKeepTheResolution()
        {
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            Assert.That(gate.OnProtocolInfo(3, JsonOnlyFormats, out _), Is.True);
            gate.NoteRequestedFormat("message_pack");
            Assert.That(gate.NegotiatedEncoding, Is.EqualTo(GameDataFormatToken.Json));

            Assert.That(gate.OnProtocolInfo(3, CanonicalFormats, out _), Is.True);
            Assert.That(gate.NegotiatedEncoding, Is.EqualTo(GameDataFormatToken.Json));
        }

        /*
            ------------------------------------------------------------------
            Pipeline: admission, representation, gating
            ------------------------------------------------------------------
        */

        [Test]
        public void NegotiatedBinaryFrameSurfacesGatedGameData()
        {
            DeliveryGate gate = NegotiatedGate();
            byte[] frame = V3Frame(seq: 43, epoch: 1);

            FramePipeline.Translate(
                new TransportFrame(frame, isText: false),
                frame.Length,
                gate,
                out FrameTranslation translated
            );

            Assert.That(translated.HasViolation, Is.False, translated.Violation.Diagnostic);
            Assert.That(translated.HasEvent, Is.True);
            Assert.That(translated.Event.Kind, Is.EqualTo(PollEventKind.GameData));
            Assert.That(translated.Event.GameData.FromPlayer, Is.EqualTo(SenderB));
            Assert.That(translated.Event.GameData.Payload.ToArray(), Is.EqualTo(PayloadBytes));
            Assert.That(translated.Event.GameData.Seq, Is.EqualTo(43uL));
            Assert.That(translated.Event.GameData.Epoch, Is.EqualTo(1u));
        }

        [Test]
        public void BinaryFramesShareOneSeqStreamWithJson()
        {
            /*
                Text and binary use the same per-sender stream: a JSON
                stamp then a binary stamp then a binary replay — the
                replay refuses like any duplicate.
            */
            DeliveryGate gate = NegotiatedGate();
            byte[] jsonFrame = Encoding.UTF8.GetBytes(
                GoldenFixtures.ReadFirstLineOfType("v3-server-messages.jsonl", "GameData")
            );
            FramePipeline.Translate(
                new TransportFrame(jsonFrame, isText: true),
                jsonFrame.Length,
                gate,
                out FrameTranslation json
            );
            Assert.That(json.HasViolation, Is.False, json.Violation.Diagnostic);

            byte[] binary = V3Frame(seq: 44, epoch: 1);
            FramePipeline.Translate(
                new TransportFrame(binary, isText: false),
                binary.Length,
                gate,
                out FrameTranslation relayed
            );
            Assert.That(relayed.HasViolation, Is.False, relayed.Violation.Diagnostic);
            Assert.That(relayed.Event.GameData.Seq, Is.EqualTo(44uL));

            FramePipeline.Translate(
                new TransportFrame(binary, isText: false),
                binary.Length,
                gate,
                out FrameTranslation replay
            );
            Assert.That(replay.HasViolation, Is.True);
            Assert.That(replay.HasEvent, Is.False);
        }

        [Test]
        public void BinaryBeforeNegotiationIsALifecycleViolation()
        {
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            byte[] frame = V3Frame(seq: 1, epoch: 1);

            FramePipeline.Translate(
                new TransportFrame(frame, isText: false),
                frame.Length,
                gate,
                out FrameTranslation translated
            );

            Assert.That(translated.HasViolation, Is.True);
            Assert.That(translated.Violation.Diagnostic, Does.Contain("lifecycle violation"));
            Assert.That(translated.HasEvent, Is.False);
        }

        [Test]
        public void BinaryUnderJsonNegotiationIsARepresentationViolation()
        {
            DeliveryGate gate = NegotiatedGate(requestedFormat: null);
            byte[] frame = V3Frame(seq: 43, epoch: 1);

            FramePipeline.Translate(
                new TransportFrame(frame, isText: false),
                frame.Length,
                gate,
                out FrameTranslation translated
            );

            Assert.That(translated.HasViolation, Is.True);
            Assert.That(
                translated.Violation.Diagnostic,
                Does.Contain("did not match negotiated json encoding")
            );
        }

        [Test]
        public void EnvelopeEncodingMismatchIsARepresentationViolation()
        {
            DeliveryGate gate = NegotiatedGate();
            byte[] frame = V3Frame(seq: 43, epoch: 1, encoding: "json");

            FramePipeline.Translate(
                new TransportFrame(frame, isText: false),
                frame.Length,
                gate,
                out FrameTranslation translated
            );

            Assert.That(translated.HasViolation, Is.True);
            Assert.That(
                translated.Violation.Diagnostic,
                Does.Contain("representation did not match negotiated message_pack encoding")
            );
            Assert.That(translated.HasEvent, Is.False);
        }

        [Test]
        public void MalformedBinaryFrameSurfacesBoundedDecodeFailed()
        {
            DeliveryGate gate = NegotiatedGate();
            byte[] frame = { 0xde, 0x00, 0x81 };

            FramePipeline.Translate(
                new TransportFrame(frame, isText: false),
                frame.Length,
                gate,
                out FrameTranslation translated
            );

            Assert.That(translated.HasViolation, Is.False);
            Assert.That(translated.HasEvent, Is.True);
            Assert.That(translated.Event.Kind, Is.EqualTo(PollEventKind.DecodeFailed));
            Assert.That(translated.Event.Error, Is.EqualTo(DecodeError.Truncated));
            Assert.That(translated.Event.ErrorOffset, Is.GreaterThanOrEqualTo(0));
            Assert.That(translated.IsClose, Is.False);
        }

        [Test]
        public void BinaryGapQuarantinesLikeJson()
        {
            DeliveryGate gate = NegotiatedGate();
            byte[] gap = V3Frame(seq: 45, epoch: 1);

            FramePipeline.Translate(
                new TransportFrame(gap, isText: false),
                gap.Length,
                gate,
                out FrameTranslation refused
            );
            Assert.That(refused.HasViolation, Is.True);
            Assert.That(refused.HasEvent, Is.False);

            /*
                The quarantine latch suppresses healthy room data until the
                next authoritative rebaseline.
            */
            byte[] healthy = V3Frame(seq: 43, epoch: 1);
            FramePipeline.Translate(
                new TransportFrame(healthy, isText: false),
                healthy.Length,
                gate,
                out FrameTranslation suppressed
            );
            Assert.That(suppressed.HasViolation, Is.False);
            Assert.That(suppressed.HasEvent, Is.False);
            Assert.That(gate.Quarantined, Is.True);
        }

        [Test]
        public void OversizedBinaryFrameIsAProtocolViolation()
        {
            DeliveryGate gate = NegotiatedGate();
            byte[] frame = new byte[64 * 1024 + 1];

            FramePipeline.Translate(
                new TransportFrame(frame, isText: false),
                frame.Length - 1,
                gate,
                out FrameTranslation translated
            );

            /*
                The bound breach surfaces as the plain transport violation
                (no envelope, no gate diagnostic), like an oversized text
                frame.
            */
            Assert.That(translated.HasEvent, Is.True);
            Assert.That(translated.Event.Kind, Is.EqualTo(PollEventKind.ProtocolViolation));
            Assert.That(translated.Event.Violation, Is.EqualTo(default(MessageKind)));
        }

        [Test]
        public void DisconnectPolicyTearsDownOnBinaryRefusal()
        {
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Disconnect);
            byte[] frame = V3Frame(seq: 1, epoch: 1);

            FramePipeline.Translate(
                new TransportFrame(frame, isText: false),
                frame.Length,
                gate,
                out FrameTranslation translated
            );

            Assert.That(translated.HasViolation, Is.True);
            Assert.That(translated.IsClose, Is.True);
            Assert.That(translated.PolicyTeardown, Is.True);
        }

        [Test]
        public void ObservePolicySurfacesRefusalsAndKeepsTheSessionFlowing()
        {
            /*
                Observe never suppresses and never tears down: the
                JSON-negotiated session reports the physical refusal and
                stays fully usable — the refused frame is not decoded into
                game data, and the cursors never move.
            */
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Observe);
            Assert.That(gate.OnProtocolInfo(3, CanonicalFormats, out _), Is.True);
            _ = gate.RebaselineSnapshot(Players(selfWithStamps: true));

            byte[] frame = V3Frame(seq: 43, epoch: 1);
            FramePipeline.Translate(
                new TransportFrame(frame, isText: false),
                frame.Length,
                gate,
                out FrameTranslation translated
            );

            Assert.That(translated.HasViolation, Is.True);
            Assert.That(translated.HasEvent, Is.False);
            Assert.That(translated.IsClose, Is.False);
            Assert.That(gate.Quarantined, Is.False);

            /*
                Cursor continuity: the refused frame consumed nothing, so
                the sender's next legitimate JSON stamp at seq 43 is
                accepted — no phantom gap.
            */
            byte[] jsonFrame = Encoding.UTF8.GetBytes(
                GoldenFixtures.ReadFirstLineOfType("v3-server-messages.jsonl", "GameData")
            );
            FramePipeline.Translate(
                new TransportFrame(jsonFrame, isText: true),
                jsonFrame.Length,
                gate,
                out FrameTranslation relayed
            );
            Assert.That(relayed.HasViolation, Is.False, relayed.Violation.Diagnostic);
            Assert.That(relayed.Event.Kind, Is.EqualTo(PollEventKind.GameData));
        }

        [Test]
        public void ObserveMismatchedEncodingSurfacesWithoutAdvancingTheCursors()
        {
            /*
                A decoded envelope that names a different encoding is
                informational under Observe: the payload surfaces, the
                cursors stay put, so the real stamp at the same seq still
                lands.
            */
            DeliveryGate gate = NegotiatedGate(policy: DeliveryViolationPolicy.Observe);
            byte[] mismatched = V3Frame(seq: 43, epoch: 1, encoding: "json");
            FramePipeline.Translate(
                new TransportFrame(mismatched, isText: false),
                mismatched.Length,
                gate,
                out FrameTranslation translated
            );

            Assert.That(translated.HasViolation, Is.True);
            Assert.That(translated.HasEvent, Is.True);
            Assert.That(translated.Event.Kind, Is.EqualTo(PollEventKind.GameData));

            byte[] jsonFrame = Encoding.UTF8.GetBytes(
                GoldenFixtures.ReadFirstLineOfType("v3-server-messages.jsonl", "GameData")
            );
            FramePipeline.Translate(
                new TransportFrame(jsonFrame, isText: true),
                jsonFrame.Length,
                gate,
                out FrameTranslation relayed
            );
            Assert.That(relayed.HasViolation, Is.False, relayed.Violation.Diagnostic);
            Assert.That(relayed.Event.Kind, Is.EqualTo(PollEventKind.GameData));
        }

        [Test]
        public void DisconnectPolicyKeepsDecodeFailuresBounded()
        {
            /*
                A malformed frame is a decode failure, never a policy
                teardown — even under Disconnect: the frame carries no
                decodable contract to enforce.
            */
            DeliveryGate gate = NegotiatedGate(policy: DeliveryViolationPolicy.Disconnect);
            byte[] frame = { 0xde, 0x00, 0x81 };

            FramePipeline.Translate(
                new TransportFrame(frame, isText: false),
                frame.Length,
                gate,
                out FrameTranslation translated
            );

            Assert.That(translated.HasViolation, Is.False);
            Assert.That(translated.Event.Kind, Is.EqualTo(PollEventKind.DecodeFailed));
            Assert.That(translated.IsClose, Is.False);
        }

        [Test]
        public void V2NegotiatedBinarySurfacesWithoutStamps()
        {
            /*
                A v2-capped connection that still negotiated message_pack
                receives the three-field envelope: no stamps, bare v2
                floor gating.
            */
            DeliveryGate gate = new DeliveryGate(DeliveryViolationPolicy.Quarantine);
            gate.NoteRequestedFormat("message_pack");
            Assert.That(gate.OnProtocolInfo(2, CanonicalFormats, out _), Is.True);
            _ = gate.RebaselineSnapshot(Players(selfWithStamps: false));

            List<byte> frame = new List<byte> { 0x83 };
            WriteKey(frame, "from_player");
            WriteBin(frame, SenderB.ToNetworkOrderBytes());
            WriteKey(frame, "encoding");
            WriteStr(frame, "message_pack");
            WriteKey(frame, "payload");
            WriteBin(frame, PayloadBytes);

            FramePipeline.Translate(
                new TransportFrame(frame.ToArray(), isText: false),
                frame.Count,
                gate,
                out FrameTranslation translated
            );

            Assert.That(translated.HasViolation, Is.False, translated.Violation.Diagnostic);
            Assert.That(translated.Event.Kind, Is.EqualTo(PollEventKind.GameData));
            Assert.That(translated.Event.GameData.Seq, Is.Null);
            Assert.That(translated.Event.GameData.Epoch, Is.Null);
        }

        /*
            ------------------------------------------------------------------
            Allocation gate
            ------------------------------------------------------------------
        */

        [Test]
        public void BinaryRelayHotPathAllocatesNothing()
        {
            DeliveryGate gate = NegotiatedGate();
            byte[][] frames = new byte[400][];
            for (int i = 0; i < frames.Length; i++)
            {
                frames[i] = V3Frame(seq: (ulong)(43 + i), epoch: 1);
            }

            long minDelta = long.MaxValue;
            int surfaced = 0;
            for (int pass = 0; pass < 4; pass++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int iteration = 0; iteration < 100; iteration++)
                {
                    byte[] frame = frames[(pass * 100) + iteration];
                    FramePipeline.Translate(
                        new TransportFrame(frame, isText: false),
                        frame.Length,
                        gate,
                        out FrameTranslation translated
                    );
                    if (translated.HasEvent)
                    {
                        surfaced++;
                    }
                }

                long after = GC.GetAllocatedBytesForCurrentThread();
                minDelta = Math.Min(minDelta, after - before);
            }

            Assert.That(surfaced, Is.EqualTo(frames.Length), "the relay stream must stay accepted");
            Assert.That(
                minDelta,
                Is.EqualTo(0),
                "Binary GameData must flow through decode + gate + engine without allocating."
            );
        }

        /*
            ------------------------------------------------------------------
            Helpers
            ------------------------------------------------------------------
        */

        /// <summary>
        /// A v3 gate that negotiated message_pack and baselined the
        /// golden roster (sender ...b at seq 42, epoch 1).
        /// </summary>
        private static DeliveryGate NegotiatedGate(
            string? requestedFormat = "message_pack",
            DeliveryViolationPolicy policy = DeliveryViolationPolicy.Quarantine
        )
        {
            DeliveryGate gate = new DeliveryGate(policy);
            gate.NoteRequestedFormat(requestedFormat);
            Assert.That(gate.OnProtocolInfo(3, CanonicalFormats, out _), Is.True);
            GateVerdict baseline = gate.RebaselineSnapshot(Players(selfWithStamps: true));
            Assert.That(baseline.Suppress, Is.False, baseline.Diagnostic);
            return gate;
        }

        private static List<PlayerInfo> Players(bool selfWithStamps)
        {
            return new List<PlayerInfo>
            {
                new PlayerInfo(
                    new Guid("00000000-0000-0000-0000-00000000000a"),
                    "Alice",
                    isAuthority: true,
                    isReady: true,
                    connectedAt: null,
                    epoch: selfWithStamps ? 1 : null,
                    seq: selfWithStamps ? 42 : null
                ),
                new PlayerInfo(
                    SenderB,
                    "Bob",
                    isAuthority: false,
                    isReady: true,
                    connectedAt: null,
                    epoch: selfWithStamps ? 1 : null,
                    seq: selfWithStamps ? 42 : null
                ),
            };
        }

        private static byte[] V3Frame(
            ulong seq,
            uint epoch,
            Guid? fromPlayer = null,
            string encoding = "message_pack"
        )
        {
            List<byte> frame = new List<byte> { 0x85 };
            WriteKey(frame, "from_player");
            WriteBin(frame, (fromPlayer ?? SenderB).ToNetworkOrderBytes());
            WriteKey(frame, "encoding");
            WriteStr(frame, encoding);
            WriteKey(frame, "payload");
            WriteBin(frame, PayloadBytes);
            WriteKey(frame, "seq");
            WriteUInt(frame, seq);
            WriteKey(frame, "epoch");
            WriteUInt(frame, epoch);
            return frame.ToArray();
        }

        private static byte[] Malformed(string shape)
        {
            switch (shape)
            {
                case "unknown-key":
                {
                    List<byte> frame = new List<byte> { 0x85 };
                    WriteKey(frame, "from_player");
                    WriteBin(frame, SenderB.ToNetworkOrderBytes());
                    WriteKey(frame, "encoding");
                    WriteStr(frame, "message_pack");
                    WriteKey(frame, "payload");
                    WriteBin(frame, PayloadBytes);
                    WriteKey(frame, "bogus");
                    WriteStr(frame, "x");
                    WriteUInt(frame, 1);
                    return frame.ToArray();
                }
                case "duplicate-key":
                {
                    List<byte> frame = new List<byte> { 0x85 };
                    WriteKey(frame, "from_player");
                    WriteBin(frame, SenderB.ToNetworkOrderBytes());
                    WriteKey(frame, "from_player");
                    WriteBin(frame, SenderB.ToNetworkOrderBytes());
                    WriteKey(frame, "encoding");
                    WriteStr(frame, "message_pack");
                    WriteKey(frame, "payload");
                    WriteBin(frame, PayloadBytes);
                    WriteKey(frame, "seq");
                    WriteUInt(frame, 1);
                    return frame.ToArray();
                }
                case "missing-key":
                {
                    List<byte> frame = new List<byte> { 0x84 };
                    WriteKey(frame, "from_player");
                    WriteBin(frame, SenderB.ToNetworkOrderBytes());
                    WriteKey(frame, "encoding");
                    WriteStr(frame, "message_pack");
                    WriteKey(frame, "payload");
                    WriteBin(frame, PayloadBytes);
                    WriteKey(frame, "seq");
                    WriteUInt(frame, 1);
                    return frame.ToArray();
                }
                case "zero-seq":
                    return V3Frame(seq: 0, epoch: 1);
                case "zero-epoch":
                    return V3Frame(seq: 1, epoch: 0);
                case "short-uuid":
                {
                    List<byte> frame = new List<byte> { 0x85 };
                    WriteKey(frame, "from_player");
                    WriteBin(frame, new byte[] { 1, 2, 3 });
                    WriteKey(frame, "encoding");
                    WriteStr(frame, "message_pack");
                    WriteKey(frame, "payload");
                    WriteBin(frame, PayloadBytes);
                    WriteKey(frame, "seq");
                    WriteUInt(frame, 1);
                    WriteKey(frame, "epoch");
                    WriteUInt(frame, 1);
                    return frame.ToArray();
                }
                case "string-payload":
                {
                    List<byte> frame = new List<byte> { 0x85 };
                    WriteKey(frame, "from_player");
                    WriteBin(frame, SenderB.ToNetworkOrderBytes());
                    WriteKey(frame, "encoding");
                    WriteStr(frame, "message_pack");
                    WriteKey(frame, "payload");
                    WriteStr(frame, "not-bin");
                    WriteKey(frame, "seq");
                    WriteUInt(frame, 1);
                    WriteKey(frame, "epoch");
                    WriteUInt(frame, 1);
                    return frame.ToArray();
                }
                case "root-array":
                    return new byte[] { 0x91, 0x01 };
                case "truncated":
                    return new byte[] { 0xde, 0x00 };
                case "trailing":
                {
                    byte[] frame = V3Frame(seq: 1, epoch: 1);
                    byte[] withTrailing = new byte[frame.Length + 1];
                    Array.Copy(frame, withTrailing, frame.Length);
                    withTrailing[^1] = 0x01;
                    return withTrailing;
                }
                case "unknown-encoding":
                    return V3Frame(seq: 1, epoch: 1, encoding: "cbor");
                case "seq-on-v2":
                {
                    List<byte> frame = new List<byte> { 0x84 };
                    WriteKey(frame, "from_player");
                    WriteBin(frame, SenderB.ToNetworkOrderBytes());
                    WriteKey(frame, "encoding");
                    WriteStr(frame, "message_pack");
                    WriteKey(frame, "payload");
                    WriteBin(frame, PayloadBytes);
                    WriteKey(frame, "seq");
                    WriteUInt(frame, 1);
                    return frame.ToArray();
                }
                default:
                    throw new ArgumentException("unknown shape: " + shape);
            }
        }

        private static void WriteKey(List<byte> frame, string key)
        {
            WriteStr(frame, key);
        }

        private static void WriteStr(List<byte> frame, string value)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            Assert.That(bytes.Length, Is.LessThanOrEqualTo(0x1f), "the test writer pins fixstr");
            frame.Add((byte)(0xa0 | bytes.Length));
            frame.AddRange(bytes);
        }

        private static void WriteWideStr(List<byte> frame, string value)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            frame.Add(0xd9);
            frame.Add((byte)bytes.Length);
            frame.AddRange(bytes);
        }

        private static void WriteWideBin(List<byte> frame, byte[] value)
        {
            frame.Add(0xc5);
            frame.Add((byte)(value.Length >> 8));
            frame.Add((byte)value.Length);
            frame.AddRange(value);
        }

        private static void WriteBin(List<byte> frame, byte[] value)
        {
            frame.Add(0xc4);
            Assert.That(value.Length, Is.LessThanOrEqualTo(0xff), "the test writer pins bin8");
            frame.Add((byte)value.Length);
            frame.AddRange(value);
        }

        private static void WriteUInt(List<byte> frame, ulong value)
        {
            if (value <= 0x7f)
            {
                frame.Add((byte)value);
            }
            else if (value <= byte.MaxValue)
            {
                frame.Add(0xcc);
                frame.Add((byte)value);
            }
            else if (value <= ushort.MaxValue)
            {
                frame.Add(0xcd);
                frame.Add((byte)(value >> 8));
                frame.Add((byte)value);
            }
            else if (value <= uint.MaxValue)
            {
                frame.Add(0xce);
                frame.Add((byte)(value >> 24));
                frame.Add((byte)(value >> 16));
                frame.Add((byte)(value >> 8));
                frame.Add((byte)value);
            }
            else
            {
                frame.Add(0xcf);
                for (int shift = 56; shift >= 0; shift -= 8)
                {
                    frame.Add((byte)(value >> shift));
                }
            }
        }
    }

    /// <summary>Test-side GUID wiring: the 16 RFC-4122 (network order) bytes.</summary>
    internal static class NetworkUuidBytes
    {
        /// <summary>
        /// The Guid's RFC-4122 byte order: the string groups big-endian.
        /// <c>ToByteArray</c> is little-endian for the first three groups,
        /// so each of those groups reverses; the tail copies verbatim.
        /// </summary>
        internal static byte[] ToNetworkOrderBytes(this Guid value)
        {
            byte[] bytes = value.ToByteArray();
            Array.Reverse(bytes, 0, 4);
            Array.Reverse(bytes, 4, 2);
            Array.Reverse(bytes, 6, 2);
            return bytes;
        }
    }
}
