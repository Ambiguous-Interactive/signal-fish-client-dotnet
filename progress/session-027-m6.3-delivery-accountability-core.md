# Session 027 — M6.3 core: delivery accountability + v3 delivery decodes

Date: 2026-09-23. Scope: advance PLAN to the next surface (M6.3,
DeliveryReport accounting — the hardest M6 task), drive down issue debt,
and cut the local red-green loop further. One deliverable PR.

## Starting state

- main == origin/main (cb93230), clean; all four workflows green.
- Open issues: #58 (unify Signal/ConnectionInfo payload depth bounds with
  the game-data contract).
- A pre-work sweep of the v3 surface found three silent decode gaps, filed
  and fixed in this session (see "Issue debt").

## Delivered

- **M6.3 core — delivery accountability engine (red-green)**
  - `V3/DeliveryAccountability` is a faithful C# port of the Rust
    reference client's `accountability.rs` (pinned
    da8f2d16): per-sender epoch/seq baselines, exact gap reports with
    range-union coverage, departed-incarnation retirement, stale
    suppression across lifecycle overtakes, reconnect watermark
    rebaselining, the 16/16/1024 resource bounds, RelayStats
    monotonicity, and unsupported-format advisory causality. Every
    validation runs before any mutation; a refused frame leaves the
    machine untouched and returns a typed diagnostic.
  - The executable spec is the Rust test module: 26 scenarios ported to
    `tests/V3/DeliveryAccountingTests.cs` (private-state introspection
    replaced with behavioral probes; scenario granularity preserved).
  - The allocation gate caught a real bug on day one: a
    `RemoveAll(lambda)` in the epoch-transition path hoisted a 24-byte
    closure onto every `RecordGameData` call. Replaced with an explicit
    in-place compaction; steady-state advances are now 0 B
    (`RecordGameDataSteadyStateAdvancesAllocatesNothing`).
  - Wire value types live in `Protocol` (SSOT with the decode surface);
    the engine owns only `GameDataDisposition` + `SenderBaseline`.
- **v3 delivery decode surface** (found via the sweep; fixed same
  session)
  - `PlayerInfo` decodes optional `epoch`/`seq` (v3 baselines);
    `PlayerLeft` decodes `epoch`/`final_seq`; `PlayerReconnected` decodes
    `epoch`; `Reconnected` decodes `sender_watermarks`. Explicit JSON
    null = absent everywhere; repeated keys and wrong-typed values are
    rejected; v2 frames decode unchanged.
  - `DeliveryReport` (per-class counters + exact gaps with reason
    tokens), `RelayStats`, and `GoingAway` payloads decode against the
    golden v3 fixtures and surface as `PollEvent`s from the shared
    pipeline — malformed frames become violations. `RelayStats` was
    previously unroutable (missing from `MessageKind`); `DeliveryReport`
    and `GoingAway` were silently absorbed. `TryReadUInt64` added to the
    scanner (overflow-bounded, rejects negatives/fractions).
  - Engine/decoder integration (violation policy, quarantine state,
    GoingAway handling) is deliberately NOT in this PR — tracked in the
    follow-up issue so the engine lands fully verified instead of
    half-wired.
- **Issue #58 — one depth contract for verbatim payloads**
  - `EnvelopeWriter.MaxVerbatimPayloadDepth` (128) is now the single
    outbound JSON depth constant. `SignalMessage` and
    `ProvideConnectionInfoMessage` validate their payloads at
    construction (the same treatment `GameDataMessage` already had), and
    the writer no longer re-walks any payload on encode. An O(1)
    emptiness guard in all three writers closes the `default(T)`-struct
    corruption hole (an empty payload would emit invalid JSON).
    Data-driven boundary tests pin accept-at-limit / refuse-one-deeper
    for each payload type.
- **Local iteration speed (round 2)**
  - `fast-check.ps1`: iteration builds skip Roslyn analyzers (the gate
    and CI still run them), tests run via `dotnet vstest` on the built
    DLL (no msbuild in the test step), and the real-socket
    `TransportLoopback` fixture skips itself in the fast lane (adapter
    `TestCaseFilter` negation is unreliable — `TestCategory!=X` runs
    everything — so the lane sets a marker variable the fixture's
    `OneTimeSetUp` obeys; `-IncludeLoopback` opts back in).
  - Measured on a warm tree: full unit suite 23.4 s → 11.3 s; a
    filtered red-green loop → ~5 s (was ~20 s via `dotnet test`). CI is
    untouched and still runs everything.

## Issue debt

- Closed #58 (this PR).
- Opened + closed: v3 delivery baselines silently dropped from room
  snapshots/reconnects (fixed by the decode surface above).
- Opened + closed: RelayStats unroutable; DeliveryReport/GoingAway
  absorbed without surfacing (fixed same session).
- Opened (follow-up): M6.3 integration — wire the engine into the shared
  pipeline behind a violation policy (Quarantine/Disconnect/Observe),
  quarantine state on the snapshot, GoingAway handling, E2E.

## Verification

- `dotnet build` (both TFMs, analyzers, -warnaserror): 0 warnings.
- `dotnet test`: 655/655 on net8.0 and net10.0 (was 578; +77 tests).
- `scripts/lint-conventions.ps1`, `csharpier -- check .`,
  `scripts/tests/run-all.ps1`: clean.
- CI time: workflows unchanged; the new fixtures are data-driven and
  cheap (accountability suite ≈ 150 ms, decode suite ≈ 250 ms) — matrix
  wall time flat within noise.

## Left for next rounds

- M6.3 integration (the follow-up issue): pipeline wiring, violation
  policies, quarantine, GoingAway, E2E; then M6.4 binary game data.
- NUnitLite in-process runner could take the fast lane to ~7 s; skipped
  this round to avoid touching the test project's output shape.
