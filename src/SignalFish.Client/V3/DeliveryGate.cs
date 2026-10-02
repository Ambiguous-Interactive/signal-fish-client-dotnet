namespace SignalFish.Client.V3
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using SignalFish.Client.Core;
    using SignalFish.Client.Protocol;

    /// <summary>
    /// One inbound frame's delivery-gate outcome. <c>default</c> means
    /// proceed: the frame keeps its ordinary fact/event surface.
    /// </summary>
    internal struct GateVerdict
    {
        /// <summary>True when the frame is dropped (no fact, no payload event).</summary>
        internal bool Suppress;

        /// <summary>True when the violation policy tears the connection down.</summary>
        internal bool Teardown;

        /// <summary>The violation text; non-null whenever the engine refused the frame.</summary>
        internal string? Diagnostic;
    }

    /// <summary>
    /// The per-session delivery-accountability surface both drivers share:
    /// it owns the engine (re-created when <c>ProtocolInfo</c> settles the
    /// negotiated version — per connection, never at construction), the
    /// violation policy, and the quarantine latch, and maps decoded wire
    /// facts onto engine calls (the paired epoch/seq baseline refusals the
    /// reshaped engine API cannot see). Fed from <c>FramePipeline</c>, the
    /// one translate point both drivers call.
    /// </summary>
    internal sealed class DeliveryGate
    {
        /// <summary>
        /// True while the quarantine policy suppresses the room's game
        /// data (a violation latched it; the next authoritative rebaseline
        /// or session end clears it).
        /// </summary>
        internal bool Quarantined => _quarantined;

        private readonly DeliveryViolationPolicy _policy;
        private DeliveryAccountability _engine = new DeliveryAccountability(false);
        private bool _protocolInfoSeen;
        private bool _protocolV3;
        private bool _quarantined;

        /// <summary>Initializes the gate with the configured policy (default quarantine).</summary>
        internal DeliveryGate(DeliveryViolationPolicy policy)
        {
            _policy = policy;
        }

        /// <summary>
        /// Swaps the engine for the negotiated version — once per
        /// connection: a same-version <c>ProtocolInfo</c> re-echo is
        /// absorbed (the cursors and gap ledger survive), a genuinely
        /// different echo re-swaps so the engine matches the negotiated
        /// floor.
        /// </summary>
        internal void OnProtocolInfo(uint? negotiatedProtocolVersion)
        {
            bool negotiatedV3 = (negotiatedProtocolVersion ?? 0) >= 3;
            if (_protocolInfoSeen && negotiatedV3 == _protocolV3)
            {
                return;
            }

            _protocolInfoSeen = true;
            _protocolV3 = negotiatedV3;
            _engine = new DeliveryAccountability(_protocolV3);
        }

        /// <summary>Clears the room-scoped cursors and the quarantine latch (room exit).</summary>
        internal void ResetRoom()
        {
            _engine.ResetRoom();
            _quarantined = false;
        }

        /// <summary>Observes the terminal socket outcome and ends the session's latch.</summary>
        internal void ObserveTerminal()
        {
            _engine.ObserveTerminal();
            _quarantined = false;
        }

        /// <summary>Feeds a room snapshot (join) as the authoritative sender baseline.</summary>
        internal GateVerdict RebaselineSnapshot(IReadOnlyList<PlayerInfo> players)
        {
            List<SenderBaseline>? mapped = MapRoster(players, "snapshot", out string? failure);
            if (failure != null)
            {
                return Refuse(failure, baseline: true);
            }

            GateVerdict verdict = Settle(
                _engine.RebaselineSnapshot(mapped, out string? diagnostic),
                diagnostic
            );
            return Accept(verdict);
        }

        /// <summary>
        /// Feeds a reconnect snapshot and its sender watermarks as the
        /// authoritative rebaseline.
        /// </summary>
        internal GateVerdict RebaselineReconnected(
            IReadOnlyList<PlayerInfo> players,
            IReadOnlyList<SenderWatermark>? watermarks
        )
        {
            List<SenderBaseline>? mapped = MapRoster(
                players,
                "reconnect snapshot",
                out string? failure
            );
            if (failure != null)
            {
                return Refuse(failure, baseline: true);
            }

            GateVerdict verdict = Settle(
                _engine.RebaselineReconnected(mapped, watermarks, out string? diagnostic),
                diagnostic
            );
            return Accept(verdict);
        }

        /// <summary>Feeds a joined player's incarnation baseline.</summary>
        internal GateVerdict NotePlayerJoined(in PlayerInfo player)
        {
            string? failure = MapBaseline(player, "PlayerJoined", out SenderBaseline baseline);
            if (failure != null)
            {
                return Refuse(failure, baseline: false);
            }

            if (!_protocolV3)
            {
                // The v2 wire omits stamps: the roster note is inert.
                return default;
            }

            return Settle(_engine.NotePlayerJoined(baseline, out string? diagnostic), diagnostic);
        }

        /// <summary>Feeds a departed player's terminal watermark.</summary>
        internal GateVerdict NotePlayerLeft(Guid playerId, uint? epoch, ulong? finalSeq)
        {
            return Settle(
                _engine.NotePlayerLeft(playerId, epoch, finalSeq, out string? diagnostic),
                diagnostic
            );
        }

        /// <summary>Feeds a reconnected player's incarnation boundary.</summary>
        internal GateVerdict NotePlayerReconnected(Guid playerId, uint? epoch)
        {
            return Settle(
                _engine.NotePlayerReconnected(playerId, epoch, out string? diagnostic),
                diagnostic
            );
        }

        /// <summary>
        /// Feeds one GameData stamp; <paramref name="disposition"/> says
        /// whether the payload reaches the application. While quarantined,
        /// healthy room game data is suppressed too (until the next
        /// authoritative rebaseline).
        /// </summary>
        internal GateVerdict RecordGameData(
            in IncomingGameData gameData,
            out GameDataDisposition disposition
        )
        {
            bool accepted = _engine.RecordGameData(
                gameData.FromPlayer,
                gameData.Seq,
                gameData.Epoch,
                gameData.WireClass,
                gameData.WireKey,
                out disposition,
                out string? diagnostic
            );
            GateVerdict verdict = Settle(accepted, diagnostic);
            if (accepted && !verdict.Suppress && !verdict.Teardown)
            {
                if (disposition == GameDataDisposition.Stale)
                {
                    // Trailing pre-epoch data: consume the stamp, drop the payload.
                    verdict.Suppress = true;
                }
                else if (_quarantined && _policy == DeliveryViolationPolicy.Quarantine)
                {
                    verdict.Suppress = true;
                }
            }

            return verdict;
        }

        /// <summary>Feeds a delivery report's gaps and counters.</summary>
        internal GateVerdict RecordReport(in DeliveryReportMessage report)
        {
            return Settle(
                _engine.RecordReport(report.Gaps, report.PerClass, out string? diagnostic),
                diagnostic
            );
        }

        /// <summary>Feeds one relay-stats interval.</summary>
        internal GateVerdict RecordRelayStats(in RelayStatsMessage stats)
        {
            return Settle(
                _engine.RecordRelayStats(
                    stats.IntervalMs,
                    stats.SentToYou,
                    stats.DroppedForYou,
                    stats.BackpressureEvents,
                    out string? diagnostic
                ),
                diagnostic
            );
        }

        /// <summary>
        /// Feeds an <c>Error(UNSUPPORTED_GAME_DATA_FORMAT)</c> advisory:
        /// accepted only after a causal report armed the expectation.
        /// </summary>
        internal GateVerdict ObserveUnsupportedFormatError()
        {
            return Settle(
                _engine.ObserveUnsupportedFormatError(out string? diagnostic),
                diagnostic
            );
        }

        /// <summary>A refusal through the configured policy.</summary>
        private GateVerdict Refuse(string diagnostic, bool baseline)
        {
            if (_policy == DeliveryViolationPolicy.Disconnect)
            {
                return new GateVerdict
                {
                    Suppress = true,
                    Teardown = true,
                    Diagnostic = diagnostic,
                };
            }

            if (_policy == DeliveryViolationPolicy.Quarantine)
            {
                _quarantined = true;
                return new GateVerdict { Suppress = true, Diagnostic = diagnostic };
            }

            /*
                Observe keeps the documented delivery behavior, except an
                authoritative baseline that fails validation: a broken
                roster must never replace the client's cursors.
            */
            if (baseline)
            {
                return new GateVerdict { Suppress = true, Diagnostic = diagnostic };
            }

            return new GateVerdict { Diagnostic = diagnostic };
        }

        /// <summary>Clears the quarantine latch when a baseline was accepted.</summary>
        private GateVerdict Accept(GateVerdict verdict)
        {
            if (!verdict.Suppress && !verdict.Teardown)
            {
                _quarantined = false;
            }

            return verdict;
        }

        /// <summary>
        /// Settles an engine call through the policy: an acceptance
        /// proceeds; a refusal becomes the policy's verdict.
        /// </summary>
        private GateVerdict Settle(bool accepted, string? diagnostic)
        {
            if (accepted)
            {
                return default;
            }

            return Refuse(diagnostic!, baseline: false);
        }

        /// <summary>
        /// Maps a decoded player onto an engine baseline. On v3 the stamps
        /// are mandatory (the paired refusal the reshaped engine API
        /// cannot see); on v2 the wire must omit them.
        /// </summary>
        private string? MapBaseline(
            in PlayerInfo player,
            string source,
            out SenderBaseline baseline
        )
        {
            baseline = default;
            if (_protocolV3)
            {
                if (!player.Epoch.HasValue || !player.Seq.HasValue)
                {
                    return "delivery accountability violation: v3 "
                        + source
                        + " omitted paired epoch/seq baseline for "
                        + player.Id;
                }

                baseline = new SenderBaseline(
                    player.Id,
                    player.Epoch.GetValueOrDefault(),
                    player.Seq.GetValueOrDefault()
                );
                return null;
            }

            if (player.Epoch.HasValue || player.Seq.HasValue)
            {
                return "delivery accountability violation: v2 "
                    + source
                    + " exposed delivery baseline ("
                    + FormatOptionalUInt(player.Epoch)
                    + ", "
                    + FormatOptionalULong(player.Seq)
                    + ") for "
                    + player.Id;
            }

            return null;
        }

        /// <summary>
        /// Maps a decoded roster onto engine baselines. On v3 the stamps
        /// are mandatory (the paired refusal the reshaped engine API
        /// cannot see); on v2 the wire must omit them and the roster maps
        /// to no senders — the engine's v2 floor is the empty roster.
        /// </summary>
        private List<SenderBaseline>? MapRoster(
            IReadOnlyList<PlayerInfo> players,
            string source,
            out string? failure
        )
        {
            List<SenderBaseline> mapped = new List<SenderBaseline>(players.Count);
            for (int i = 0; i < players.Count; i++)
            {
                failure = MapBaseline(players[i], source, out SenderBaseline baseline);
                if (failure != null)
                {
                    return null;
                }

                if (_protocolV3)
                {
                    mapped.Add(baseline);
                }
            }

            failure = null;
            return mapped;
        }

        private static string FormatOptionalUInt(uint? value)
        {
            return value.HasValue
                ? value.GetValueOrDefault().ToString(CultureInfo.InvariantCulture)
                : "None";
        }

        private static string FormatOptionalULong(ulong? value)
        {
            return value.HasValue
                ? value.GetValueOrDefault().ToString(CultureInfo.InvariantCulture)
                : "None";
        }
    }
}
