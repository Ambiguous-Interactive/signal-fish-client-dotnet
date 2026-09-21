# Session 017 — Client snapshot (M3.5) + issue-debt zeroing

Date: 2026-09-22. Branch: `client-snapshot-m3.5`.

## Scope

Advance the plan to M3.5 (red-green) and drive open-issue debt to zero
(#17, #42, #23). PR CI time flat.

## Delivered

- **M3.5 — `ClientSnapshot` + accessors**: `SignalFishStateMachine.CreateSnapshot()`
  and `SignalFishPollingClient.Snapshot` return one coherent readonly-struct
  view of the session: the Rust-docs phase table (data-driven test over all
  six rows, including terminal), the confirmed membership identity, and the
  latest reconnection token. Red-green anchored by
  `ClientSnapshotTests` (phase table, membership mirror, token lifecycle
  table, fence coherence, equality, ToString redaction).
- **Wire-truth sweep (same-class fix)**: landing the snapshot exposed that
  the server's `reconnection_token` — delivered on `RoomJoined`/`Reconnected`
  for v3+ deployments (server `docs/protocol.md`) — was silently dropped by
  the v2-floor decoders (unknown-field tolerance). The whole failure class
  is now handled: both decoders capture the token (repeated/non-string
  rejected fail-closed; explicit JSON null = absent), the state machine
  rotates it on reconnect and clears it on spectator baselines (no
  spectator reconnect), confirmed exits, and terminal teardown — matching
  Rust `set_room`/`clear_room`/`clear_session` semantics. `ToString`
  redacts it: a bearer seat credential can never leak through logs.
- **#17 closed (auto-release + operator runbook)**: `release.yml` gains an
  opt-in nuget.org publish step (`NUGET_API_KEY` secret; skips itself while
  absent, so nothing changes until populated — tag-only workflow, PR CI
  untouched). New `docs/releasing.md`: what a tag triggers, the two-row
  secrets table, cut-a-release steps, post-run checks, troubleshooting
  (immutable versions, snupkg-by-design, re-release rules). Answers the
  issue's "can we auto-publish" ask: yes — tag → GitHub Packages always,
  nuget.org once one secret exists.
- **#42 folded into PLAN (SSOT) and closed**: the OpenUPM + NPM asks are now
  explicit M9.2 scope (with the M7.1 sequencing note), so the issue tracked
  no unique work. Closed with a pointer to PLAN.
- **#23 closed (SSOT/KISS/SOLID)**: audit found the doctrine structurally
  enforced (pointer files, generated index, server-wins fact chain, six
  convention lints) but never stated as agent-applicable rules. Added the
  "Design Principles" block to `.llm/context.md`, each principle bound to
  its existing enforcement; audit logged in `improvement-log.md`.

## CI time

- PR CI job set byte-identical: docs.yml unchanged (runbook is a static
  page, already covered by the mkdocs build); release.yml is tag-only.
- Coverage grew with the suite (15 new tests), no gate weakened.

## Verification

- `dotnet build -warnaserror` clean; 429 tests green on net8.0 + net10.0
  (was 414: +15, zero removed).
- All six convention lints, file-size lint, LLM-instructions lint, skills
  index freshness, automation self-tests, csharpier check green.
- Rust parity hand-verified against `client.rs`/`client_core.rs`
  (`ClientSnapshot` struct, `set_room`, `clear_room`, `clear_session`).

## Leftovers

- M3.6 (Docker E2E checklist) is the next plan surface; the command-send
  surface rides with it (as noted in M3.4).
- M4.4 can now consume `Snapshot.ReconnectionToken` for the persisted
  reconnect triple.
