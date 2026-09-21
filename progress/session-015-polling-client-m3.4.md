# Session 015 — Polling client (M3.4) + conventions round

Date: 2026-09-21. Branch: `polling-client-m3.4`.

## Scope

One focused surface: the M3.4 polling client (the next PLAN milestone task),
plus the issue debt it unblocks — #35 (member-order lint, explicitly
scheduled after M3.4), #36 (TUnit spike, same), #26 (final closure), and the
#7 idle-poll allocation gate. CI time must not increase.

## Delivered

- **M3.4 `SignalFishPollingClient`** (correctness/usability):
  - One inbound frame → at most one `PollEvent` (readonly struct union:
    session facts with membership + room snapshot, gameplay payloads,
    violations, forward-compat events). Session facts still flow through
    `SessionEventMapper` → `SignalFishStateMachine` (phase/membership/fence
    parity unchanged; `IsSessionFact` added so a failed mapping of a routed
    session kind is a violation, not silence).
  - Full v2 payload surface as typed structs decoded against the pinned
    `07a6fd08` golden bytes: `AuthenticatedMessage` (+ `RateLimits`),
    `ProtocolInfoMessage`, `LobbyStateChangedMessage`, `PlayerInfo`/
    `SpectatorInfo` + Player* messages, `GameStartingMessage` (+ `PeerConnection`,
    `ConnectionEndpoint` with per-element-optional `connection_info`),
    `AuthorityResponseMessage`/`AuthorityChangedMessage`, spectator roster
    trio, `FailureMessage` (the `reason`/`message`/`error` alias family),
    `IncomingGameData` (verbatim payload), and `RoomSnapshot` shared by
    RoomJoined/SpectatorJoined/Reconnected (rich fields optional-by-absence,
    wrong-typed known keys rejected; `Reconnected.missed_events` skipped —
    element shape unobservable in v2; typed replay lands with M4.4).
  - Heartbeat on the injected clock: ~30 s ping cadence (anchored to the
    last ping, not traffic), 2x-ping liveness timeout → terminal teardown;
    any inbound frame refreshes liveness. Ping send failures fold back into
    the next poll (off-thread completion never mutates session state).
  - Budgets: 64 frames/poll, 64 KiB per-frame bound (oversize/binary →
    violation), 256-event ring with cooperative backpressure (stops
    consuming; never drops — asserted by test).
  - **Idle-poll 0 B allocation gate** (#7): exactly one outstanding receive,
    issued at connect and re-issued only after a delivered frame; idle polls
    only observe completion. Gate test red-checked with a planted allocation.
- **#35 member-order lint** (style): DoxReloaded canonical order (nested
  types, const, static fields, properties, fields, ctors, methods; tiers
  public→protected→internal→private, static-first). String-aware lexer
  (verbatim/raw-string fix over the comment-form lexer), CSharpier-indent
  anchoring, unknown constructs skipped. RED: 47 violation sites/~30 files;
  all fixed mechanically (field-init-order audit clean; 409 × 2 TFM green).
  Self-test: 12 cases / 16 assertions. Hook + CI wired; `.llm/context.md` rule 21.
- **CI time held flat** despite the new gate: the five per-lint CI steps
  collapsed into one consolidated `lint-conventions.ps1` step (six lints in
  one pwsh process — `exit N` from `&`-invoked scripts returns control and
  sets `$LASTEXITCODE`, so every lint still runs and failures are named).
  Pre-commit hook gained the member-order gate the same way.
- **#36 TUnit spike** (measurement only, scratch project outside the repo,
  nothing shipped): 4-test representative port. TUnit test exec 170–203 ms
  vs NUnit 8–11 ms on the same tests; full-suite exec is ~3 s of a ~2 min
  CI wall — the ceiling for any swap is noise. Migration cost: no constraint
  syntax (full fluent/async rewrite of ~3000 assertions) + MTP hard-errors
  under classic `dotnet test` on .NET 10 SDK (runner migration). Decision:
  stay on NUnit; revisit if suite exec ever dominates CI wall.

## Verification

- `dotnet build -warnaserror` 0 warnings; `dotnet test` **409 × {net8.0,
  net10.0}** green (61 new: 28 payload decode, 33 polling/options/ring).
- Idle-poll gate red-checked; payload decodes red-checked (planted bug in
  `AuthorityResponseMessage` null handling → test failed → reverted).
- All six lints green (69 files); `lint-conventions.ps1` consolidated step
  green; CSharpier clean; all 12 self-test files green.

## Left for later

- M3.5 `ClientSnapshot` + accessors (next PLAN surface).
- M3.6 command-send surface + scripted E2E (heartbeat Ping is the only send
  today; server-initiated Ping → client Pong reply also lands there).
- M4.1 buffer spike (#7 tail), M9.4 scheduled bench/budget baselines for the
  polling path.
