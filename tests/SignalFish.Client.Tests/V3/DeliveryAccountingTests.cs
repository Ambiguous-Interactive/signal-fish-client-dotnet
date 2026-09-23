namespace SignalFish.Client.Tests.V3
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using NUnit.Framework;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.V3;

    /// <summary>
    /// Port of the Rust delivery-accountability test scenarios: per-sender
    /// epoch/seq baselines, exact gap reports with counter-delta pairing,
    /// departed-sender retirement, stale dispositions, resource bounds,
    /// monotonic <c>RelayStats</c>, and unsupported-format advisory
    /// causality.
    /// </summary>
    [TestFixture]
    public class DeliveryAccountingTests
    {
        private enum MonotonicBucket
        {
            ReliableDelivered,
            LatestDelivered,
            VolatileDelivered,
            ReliableAbandoned,
            LatestAbandoned,
            VolatileAbandoned,
        }

        private readonly struct OnlyPriorCase
        {
            public string Name { get; }

            public DeliveryGap[] Gaps { get; }

            public ulong NextSeq { get; }

            public OnlyPriorCase(string name, DeliveryGap[] gaps, ulong nextSeq)
            {
                Name = name;
                Gaps = gaps;
                NextSeq = nextSeq;
            }
        }

        private readonly struct RelayStatsCase
        {
            public string Name { get; }

            public ulong[]? First { get; }

            public ulong[] Next { get; }

            public string Expected { get; }

            public RelayStatsCase(string name, ulong[]? first, ulong[] next, string expected)
            {
                Name = name;
                First = first;
                Next = next;
                Expected = expected;
            }
        }

        private static readonly TestCaseData[] ClassKeyCases =
        {
            new TestCaseData(null, null, true).SetName(
                "ClassKeyValidationIsDataDriven.AbsentClassAbsentKeyIsValid"
            ),
            new TestCaseData(GameDataClass.Reliable, null, true).SetName(
                "ClassKeyValidationIsDataDriven.ReliableClassAbsentKeyIsValid"
            ),
            new TestCaseData(GameDataClass.Volatile, null, true).SetName(
                "ClassKeyValidationIsDataDriven.VolatileClassAbsentKeyIsValid"
            ),
            new TestCaseData(GameDataClass.Latest, 7U, true).SetName(
                "ClassKeyValidationIsDataDriven.LatestClassWithKeyIsValid"
            ),
            new TestCaseData(null, 7U, false).SetName(
                "ClassKeyValidationIsDataDriven.AbsentClassWithKeyIsInvalid"
            ),
            new TestCaseData(GameDataClass.Reliable, 7U, false).SetName(
                "ClassKeyValidationIsDataDriven.ReliableClassWithKeyIsInvalid"
            ),
            new TestCaseData(GameDataClass.Volatile, 7U, false).SetName(
                "ClassKeyValidationIsDataDriven.VolatileClassWithKeyIsInvalid"
            ),
            new TestCaseData(GameDataClass.Latest, null, false).SetName(
                "ClassKeyValidationIsDataDriven.LatestClassAbsentKeyIsInvalid"
            ),
        };

        private static readonly RelayStatsCase[] RelayStatsViolations =
        {
            new RelayStatsCase(
                "ZeroInterval",
                null,
                new ulong[] { 0, 0, 0, 0 },
                "interval_ms must be positive"
            ),
            new RelayStatsCase(
                "ChangedInterval",
                new ulong[] { 1_000, 4, 2, 1 },
                new ulong[] { 2_000, 4, 2, 1 },
                "interval_ms changed"
            ),
            new RelayStatsCase(
                "SentMovedBackward",
                new ulong[] { 1_000, 4, 2, 1 },
                new ulong[] { 1_000, 3, 2, 1 },
                "counters moved backward"
            ),
            new RelayStatsCase(
                "DroppedMovedBackward",
                new ulong[] { 1_000, 4, 2, 1 },
                new ulong[] { 1_000, 4, 1, 1 },
                "counters moved backward"
            ),
            new RelayStatsCase(
                "BackpressureMovedBackward",
                new ulong[] { 1_000, 4, 2, 1 },
                new ulong[] { 1_000, 4, 2, 0 },
                "counters moved backward"
            ),
        };

        [TestCaseSource(nameof(ClassKeyCases))]
        public void ClassKeyValidationIsDataDriven(
            GameDataClass? classification,
            uint? key,
            bool expectedValid
        )
        {
            Assert.That(
                DeliveryAccountability.ValidateClassKey(classification, key),
                Is.EqualTo(expectedValid),
                classification?.ToString() ?? "absent class"
            );
        }

        [Test]
        public void OnlyPriorExactRangesAuthorizeAGap()
        {
            Guid sender = Id(1);
            DeliveryAccountability valid = NewV3();
            Assert.That(Join(valid, Player(sender, 1)), Is.True);
            Assert.That(Stamp(valid, sender, 1), Is.True);
            Assert.That(Report(valid, CountersWithSuperseded(2), Gap(sender, 2, 3)), Is.True);
            Assert.That(Stamp(valid, sender, 4), Is.True);

            /*
                Each deficient shape must fail with an exact cause: a missing
                report, an incomplete range, or a range that reaches one past
                the frame actually being validated.
            */
            OnlyPriorCase[] cases =
            {
                new OnlyPriorCase("Missing", Array.Empty<DeliveryGap>(), 3),
                new OnlyPriorCase("Incomplete", new[] { Gap(sender, 2, 2) }, 4),
                new OnlyPriorCase("Overreaching", new[] { Gap(sender, 2, 4) }, 4),
            };
            foreach (OnlyPriorCase onlyPrior in cases)
            {
                DeliveryAccountability state = NewV3();
                Assert.That(Join(state, Player(sender, 1)), Is.True, onlyPrior.Name);
                Assert.That(Stamp(state, sender, 1), Is.True, onlyPrior.Name);
                if (onlyPrior.Gaps.Length > 0)
                {
                    Assert.That(
                        Report(
                            state,
                            CountersWithSuperseded(ReportedUnits(onlyPrior.Gaps)),
                            onlyPrior.Gaps
                        ),
                        Is.True,
                        onlyPrior.Name
                    );
                }

                AssertRefused(
                    state.RecordGameData(
                        sender,
                        onlyPrior.NextSeq,
                        1U,
                        null,
                        null,
                        out _,
                        out string? diagnostic
                    ),
                    diagnostic,
                    onlyPrior.Name + " exact cause must fail"
                );
            }
        }

        [Test]
        public void DroppedFullAndVolatileGapReasonsAlignWithTheirCounterBuckets()
        {
            /*
                Every gap reason must sum into its own counter bucket: a
                bucket swap (or a dropped mapping arm) would corrupt server
                loss accounting validation, so both LatestDroppedFull and
                VolatileDropped are pinned in the accepted pairing and
                against a wrong-bucket pairing.
            */
            DeliveryGapReason[] reasons =
            {
                DeliveryGapReason.LatestDroppedFull,
                DeliveryGapReason.VolatileDropped,
            };
            foreach (DeliveryGapReason reason in reasons)
            {
                Guid sender = Id(1);

                // Accepted pairing: the causal gap count lands in its own bucket.
                DeliveryAccountability paired = NewV3();
                Assert.That(Join(paired, Player(sender, 1)), Is.True, reason.ToString());
                Assert.That(Stamp(paired, sender, 1), Is.True, reason.ToString());
                Assert.That(
                    Report(paired, BucketCounters(reason, 2), GapWithReason(sender, reason, 2, 3)),
                    Is.True,
                    reason + " pairing must be accepted"
                );
                // The covered range authorizes the next sequence.
                Assert.That(
                    Stamp(paired, sender, 4),
                    Is.True,
                    reason + " coverage must authorize seq 4"
                );

                // Wrong-bucket pairing: the same gap units reported against a different bucket must violate.
                DeliveryAccountability wrongBucket = NewV3();
                Assert.That(Join(wrongBucket, Player(sender, 1)), Is.True, reason.ToString());
                Assert.That(Stamp(wrongBucket, sender, 1), Is.True, reason.ToString());
                AssertRefused(
                    wrongBucket.RecordReport(
                        new[] { GapWithReason(sender, reason, 2, 3) },
                        CountersWithSuperseded(2),
                        out string? diagnostic
                    ),
                    diagnostic,
                    reason + " gaps must not validate against another bucket's counters"
                );
            }
        }

        [Test]
        public void SameSocketFrontiersSurviveReconnectWatermarkRebaseline()
        {
            Guid sender = Id(2);
            DeliveryAccountability state = NewV3();
            Assert.That(Join(state, Player(sender, 1)), Is.True);
            Assert.That(Report(state, Counters(2)), Is.True);
            Assert.That(state.RecordRelayStats(1_000, 4, 2, 1, out _), Is.True);
            AssertRefused(
                state.RecordReport(Array.Empty<DeliveryGap>(), Counters(1), out string? regression),
                regression,
                "counters must not move backward"
            );

            Assert.That(
                state.RebaselineReconnected(
                    new[] { PlayerAt(sender, 1U, 9UL) },
                    new[] { new SenderWatermark(sender, 1U, 9UL) },
                    out string? rebaselineError
                ),
                Is.True,
                rebaselineError
            );
            AssertRefused(
                state.RecordReport(Array.Empty<DeliveryGap>(), Counters(1), out string? survived),
                survived,
                "counters survive the reconnect rebaseline"
            );
            AssertRefused(
                state.RecordRelayStats(1_000, 3, 2, 1, out string? relay),
                relay,
                "relay stats survive the reconnect rebaseline"
            );
            Assert.That(Report(state, Counters(3)), Is.True);
            Assert.That(state.RecordRelayStats(1_000, 5, 3, 2, out _), Is.True);
            Assert.That(Stamp(state, sender, 10), Is.True);
            AssertRefused(
                state.RecordGameData(sender, 12UL, 1U, null, null, out _, out string? jump),
                jump,
                "the watermark frontier still demands exact sequence continuity"
            );
        }

        [Test]
        public void SameEpochLifecycleIsIdempotentOnlyWhileSenderIsPresent()
        {
            Guid sender = Id(3);
            ulong[] watermarkSeqs = { 0, 7 };
            foreach (ulong watermarkSeq in watermarkSeqs)
            {
                string caseName = watermarkSeq.ToString(CultureInfo.InvariantCulture);
                DeliveryAccountability state = NewV3();
                Assert.That(
                    state.RebaselineReconnected(
                        new[] { PlayerAt(sender, 4U, watermarkSeq) },
                        new[] { new SenderWatermark(sender, 4U, watermarkSeq) },
                        out string? rebaselineError
                    ),
                    Is.True,
                    rebaselineError
                );

                Assert.That(Join(state, Player(sender, 4)), Is.True, caseName);
                Assert.That(state.NotePlayerReconnected(sender, 4U, out _), Is.True, caseName);

                Assert.That(
                    state.NotePlayerLeft(sender, 4U, watermarkSeq + 1, out _),
                    Is.True,
                    caseName
                );
                AssertRefused(
                    state.NotePlayerJoined(Player(sender, 4), out string? rejoined),
                    rejoined,
                    caseName + ": a departed same-epoch join must fail"
                );
                AssertRefused(
                    state.NotePlayerReconnected(sender, 4U, out string? reconnected),
                    reconnected,
                    caseName + ": a departed same-epoch reconnect must fail"
                );
            }
        }

        [Test]
        public void OverlappingRangesInOneReportAreRejectedAtomically()
        {
            Guid sender = Id(4);
            DeliveryAccountability state = NewV3();
            Assert.That(Join(state, Player(sender, 1)), Is.True);
            Assert.That(Stamp(state, sender, 1), Is.True);

            AssertRefused(
                state.RecordReport(
                    new[] { Gap(sender, 2, 3), Gap(sender, 3, 4) },
                    Counters(2),
                    out string? diagnostic
                ),
                diagnostic,
                "overlapping ranges in one report are rejected"
            );

            // Neither the counters nor either range from the rejected frame were committed.
            Assert.That(
                Report(state, CountersWithSuperseded(1), Gap(sender, 2, 2)),
                Is.True,
                "the rejected report must have committed nothing"
            );
            Assert.That(Stamp(state, sender, 3), Is.True);
        }

        [Test]
        public void PriorityLifecycleControlDoesNotInvalidateQueuedOldEpochData()
        {
            Guid sender = Id(5);
            DeliveryAccountability state = NewV3();
            Assert.That(Join(state, Player(sender, 1)), Is.True);
            Assert.That(Stamp(state, sender, 1), Is.True);

            Assert.That(state.NotePlayerLeft(sender, 1U, 2UL, out _), Is.True);
            Assert.That(state.NotePlayerReconnected(sender, 2U, out _), Is.True);
            Assert.That(
                Report(state, CountersWithSuperseded(2), GapAtEpoch(sender, 2U, 1, 2)),
                Is.True
            );

            // Old data already queued before priority control still drains first.
            Assert.That(Stamp(state, sender, 2, out GameDataDisposition staleOld), Is.True);
            Assert.That(staleOld, Is.EqualTo(GameDataDisposition.Stale));
            Assert.That(
                StampEpoch(state, sender, 2U, 3, out GameDataDisposition freshNew),
                Is.True
            );
            Assert.That(freshNew, Is.EqualTo(GameDataDisposition.Apply));
            AssertRefused(
                state.RecordGameData(sender, 3UL, 1U, null, null, out _, out string? oldDup),
                oldDup,
                "old-epoch data beyond its drained frontier is refused"
            );
            AssertRefused(
                state.RecordGameData(sender, 1UL, 99U, null, null, out _, out string? unannounced),
                unannounced,
                "an unannounced epoch is refused"
            );
            AssertRefused(
                state.RecordReport(
                    new[] { GapAtEpoch(sender, 99U, 1, 1) },
                    Counters(2),
                    out string? gapEpoch
                ),
                gapEpoch,
                "a gap on an unannounced epoch is refused"
            );

            DeliveryAccountability mismatch = NewV3();
            Assert.That(Join(mismatch, Player(sender, 1)), Is.True);
            AssertRefused(
                mismatch.RecordReport(
                    Array.Empty<DeliveryGap>(),
                    CountersWithSuperseded(1),
                    out string? counters
                ),
                counters,
                "superseded deltas demand causal gap units"
            );
        }

        [Test]
        public void PlayerLeftTerminalRetiresDeliveredAndExactlyOmittedTails()
        {
            Guid sender = Id(6);
            DeliveryAccountability snapshotTail = NewV3();
            Assert.That(
                snapshotTail.RebaselineSnapshot(new[] { PlayerAt(sender, 1U, 41UL) }, out _),
                Is.True
            );
            Assert.That(snapshotTail.NotePlayerLeft(sender, 1U, 43UL, out _), Is.True);
            Assert.That(Stamp(snapshotTail, sender, 42, out GameDataDisposition tail42), Is.True);
            Assert.That(tail42, Is.EqualTo(GameDataDisposition.Stale));
            Assert.That(Stamp(snapshotTail, sender, 43, out GameDataDisposition tail43), Is.True);
            Assert.That(tail43, Is.EqualTo(GameDataDisposition.Stale));
            AssertRefused(
                snapshotTail.RecordGameData(
                    sender,
                    1UL,
                    1U,
                    null,
                    null,
                    out _,
                    out string? drained
                ),
                drained,
                "the exact tail retirement emptied the sender and departed tables"
            );

            DeliveryAccountability deliveredTail = NewV3();
            Assert.That(Join(deliveredTail, Player(sender, 1)), Is.True);
            Assert.That(Stamp(deliveredTail, sender, 1), Is.True);
            Assert.That(deliveredTail.NotePlayerLeft(sender, 1U, 4UL, out _), Is.True);
            Assert.That(
                Report(deliveredTail, CountersWithSuperseded(2), Gap(sender, 2, 3)),
                Is.True
            );
            Assert.That(
                Stamp(deliveredTail, sender, 4, out GameDataDisposition delivered),
                Is.True
            );
            Assert.That(delivered, Is.EqualTo(GameDataDisposition.Stale));
            AssertRefused(
                deliveredTail.RecordGameData(
                    sender,
                    5UL,
                    1U,
                    null,
                    null,
                    out _,
                    out string? beyond
                ),
                beyond,
                "data past the terminal watermark is refused"
            );

            DeliveryAccountability omittedTail = NewV3();
            Assert.That(Join(omittedTail, Player(sender, 1)), Is.True);
            Assert.That(omittedTail.NotePlayerLeft(sender, 1U, 2UL, out _), Is.True);
            Assert.That(Report(omittedTail, CountersWithSuperseded(2), Gap(sender, 1, 2)), Is.True);
            AssertRefused(
                omittedTail.RecordGameData(sender, 1UL, 1U, null, null, out _, out string? omitted),
                omitted,
                "a fully omitted tail retires the sender outright"
            );

            DeliveryAccountability overreaching = NewV3();
            Assert.That(Join(overreaching, Player(sender, 1)), Is.True);
            Assert.That(overreaching.NotePlayerLeft(sender, 1U, 2UL, out _), Is.True);
            AssertRefused(
                overreaching.RecordReport(
                    new[] { Gap(sender, 1, 3) },
                    CountersWithSuperseded(3),
                    out string? overreach
                ),
                overreach,
                "a gap beyond the terminal watermark is refused"
            );
        }

        [Test]
        public void MultipleOvertakingPlayerLeftEpochsRetireIndependently()
        {
            Guid sender = Id(7);
            DeliveryAccountability state = NewV3();
            Assert.That(Join(state, Player(sender, 1)), Is.True);
            for (uint epoch = 1; epoch <= 3; epoch++)
            {
                string caseName = epoch.ToString(CultureInfo.InvariantCulture);
                if (epoch > 1)
                {
                    Assert.That(
                        state.NotePlayerReconnected(sender, epoch, out _),
                        Is.True,
                        caseName
                    );
                }

                Assert.That(state.NotePlayerLeft(sender, epoch, 2UL, out _), Is.True, caseName);
            }

            Assert.That(
                Report(state, CountersWithSuperseded(2), GapAtEpoch(sender, 2U, 1, 2)),
                Is.True
            );
            Assert.That(StampEpoch(state, sender, 1U, 1, out GameDataDisposition epoch1), Is.True);
            Assert.That(epoch1, Is.EqualTo(GameDataDisposition.Stale));
            Assert.That(
                Report(state, CountersWithSuperseded(3), GapAtEpoch(sender, 1U, 2, 2)),
                Is.True
            );
            Assert.That(
                Report(state, CountersWithSuperseded(4), GapAtEpoch(sender, 3U, 1, 1)),
                Is.True
            );
            Assert.That(StampEpoch(state, sender, 3U, 2, out GameDataDisposition epoch3), Is.True);
            Assert.That(epoch3, Is.EqualTo(GameDataDisposition.Stale));
            AssertRefused(
                state.RecordGameData(sender, 1UL, 1U, null, null, out _, out string? retired),
                retired,
                "every overtaking incarnation retired, so no baseline remains"
            );
        }

        [Test]
        public void RetiredIncarnationGapsDoNotAuthorizeReusedEpochData()
        {
            (DeliveryAccountability state, Guid sender) =
                SenderWithOrphanedGapFromRetiredIncarnation();
            AssertRefused(
                state.RecordGameData(sender, 2UL, 1U, null, null, out _, out string? diagnostic),
                diagnostic,
                "a dead incarnation's gap must not explain a reused epoch's jump"
            );
            Assert.That(diagnostic, Does.Contain("unexplained gap"));
        }

        [Test]
        public void RetiredIncarnationGapsDoNotCollideWithReusedEpochReports()
        {
            (DeliveryAccountability state, Guid sender) =
                SenderWithOrphanedGapFromRetiredIncarnation();
            Assert.That(
                state.RecordReport(
                    new[] { GapAtEpoch(sender, 1U, 1, 2) },
                    CountersWithSuperseded(3),
                    out string? reportError
                ),
                Is.True,
                reportError
            );
            Assert.That(
                state.RecordGameData(
                    sender,
                    3UL,
                    1U,
                    null,
                    null,
                    out GameDataDisposition disposition,
                    out string? stampError
                ),
                Is.True,
                stampError
            );
            Assert.That(disposition, Is.EqualTo(GameDataDisposition.Apply));
        }

        [Test]
        public void PlayerLeftTerminalKeepsLongSeatChurnBoundedAndV2Frozen()
        {
            DeliveryAccountability state = NewV3();
            for (ulong value = 1; value <= 1_024; value++)
            {
                string caseName = value.ToString(CultureInfo.InvariantCulture);
                Guid sender = Id(value);
                Assert.That(Join(state, Player(sender, 1)), Is.True, caseName);
                Assert.That(state.NotePlayerLeft(sender, 1U, 0UL, out _), Is.True, caseName);
            }

            AssertRefused(
                state.RecordGameData(Id(1), 1UL, 1U, null, null, out _, out string? drained),
                drained,
                "idle departures retire instantly, so no sender state accumulates"
            );

            DeliveryAccountability v2 = NewV2();
            Assert.That(v2.NotePlayerLeft(Id(1), null, null, out _), Is.True);
            AssertRefused(
                v2.NotePlayerLeft(Id(1), 1U, 0UL, out string? frozen),
                frozen,
                "v2 PlayerLeft must not expose terminal watermark fields"
            );
        }

        [Test]
        public void RoomSnapshotAllowsALateJoinBaselineAndResetForgetsIt()
        {
            Guid sender = Id(3);
            DeliveryAccountability state = NewV3();
            Assert.That(
                state.RebaselineSnapshot(new[] { PlayerAt(sender, 4U, 89UL) }, out _),
                Is.True
            );
            Assert.That(StampEpoch(state, sender, 4U, 90), Is.True);
            state.ResetRoom();
            AssertRefused(
                state.RecordGameData(sender, 91UL, 4U, null, null, out _, out string? forgotten),
                forgotten,
                "reset forgets the snapshot baseline"
            );

            // Room transitions do not reset physical-connection counters.
            Assert.That(Report(state, Counters(2)), Is.True);
            Assert.That(state.RebaselineSnapshot(Array.Empty<SenderBaseline>(), out _), Is.True);
            AssertRefused(
                state.RecordReport(Array.Empty<DeliveryGap>(), Counters(1), out string? survived),
                survived,
                "counters survive a room rebaseline"
            );
        }

        [Test]
        public void NegotiatedModeRequiresExactSnapshotAndMetadataShapes()
        {
            Guid sender = Id(6);
            DeliveryAccountability v3 = NewV3();
            AssertRefused(
                v3.RebaselineReconnected(
                    new[] { Player(sender, 1) },
                    Array.Empty<SenderWatermark>(),
                    out string? uncovered
                ),
                uncovered,
                "watermarks must exactly cover the reconnect snapshot"
            );
            /*
                The Rust scenario also refuses snapshot entries whose epoch or
                seq is absent; the C# SenderBaseline carries a non-nullable
                epoch/seq, so those shapes cannot be constructed here and the
                v3 refusal is enforced by the type system instead.
            */

            DeliveryAccountability v2 = NewV2();
            Assert.That(v2.RebaselineSnapshot(Array.Empty<SenderBaseline>(), out _), Is.True);
            AssertRefused(
                v2.RebaselineSnapshot(new[] { Player(sender, 1) }, out string? exposed),
                exposed,
                "a v2 snapshot must not expose a delivery baseline"
            );
            Assert.That(v2.ObserveUnsupportedFormatError(out _), Is.True);
            Assert.That(
                v2.RecordGameData(
                    sender,
                    null,
                    null,
                    null,
                    null,
                    out GameDataDisposition floor,
                    out string? floorError
                ),
                Is.True,
                floorError
            );
            Assert.That(floor, Is.EqualTo(GameDataDisposition.Apply));
            AssertRefused(
                v2.RecordGameData(sender, 1UL, 1U, null, null, out _, out string? metadata),
                metadata,
                "v2 GameData must not expose v3 metadata"
            );
            AssertRefused(
                v2.RecordReport(Array.Empty<DeliveryGap>(), Counters(0), out string? report),
                report,
                "v2 connections refuse DeliveryReport"
            );
        }

        [Test]
        public void FailedRebaselinesPreserveThePreviousRoomState()
        {
            Guid sender = Id(61);
            DeliveryAccountability snapshot = NewV3();
            Assert.That(
                snapshot.RebaselineSnapshot(new[] { PlayerAt(sender, 1U, 5UL) }, out _),
                Is.True
            );
            AssertRefused(
                snapshot.RebaselineSnapshot(
                    new[] { PlayerAt(Id(62), 1U, 0UL), PlayerAt(Id(62), 1U, 0UL) },
                    out string? duplicate
                ),
                duplicate,
                "duplicate snapshot entries are refused"
            );
            Assert.That(
                snapshot.RecordGameData(
                    sender,
                    6UL,
                    1U,
                    null,
                    null,
                    out GameDataDisposition afterSnapshot,
                    out string? snapshotError
                ),
                Is.True,
                snapshotError
            );
            Assert.That(afterSnapshot, Is.EqualTo(GameDataDisposition.Apply));

            DeliveryAccountability reconnect = NewV3();
            Assert.That(
                reconnect.RebaselineSnapshot(new[] { PlayerAt(sender, 1U, 5UL) }, out _),
                Is.True
            );
            AssertRefused(
                reconnect.RebaselineReconnected(
                    new[] { PlayerAt(Id(63), 2U, 9UL) },
                    new[] { new SenderWatermark(Id(63), 2U, 8UL) },
                    out string? mismatched
                ),
                mismatched,
                "a watermark disagreeing with the snapshot is refused"
            );
            Assert.That(
                reconnect.RecordGameData(
                    sender,
                    6UL,
                    1U,
                    null,
                    null,
                    out GameDataDisposition afterReconnect,
                    out string? reconnectError
                ),
                Is.True,
                reconnectError
            );
            Assert.That(afterReconnect, Is.EqualTo(GameDataDisposition.Apply));
        }

        [Test]
        public void CumulativeGapCountersHoldAcrossA256PlusOneFrontier()
        {
            Guid sender = Id(7);
            DeliveryAccountability state = NewV3();
            Assert.That(Join(state, Player(sender, 1)), Is.True);
            List<DeliveryGap> firstFrontier = new List<DeliveryGap>();
            for (int index = 0; index < DeliveryAccountability.DeliveryReportMaxGaps; index++)
            {
                ulong seq = ((ulong)index * 2UL) + 1UL;
                firstFrontier.Add(Gap(sender, seq, seq));
            }

            Assert.That(
                Report(
                    state,
                    CountersWithSuperseded((ulong)DeliveryAccountability.DeliveryReportMaxGaps),
                    firstFrontier.ToArray()
                ),
                Is.True
            );
            Assert.That(
                Report(
                    state,
                    CountersWithSuperseded(
                        (ulong)DeliveryAccountability.DeliveryReportMaxGaps + 1UL
                    ),
                    Gap(sender, 513, 513)
                ),
                Is.True
            );

            List<DeliveryGap> tooMany = new List<DeliveryGap>();
            for (int index = 0; index <= DeliveryAccountability.DeliveryReportMaxGaps; index++)
            {
                ulong seq = ((ulong)index * 2UL) + 1_001UL;
                tooMany.Add(Gap(sender, seq, seq));
            }

            AssertRefused(
                state.RecordReport(
                    tooMany.ToArray(),
                    CountersWithSuperseded(
                        ((ulong)DeliveryAccountability.DeliveryReportMaxGaps * 2UL) + 2UL
                    ),
                    out string? diagnostic
                ),
                diagnostic,
                "a report above the wire gap bound is refused"
            );
        }

        [Test]
        public void SnapshotBaselineValidatesOnlyTheRecipientVisibleTail()
        {
            Guid sender = Id(8);
            DeliveryAccountability state = NewV3();
            Assert.That(
                state.RebaselineSnapshot(new[] { PlayerAt(sender, 1U, 90UL) }, out _),
                Is.True
            );
            Assert.That(Report(state, CountersWithSuperseded(1), Gap(sender, 92, 92)), Is.True);
            Assert.That(Stamp(state, sender, 91), Is.True);
            Assert.That(Stamp(state, sender, 93), Is.True);

            DeliveryAccountability preBaseline = NewV3();
            Assert.That(
                preBaseline.RebaselineSnapshot(new[] { PlayerAt(sender, 1U, 90UL) }, out _),
                Is.True
            );
            AssertRefused(
                preBaseline.RecordReport(
                    new[] { Gap(sender, 89, 89) },
                    CountersWithSuperseded(1),
                    out string? diagnostic
                ),
                diagnostic,
                "a gap below the snapshot baseline is outside the recipient's obligation"
            );
        }

        [Test]
        public void UnsupportedAdvisoryRequiresAPriorReportButNotAdjacency()
        {
            Guid sender = Id(9);
            DeliveryGap advisory = UnsupportedGap(sender, 1);

            DeliveryAccountability paired = NewV3();
            Assert.That(Join(paired, Player(sender, 1)), Is.True);
            Assert.That(Report(paired, CountersWithUnsupported(1), advisory), Is.True);
            /*
                The Rust scenario interleaves observe_server_message(false)
                no-ops around the advisory to prove causality survives
                unrelated traffic; that call is a no-op in C# too (the
                engine simply is not told anything), so the interleaving
                cannot change state and the no-op calls are omitted.
                ObserveTerminal below is the Rust observe_terminal, not the
                no-op arm.
            */
            Assert.That(paired.ObserveUnsupportedFormatError(out _), Is.True);
            paired.ObserveTerminal();
            AssertRefused(
                paired.ObserveUnsupportedFormatError(out string? exhausted),
                exhausted,
                "each advisory consumes exactly one causal report"
            );

            DeliveryAccountability rollover = NewV3();
            Assert.That(Join(rollover, Player(sender, 1)), Is.True);
            Assert.That(Report(rollover, CountersWithUnsupported(1), advisory), Is.True);
            Assert.That(
                Report(rollover, CountersWithUnsupported(2), UnsupportedGap(sender, 2)),
                Is.True
            );
            Assert.That(rollover.ObserveUnsupportedFormatError(out _), Is.True);

            DeliveryAccountability roomReset = NewV3();
            Assert.That(Join(roomReset, Player(sender, 1)), Is.True);
            Assert.That(Report(roomReset, CountersWithUnsupported(1), advisory), Is.True);
            roomReset.ResetRoom();
            AssertRefused(
                roomReset.ObserveUnsupportedFormatError(out string? reset),
                reset,
                "a room reset discharges the armed advisory"
            );

            DeliveryAccountability terminal = NewV3();
            Assert.That(Join(terminal, Player(sender, 1)), Is.True);
            Assert.That(Report(terminal, CountersWithUnsupported(1), advisory), Is.True);
            terminal.ObserveTerminal();
            AssertRefused(
                terminal.ObserveUnsupportedFormatError(out string? terminalCleared),
                terminalCleared,
                "a terminal outcome ends the observable stream"
            );

            /*
                The server can absorb an unsupported range into an
                already-queued mixed report, so the unsupported range need not
                be first; the private unadvised_unsupported_gap field the Rust
                test asserts is observed behaviorally through the advisory it
                arms.
            */
            DeliveryAccountability mixed = NewV3();
            Assert.That(Join(mixed, Player(sender, 1)), Is.True);
            DeliveryCountersByClass mixedCounters = new DeliveryCountersByClass(
                new ReliableDeliveryCounters(0, 0, 1),
                new LatestDeliveryCounters(0, 1, 0, 0, 0),
                new VolatileDeliveryCounters(0, 0, 0, 0)
            );
            Assert.That(
                mixed.RecordReport(
                    new[] { Gap(sender, 2, 2), UnsupportedGap(sender, 7) },
                    mixedCounters,
                    out string? mixedError
                ),
                Is.True,
                mixedError
            );
            Assert.That(mixed.ObserveUnsupportedFormatError(out string? armed), Is.True, armed);
        }

        [Test]
        public void CoalescedUnsupportedRangesAreAcceptedOnlyWhenCountersMatch()
        {
            Guid sender = Id(10);
            DeliveryAccountability exact = NewV3();
            Assert.That(Join(exact, Player(sender, 1)), Is.True);
            Assert.That(
                Report(exact, CountersWithUnsupported(3), UnsupportedGapRange(sender, 4, 6)),
                Is.True
            );
            Assert.That(exact.ObserveUnsupportedFormatError(out _), Is.True);

            ulong[] skewedCounts = { 2, 4 };
            foreach (ulong skewedCount in skewedCounts)
            {
                string caseName = skewedCount.ToString(CultureInfo.InvariantCulture);
                DeliveryAccountability skewed = NewV3();
                Assert.That(Join(skewed, Player(sender, 1)), Is.True, caseName);
                AssertRefused(
                    skewed.RecordReport(
                        new[] { UnsupportedGapRange(sender, 4, 6) },
                        CountersWithUnsupported(skewedCount),
                        out string? rejected
                    ),
                    rejected,
                    "a " + caseName + "-count report for a 3-sequence range must be rejected"
                );
                AssertRefused(
                    skewed.ObserveUnsupportedFormatError(out string? unauthorized),
                    unauthorized,
                    "a rejected report must not authorize an unsupported-format advisory"
                );
                Assert.That(
                    Report(skewed, CountersWithUnsupported(3), UnsupportedGapRange(sender, 4, 6)),
                    Is.True,
                    "a rejected report must not commit counters or gap ranges"
                );
                Assert.That(
                    skewed.ObserveUnsupportedFormatError(out string? authorized),
                    Is.True,
                    authorized
                );
            }

            DeliveryAccountability split = NewV3();
            Assert.That(Join(split, Player(sender, 1)), Is.True);
            Assert.That(
                split.RecordReport(
                    new[] { UnsupportedGapRange(sender, 1, 2), UnsupportedGapRange(sender, 7, 9) },
                    CountersWithUnsupported(5),
                    out string? splitError
                ),
                Is.True,
                splitError
            );
            Assert.That(split.ObserveUnsupportedFormatError(out _), Is.True);
        }

        [Test]
        public void DeliveredAndAbandonedBucketsAreMonotonicityCheckedOnly()
        {
            /*
                Issue #275 decision: the delivered/abandoned buckets are
                monotonicity-checked only. The report carries no evidence
                structure to delta-check them against, and any admission
                ceiling would be an arbitrary threshold that false-rejects
                conformant high-throughput servers. Both faces stay pinned per
                class and bucket so a semantics change must consciously update
                them.
            */
            DeliveryAccountability state = NewV3();

            // Counter-only shape: the absurd single-report jump in every monotonic-only bucket is accepted.
            Assert.That(Report(state, PoisonedCounters()), Is.True);
            // Equal cumulative values stay monotonic and keep every loss-bucket delta at zero, so the poison is sticky by design.
            Assert.That(Report(state, PoisonedCounters()), Is.True);

            // A later honest report regressing exactly one of the six monotonic-only buckets is refused as moved backward.
            MonotonicBucket[] regressions =
            {
                MonotonicBucket.ReliableDelivered,
                MonotonicBucket.LatestDelivered,
                MonotonicBucket.VolatileDelivered,
                MonotonicBucket.ReliableAbandoned,
                MonotonicBucket.LatestAbandoned,
                MonotonicBucket.VolatileAbandoned,
            };
            foreach (MonotonicBucket bucket in regressions)
            {
                AssertRefused(
                    state.RecordReport(
                        Array.Empty<DeliveryGap>(),
                        RegressedPoison(bucket),
                        out string? diagnostic
                    ),
                    diagnostic,
                    bucket + " regression must be refused"
                );
                Assert.That(
                    diagnostic,
                    Does.Contain("cumulative per-class counters moved backward"),
                    bucket.ToString()
                );
            }

            // The refused reports committed nothing: the poisoned baseline stays authoritative.
            Assert.That(Report(state, PoisonedCounters()), Is.True);
        }

        [Test]
        public void RelayStatsArePositiveStableAndCumulativePerConnection()
        {
            DeliveryAccountability valid = NewV3();
            Assert.That(valid.RecordRelayStats(1_000, 4, 2, 1, out _), Is.True);
            Assert.That(valid.RecordRelayStats(1_000, 5, 2, 3, out _), Is.True);
            valid.ResetRoom();
            Assert.That(valid.RecordRelayStats(1_000, 5, 3, 3, out _), Is.True);

            foreach (RelayStatsCase violation in RelayStatsViolations)
            {
                DeliveryAccountability state = NewV3();
                if (violation.First != null)
                {
                    Assert.That(
                        state.RecordRelayStats(
                            violation.First[0],
                            violation.First[1],
                            violation.First[2],
                            violation.First[3],
                            out _
                        ),
                        Is.True,
                        violation.Name
                    );
                }

                AssertRefused(
                    state.RecordRelayStats(
                        violation.Next[0],
                        violation.Next[1],
                        violation.Next[2],
                        violation.Next[3],
                        out string? diagnostic
                    ),
                    diagnostic,
                    violation.Name
                );
                Assert.That(diagnostic, Does.Contain(violation.Expected), violation.Name);
            }

            DeliveryAccountability v2 = NewV2();
            AssertRefused(
                v2.RecordRelayStats(1_000, 0, 0, 0, out string? frozen),
                frozen,
                "v2 connections refuse RelayStats"
            );

            /*
                reset_connection has no C# counterpart: the engine models a
                fresh physical connection with a fresh machine, whose empty
                relay-stats window accepts a new interval.
            */
            DeliveryAccountability freshConnection = NewV3();
            Assert.That(
                freshConnection.RecordRelayStats(2_000, 0, 0, 0, out string? reset),
                Is.True,
                reset
            );
        }

        [Test]
        public void ReconnectEpochFloodHitsTheAnnouncementBound()
        {
            Guid sender = Id(1);
            DeliveryAccountability state = NewV3();
            Assert.That(state.RebaselineSnapshot(new[] { Player(sender, 1) }, out _), Is.True);

            /*
                Exactly the bound's worth of newer announcements is accepted.
                The literal pins the shipped threshold: retuning the constant
                must consciously update this evidence.
            */
            Assert.That(DeliveryAccountability.MaxAnnouncedEpochsPerSender, Is.EqualTo(16));
            for (uint epoch = 2; epoch <= 17; epoch++)
            {
                Assert.That(
                    state.NotePlayerReconnected(sender, epoch, out _),
                    Is.True,
                    epoch.ToString(CultureInfo.InvariantCulture)
                );
            }

            AssertRefused(
                state.RecordGameData(sender, 1UL, 18U, null, null, out _, out string? before),
                before,
                "epoch 18 must still be unannounced after the bound's worth of announcements"
            );

            // The next announcement is refused as server misbehavior.
            AssertRefused(
                state.NotePlayerReconnected(sender, 18U, out string? flood),
                flood,
                "the 17th unresolved announcement is refused"
            );
            Assert.That(flood, Does.Contain("unresolved announcement"));

            // The refused frame leaves the machine untouched: epoch 18 stays unannounced.
            AssertRefused(
                state.RecordGameData(sender, 1UL, 18U, null, null, out _, out string? after),
                after,
                "the refused announcement must not have committed epoch 18"
            );
        }

        [Test]
        public void UncoverableDepartureFloodStaysBounded()
        {
            Guid sender = Id(3);
            DeliveryAccountability state = NewV3();
            Assert.That(
                state.RebaselineSnapshot(new[] { PlayerAt(sender, 1U, 0UL) }, out _),
                Is.True
            );

            /*
                Each churn cycle announces a newer epoch and departs it with a
                terminal watermark retirement can never cover (the issue #166
                pathology), so both per-sender structures grow to the bound.
            */
            for (uint epoch = 2; epoch <= 17; epoch++)
            {
                Assert.That(
                    state.NotePlayerReconnected(sender, epoch, out _),
                    Is.True,
                    epoch.ToString(CultureInfo.InvariantCulture)
                );
                Assert.That(
                    state.NotePlayerLeft(sender, epoch, ulong.MaxValue, out _),
                    Is.True,
                    epoch.ToString(CultureInfo.InvariantCulture)
                );
            }

            // The next cycle is refused at the announcement bound.
            AssertRefused(
                state.NotePlayerReconnected(sender, 18U, out string? flood),
                flood,
                "the 17th unresolved announcement is refused"
            );
            Assert.That(flood, Does.Contain("unresolved announcement"));

            /*
                The departed table stays at its bound too: the refused frame
                must not have announced epoch 18, so an uncoverable leave for
                it is refused as unannounced rather than as a bound excess.
            */
            AssertRefused(
                state.NotePlayerLeft(sender, 18U, ulong.MaxValue, out string? departed),
                departed,
                "the refused announcement must not have committed epoch 18"
            );
            Assert.That(departed, Does.Contain("unannounced epoch"));
        }

        [Test]
        public void HealthyReconnectChurnNeverAccumulatesTowardTheBounds()
        {
            Guid sender = Id(5);
            DeliveryAccountability state = NewV3();
            Assert.That(
                state.RebaselineSnapshot(new[] { PlayerAt(sender, 1U, 0UL) }, out _),
                Is.True
            );

            /*
                Legitimate flapping retires instantly: an idle departure
                carries final_seq == 0, so every churn cycle prunes both
                structures.
            */
            for (uint epoch = 2; epoch <= 64; epoch++)
            {
                Assert.That(
                    state.NotePlayerReconnected(sender, epoch, out _),
                    Is.True,
                    epoch.ToString(CultureInfo.InvariantCulture)
                );
                Assert.That(
                    state.NotePlayerLeft(sender, epoch, 0UL, out _),
                    Is.True,
                    epoch.ToString(CultureInfo.InvariantCulture)
                );
            }

            AssertRefused(
                state.RecordGameData(sender, 1UL, 1U, null, null, out _, out string? drained),
                drained,
                "full retirement removes the sender's baseline, announced epochs, and departed terminals"
            );
        }

        [Test]
        public void DisjointGapFloodHitsTheRetentionCeiling()
        {
            Guid sender = Id(4);
            DeliveryAccountability state = NewV3();
            Assert.That(
                state.RebaselineSnapshot(new[] { PlayerAt(sender, 1U, 0UL) }, out _),
                Is.True
            );

            /*
                Four full reports of 256 singleton exact ranges saturate the
                bound; counter deltas match the causal gap units exactly
                throughout.
            */
            int batchSize = DeliveryAccountability.DeliveryReportMaxGaps;
            for (ulong batch = 1; batch <= 4; batch++)
            {
                List<DeliveryGap> gaps = new List<DeliveryGap>();
                for (int index = 0; index < batchSize; index++)
                {
                    ulong seq = ((batch - 1UL) * (ulong)batchSize) + (ulong)index + 1UL;
                    gaps.Add(Gap(sender, seq, seq));
                }

                Assert.That(
                    Report(state, CountersWithSuperseded(batch * (ulong)batchSize), gaps.ToArray()),
                    Is.True,
                    "batch " + batch.ToString(CultureInfo.InvariantCulture)
                );
            }

            /*
                One more exact range exceeds the ceiling; the diagnostic pins
                the outstanding count at the retention bound, which is the
                behavioral stand-in for the Rust pending_gaps introspection.
            */
            string saturated =
                "exact-gap retention bound ("
                + DeliveryAccountability.MaxTotalPendingGaps.ToString(CultureInfo.InvariantCulture)
                + " outstanding plus 1 new)";
            AssertRefused(
                state.RecordReport(
                    new[]
                    {
                        Gap(sender, (4UL * (ulong)batchSize) + 1UL, (4UL * (ulong)batchSize) + 1UL),
                    },
                    CountersWithSuperseded((4UL * (ulong)batchSize) + 1UL),
                    out string? overflow
                ),
                overflow,
                "the retention ceiling refuses further retention"
            );
            Assert.That(overflow, Does.Contain("retention bound"));
            Assert.That(overflow, Does.Contain(saturated));

            // The refused report does not mutate pending state.
            AssertRefused(
                state.RecordReport(
                    new[]
                    {
                        Gap(sender, (4UL * (ulong)batchSize) + 1UL, (4UL * (ulong)batchSize) + 1UL),
                    },
                    CountersWithSuperseded((4UL * (ulong)batchSize) + 1UL),
                    out string? retry
                ),
                retry,
                "the refused report must not have committed its range"
            );
            Assert.That(retry, Does.Contain(saturated));
        }

        [Test]
        public void DepartureBoundRefusesTheNextUncoverableLeave()
        {
            Guid sender = Id(6);
            DeliveryAccountability state = NewV3();
            Assert.That(
                state.RebaselineSnapshot(new[] { PlayerAt(sender, 1U, 0UL) }, out _),
                Is.True
            );

            /*
                One uncovered departure at the current epoch needs no
                announcement, so the departure count runs one ahead of the
                announcements.
            */
            Assert.That(state.NotePlayerLeft(sender, 1U, ulong.MaxValue, out _), Is.True);
            for (uint epoch = 2; epoch <= 16; epoch++)
            {
                Assert.That(
                    state.NotePlayerReconnected(sender, epoch, out _),
                    Is.True,
                    epoch.ToString(CultureInfo.InvariantCulture)
                );
                Assert.That(
                    state.NotePlayerLeft(sender, epoch, ulong.MaxValue, out _),
                    Is.True,
                    epoch.ToString(CultureInfo.InvariantCulture)
                );
            }

            /*
                The next announcement still fits under the announcement bound,
                but its uncoverable leave is refused by the departure bound,
                proving the two bounds are independent.
            */
            Assert.That(state.NotePlayerReconnected(sender, 17U, out _), Is.True);
            AssertRefused(
                state.NotePlayerLeft(sender, 17U, ulong.MaxValue, out string? bound),
                bound,
                "the 17th uncovered departure is refused"
            );
            Assert.That(bound, Does.Contain("uncovered departure"));

            // The refused leave mutates nothing: a retry is refused identically.
            AssertRefused(
                state.NotePlayerLeft(sender, 17U, ulong.MaxValue, out string? retry),
                retry,
                "the refused leave must not have committed its terminal"
            );
            Assert.That(retry, Does.Contain("uncovered departure"));

            /*
                The announcement table is now exactly at its bound: epoch 18
                is refused as the 17th unresolved announcement.
            */
            AssertRefused(
                state.NotePlayerReconnected(sender, 18U, out string? announcementBound),
                announcementBound,
                "the announcement table is saturated"
            );
            Assert.That(announcementBound, Does.Contain("unresolved announcement"));
        }

        /*
            The receive hot path is allocation-gated: once a sender is
            baselined, steady-state sequence advances must not allocate
            (the announced-epoch compaction is an explicit loop for this
            reason — a lambda would hoist a closure allocation onto every
            call).
        */
        [Test]
        public void RecordGameDataSteadyStateAdvancesAllocatesNothing()
        {
            DeliveryAccountability state = NewV3();
            Guid sender = Id(1);
            Assert.That(Join(state, Player(sender, 1)), Is.True);
            Assert.That(Stamp(state, sender, 1, out GameDataDisposition first), Is.True);
            Assert.That(first, Is.EqualTo(GameDataDisposition.Apply));

            bool advanced = true;
            long minDelta = long.MaxValue;
            ulong seq = 1;
            for (int pass = 0; pass < 4; pass++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int iteration = 0; iteration < 100; iteration++)
                {
                    seq++;
                    advanced =
                        advanced & Stamp(state, sender, seq, out GameDataDisposition applied);
                    advanced = advanced & (applied == GameDataDisposition.Apply);
                }

                long after = GC.GetAllocatedBytesForCurrentThread();
                minDelta = Math.Min(minDelta, after - before);
            }

            Assert.That(advanced, Is.True, "every steady-state advance must apply");
            Assert.That(
                minDelta,
                Is.EqualTo(0),
                "Steady-state sequence advances must not allocate."
            );
        }

        // Mirrors the Rust id(value) helper: a deterministic Guid whose first eight bytes carry the value little-endian.
        private static Guid Id(ulong value)
        {
            return new Guid(
                (int)(value & 0xFFFFFFFF),
                (short)((value >> 32) & 0xFFFF),
                (short)((value >> 48) & 0xFFFF),
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0
            );
        }

        private static DeliveryAccountability NewV3()
        {
            return new DeliveryAccountability(true);
        }

        private static DeliveryAccountability NewV2()
        {
            return new DeliveryAccountability(false);
        }

        private static SenderBaseline Player(Guid playerId, uint epoch)
        {
            return new SenderBaseline(playerId, epoch, 0UL);
        }

        private static SenderBaseline PlayerAt(Guid playerId, uint epoch, ulong seq)
        {
            return new SenderBaseline(playerId, epoch, seq);
        }

        private static bool Join(DeliveryAccountability state, SenderBaseline player)
        {
            return state.NotePlayerJoined(player, out _);
        }

        private static bool Stamp(DeliveryAccountability state, Guid sender, ulong seq)
        {
            return state.RecordGameData(sender, seq, 1U, null, null, out _, out _);
        }

        private static bool Stamp(
            DeliveryAccountability state,
            Guid sender,
            ulong seq,
            out GameDataDisposition disposition
        )
        {
            return state.RecordGameData(sender, seq, 1U, null, null, out disposition, out _);
        }

        private static bool StampEpoch(
            DeliveryAccountability state,
            Guid sender,
            uint epoch,
            ulong seq
        )
        {
            return state.RecordGameData(sender, seq, epoch, null, null, out _, out _);
        }

        private static bool StampEpoch(
            DeliveryAccountability state,
            Guid sender,
            uint epoch,
            ulong seq,
            out GameDataDisposition disposition
        )
        {
            return state.RecordGameData(sender, seq, epoch, null, null, out disposition, out _);
        }

        private static bool Report(
            DeliveryAccountability state,
            DeliveryCountersByClass counters,
            params DeliveryGap[] gaps
        )
        {
            return state.RecordReport(gaps, counters, out _);
        }

        private static DeliveryGap Gap(Guid sender, ulong fromSeq, ulong toSeq)
        {
            return GapAtEpoch(sender, 1U, fromSeq, toSeq);
        }

        private static DeliveryGap GapAtEpoch(Guid sender, uint epoch, ulong fromSeq, ulong toSeq)
        {
            return new DeliveryGap(
                sender,
                epoch,
                fromSeq,
                toSeq,
                DeliveryGapReason.LatestSuperseded
            );
        }

        private static DeliveryGap GapWithReason(
            Guid sender,
            DeliveryGapReason reason,
            ulong fromSeq,
            ulong toSeq
        )
        {
            return new DeliveryGap(sender, 1U, fromSeq, toSeq, reason);
        }

        private static DeliveryGap UnsupportedGap(Guid sender, ulong seq)
        {
            return UnsupportedGapRange(sender, seq, seq);
        }

        private static DeliveryGap UnsupportedGapRange(Guid sender, ulong fromSeq, ulong toSeq)
        {
            return new DeliveryGap(sender, 1U, fromSeq, toSeq, DeliveryGapReason.UnsupportedFormat);
        }

        private static ulong ReportedUnits(DeliveryGap[] gaps)
        {
            ulong units = 0;
            for (int i = 0; i < gaps.Length; i++)
            {
                units += gaps[i].ToSeq - gaps[i].FromSeq + 1UL;
            }

            return units;
        }

        private static DeliveryCountersByClass Counters(ulong seed)
        {
            return new DeliveryCountersByClass(
                new ReliableDeliveryCounters(seed, 0, 0),
                new LatestDeliveryCounters(seed, 0, 0, 0, 0),
                new VolatileDeliveryCounters(seed, 0, 0, 0)
            );
        }

        private static DeliveryCountersByClass CountersWithSuperseded(ulong count)
        {
            return new DeliveryCountersByClass(
                new ReliableDeliveryCounters(0, 0, 0),
                new LatestDeliveryCounters(0, count, 0, 0, 0),
                new VolatileDeliveryCounters(0, 0, 0, 0)
            );
        }

        private static DeliveryCountersByClass CountersWithUnsupported(ulong count)
        {
            return new DeliveryCountersByClass(
                new ReliableDeliveryCounters(0, 0, count),
                new LatestDeliveryCounters(0, 0, 0, 0, 0),
                new VolatileDeliveryCounters(0, 0, 0, 0)
            );
        }

        private static DeliveryCountersByClass BucketCounters(DeliveryGapReason reason, ulong count)
        {
            DeliveryCountersByClass value = Counters(0);
            if (reason == DeliveryGapReason.LatestDroppedFull)
            {
                LatestDeliveryCounters latest = value.Latest;
                return new DeliveryCountersByClass(
                    value.Reliable,
                    new LatestDeliveryCounters(
                        latest.Delivered,
                        latest.Superseded,
                        count,
                        latest.Abandoned,
                        latest.UnsupportedFormat
                    ),
                    value.Volatile
                );
            }

            VolatileDeliveryCounters volatileCounters = value.Volatile;
            return new DeliveryCountersByClass(
                value.Reliable,
                value.Latest,
                new VolatileDeliveryCounters(
                    volatileCounters.Delivered,
                    count,
                    volatileCounters.Abandoned,
                    volatileCounters.UnsupportedFormat
                )
            );
        }

        private static DeliveryCountersByClass PoisonedCounters()
        {
            return new DeliveryCountersByClass(
                new ReliableDeliveryCounters(ulong.MaxValue, ulong.MaxValue, 0),
                new LatestDeliveryCounters(ulong.MaxValue, 0, 0, ulong.MaxValue, 0),
                new VolatileDeliveryCounters(ulong.MaxValue, 0, ulong.MaxValue, 0)
            );
        }

        private static DeliveryCountersByClass RegressedPoison(MonotonicBucket bucket)
        {
            DeliveryCountersByClass value = PoisonedCounters();
            ReliableDeliveryCounters reliable = value.Reliable;
            LatestDeliveryCounters latest = value.Latest;
            VolatileDeliveryCounters volatileCounters = value.Volatile;
            switch (bucket)
            {
                case MonotonicBucket.ReliableDelivered:
                    reliable = new ReliableDeliveryCounters(
                        4,
                        reliable.Abandoned,
                        reliable.UnsupportedFormat
                    );
                    break;
                case MonotonicBucket.LatestDelivered:
                    latest = new LatestDeliveryCounters(
                        4,
                        latest.Superseded,
                        latest.DroppedFull,
                        latest.Abandoned,
                        latest.UnsupportedFormat
                    );
                    break;
                case MonotonicBucket.VolatileDelivered:
                    volatileCounters = new VolatileDeliveryCounters(
                        4,
                        volatileCounters.Dropped,
                        volatileCounters.Abandoned,
                        volatileCounters.UnsupportedFormat
                    );
                    break;
                case MonotonicBucket.ReliableAbandoned:
                    reliable = new ReliableDeliveryCounters(
                        reliable.Delivered,
                        4,
                        reliable.UnsupportedFormat
                    );
                    break;
                case MonotonicBucket.LatestAbandoned:
                    latest = new LatestDeliveryCounters(
                        latest.Delivered,
                        latest.Superseded,
                        latest.DroppedFull,
                        4,
                        latest.UnsupportedFormat
                    );
                    break;
                case MonotonicBucket.VolatileAbandoned:
                    volatileCounters = new VolatileDeliveryCounters(
                        volatileCounters.Delivered,
                        volatileCounters.Dropped,
                        4,
                        volatileCounters.UnsupportedFormat
                    );
                    break;
            }

            return new DeliveryCountersByClass(reliable, latest, volatileCounters);
        }

        private static void AssertRefused(bool accepted, string? diagnostic, string because)
        {
            Assert.That(accepted, Is.False, because);
            Assert.That(diagnostic, Does.StartWith("delivery accountability violation:"), because);
        }

        /*
            Seat churn that ends in a full sender retirement (zero terminal at
            the newest announced epoch) with a pending exact range from an
            older incarnation. The join of the reused epoch succeeding is the
            behavioral proof that the departed table emptied, and the gap
            pruning is proven by the two scenarios that consume this setup.
        */
        private static (
            DeliveryAccountability State,
            Guid Sender
        ) SenderWithOrphanedGapFromRetiredIncarnation()
        {
            Guid sender = Id(9);
            DeliveryAccountability state = NewV3();
            Assert.That(Join(state, Player(sender, 1)), Is.True);
            Assert.That(
                Report(state, CountersWithSuperseded(1), GapAtEpoch(sender, 1U, 1, 1)),
                Is.True
            );
            Assert.That(Join(state, Player(sender, 2)), Is.True);
            Assert.That(state.NotePlayerLeft(sender, 2U, 0UL, out _), Is.True);
            // The server reuses epoch value 1 for the rejoining incarnation.
            Assert.That(Join(state, Player(sender, 1)), Is.True);
            return (state, sender);
        }
    }
}
