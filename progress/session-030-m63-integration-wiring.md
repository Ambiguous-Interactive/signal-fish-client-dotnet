# Session 030 — M6.3 integration: delivery accountability wired

Date: 2026-10-02. Scope: drive M6.3 integration (#61) — resolve the #66
design blocker, wire the delivery-accountability engine into the shared
pipeline behind a violation policy, and carry the deliverable to a green
PR.

## Starting state

- `main` green (dotnet/Docs/LLM Context/e2e on #65); the 2026-09-28
  scheduled fuzz failure was already RCA'd and fixed by #65 — no new CI
  debt. No open PRs. Two open issues: #61 (M6.3 integration) and #66
  (the JSON-GameData stamp design question blocking it).
- The engine (`V3/DeliveryAccountability`, session 027) had zero
  production call sites.

## Delivered

- **#66 resolved from the sources, not guessed.** The Rust client's
  `ServerMessage::GameData` decodes optional `seq`/`epoch`; the server
  stamps every relayed frame (JSON and binary) with a per-sender stamp
  and strips them only for pre-v3 recipients (`websocket/sending.rs`
  materializer, fail-closed `gated_relay_stamps` for v3). The C# decode
  simply never read the fields. `IncomingGameData` now decodes both
  stamps plus wire presence for `class`/`key` (the v2 floor requires
  absent metadata; the public `Class`/`Key` normalizing behavior is
  unchanged, presence rides internal `WireClass`/`WireKey`).
- **The gate.** New internal per-session `V3/DeliveryGate` owns the
  engine, the policy, and the quarantine latch, and maps decoded facts
  onto engine calls: `ProtocolInfo` settles the negotiated version
  (engine swap, once per version — same-version re-echoes are absorbed
  so cursors survive the state machine's legitimate replacement
  semantics), join/reconnect snapshots rebaseline (the paired epoch/seq
  refusals the reshaped engine API cannot see live here), roster
  lifecycle notes, `GameData` stamps gate the payload, reports and
  relay stats record, and `Error(UNSUPPORTED_GAME_DATA_FORMAT)` arms
  its causality check.
- **Pipeline wiring.** `FramePipeline` feeds the gate at the one
  translate point both drivers call. A refused frame surfaces a
  violation event (with diagnostic, new `PollEvent.Diagnostic`) ahead
  of its own event; policy decides the rest — `Quarantine` (default)
  suppresses the frame and latches the room's game data until the next
  authoritative rebaseline (`ClientSnapshot.Quarantined`),
  `Disconnect` ends the session terminally (never reconnected, Rust
  parity), `Observe` keeps frames flowing except failed baselines.
  Stale frames never reach the application. Both drivers observe the
  terminal outcome at teardown; the polling ring reserves two slots
  (terminal + two-event frame), so a violation can never evict the
  disconnect event.
- **Verification.** 674 unit tests green on net8.0 + net10.0, including
  17 new gate tests (policy matrix, latch lifecycle, re-echo cursor
  continuity, v2-floor refusals, pipeline translation paths) and a
  Translate-level allocation gate proving decode + gate + engine stays
  0 B on the relay hot path. The v3 delivery-classes E2E now also
  asserts the live stamps arrive, no violation surfaces, and the room
  never quarantines.

## The bug the tests caught

The first wiring mapped v2 rosters onto zeroed `SenderBaseline` values
and fed them to the engine, which refuses any non-empty v2 roster —
every v2 join refused, caught by the fixture sweep as 38-47 reds. The
adversarial-review sub-agent then caught that the fixture "fix" (empty
rosters) had worked around the product bug; the gate now skips unmapped
senders on v2 (the engine's floor is the empty roster) and the fixtures
returned to the golden SSOT. Lesson recorded: when a sweep of tests
fails for "one reason", check whether the fix is in the product or the
tests before editing thirty files.

## Adversarial review outcomes (all fixed in f0a5454)

- `eventCapacity` 1-2 made `Poll()` permanently consume zero frames
  after the two-slot reservation — options now validate the floor,
  pinned data-driven.
- A `ProtocolInfo` re-echo rebuilt the engine and blinded the room;
  same-version echoes are now absorbed (pinned).
- Reconnect-baseline diagnostics now say "reconnect snapshot" (Rust
  parity); duplicated teardown call removed; fixture SSOT restored;
  CHANGELOG documents the policy surface and the two behavior changes.

## Not done here (deliberately)

- The live gap/report/stale E2E leg: `RelayStats` is opt-in server
  config (off in CI) and gaps/reports need server-side scripting — that
  leg is the M6.6 scripted-scenario work; the conformance row stays
  open with a narrower note.
- The polling client's receive path allocates a `Task` per frame
  (pre-existing `ReceiveAsync().AsTask()`); the allocation gate pins
  the M6.3 surface instead. Filed as follow-up.
