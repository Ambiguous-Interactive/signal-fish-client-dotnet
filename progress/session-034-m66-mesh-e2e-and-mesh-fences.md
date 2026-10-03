# Session 034 — M6.6: live mesh E2E + the mesh session fences

Date: 2026-10-03. Scope: the M6.6 milestone item — the three worked v3
sessions (`v3-mesh-webrtc`, `v3-host-topology`, `v3-host-failover`) as
live conformance scenarios, plus issue #71's deferred Rust behaviors
(retired-generation plan fence, inbound stale-signal suppression).
Routine drift check first: origin/main at 195a406 (M6.5 merged as #72),
working tree clean, no open/draft PRs, one open issue (#71).

## Starting state

- The mesh client surface (M6.5) decoded and sent every v3 mesh kind,
  but the machine applied plans naive latest-wins (a superseded plan
  replay could overwrite the current one) and every decodable `Signal`
  surfaced to the consumer, stale or not.
- The E2E suite covered the v2 floor + v3 negotiation/delivery; no
  SessionPlan had ever been observed live. The server's desired-topology
  ceiling defaults to `relay`, so no plan can be emitted without a
  deployment setting.

## Delivered

- **Retired-generation plan fence** (`SignalFishStateMachine`): a bounded
  ring (8) of recently superseded generations, retired on every
  generation change; `FramePipeline` rejects a plan whose generation sits
  in the fence as a `ProtocolViolation` naming the kind, and the fact
  never applies (latest-wins by authority, not by arrival — Rust
  `validate_session_plan` parity). Replays older than the fence degrade
  to fresh plans; every membership baseline clears the fence.
- **Inbound stale-signal suppression** (`FramePipeline` + machine):
  `ShouldSuppressInboundSignal` absorbs a `Signal` as a benign relay
  race when no authoritative plan has arrived, the generation is stale,
  or the sender was retired by the live generation. Retirement arms on
  `PlayerLeft` (webrtc sessions only) and same-generation re-plan drops,
  disarms on `NewPeer` and on any plan naming the peer. Suppressed
  frames surface nothing — no event, fact, or violation.
- **Roster feeds ride accepted frames**: `OnPlayerLeft` runs only when
  the delivery gate doesn't refuse the frame (Rust parity: a
  gate-refused departure updates no client state).
- **Async lock confinement**: `ProcessFrameAsync` now translates and
  applies the fact inside one critical section, so a frame can never
  straddle a reconnect's machine swap and the mesh feeds can't interleave
  with `Sever`'s teardown (adversarial-review fix).
- **Live mesh conformance** (`MeshConformanceTests`, 4 scenarios): mesh
  + WebRTC signaling with the lesser-UUID glare rule verified, verbatim
  signal relay under the shared generation, `TransportStatus` fan-out,
  the relay floor flowing before/through/after the fallback report; the
  host star (authority elected, clients signal only the host); host
  failover (departure, re-election by join order, sticky topology,
  fresh generation, new star edge, relay continuity); and the explicit
  relay-floor plan a relay-only room publishes at finalize.
- **Live binary game data** (`BinaryGameDataRoundTripsOnALiveV3Room`):
  closes the M6.4 promise — two async clients negotiate MessagePack and
  round-trip binary payloads with accountability stamps.
- **Deployment knob**: the e2e workflow and `run-e2e.ps1` set
  `SIGNAL_FISH__SESSION__DEFAULT_TOPOLOGY='mesh'` — the ceiling that
  admits the v3 rungs; relay-only advertisements floor exactly as
  before (verified against the server's ladder and pre-gather gates).

## Scope decision (documented divergence)

The Rust client's further lifecycle-validation arm — classifying a
surviving signal as a violation when the transport is not WebRTC or the
sender is not a session peer — is deliberately not ported. Every
surviving signal surfaces as a typed event and the consumer filters by
its mesh view (the documented consumer-side contract); no other .NET
mesh kind carries the Rust lifecycle layer either, so a partial port
would classify inconsistently. The machine doc carries the scope note;
revisit if the lifecycle layer lands for other kinds.

## Verification

- `dotnet build -warnaserror` clean; 826 unit tests green on net8.0 and
  net10.0 (12 new fence tests + the suppression table); all six
  convention lints green; `scripts/tests/run-all.ps1` green.
- The E2E suite (18 scenarios) skips cleanly without a server; the live
  mesh scenarios validate on the CI e2e lane against the real server
  (Docker unavailable locally).

## Deferred (tracked)

- Live-server reconnection drill (the last open conformance item).
- The generation-less Server-0.4 inbound `Signal` decodes as a violation
  rather than Rust's benign absorb (decoder strictness predates this
  change; legacy-only).
