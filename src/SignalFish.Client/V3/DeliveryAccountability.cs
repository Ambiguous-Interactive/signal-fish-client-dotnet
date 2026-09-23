namespace SignalFish.Client.V3
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using SignalFish.Client.Protocol;

    /// <summary>
    /// Stateful validator for server-stamped relay delivery (protocol v3):
    /// per-sender epoch/sequence baselines, exact gap reports with range
    /// coverage, departed-sender retirement, stale suppression, resource
    /// bounds, monotonic <c>RelayStats</c>, and unsupported-format advisory
    /// causality. A refused call leaves the state untouched and returns
    /// <see langword="false"/> with a non-null diagnostic prefixed
    /// <c>delivery accountability violation:</c>; success returns
    /// <see langword="true"/> with a <see langword="null"/> diagnostic.
    /// Delivery counters survive <see cref="ResetRoom"/> (they are
    /// cumulative for the lifetime of the physical connection).
    /// </summary>
    public sealed class DeliveryAccountability
    {
        private readonly struct SenderProgress
        {
            public uint Epoch { get; }

            // Last sequence already delivered or outside this recipient's obligation.
            public ulong LastSeq { get; }

            public SenderProgress(uint epoch, ulong lastSeq)
            {
                Epoch = epoch;
                LastSeq = lastSeq;
            }
        }

        private readonly struct RelayStatsSnapshot
        {
            public ulong IntervalMs { get; }

            public ulong SentToYou { get; }

            public ulong DroppedForYou { get; }

            public ulong BackpressureEvents { get; }

            public RelayStatsSnapshot(
                ulong intervalMs,
                ulong sentToYou,
                ulong droppedForYou,
                ulong backpressureEvents
            )
            {
                IntervalMs = intervalMs;
                SentToYou = sentToYou;
                DroppedForYou = droppedForYou;
                BackpressureEvents = backpressureEvents;
            }
        }

        /// <summary>
        /// Maximum gap ranges one delivery report may carry, mirroring the
        /// server's <c>DELIVERY_REPORT_MAX_GAPS</c> wire bound.
        /// </summary>
        public const int DeliveryReportMaxGaps = 256;

        /// <summary>
        /// Maximum simultaneously unresolved incarnation announcements
        /// tracked for one sender. Legitimate reconnect churn retires
        /// instantly (<c>final_seq == 0</c>) or advances watermarks,
        /// pruning announcements; only churn that never delivers data and
        /// never completes retirement accumulates toward this bound.
        /// </summary>
        internal const int MaxAnnouncedEpochsPerSender = 16;

        /// <summary>
        /// Maximum simultaneously uncovered departed incarnations retained
        /// for one sender. Retirement completes whenever terminal coverage
        /// is explainable; retaining more than this many uncovered
        /// terminals per sender means the server keeps leaving incarnations
        /// whose tails it never explains.
        /// </summary>
        internal const int MaxDepartedSendersPerPlayer = 16;

        /// <summary>
        /// Maximum total exact gap ranges retained across all senders while
        /// awaiting matching data, retirement, or a room reset. One report
        /// carries at most <see cref="DeliveryReportMaxGaps"/> ranges, so
        /// legitimate loss patterns drain far below this ceiling between
        /// reports.
        /// </summary>
        internal const int MaxTotalPendingGaps = 1024;

        private readonly bool _protocolV3;

        private Dictionary<Guid, SenderProgress> _senders = new Dictionary<Guid, SenderProgress>();

        private readonly Dictionary<Guid, List<uint>> _announcedEpochs =
            new Dictionary<Guid, List<uint>>();

        private readonly HashSet<Guid> _staleSenders = new HashSet<Guid>();

        private readonly Dictionary<(Guid PlayerId, uint Epoch), ulong> _departedSenders =
            new Dictionary<(Guid PlayerId, uint Epoch), ulong>();

        private readonly Dictionary<(Guid PlayerId, uint Epoch), List<DeliveryGap>> _pendingGaps =
            new Dictionary<(Guid PlayerId, uint Epoch), List<DeliveryGap>>();

        private DeliveryGap? _unadvisedUnsupportedGap;

        private DeliveryCountersByClass? _counters;

        private RelayStatsSnapshot? _lastRelayStats;

        /// <summary>Initializes a new delivery-accountability machine.</summary>
        /// <param name="protocolV3">
        /// Whether the connection negotiated protocol v3; v2 connections
        /// refuse every v3-only frame and accept only the relay-floor form.
        /// </param>
        public DeliveryAccountability(bool protocolV3)
        {
            _protocolV3 = protocolV3;
        }

        /// <summary>
        /// Clears room-scoped sender cursors and exact causes. Delivery
        /// counters remain cumulative for the lifetime of the physical
        /// connection.
        /// </summary>
        public void ResetRoom()
        {
            _senders.Clear();
            _announcedEpochs.Clear();
            _staleSenders.Clear();
            _departedSenders.Clear();
            _pendingGaps.Clear();
            _unadvisedUnsupportedGap = null;
        }

        /// <summary>
        /// Establishes a fresh room/spectator snapshot from exact
        /// recipient-visible relay baselines. Every entry carries an
        /// epoch/seq baseline, so for a v2 connection any non-empty list is
        /// refused; pass an empty list for v2.
        /// </summary>
        /// <param name="players">The snapshot roster; <see langword="null"/> means no senders.</param>
        /// <param name="diagnostic">The violation text on failure; <see langword="null"/> on success.</param>
        /// <returns><see langword="true"/> when the snapshot was accepted.</returns>
        public bool RebaselineSnapshot(
            IReadOnlyList<SenderBaseline>? players,
            out string? diagnostic
        )
        {
            Dictionary<Guid, SenderProgress> senders = new Dictionary<Guid, SenderProgress>();
            HashSet<Guid> seen = new HashSet<Guid>();
            if (players != null)
            {
                for (int i = 0; i < players.Count; i++)
                {
                    SenderBaseline player = players[i];
                    if (!seen.Add(player.PlayerId))
                    {
                        diagnostic =
                            "delivery accountability violation: snapshot contains duplicate player "
                            + player.PlayerId;
                        return false;
                    }

                    if (!_protocolV3)
                    {
                        diagnostic =
                            "delivery accountability violation: v2 snapshot exposed delivery baseline ("
                            + FormatOptional(player.Epoch)
                            + ", "
                            + FormatOptional(player.Seq)
                            + ") for "
                            + player.PlayerId;
                        return false;
                    }

                    string? epochError = ValidateEpoch(player.PlayerId, player.Epoch, "snapshot");
                    if (epochError != null)
                    {
                        diagnostic = epochError;
                        return false;
                    }

                    senders[player.PlayerId] = new SenderProgress(player.Epoch, player.Seq);
                }
            }

            ResetRoom();
            _senders = senders;
            diagnostic = null;
            return true;
        }

        /// <summary>
        /// Replaces room cursors with the authoritative reconnect
        /// watermarks. The watermark set must exactly cover the snapshot
        /// roster with identical stamps.
        /// </summary>
        /// <param name="players">The reconnect snapshot roster; <see langword="null"/> means no senders.</param>
        /// <param name="watermarks">The server-issued watermarks; <see langword="null"/> means none.</param>
        /// <param name="diagnostic">The violation text on failure; <see langword="null"/> on success.</param>
        /// <returns><see langword="true"/> when the rebaseline was accepted.</returns>
        public bool RebaselineReconnected(
            IReadOnlyList<SenderBaseline>? players,
            IReadOnlyList<SenderWatermark>? watermarks,
            out string? diagnostic
        )
        {
            Dictionary<Guid, (uint Epoch, ulong Seq)> snapshotStamps =
                new Dictionary<Guid, (uint Epoch, ulong Seq)>();
            HashSet<Guid> snapshotIds = new HashSet<Guid>();
            if (players != null)
            {
                for (int i = 0; i < players.Count; i++)
                {
                    SenderBaseline player = players[i];
                    if (!snapshotIds.Add(player.PlayerId))
                    {
                        diagnostic =
                            "delivery accountability violation: reconnect snapshot contains duplicate player "
                            + player.PlayerId;
                        return false;
                    }

                    if (!_protocolV3)
                    {
                        diagnostic =
                            "delivery accountability violation: v2 reconnect snapshot exposed delivery baseline ("
                            + FormatOptional(player.Epoch)
                            + ", "
                            + FormatOptional(player.Seq)
                            + ") for "
                            + player.PlayerId;
                        return false;
                    }

                    string? epochError = ValidateEpoch(
                        player.PlayerId,
                        player.Epoch,
                        "reconnect snapshot"
                    );
                    if (epochError != null)
                    {
                        diagnostic = epochError;
                        return false;
                    }

                    snapshotStamps[player.PlayerId] = (player.Epoch, player.Seq);
                }
            }

            if (!_protocolV3)
            {
                if (watermarks == null || watermarks.Count == 0)
                {
                    ResetRoom();
                    diagnostic = null;
                    return true;
                }

                diagnostic =
                    "delivery accountability violation: v2 Reconnected exposed sender_watermarks";
                return false;
            }

            HashSet<Guid> seen = new HashSet<Guid>();
            Dictionary<Guid, SenderProgress> senders = new Dictionary<Guid, SenderProgress>();
            if (watermarks != null)
            {
                for (int i = 0; i < watermarks.Count; i++)
                {
                    SenderWatermark watermark = watermarks[i];
                    string? epochError = ValidateEpoch(
                        watermark.PlayerId,
                        watermark.Epoch,
                        "reconnect watermark"
                    );
                    if (epochError != null)
                    {
                        diagnostic = epochError;
                        return false;
                    }

                    if (!seen.Add(watermark.PlayerId))
                    {
                        diagnostic =
                            "delivery accountability violation: duplicate reconnect watermark for "
                            + watermark.PlayerId;
                        return false;
                    }

                    if (
                        snapshotStamps.TryGetValue(
                            watermark.PlayerId,
                            out (uint Epoch, ulong Seq) stamp
                        )
                    )
                    {
                        if (stamp.Epoch != watermark.Epoch || stamp.Seq != watermark.Seq)
                        {
                            diagnostic =
                                "delivery accountability violation: reconnect watermark ("
                                + FormatNum(watermark.Epoch)
                                + ", "
                                + FormatNum(watermark.Seq)
                                + ") for "
                                + watermark.PlayerId
                                + " disagrees with snapshot ("
                                + FormatNum(stamp.Epoch)
                                + ", "
                                + FormatNum(stamp.Seq)
                                + ")";
                            return false;
                        }
                    }
                    else
                    {
                        diagnostic =
                            "delivery accountability violation: reconnect watermark names "
                            + watermark.PlayerId
                            + " outside the room snapshot";
                        return false;
                    }

                    senders[watermark.PlayerId] = new SenderProgress(
                        watermark.Epoch,
                        watermark.Seq
                    );
                }
            }

            if (!seen.SetEquals(snapshotIds))
            {
                diagnostic =
                    "delivery accountability violation: reconnect watermarks do not cover the current room snapshot (watermarks=["
                    + FormatIdSet(seen)
                    + "], snapshot=["
                    + FormatIdSet(snapshotIds)
                    + "])";
                return false;
            }

            ResetRoom();
            _senders = senders;
            diagnostic = null;
            return true;
        }

        /// <summary>
        /// Records a live player incarnation boundary. Replayed/snapshot
        /// duplicates of an as-yet-unobserved epoch are idempotent.
        /// </summary>
        public bool NotePlayerJoined(in SenderBaseline player, out string? diagnostic)
        {
            return NoteEpoch(
                player.PlayerId,
                player.Epoch,
                player.Seq,
                "PlayerJoined",
                out diagnostic
            );
        }

        /// <summary>
        /// Records a reconnected player incarnation boundary; the new
        /// incarnation restarts its sequence at zero.
        /// </summary>
        public bool NotePlayerReconnected(Guid playerId, uint? epoch, out string? diagnostic)
        {
            ulong? seq = epoch.HasValue ? (ulong?)0UL : null;
            return NoteEpoch(playerId, epoch, seq, "PlayerReconnected", out diagnostic);
        }

        /// <summary>
        /// Records a departed player incarnation and its terminal
        /// watermark; retirement completes once the terminal coverage is
        /// explainable.
        /// </summary>
        public bool NotePlayerLeft(
            Guid playerId,
            uint? epoch,
            ulong? finalSeq,
            out string? diagnostic
        )
        {
            if (!_protocolV3)
            {
                if (!epoch.HasValue && !finalSeq.HasValue)
                {
                    diagnostic = null;
                    return true;
                }

                diagnostic =
                    "delivery accountability violation: v2 PlayerLeft exposed terminal delivery watermark fields";
                return false;
            }

            if (!epoch.HasValue || !finalSeq.HasValue)
            {
                diagnostic =
                    "delivery accountability violation: v3 PlayerLeft omitted epoch/final_seq terminal watermark";
                return false;
            }

            uint leftEpoch = epoch.GetValueOrDefault();
            ulong leftFinalSeq = finalSeq.GetValueOrDefault();
            string? epochError = ValidateEpoch(playerId, leftEpoch, "PlayerLeft");
            if (epochError != null)
            {
                diagnostic = epochError;
                return false;
            }

            if (!_senders.TryGetValue(playerId, out SenderProgress progress))
            {
                diagnostic =
                    "delivery accountability violation: PlayerLeft terminal watermark names unknown sender "
                    + playerId;
                return false;
            }

            if (leftEpoch < progress.Epoch)
            {
                diagnostic =
                    "delivery accountability violation: PlayerLeft epoch "
                    + FormatNum(leftEpoch)
                    + " for "
                    + playerId
                    + " moved backward from "
                    + FormatNum(progress.Epoch);
                return false;
            }

            if (leftEpoch > progress.Epoch && !AnnouncedContains(playerId, leftEpoch))
            {
                diagnostic =
                    "delivery accountability violation: PlayerLeft for "
                    + playerId
                    + " used unannounced epoch "
                    + FormatNum(leftEpoch);
                return false;
            }

            if (leftEpoch == progress.Epoch && leftFinalSeq < progress.LastSeq)
            {
                diagnostic =
                    "delivery accountability violation: PlayerLeft final_seq "
                    + FormatNum(leftFinalSeq)
                    + " for "
                    + playerId
                    + " moved backward from "
                    + FormatNum(progress.LastSeq);
                return false;
            }

            if (
                _departedSenders.TryGetValue((playerId, leftEpoch), out ulong existingFinalSeq)
                && existingFinalSeq != leftFinalSeq
            )
            {
                diagnostic =
                    "delivery accountability violation: PlayerLeft terminal watermark changed for "
                    + playerId
                    + " epoch "
                    + FormatNum(leftEpoch);
                return false;
            }

            foreach (KeyValuePair<(Guid PlayerId, uint Epoch), ulong> entry in _departedSenders)
            {
                if (entry.Key.PlayerId == playerId && entry.Key.Epoch > leftEpoch)
                {
                    diagnostic =
                        "delivery accountability violation: PlayerLeft terminal epoch "
                        + FormatNum(leftEpoch)
                        + " for "
                        + playerId
                        + " arrived after a newer leave";
                    return false;
                }
            }

            if (_pendingGaps.TryGetValue((playerId, leftEpoch), out List<DeliveryGap>? gaps))
            {
                for (int i = 0; i < gaps.Count; i++)
                {
                    if (gaps[i].ToSeq > leftFinalSeq)
                    {
                        diagnostic =
                            "delivery accountability violation: gap report for "
                            + playerId
                            + " extends beyond PlayerLeft final_seq "
                            + FormatNum(leftFinalSeq);
                        return false;
                    }
                }
            }

            if (
                !_departedSenders.ContainsKey((playerId, leftEpoch))
                && DepartedCount(playerId) >= MaxDepartedSendersPerPlayer
            )
            {
                diagnostic =
                    "delivery accountability violation: PlayerLeft terminal epoch "
                    + FormatNum(leftEpoch)
                    + " for "
                    + playerId
                    + " exceeds the "
                    + MaxDepartedSendersPerPlayer.ToString(CultureInfo.InvariantCulture)
                    + " uncovered departure bound";
                return false;
            }

            _departedSenders[(playerId, leftEpoch)] = leftFinalSeq;
            _staleSenders.Add(playerId);
            return TryRetireDeparted(playerId, leftEpoch, out diagnostic);
        }

        /// <summary>
        /// Accepts a rate-limited <c>Error(UnsupportedGameDataFormat)</c>
        /// advisory only after a causal exact report.
        /// </summary>
        public bool ObserveUnsupportedFormatError(out string? diagnostic)
        {
            if (!_protocolV3)
            {
                diagnostic = null;
                return true;
            }

            bool armed = _unadvisedUnsupportedGap.HasValue;
            _unadvisedUnsupportedGap = null;
            if (!armed)
            {
                diagnostic =
                    "delivery accountability violation: Error(UnsupportedGameDataFormat) lacked a prior causal DeliveryReport";
                return false;
            }

            diagnostic = null;
            return true;
        }

        /// <summary>
        /// A terminal socket outcome ends the observable stream, so no
        /// supplemental error is required after the final report.
        /// </summary>
        public void ObserveTerminal()
        {
            _unadvisedUnsupportedGap = null;
        }

        /// <summary>
        /// Records one cumulative <c>RelayStats</c> window; the interval is
        /// fixed per connection and the counters are monotonic.
        /// </summary>
        public bool RecordRelayStats(
            ulong intervalMs,
            ulong sentToYou,
            ulong droppedForYou,
            ulong backpressureEvents,
            out string? diagnostic
        )
        {
            if (!_protocolV3)
            {
                diagnostic = "delivery accountability violation: v2 connection received RelayStats";
                return false;
            }

            if (intervalMs == 0)
            {
                diagnostic =
                    "delivery accountability violation: RelayStats interval_ms must be positive";
                return false;
            }

            RelayStatsSnapshot next = new RelayStatsSnapshot(
                intervalMs,
                sentToYou,
                droppedForYou,
                backpressureEvents
            );
            if (_lastRelayStats.HasValue)
            {
                RelayStatsSnapshot previous = _lastRelayStats.GetValueOrDefault();
                if (next.IntervalMs != previous.IntervalMs)
                {
                    diagnostic =
                        "delivery accountability violation: RelayStats interval_ms changed within one connection (previous="
                        + FormatNum(previous.IntervalMs)
                        + ", next="
                        + FormatNum(next.IntervalMs)
                        + ")";
                    return false;
                }

                if (
                    next.SentToYou < previous.SentToYou
                    || next.DroppedForYou < previous.DroppedForYou
                    || next.BackpressureEvents < previous.BackpressureEvents
                )
                {
                    diagnostic =
                        "delivery accountability violation: cumulative RelayStats counters moved backward";
                    return false;
                }
            }

            _lastRelayStats = next;
            diagnostic = null;
            return true;
        }

        /// <summary>
        /// Records one causally prior exact gap report and its cumulative
        /// counters. The whole report is validated before any state moves.
        /// </summary>
        /// <param name="gaps">The exact gap ranges; <see langword="null"/> means none.</param>
        /// <param name="counters">The cumulative per-class counters.</param>
        /// <param name="diagnostic">The violation text on failure; <see langword="null"/> on success.</param>
        /// <returns><see langword="true"/> when the report was accepted.</returns>
        public bool RecordReport(
            IReadOnlyList<DeliveryGap>? gaps,
            in DeliveryCountersByClass counters,
            out string? diagnostic
        )
        {
            if (!_protocolV3)
            {
                diagnostic =
                    "delivery accountability violation: v2 connection received DeliveryReport";
                return false;
            }

            IReadOnlyList<DeliveryGap> reportGaps = gaps ?? Array.Empty<DeliveryGap>();
            if (reportGaps.Count > DeliveryReportMaxGaps)
            {
                diagnostic =
                    "delivery accountability violation: DeliveryReport contains "
                    + reportGaps.Count.ToString(CultureInfo.InvariantCulture)
                    + " gap ranges, limit is "
                    + DeliveryReportMaxGaps.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            ulong outstandingGaps = 0;
            foreach (List<DeliveryGap> pending in _pendingGaps.Values)
            {
                outstandingGaps += (ulong)pending.Count;
            }

            if (outstandingGaps + (ulong)reportGaps.Count > (ulong)MaxTotalPendingGaps)
            {
                diagnostic =
                    "delivery accountability violation: DeliveryReport would exceed the "
                    + MaxTotalPendingGaps.ToString(CultureInfo.InvariantCulture)
                    + " exact-gap retention bound ("
                    + FormatNum(outstandingGaps)
                    + " outstanding plus "
                    + reportGaps.Count.ToString(CultureInfo.InvariantCulture)
                    + " new)";
                return false;
            }

            DeliveryCountersByClass previous = _counters.GetValueOrDefault();
            string? monotonicError = ValidateMonotonicCounters(previous, counters);
            if (monotonicError != null)
            {
                diagnostic = monotonicError;
                return false;
            }

            // Validate the whole report before mutating state, including ranges that overlap another range in this same report.
            Dictionary<(Guid PlayerId, uint Epoch), List<(ulong From, ulong To)>> reportRanges =
                new Dictionary<(Guid PlayerId, uint Epoch), List<(ulong From, ulong To)>>();
            ulong[] causalCounts = new ulong[4];
            DeliveryGap? unsupportedGap = null;
            for (int i = 0; i < reportGaps.Count; i++)
            {
                DeliveryGap gap = reportGaps[i];
                string? gapError = ValidateGap(gap);
                if (gapError != null)
                {
                    diagnostic = gapError;
                    return false;
                }

                ulong count;
                ulong span = gap.ToSeq - gap.FromSeq;
                if (span == ulong.MaxValue)
                {
                    diagnostic = "delivery accountability violation: exact gap length overflowed";
                    return false;
                }

                count = span + 1;
                int index;
                switch (gap.Reason)
                {
                    case DeliveryGapReason.LatestSuperseded:
                        index = 0;
                        break;
                    case DeliveryGapReason.LatestDroppedFull:
                        index = 1;
                        break;
                    case DeliveryGapReason.VolatileDropped:
                        index = 2;
                        break;
                    case DeliveryGapReason.UnsupportedFormat:
                        unsupportedGap = gap;
                        index = 3;
                        break;
                    default:
                        diagnostic =
                            "delivery accountability violation: internal gap category index";
                        return false;
                }

                if (causalCounts[index] > ulong.MaxValue - count)
                {
                    diagnostic = "delivery accountability violation: causal gap count overflowed";
                    return false;
                }

                causalCounts[index] += count;
                (Guid PlayerId, uint Epoch) rangeKey = (gap.FromPlayer, gap.Epoch);
                if (!reportRanges.TryGetValue(rangeKey, out List<(ulong From, ulong To)>? ranges))
                {
                    ranges = new List<(ulong From, ulong To)>();
                    reportRanges[rangeKey] = ranges;
                }

                for (int r = 0; r < ranges.Count; r++)
                {
                    if (gap.FromSeq <= ranges[r].To && ranges[r].From <= gap.ToSeq)
                    {
                        diagnostic =
                            "delivery accountability violation: overlapping/duplicate gap "
                            + FormatNum(gap.FromSeq)
                            + "..="
                            + FormatNum(gap.ToSeq)
                            + " for "
                            + gap.FromPlayer
                            + " epoch "
                            + FormatNum(gap.Epoch)
                            + " in one report";
                        return false;
                    }
                }

                ranges.Add((gap.FromSeq, gap.ToSeq));
            }

            // Monotonicity was already rejected above, so these subtractions are total; the guards keep that invariant machine-checked.
            string? deltaError;
            ulong reliableUnsupported = Delta(
                counters.Reliable.UnsupportedFormat,
                previous.Reliable.UnsupportedFormat,
                out deltaError
            );
            if (deltaError != null)
            {
                diagnostic = deltaError;
                return false;
            }

            ulong latestUnsupported = Delta(
                counters.Latest.UnsupportedFormat,
                previous.Latest.UnsupportedFormat,
                out deltaError
            );
            if (deltaError != null)
            {
                diagnostic = deltaError;
                return false;
            }

            ulong volatileUnsupported = Delta(
                counters.Volatile.UnsupportedFormat,
                previous.Volatile.UnsupportedFormat,
                out deltaError
            );
            if (deltaError != null)
            {
                diagnostic = deltaError;
                return false;
            }

            ulong unsupportedDelta = reliableUnsupported;
            if (unsupportedDelta > ulong.MaxValue - latestUnsupported)
            {
                diagnostic =
                    "delivery accountability violation: unsupported-format delta overflowed";
                return false;
            }

            unsupportedDelta += latestUnsupported;
            if (unsupportedDelta > ulong.MaxValue - volatileUnsupported)
            {
                diagnostic =
                    "delivery accountability violation: unsupported-format delta overflowed";
                return false;
            }

            unsupportedDelta += volatileUnsupported;
            ulong[] counterDeltas = new ulong[4];
            counterDeltas[0] = Delta(
                counters.Latest.Superseded,
                previous.Latest.Superseded,
                out deltaError
            );
            if (deltaError != null)
            {
                diagnostic = deltaError;
                return false;
            }

            counterDeltas[1] = Delta(
                counters.Latest.DroppedFull,
                previous.Latest.DroppedFull,
                out deltaError
            );
            if (deltaError != null)
            {
                diagnostic = deltaError;
                return false;
            }

            counterDeltas[2] = Delta(
                counters.Volatile.Dropped,
                previous.Volatile.Dropped,
                out deltaError
            );
            if (deltaError != null)
            {
                diagnostic = deltaError;
                return false;
            }

            counterDeltas[3] = unsupportedDelta;
            bool deltasMatch = true;
            for (int i = 0; i < 4; i++)
            {
                if (counterDeltas[i] != causalCounts[i])
                {
                    deltasMatch = false;
                    break;
                }
            }

            if (!deltasMatch)
            {
                diagnostic =
                    "delivery accountability violation: loss counter deltas "
                    + FormatNums(counterDeltas)
                    + " do not match exact gap units "
                    + FormatNums(causalCounts);
                return false;
            }

            for (int i = 0; i < reportGaps.Count; i++)
            {
                DeliveryGap gap = reportGaps[i];
                if (
                    !_pendingGaps.TryGetValue(
                        (gap.FromPlayer, gap.Epoch),
                        out List<DeliveryGap>? pending
                    )
                )
                {
                    pending = new List<DeliveryGap>();
                    _pendingGaps[(gap.FromPlayer, gap.Epoch)] = pending;
                }

                pending.Add(gap);
                // Ties are impossible: equal from_seq ranges overlap and were already rejected, so the unstable sort is fully deterministic.
                pending.Sort(CompareGapByFromSeq);
            }

            if (unsupportedGap.HasValue)
            {
                // Arm causality from an unsupported-format range regardless of its position in a potentially mixed-reason report.
                _unadvisedUnsupportedGap = unsupportedGap;
            }

            _counters = counters;
            for (int i = 0; i < reportGaps.Count; i++)
            {
                DeliveryGap gap = reportGaps[i];
                if (!TryRetireDeparted(gap.FromPlayer, gap.Epoch, out diagnostic))
                {
                    return false;
                }
            }

            diagnostic = null;
            return true;
        }

        /// <summary>
        /// Validates and advances one received GameData stamp; on success
        /// <paramref name="disposition"/> says whether the application may
        /// apply the payload.
        /// </summary>
        public bool RecordGameData(
            Guid fromPlayer,
            ulong? seq,
            uint? epoch,
            GameDataClass? classification,
            uint? key,
            out GameDataDisposition disposition,
            out string? diagnostic
        )
        {
            if (!_protocolV3)
            {
                if (!seq.HasValue && !epoch.HasValue && !classification.HasValue && !key.HasValue)
                {
                    disposition = GameDataDisposition.Apply;
                    diagnostic = null;
                    return true;
                }

                disposition = default;
                diagnostic =
                    "delivery accountability violation: v2 GameData from "
                    + fromPlayer
                    + " exposed v3 metadata (seq="
                    + FormatOptional(seq)
                    + ", epoch="
                    + FormatOptional(epoch)
                    + ", class="
                    + FormatOptional(classification)
                    + ", key="
                    + FormatOptional(key)
                    + ")";
                return false;
            }

            if (!ValidateClassKey(classification, key))
            {
                disposition = default;
                diagnostic =
                    "delivery accountability violation: invalid received class/key combination ("
                    + FormatOptional(classification)
                    + ", "
                    + FormatOptional(key)
                    + ")";
                return false;
            }

            if (!seq.HasValue && !epoch.HasValue)
            {
                disposition = default;
                diagnostic =
                    "delivery accountability violation: v3 GameData omitted its seq/epoch stamp";
                return false;
            }

            if (!seq.HasValue || !epoch.HasValue)
            {
                disposition = default;
                diagnostic =
                    "delivery accountability violation: GameData from "
                    + fromPlayer
                    + " must carry seq and epoch together (seq="
                    + FormatOptional(seq)
                    + ", epoch="
                    + FormatOptional(epoch)
                    + ")";
                return false;
            }

            ulong dataSeq = seq.GetValueOrDefault();
            uint dataEpoch = epoch.GetValueOrDefault();
            if (dataSeq == 0 || dataEpoch == 0)
            {
                disposition = default;
                diagnostic =
                    "delivery accountability violation: GameData from "
                    + fromPlayer
                    + " has non-positive stamp ("
                    + FormatNum(dataEpoch)
                    + ", "
                    + FormatNum(dataSeq)
                    + ")";
                return false;
            }

            if (!_senders.TryGetValue(fromPlayer, out SenderProgress progress))
            {
                disposition = default;
                diagnostic =
                    "delivery accountability violation: GameData from "
                    + fromPlayer
                    + " arrived before a room/lifecycle baseline";
                return false;
            }

            if (dataEpoch < progress.Epoch)
            {
                disposition = default;
                diagnostic =
                    "delivery accountability violation: GameData from "
                    + fromPlayer
                    + " moved backward to epoch "
                    + FormatNum(dataEpoch)
                    + " from "
                    + FormatNum(progress.Epoch);
                return false;
            }

            if (dataEpoch > progress.Epoch && !AnnouncedContains(fromPlayer, dataEpoch))
            {
                disposition = default;
                diagnostic =
                    "delivery accountability violation: GameData from "
                    + fromPlayer
                    + " used unannounced epoch "
                    + FormatNum(dataEpoch)
                    + " after "
                    + FormatNum(progress.Epoch);
                return false;
            }

            if (
                _departedSenders.TryGetValue((fromPlayer, dataEpoch), out ulong terminalSeq)
                && dataSeq > terminalSeq
            )
            {
                disposition = default;
                diagnostic =
                    "delivery accountability violation: GameData from "
                    + fromPlayer
                    + " advanced beyond PlayerLeft terminal ("
                    + FormatNum(dataEpoch)
                    + ", "
                    + FormatNum(terminalSeq)
                    + ")";
                return false;
            }

            bool transitioned = dataEpoch > progress.Epoch;
            if (transitioned)
            {
                List<uint> olderTerminals = new List<uint>();
                foreach (KeyValuePair<(Guid PlayerId, uint Epoch), ulong> entry in _departedSenders)
                {
                    if (entry.Key.PlayerId == fromPlayer && entry.Key.Epoch < dataEpoch)
                    {
                        olderTerminals.Add(entry.Key.Epoch);
                    }
                }

                for (int i = 0; i < olderTerminals.Count; i++)
                {
                    if (!TryRetireDeparted(fromPlayer, olderTerminals[i], out diagnostic))
                    {
                        disposition = default;
                        return false;
                    }
                }

                bool hasOlderTerminal = HasOlderTerminal(fromPlayer, dataEpoch);
                if (hasOlderTerminal)
                {
                    disposition = default;
                    diagnostic =
                        "delivery accountability violation: GameData from "
                        + fromPlayer
                        + " advanced to epoch "
                        + FormatNum(dataEpoch)
                        + " before older PlayerLeft tails retired";
                    return false;
                }

                if (!ConsumeExactGap((fromPlayer, dataEpoch), 1, dataSeq, out diagnostic))
                {
                    disposition = default;
                    return false;
                }

                PrunePendingGapsBelow(fromPlayer, dataEpoch);
            }
            else
            {
                ulong lastSeq = progress.LastSeq;
                if (dataSeq <= lastSeq)
                {
                    disposition = default;
                    diagnostic =
                        "delivery accountability violation: duplicate/backward GameData from "
                        + fromPlayer
                        + " epoch "
                        + FormatNum(dataEpoch)
                        + ": "
                        + FormatNum(dataSeq)
                        + " after "
                        + FormatNum(lastSeq);
                    return false;
                }

                if (lastSeq == ulong.MaxValue)
                {
                    disposition = default;
                    diagnostic =
                        "delivery accountability violation: sequence overflow after "
                        + FormatNum(lastSeq)
                        + " from "
                        + fromPlayer
                        + " epoch "
                        + FormatNum(dataEpoch);
                    return false;
                }

                if (!ConsumeExactGap((fromPlayer, dataEpoch), lastSeq + 1, dataSeq, out diagnostic))
                {
                    disposition = default;
                    return false;
                }
            }

            _senders[fromPlayer] = new SenderProgress(dataEpoch, dataSeq);
            if (transitioned)
            {
                bool noNewerAnnouncement = true;
                if (_announcedEpochs.TryGetValue(fromPlayer, out List<uint>? announced))
                {
                    /*
                        In-place compaction rather than RemoveAll: a lambda
                        would hoist a display-class allocation onto every
                        call, and this runs on the receive hot path.
                    */
                    int kept = 0;
                    for (int i = 0; i < announced.Count; i++)
                    {
                        if (announced[i] > dataEpoch)
                        {
                            announced[kept++] = announced[i];
                        }
                    }

                    if (kept < announced.Count)
                    {
                        announced.RemoveRange(kept, announced.Count - kept);
                    }

                    noNewerAnnouncement = announced.Count == 0;
                }

                if (noNewerAnnouncement && !_departedSenders.ContainsKey((fromPlayer, dataEpoch)))
                {
                    _staleSenders.Remove(fromPlayer);
                }
            }

            bool stale =
                _departedSenders.ContainsKey((fromPlayer, dataEpoch))
                || AnnouncedHasNewer(fromPlayer, dataEpoch);
            disposition = stale ? GameDataDisposition.Stale : GameDataDisposition.Apply;
            if (!TryRetireDeparted(fromPlayer, dataEpoch, out diagnostic))
            {
                disposition = default;
                return false;
            }

            diagnostic = null;
            return true;
        }

        /// <summary>
        /// Validates the protocol's delivery class/key pairing:
        /// <see cref="GameDataClass.Reliable"/> and
        /// <see cref="GameDataClass.Volatile"/> (or an absent class) carry
        /// no key, <see cref="GameDataClass.Latest"/> always does. A
        /// <see langword="null"/> class means the class was absent.
        /// </summary>
        public static bool ValidateClassKey(GameDataClass? classification, uint? key)
        {
            if (!classification.HasValue)
            {
                return !key.HasValue;
            }

            GameDataClass value = classification.GetValueOrDefault();
            if (value == GameDataClass.Latest)
            {
                return key.HasValue;
            }

            if (value == GameDataClass.Reliable || value == GameDataClass.Volatile)
            {
                return !key.HasValue;
            }

            return false;
        }

        private bool NoteEpoch(
            Guid playerId,
            uint? epoch,
            ulong? seq,
            string source,
            out string? diagnostic
        )
        {
            if (_protocolV3)
            {
                if (!epoch.HasValue || !seq.HasValue)
                {
                    diagnostic =
                        "delivery accountability violation: v3 "
                        + source
                        + " omitted paired epoch/seq baseline for "
                        + playerId;
                    return false;
                }
            }
            else
            {
                if (!epoch.HasValue && !seq.HasValue)
                {
                    diagnostic = null;
                    return true;
                }

                diagnostic =
                    "delivery accountability violation: v2 "
                    + source
                    + " exposed delivery baseline ("
                    + FormatOptional(epoch)
                    + ", "
                    + FormatOptional(seq)
                    + ") for "
                    + playerId;
                return false;
            }

            uint noteEpoch = epoch.GetValueOrDefault();
            ulong noteSeq = seq.GetValueOrDefault();
            string? epochError = ValidateEpoch(playerId, noteEpoch, source);
            if (epochError != null)
            {
                diagnostic = epochError;
                return false;
            }

            if (!_senders.TryGetValue(playerId, out SenderProgress previous))
            {
                _senders[playerId] = new SenderProgress(noteEpoch, noteSeq);
                _staleSenders.Remove(playerId);
                diagnostic = null;
                return true;
            }

            if (previous.Epoch == noteEpoch && !_staleSenders.Contains(playerId))
            {
                diagnostic = null;
                return true;
            }

            if (noteEpoch <= previous.Epoch)
            {
                diagnostic =
                    "delivery accountability violation: "
                    + source
                    + " epoch "
                    + FormatNum(noteEpoch)
                    + " for "
                    + playerId
                    + " is not newer than "
                    + FormatNum(previous.Epoch);
                return false;
            }

            if (!_announcedEpochs.TryGetValue(playerId, out List<uint>? announced))
            {
                announced = new List<uint>();
                _announcedEpochs[playerId] = announced;
            }

            if (announced.BinarySearch(noteEpoch) >= 0)
            {
                diagnostic = null;
                return true;
            }

            if (announced.Count > 0 && noteEpoch <= announced[announced.Count - 1])
            {
                diagnostic =
                    "delivery accountability violation: "
                    + source
                    + " epoch "
                    + FormatNum(noteEpoch)
                    + " for "
                    + playerId
                    + " is not newer than announced epochs "
                    + FormatNums(announced);
                return false;
            }

            if (announced.Count >= MaxAnnouncedEpochsPerSender)
            {
                diagnostic =
                    "delivery accountability violation: "
                    + source
                    + " epoch "
                    + FormatNum(noteEpoch)
                    + " for "
                    + playerId
                    + " exceeds the "
                    + MaxAnnouncedEpochsPerSender.ToString(CultureInfo.InvariantCulture)
                    + " unresolved announcement bound";
                return false;
            }

            InsertSorted(announced, noteEpoch);
            _staleSenders.Add(playerId);
            diagnostic = null;
            return true;
        }

        private string? ValidateGap(DeliveryGap gap)
        {
            if (gap.Epoch == 0 || gap.FromSeq == 0 || gap.ToSeq < gap.FromSeq)
            {
                return "delivery accountability violation: invalid exact gap for "
                    + gap.FromPlayer
                    + ": epoch "
                    + FormatNum(gap.Epoch)
                    + ", range "
                    + FormatNum(gap.FromSeq)
                    + "..="
                    + FormatNum(gap.ToSeq);
            }

            if (!_senders.TryGetValue(gap.FromPlayer, out SenderProgress progress))
            {
                return "delivery accountability violation: gap report names unknown sender "
                    + gap.FromPlayer;
            }

            if (gap.Epoch < progress.Epoch)
            {
                return "delivery accountability violation: gap report for "
                    + gap.FromPlayer
                    + " moved backward to epoch "
                    + FormatNum(gap.Epoch)
                    + " from "
                    + FormatNum(progress.Epoch);
            }

            if (gap.Epoch > progress.Epoch && !AnnouncedContains(gap.FromPlayer, gap.Epoch))
            {
                return "delivery accountability violation: gap report for "
                    + gap.FromPlayer
                    + " used unannounced epoch "
                    + FormatNum(gap.Epoch)
                    + " after "
                    + FormatNum(progress.Epoch);
            }

            if (
                _departedSenders.TryGetValue((gap.FromPlayer, gap.Epoch), out ulong finalSeq)
                && gap.ToSeq > finalSeq
            )
            {
                return "delivery accountability violation: gap report for "
                    + gap.FromPlayer
                    + " extends beyond PlayerLeft terminal ("
                    + FormatNum(gap.Epoch)
                    + ", "
                    + FormatNum(finalSeq)
                    + ")";
            }

            if (progress.Epoch == gap.Epoch && gap.FromSeq <= progress.LastSeq)
            {
                return "delivery accountability violation: gap "
                    + FormatNum(gap.FromSeq)
                    + "..="
                    + FormatNum(gap.ToSeq)
                    + " for "
                    + gap.FromPlayer
                    + " epoch "
                    + FormatNum(gap.Epoch)
                    + " was reported after data at or beyond its start";
            }

            if (
                _pendingGaps.TryGetValue(
                    (gap.FromPlayer, gap.Epoch),
                    out List<DeliveryGap>? pending
                )
            )
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    DeliveryGap existing = pending[i];
                    if (gap.FromSeq <= existing.ToSeq && existing.FromSeq <= gap.ToSeq)
                    {
                        return "delivery accountability violation: overlapping/duplicate gap "
                            + FormatNum(gap.FromSeq)
                            + "..="
                            + FormatNum(gap.ToSeq)
                            + " for "
                            + gap.FromPlayer
                            + " epoch "
                            + FormatNum(gap.Epoch);
                    }
                }
            }

            return null;
        }

        private bool ConsumeExactGap(
            (Guid PlayerId, uint Epoch) key,
            ulong expected,
            ulong received,
            out string? diagnostic
        )
        {
            if (!_pendingGaps.TryGetValue(key, out List<DeliveryGap>? gaps))
            {
                if (received == expected)
                {
                    diagnostic = null;
                    return true;
                }

                diagnostic =
                    "delivery accountability violation: unexplained gap for "
                    + key.PlayerId
                    + " epoch "
                    + FormatNum(key.Epoch)
                    + ": expected "
                    + FormatNum(expected)
                    + ", received "
                    + FormatNum(received);
                return false;
            }

            if (received == expected)
            {
                for (int i = 0; i < gaps.Count; i++)
                {
                    if (gaps[i].FromSeq <= received && received <= gaps[i].ToSeq)
                    {
                        diagnostic =
                            "delivery accountability violation: prior gap report for "
                            + key.PlayerId
                            + " epoch "
                            + FormatNum(key.Epoch)
                            + " includes delivered seq "
                            + FormatNum(received);
                        return false;
                    }
                }

                diagnostic = null;
                return true;
            }

            ulong next = expected;
            int consumed = 0;
            for (int index = 0; index < gaps.Count; index++)
            {
                DeliveryGap gap = gaps[index];
                if (gap.FromSeq != next || gap.ToSeq >= received)
                {
                    break;
                }

                if (gap.ToSeq == ulong.MaxValue)
                {
                    diagnostic =
                        "delivery accountability violation: gap range overflow for "
                        + key.PlayerId
                        + " epoch "
                        + FormatNum(key.Epoch);
                    return false;
                }

                next = gap.ToSeq + 1;
                consumed = index + 1;
                if (next == received)
                {
                    break;
                }
            }

            if (next != received)
            {
                // Sequence stamps start at 1, so the last missing sequence is received - 1; saturation is unreachable for validated stamps.
                diagnostic =
                    "delivery accountability violation: prior exact reports do not cover "
                    + key.PlayerId
                    + " epoch "
                    + FormatNum(key.Epoch)
                    + " gap "
                    + FormatNum(expected)
                    + "..="
                    + FormatNum(received == 0 ? 0UL : received - 1);
                return false;
            }

            gaps.RemoveRange(0, consumed);
            if (gaps.Count == 0)
            {
                _pendingGaps.Remove(key);
            }

            diagnostic = null;
            return true;
        }

        private bool TryRetireDeparted(Guid playerId, uint epoch, out string? diagnostic)
        {
            if (!_departedSenders.TryGetValue((playerId, epoch), out ulong finalSeq))
            {
                diagnostic = null;
                return true;
            }

            if (!_senders.TryGetValue(playerId, out SenderProgress progress))
            {
                diagnostic = null;
                return true;
            }

            ulong next;
            if (finalSeq == 0 || progress.Epoch < epoch)
            {
                next = 1;
            }
            else if (progress.Epoch == epoch)
            {
                ulong lastSeq = progress.LastSeq;
                if (lastSeq >= finalSeq)
                {
                    RetireDeparted(playerId, epoch);
                    diagnostic = null;
                    return true;
                }

                // Guarded above: last_seq < final_seq, so the successor still fits; the check keeps that invariant machine-checked.
                if (lastSeq == ulong.MaxValue)
                {
                    diagnostic =
                        "delivery accountability violation: sender "
                        + playerId
                        + " sequence overflow before PlayerLeft epoch "
                        + FormatNum(epoch);
                    return false;
                }

                next = lastSeq + 1;
            }
            else
            {
                diagnostic =
                    "delivery accountability violation: sender "
                    + playerId
                    + " advanced beyond its unresolved PlayerLeft epoch "
                    + FormatNum(epoch);
                return false;
            }

            (Guid PlayerId, uint Epoch) key = (playerId, epoch);
            _pendingGaps.TryGetValue(key, out List<DeliveryGap>? gaps);
            int consumed = 0;
            bool covered = finalSeq == 0;
            if (gaps != null)
            {
                for (int index = 0; index < gaps.Count; index++)
                {
                    DeliveryGap gap = gaps[index];
                    if (next > finalSeq)
                    {
                        break;
                    }

                    if (gap.FromSeq != next || gap.ToSeq > finalSeq)
                    {
                        diagnostic = null;
                        return true;
                    }

                    consumed = index + 1;
                    if (gap.ToSeq == finalSeq)
                    {
                        covered = true;
                        break;
                    }

                    if (gap.ToSeq == ulong.MaxValue)
                    {
                        diagnostic =
                            "delivery accountability violation: gap range overflow for "
                            + playerId
                            + " epoch "
                            + FormatNum(epoch);
                        return false;
                    }

                    next = gap.ToSeq + 1;
                }
            }

            if (!covered)
            {
                diagnostic = null;
                return true;
            }

            if (consumed > 0)
            {
                if (!_pendingGaps.TryGetValue(key, out List<DeliveryGap>? pending))
                {
                    diagnostic = "delivery accountability violation: pending gap state disappeared";
                    return false;
                }

                pending.RemoveRange(0, consumed);
                if (pending.Count == 0)
                {
                    _pendingGaps.Remove(key);
                }
            }

            RetireDeparted(playerId, epoch);
            diagnostic = null;
            return true;
        }

        private void RetireDeparted(Guid playerId, uint epoch)
        {
            _departedSenders.Remove((playerId, epoch));
            _pendingGaps.Remove((playerId, epoch));
            if (_announcedEpochs.TryGetValue(playerId, out List<uint>? announced))
            {
                int index = announced.BinarySearch(epoch);
                if (index >= 0)
                {
                    announced.RemoveAt(index);
                }

                if (announced.Count == 0)
                {
                    _announcedEpochs.Remove(playerId);
                }
            }

            bool hasTerminal = false;
            foreach ((Guid PlayerId, uint Epoch) key in _departedSenders.Keys)
            {
                if (key.PlayerId == playerId)
                {
                    hasTerminal = true;
                    break;
                }
            }

            if (!hasTerminal && !_announcedEpochs.ContainsKey(playerId))
            {
                _senders.Remove(playerId);
                _staleSenders.Remove(playerId);
                // Full retirement leaves no live incarnation: any pending range still keyed to this player belongs to a retired incarnation and must never explain or reject a future epoch-reuse incarnation.
                PrunePendingGapsForPlayer(playerId);
            }
        }

        private int DepartedCount(Guid playerId)
        {
            int count = 0;
            foreach ((Guid PlayerId, uint Epoch) key in _departedSenders.Keys)
            {
                if (key.PlayerId == playerId)
                {
                    count++;
                }
            }

            return count;
        }

        private bool AnnouncedContains(Guid playerId, uint epoch)
        {
            return _announcedEpochs.TryGetValue(playerId, out List<uint>? announced)
                && announced.BinarySearch(epoch) >= 0;
        }

        private bool AnnouncedHasNewer(Guid playerId, uint epoch)
        {
            if (!_announcedEpochs.TryGetValue(playerId, out List<uint>? announced))
            {
                return false;
            }

            for (int i = 0; i < announced.Count; i++)
            {
                if (announced[i] > epoch)
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasOlderTerminal(Guid playerId, uint epoch)
        {
            foreach (KeyValuePair<(Guid PlayerId, uint Epoch), ulong> entry in _departedSenders)
            {
                if (entry.Key.PlayerId == playerId && entry.Key.Epoch < epoch)
                {
                    return true;
                }
            }

            return false;
        }

        private void PrunePendingGapsBelow(Guid playerId, uint epoch)
        {
            List<(Guid PlayerId, uint Epoch)>? stale = null;
            foreach ((Guid PlayerId, uint Epoch) key in _pendingGaps.Keys)
            {
                if (key.PlayerId == playerId && key.Epoch < epoch)
                {
                    if (stale == null)
                    {
                        stale = new List<(Guid PlayerId, uint Epoch)>();
                    }

                    stale.Add(key);
                }
            }

            if (stale != null)
            {
                for (int i = 0; i < stale.Count; i++)
                {
                    _pendingGaps.Remove(stale[i]);
                }
            }
        }

        private void PrunePendingGapsForPlayer(Guid playerId)
        {
            List<(Guid PlayerId, uint Epoch)>? stale = null;
            foreach ((Guid PlayerId, uint Epoch) key in _pendingGaps.Keys)
            {
                if (key.PlayerId == playerId)
                {
                    if (stale == null)
                    {
                        stale = new List<(Guid PlayerId, uint Epoch)>();
                    }

                    stale.Add(key);
                }
            }

            if (stale != null)
            {
                for (int i = 0; i < stale.Count; i++)
                {
                    _pendingGaps.Remove(stale[i]);
                }
            }
        }

        private static void InsertSorted(List<uint> announced, uint epoch)
        {
            int index = announced.BinarySearch(epoch);
            if (index < 0)
            {
                index = ~index;
            }

            announced.Insert(index, epoch);
        }

        private static int CompareGapByFromSeq(DeliveryGap left, DeliveryGap right)
        {
            return left.FromSeq.CompareTo(right.FromSeq);
        }

        private static string? ValidateEpoch(Guid playerId, uint epoch, string source)
        {
            if (epoch == 0)
            {
                return "delivery accountability violation: "
                    + source
                    + " advertised epoch 0 for "
                    + playerId;
            }

            return null;
        }

        private static string? ValidateMonotonicCounters(
            in DeliveryCountersByClass previous,
            in DeliveryCountersByClass next
        )
        {
            bool monotonic =
                next.Reliable.Delivered >= previous.Reliable.Delivered
                && next.Reliable.Abandoned >= previous.Reliable.Abandoned
                && next.Reliable.UnsupportedFormat >= previous.Reliable.UnsupportedFormat
                && next.Latest.Delivered >= previous.Latest.Delivered
                && next.Latest.Superseded >= previous.Latest.Superseded
                && next.Latest.DroppedFull >= previous.Latest.DroppedFull
                && next.Latest.Abandoned >= previous.Latest.Abandoned
                && next.Latest.UnsupportedFormat >= previous.Latest.UnsupportedFormat
                && next.Volatile.Delivered >= previous.Volatile.Delivered
                && next.Volatile.Dropped >= previous.Volatile.Dropped
                && next.Volatile.Abandoned >= previous.Volatile.Abandoned
                && next.Volatile.UnsupportedFormat >= previous.Volatile.UnsupportedFormat;
            if (!monotonic)
            {
                return "delivery accountability violation: cumulative per-class counters moved backward (previous="
                    + FormatCounters(previous)
                    + ", next="
                    + FormatCounters(next)
                    + ")";
            }

            return null;
        }

        private static ulong Delta(ulong next, ulong prior, out string? error)
        {
            if (next < prior)
            {
                error =
                    "delivery accountability violation: cumulative per-class counters moved backward";
                return 0;
            }

            error = null;
            return next - prior;
        }

        private static string FormatNum(uint value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatNum(ulong value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatOptional(uint? value)
        {
            return value.HasValue ? FormatNum(value.GetValueOrDefault()) : "None";
        }

        private static string FormatOptional(ulong? value)
        {
            return value.HasValue ? FormatNum(value.GetValueOrDefault()) : "None";
        }

        private static string FormatOptional(GameDataClass? value)
        {
            return value.HasValue ? value.GetValueOrDefault().ToString() : "None";
        }

        private static string FormatNums(List<uint> values)
        {
            StringBuilder builder = new StringBuilder((values.Count * 4) + 2);
            builder.Append('[');
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(FormatNum(values[i]));
            }

            builder.Append(']');
            return builder.ToString();
        }

        private static string FormatNums(ulong[] values)
        {
            StringBuilder builder = new StringBuilder((values.Length * 6) + 2);
            builder.Append('[');
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(FormatNum(values[i]));
            }

            builder.Append(']');
            return builder.ToString();
        }

        private static string FormatIdSet(HashSet<Guid> ids)
        {
            List<Guid> sorted = new List<Guid>(ids);
            sorted.Sort();
            StringBuilder builder = new StringBuilder((sorted.Count * 40) + 2);
            builder.Append('[');
            for (int i = 0; i < sorted.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(sorted[i]);
            }

            builder.Append(']');
            return builder.ToString();
        }

        private static string FormatCounters(in DeliveryCountersByClass counters)
        {
            return "Reliable(delivered="
                + FormatNum(counters.Reliable.Delivered)
                + ", abandoned="
                + FormatNum(counters.Reliable.Abandoned)
                + ", unsupported_format="
                + FormatNum(counters.Reliable.UnsupportedFormat)
                + "), Latest(delivered="
                + FormatNum(counters.Latest.Delivered)
                + ", superseded="
                + FormatNum(counters.Latest.Superseded)
                + ", dropped_full="
                + FormatNum(counters.Latest.DroppedFull)
                + ", abandoned="
                + FormatNum(counters.Latest.Abandoned)
                + ", unsupported_format="
                + FormatNum(counters.Latest.UnsupportedFormat)
                + "), Volatile(delivered="
                + FormatNum(counters.Volatile.Delivered)
                + ", dropped="
                + FormatNum(counters.Volatile.Dropped)
                + ", abandoned="
                + FormatNum(counters.Volatile.Abandoned)
                + ", unsupported_format="
                + FormatNum(counters.Volatile.UnsupportedFormat)
                + ")";
        }
    }
}
