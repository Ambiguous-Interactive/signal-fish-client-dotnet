namespace SignalFish.Client.Tests
{
    using System;
    using System.Text;
    using NUnit.Framework;
    using SignalFish.Client.Protocol;

    /// <summary>
    /// Red-green coverage for the v3 delivery-accountability decode
    /// surface: the pinned <c>tests/Golden/v3-server-messages.jsonl</c>
    /// samples (DeliveryReport, GoingAway, epoch/seq baselines, sender
    /// watermarks), v2 backward compatibility (absent fields), explicit
    /// JSON null treated as absent, and the negative policies (wrong
    /// types, repeated keys, unknown gap reasons, missing required
    /// counters). Each golden sample goes through
    /// <see cref="EnvelopeReader"/> first, so payload decode is exercised
    /// on real envelope slices.
    /// </summary>
    [TestFixture]
    public class V3DeliveryDecodeTests
    {
        [Test]
        public void DeliveryReportGoldenFixtureDecodesAllCountersAndGap()
        {
            EnvelopeEvent envelope = DecodeV3Envelope("DeliveryReport");
            Assert.That(
                DeliveryReportMessage.TryDecode(envelope.Data, out DeliveryReportMessage message),
                Is.True
            );

            Assert.That(
                message.PerClass.Reliable,
                Is.EqualTo(new ReliableDeliveryCounters(8, 0, 0))
            );
            Assert.That(
                message.PerClass.Latest,
                Is.EqualTo(new LatestDeliveryCounters(12, 1, 0, 0, 0))
            );
            Assert.That(
                message.PerClass.Volatile,
                Is.EqualTo(new VolatileDeliveryCounters(4, 0, 0, 0))
            );

            Assert.That(message.Gaps, Has.Count.EqualTo(1));
            Assert.That(
                message.Gaps[0],
                Is.EqualTo(
                    new DeliveryGap(
                        new Guid("00000000-0000-0000-0000-00000000000b"),
                        1,
                        42,
                        42,
                        DeliveryGapReason.LatestSuperseded
                    )
                )
            );
        }

        [Test]
        public void DeliveryReportWithoutGapsDecodesEmpty()
        {
            bool decoded = DeliveryReportMessage.TryDecode(
                Bytes(
                    "{\"per_class\": {\"reliable\": {\"delivered\": 1, "
                        + "\"abandoned\": 0, \"unsupported_format\": 0}, "
                        + "\"latest\": {\"delivered\": 0, \"superseded\": 0, "
                        + "\"dropped_full\": 0, \"abandoned\": 0, "
                        + "\"unsupported_format\": 0}, \"volatile\": "
                        + "{\"delivered\": 0, \"dropped\": 0, \"abandoned\": 0, "
                        + "\"unsupported_format\": 0}}}"
                ),
                out DeliveryReportMessage message
            );
            Assert.That(decoded, Is.True);
            Assert.That(message.Gaps, Is.Empty);
        }

        [Test]
        public void DeliveryReportWithExplicitNullGapsDecodesEmpty()
        {
            bool decoded = DeliveryReportMessage.TryDecode(
                Bytes(
                    "{\"per_class\": {\"reliable\": {\"delivered\": 1, "
                        + "\"abandoned\": 0, \"unsupported_format\": 0}, "
                        + "\"latest\": {\"delivered\": 0, \"superseded\": 0, "
                        + "\"dropped_full\": 0, \"abandoned\": 0, "
                        + "\"unsupported_format\": 0}, \"volatile\": "
                        + "{\"delivered\": 0, \"dropped\": 0, \"abandoned\": 0, "
                        + "\"unsupported_format\": 0}}, \"gaps\": null}"
                ),
                out DeliveryReportMessage message
            );
            Assert.That(decoded, Is.True);
            Assert.That(message.Gaps, Is.Empty);
        }

        [Test]
        public void DeliveryReportWithMissingCounterFieldRejectsDecode()
        {
            bool decoded = DeliveryReportMessage.TryDecode(
                Bytes(
                    "{\"per_class\": {\"reliable\": {\"delivered\": 8}, "
                        + "\"latest\": {\"delivered\": 0, \"superseded\": 0, "
                        + "\"dropped_full\": 0, \"abandoned\": 0, "
                        + "\"unsupported_format\": 0}, \"volatile\": "
                        + "{\"delivered\": 0, \"dropped\": 0, \"abandoned\": 0, "
                        + "\"unsupported_format\": 0}}}"
                ),
                out DeliveryReportMessage message
            );
            Assert.That(decoded, Is.False);
            Assert.That(message, Is.EqualTo(default(DeliveryReportMessage)));
        }

        [Test]
        public void DeliveryReportWithMissingCounterSubObjectRejectsDecode()
        {
            bool decoded = DeliveryReportMessage.TryDecode(
                Bytes(
                    "{\"per_class\": {\"reliable\": {\"delivered\": 8, "
                        + "\"abandoned\": 0, \"unsupported_format\": 0}, "
                        + "\"volatile\": {\"delivered\": 0, \"dropped\": 0, "
                        + "\"abandoned\": 0, \"unsupported_format\": 0}}}"
                ),
                out DeliveryReportMessage message
            );
            Assert.That(decoded, Is.False);
            Assert.That(message, Is.EqualTo(default(DeliveryReportMessage)));
        }

        [Test]
        public void DeliveryReportWithMissingPerClassRejectsDecode()
        {
            bool decoded = DeliveryReportMessage.TryDecode(
                Bytes("{}"),
                out DeliveryReportMessage message
            );
            Assert.That(decoded, Is.False);
            Assert.That(message, Is.EqualTo(default(DeliveryReportMessage)));
        }

        [TestCase(
            "{\"per_class\": {\"reliable\": {\"delivered\": 8, "
                + "\"abandoned\": 0, \"unsupported_format\": 0}, "
                + "\"latest\": {\"delivered\": 0, \"superseded\": 0, "
                + "\"dropped_full\": 0, \"abandoned\": 0, "
                + "\"unsupported_format\": 0}, \"volatile\": "
                + "{\"delivered\": 0, \"dropped\": 0, \"abandoned\": 0, "
                + "\"unsupported_format\": 0}}, \"gaps\": "
                + "[{\"from_player\": \"00000000-0000-0000-0000-00000000000b\", "
                + "\"epoch\": 1, \"from_seq\": 42, \"to_seq\": 42, "
                + "\"reason\": \"eventual\"}]}",
            TestName = "UnknownGapReason"
        )]
        [TestCase(
            "{\"per_class\": {\"reliable\": {\"delivered\": 8, "
                + "\"abandoned\": 0, \"unsupported_format\": 0}, "
                + "\"latest\": {\"delivered\": 0, \"superseded\": 0, "
                + "\"dropped_full\": 0, \"abandoned\": 0, "
                + "\"unsupported_format\": 0}, \"volatile\": "
                + "{\"delivered\": 0, \"dropped\": 0, \"abandoned\": 0, "
                + "\"unsupported_format\": 0}}, \"gaps\": "
                + "[{\"from_player\": \"00000000-0000-0000-0000-00000000000b\", "
                + "\"epoch\": 1, \"from_seq\": 42, \"to_seq\": 42, "
                + "\"reason\": 7}]}",
            TestName = "NonStringGapReason"
        )]
        [TestCase(
            "{\"per_class\": {\"reliable\": {\"delivered\": 8, "
                + "\"abandoned\": 0, \"unsupported_format\": 0}, "
                + "\"latest\": {\"delivered\": 0, \"superseded\": 0, "
                + "\"dropped_full\": 0, \"abandoned\": 0, "
                + "\"unsupported_format\": 0}, \"volatile\": "
                + "{\"delivered\": 0, \"dropped\": 0, \"abandoned\": 0, "
                + "\"unsupported_format\": 0}}, \"gaps\": "
                + "[{\"from_player\": \"00000000-0000-0000-0000-00000000000b\", "
                + "\"epoch\": 1, \"from_seq\": 42, \"to_seq\": 42}]}",
            TestName = "MissingGapReason"
        )]
        [TestCase(
            "{\"per_class\": {\"reliable\": {\"delivered\": \"eight\", "
                + "\"abandoned\": 0, \"unsupported_format\": 0}, "
                + "\"latest\": {\"delivered\": 0, \"superseded\": 0, "
                + "\"dropped_full\": 0, \"abandoned\": 0, "
                + "\"unsupported_format\": 0}, \"volatile\": "
                + "{\"delivered\": 0, \"dropped\": 0, \"abandoned\": 0, "
                + "\"unsupported_format\": 0}}}",
            TestName = "WrongTypedCounterValue"
        )]
        [TestCase(
            "{\"per_class\": {\"reliable\": {\"delivered\": 8, "
                + "\"abandoned\": 0, \"unsupported_format\": 0, "
                + "\"delivered\": 9}, "
                + "\"latest\": {\"delivered\": 0, \"superseded\": 0, "
                + "\"dropped_full\": 0, \"abandoned\": 0, "
                + "\"unsupported_format\": 0}, \"volatile\": "
                + "{\"delivered\": 0, \"dropped\": 0, \"abandoned\": 0, "
                + "\"unsupported_format\": 0}}}",
            TestName = "RepeatedCounterKey"
        )]
        [TestCase(
            "{\"per_class\": {\"reliable\": {\"delivered\": 8, "
                + "\"abandoned\": 0, \"unsupported_format\": 0}, "
                + "\"reliable\": {\"delivered\": 0, \"abandoned\": 0, "
                + "\"unsupported_format\": 0}, "
                + "\"latest\": {\"delivered\": 0, \"superseded\": 0, "
                + "\"dropped_full\": 0, \"abandoned\": 0, "
                + "\"unsupported_format\": 0}, \"volatile\": "
                + "{\"delivered\": 0, \"dropped\": 0, \"abandoned\": 0, "
                + "\"unsupported_format\": 0}}}",
            TestName = "RepeatedPerClassSubObject"
        )]
        public void DeliveryReportWithMalformedPayloadRejectsDecode(string wire)
        {
            bool decoded = DeliveryReportMessage.TryDecode(
                Bytes(wire),
                out DeliveryReportMessage message
            );
            Assert.That(decoded, Is.False);
            Assert.That(message, Is.EqualTo(default(DeliveryReportMessage)));
        }

        [Test]
        public void RoomJoinedV3GoldenPlayerCarriesEpochAndSeqBaselines()
        {
            EnvelopeEvent envelope = DecodeV3Envelope("RoomJoined");
            Assert.That(
                RoomJoinedMessage.TryDecode(envelope.Data, out RoomJoinedMessage message),
                Is.True
            );
            Assert.That(message.Snapshot.CurrentPlayers, Has.Count.EqualTo(1));
            PlayerInfo alice = message.Snapshot.CurrentPlayers[0];
            Assert.That(alice.Epoch, Is.EqualTo(1u));
            Assert.That(alice.Seq, Is.EqualTo(0ul));
            Assert.That(alice.ConnectedAt, Is.Null);
        }

        [Test]
        public void ReconnectedV3GoldenDecodesSenderWatermarks()
        {
            EnvelopeEvent envelope = DecodeV3Envelope("Reconnected");
            Assert.That(
                ReconnectedMessage.TryDecode(envelope.Data, out ReconnectedMessage message),
                Is.True
            );

            Assert.That(message.SenderWatermarks, Has.Count.EqualTo(2));
            Assert.That(
                message.SenderWatermarks![0],
                Is.EqualTo(
                    new SenderWatermark(new Guid("00000000-0000-0000-0000-00000000000a"), 1, 42)
                )
            );
            Assert.That(
                message.SenderWatermarks[1],
                Is.EqualTo(
                    new SenderWatermark(new Guid("00000000-0000-0000-0000-00000000000b"), 2, 0)
                )
            );
        }

        [Test]
        public void GoingAwayGoldenFixtureDecodesDeadlineAndRetryAfter()
        {
            EnvelopeEvent envelope = DecodeV3Envelope("GoingAway");
            Assert.That(
                GoingAwayMessage.TryDecode(envelope.Data, out GoingAwayMessage message),
                Is.True
            );
            Assert.That(message.DeadlineMs, Is.EqualTo(1700000000000ul));
            Assert.That(message.RetryAfterSecs, Is.EqualTo(30ul));
        }

        [Test]
        public void RelayStatsWireDecodesAllCounters()
        {
            bool decoded = RelayStatsMessage.TryDecode(
                Bytes(
                    "{\"interval_ms\": 1000, \"sent_to_you\": 120, "
                        + "\"dropped_for_you\": 3, \"backpressure_events\": 1}"
                ),
                out RelayStatsMessage message
            );
            Assert.That(decoded, Is.True);
            Assert.That(message.IntervalMs, Is.EqualTo(1000ul));
            Assert.That(message.SentToYou, Is.EqualTo(120ul));
            Assert.That(message.DroppedForYou, Is.EqualTo(3ul));
            Assert.That(message.BackpressureEvents, Is.EqualTo(1ul));
        }

        // --- v2 backward compatibility ---------------------------------------
        [Test]
        public void V2PlayerGoldenFixturesDecodeWithNullV3Optionals()
        {
            EnvelopeEvent joined = DecodeV2Envelope("PlayerJoined");
            Assert.That(
                PlayerJoinedMessage.TryDecode(joined.Data, out PlayerJoinedMessage playerJoined),
                Is.True
            );
            Assert.That(playerJoined.Player.Epoch, Is.Null);
            Assert.That(playerJoined.Player.Seq, Is.Null);
            Assert.That(playerJoined.Player.ConnectedAt, Is.Not.Null);

            EnvelopeEvent left = DecodeV2Envelope("PlayerLeft");
            Assert.That(
                PlayerLeftMessage.TryDecode(left.Data, out PlayerLeftMessage playerLeft),
                Is.True
            );
            Assert.That(playerLeft.Epoch, Is.Null);
            Assert.That(playerLeft.FinalSeq, Is.Null);

            EnvelopeEvent reconnected = DecodeV2Envelope("PlayerReconnected");
            Assert.That(
                PlayerReconnectedMessage.TryDecode(
                    reconnected.Data,
                    out PlayerReconnectedMessage playerReconnected
                ),
                Is.True
            );
            Assert.That(playerReconnected.Epoch, Is.Null);
        }

        [Test]
        public void V2ReconnectedGoldenDecodesNullSenderWatermarks()
        {
            EnvelopeEvent envelope = DecodeV2Envelope("Reconnected");
            Assert.That(
                ReconnectedMessage.TryDecode(envelope.Data, out ReconnectedMessage message),
                Is.True
            );
            Assert.That(message.SenderWatermarks, Is.Null);
        }

        // --- Explicit JSON null counts as absent ------------------------------
        [Test]
        public void PlayerInfoWithExplicitNullEpochAndSeqTreatsThemAsAbsent()
        {
            bool decoded = PlayerInfo.TryDecode(
                Bytes(
                    "{\"id\": \"00000000-0000-0000-0000-00000000000a\", "
                        + "\"name\": \"alice\", \"is_authority\": true, "
                        + "\"is_ready\": false, \"epoch\": null, \"seq\": null}"
                ),
                out PlayerInfo player
            );
            Assert.That(decoded, Is.True);
            Assert.That(player.Epoch, Is.Null);
            Assert.That(player.Seq, Is.Null);
        }

        [Test]
        public void PlayerLeftWithExplicitNullEpochAndFinalSeqTreatsThemAsAbsent()
        {
            bool decoded = PlayerLeftMessage.TryDecode(
                Bytes(
                    "{\"player_id\": \"00000000-0000-0000-0000-00000000000b\", "
                        + "\"epoch\": null, \"final_seq\": null}"
                ),
                out PlayerLeftMessage message
            );
            Assert.That(decoded, Is.True);
            Assert.That(message.Epoch, Is.Null);
            Assert.That(message.FinalSeq, Is.Null);
        }

        [Test]
        public void PlayerReconnectedWithExplicitNullEpochTreatsItAsAbsent()
        {
            bool decoded = PlayerReconnectedMessage.TryDecode(
                Bytes(
                    "{\"player_id\": \"00000000-0000-0000-0000-00000000000b\", "
                        + "\"epoch\": null}"
                ),
                out PlayerReconnectedMessage message
            );
            Assert.That(decoded, Is.True);
            Assert.That(message.Epoch, Is.Null);
        }

        [Test]
        public void ReconnectedWithExplicitNullSenderWatermarksTreatsThemAsAbsent()
        {
            bool decoded = ReconnectedMessage.TryDecode(
                Bytes(
                    "{\"player_id\": \"00000000-0000-0000-0000-00000000000a\", "
                        + "\"room_id\": \"11111111-1111-1111-1111-111111111111\", "
                        + "\"room_code\": \"ABC123\", \"sender_watermarks\": null}"
                ),
                out ReconnectedMessage message
            );
            Assert.That(decoded, Is.True);
            Assert.That(message.SenderWatermarks, Is.Null);
        }

        // --- Negative v3-field policies ---------------------------------------
        [TestCase("three", TestName = "WrongTyped.Epoch")]
        [TestCase("-1", TestName = "Negative.Epoch")]
        [TestCase("1.5", TestName = "Fractional.Epoch")]
        public void PlayerInfoWithMalformedEpochRejectsDecode(string epochValue)
        {
            bool decoded = PlayerInfo.TryDecode(
                Bytes(
                    "{\"id\": \"00000000-0000-0000-0000-00000000000a\", "
                        + "\"name\": \"alice\", \"is_authority\": true, "
                        + "\"is_ready\": false, \"epoch\": "
                        + epochValue
                        + "}"
                ),
                out PlayerInfo player
            );
            Assert.That(decoded, Is.False);
            Assert.That(player, Is.EqualTo(default(PlayerInfo)));
        }

        [TestCase("\"42\"", TestName = "WrongTyped.Seq")]
        [TestCase("-42", TestName = "Negative.Seq")]
        public void PlayerInfoWithWrongTypedSeqRejectsDecode(string seqValue)
        {
            bool decoded = PlayerInfo.TryDecode(
                Bytes(
                    "{\"id\": \"00000000-0000-0000-0000-00000000000a\", "
                        + "\"name\": \"alice\", \"is_authority\": true, "
                        + "\"is_ready\": false, \"seq\": "
                        + seqValue
                        + "}"
                ),
                out PlayerInfo player
            );
            Assert.That(decoded, Is.False);
            Assert.That(player, Is.EqualTo(default(PlayerInfo)));
        }

        [Test]
        public void PlayerInfoWithRepeatedEpochKeyRejectsDecode()
        {
            bool decoded = PlayerInfo.TryDecode(
                Bytes(
                    "{\"id\": \"00000000-0000-0000-0000-00000000000a\", "
                        + "\"name\": \"alice\", \"is_authority\": true, "
                        + "\"is_ready\": false, \"epoch\": 1, \"epoch\": 2}"
                ),
                out PlayerInfo player
            );
            Assert.That(decoded, Is.False);
            Assert.That(player, Is.EqualTo(default(PlayerInfo)));
        }

        [Test]
        public void PlayerLeftWithRepeatedFinalSeqKeyRejectsDecode()
        {
            bool decoded = PlayerLeftMessage.TryDecode(
                Bytes(
                    "{\"player_id\": \"00000000-0000-0000-0000-00000000000b\", "
                        + "\"final_seq\": 42, \"final_seq\": 43}"
                ),
                out PlayerLeftMessage message
            );
            Assert.That(decoded, Is.False);
            Assert.That(message, Is.EqualTo(default(PlayerLeftMessage)));
        }

        [Test]
        public void ReconnectedWithMalformedWatermarkElementRejectsDecode()
        {
            bool decoded = ReconnectedMessage.TryDecode(
                Bytes(
                    "{\"player_id\": \"00000000-0000-0000-0000-00000000000a\", "
                        + "\"room_id\": \"11111111-1111-1111-1111-111111111111\", "
                        + "\"room_code\": \"ABC123\", \"sender_watermarks\": "
                        + "[{\"player_id\": \"00000000-0000-0000-0000-00000000000a\", "
                        + "\"epoch\": 1}]}"
                ),
                out ReconnectedMessage message
            );
            Assert.That(decoded, Is.False);
            Assert.That(message, Is.EqualTo(default(ReconnectedMessage)));
        }

        [Test]
        public void ReconnectedWithWrongTypedWatermarkEpochRejectsDecode()
        {
            bool decoded = ReconnectedMessage.TryDecode(
                Bytes(
                    "{\"player_id\": \"00000000-0000-0000-0000-00000000000a\", "
                        + "\"room_id\": \"11111111-1111-1111-1111-111111111111\", "
                        + "\"room_code\": \"ABC123\", \"sender_watermarks\": "
                        + "[{\"player_id\": \"00000000-0000-0000-0000-00000000000a\", "
                        + "\"epoch\": \"one\", \"seq\": 42}]}"
                ),
                out ReconnectedMessage message
            );
            Assert.That(decoded, Is.False);
            Assert.That(message, Is.EqualTo(default(ReconnectedMessage)));
        }

        [Test]
        public void RelayStatsWithMissingFieldRejectsDecode()
        {
            bool decoded = RelayStatsMessage.TryDecode(
                Bytes("{\"interval_ms\": 1000, \"sent_to_you\": 120, " + "\"dropped_for_you\": 3}"),
                out RelayStatsMessage message
            );
            Assert.That(decoded, Is.False);
            Assert.That(message, Is.EqualTo(default(RelayStatsMessage)));
        }

        [TestCase("\"soon\"", TestName = "WrongTyped.DeadlineMs")]
        [TestCase("-5", TestName = "Negative.DeadlineMs")]
        public void GoingAwayWithMalformedDeadlineRejectsDecode(string deadlineValue)
        {
            bool decoded = GoingAwayMessage.TryDecode(
                Bytes("{\"deadline_ms\": " + deadlineValue + ", \"retry_after_secs\": 30}"),
                out GoingAwayMessage message
            );
            Assert.That(decoded, Is.False);
            Assert.That(message, Is.EqualTo(default(GoingAwayMessage)));
        }

        [Test]
        public void GoingAwayWithMissingRetryAfterRejectsDecode()
        {
            bool decoded = GoingAwayMessage.TryDecode(
                Bytes("{\"deadline_ms\": 1700000000000}"),
                out GoingAwayMessage message
            );
            Assert.That(decoded, Is.False);
            Assert.That(message, Is.EqualTo(default(GoingAwayMessage)));
        }

        [Test]
        public void GoingAwayWithRepeatedDeadlineKeyRejectsDecode()
        {
            bool decoded = GoingAwayMessage.TryDecode(
                Bytes(
                    "{\"deadline_ms\": 1700000000000, \"deadline_ms\": 1700000000001, "
                        + "\"retry_after_secs\": 30}"
                ),
                out GoingAwayMessage message
            );
            Assert.That(decoded, Is.False);
            Assert.That(message, Is.EqualTo(default(GoingAwayMessage)));
        }

        private static ReadOnlyMemory<byte> Bytes(string json) => Encoding.UTF8.GetBytes(json);

        private static EnvelopeEvent DecodeV3Envelope(string wireType)
        {
            return DecodeEnvelope("v3-server-messages.jsonl", wireType);
        }

        private static EnvelopeEvent DecodeV2Envelope(string wireType)
        {
            return DecodeEnvelope("v2-server-messages.jsonl", wireType);
        }

        private static EnvelopeEvent DecodeEnvelope(string fileName, string wireType)
        {
            string line = GoldenFixtures.ReadFirstLineOfType(fileName, wireType);
            EnvelopeEvent envelope = EnvelopeReader.Decode(Encoding.UTF8.GetBytes(line));
            Assert.That(envelope.Kind, Is.EqualTo(EnvelopeEventKind.Message));
            return envelope;
        }
    }
}
