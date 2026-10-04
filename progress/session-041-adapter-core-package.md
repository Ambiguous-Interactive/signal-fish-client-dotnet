# Session 041 — M8: shared adapter-core package hoist

Date: 2026-10-04
Branch: `adapter-core-package`
Closes: #83

## What shipped

**Issue #83** (filed by session 040): the FishNet and Mirror adapter
cores — mechanical twins (same 18-byte header bytes, same RFC-4122 UUID
spelling, same peer router, same receive rules, same MTU math) — are now
one shared UPM source package:

- `unity/Adapters/Core` (`com.ambiguous-interactive.signalfish.adapters.core`,
  `SignalFish.Adapters.Core` asmdef, `noEngineReferences: true`,
  namespace `SignalFish.Client.Adapters`): `AdapterWire`, `AdapterMtu`,
  `SignalFishPeerRouter` (`HostConnectionId`; FishNet renames from
  `HostClientConnectionId`), `SignalFishReceiveRules` +
  `AdapterFrameRoute`. The delivery-channel bytes stay in the shared
  wire (both pinned engines spell reliable=0/unreliable=1 — verified
  against FishNet 4.0.0 and the Mirror v96.9.23 source
  `Assets/Mirror/Core/Tools/Utils.cs`), and each bridge must restate
  its engine's channel pin in place, checked by the lint.
- Both adapter packages depend on the core package and their asmdefs
  reference the core assembly; Mirror's `Runtime/Core` is gone entirely,
  FishNet keeps only its engine-specific `HostLoopback` there.
- `lint-unity-adapter.ps1` gains a shared-core lane (Unity-floor compile,
  asmdef honesty: no define gates, `noEngineReferences`), a duplication
  pin (no adapter may declare the shared types — a fork fails the lint),
  required core references (asmdef assembly + package.json dependency),
  and the channel-pin bridge check. A negated `#if !<define>` no longer
  counts as a guard. `-Adapter Core` scopes a run. Self-test rewritten
  (58 assertions) with the new failure shapes; the lint also became
  robust to malformed asmdef and package.json files (missing JSON keys
  no longer crash it — three PowerShell failure classes folded into
  `powershell-tooling`).
- Tests: the twin batteries collapse — the wire/MTU/router/rules set
  ran twice (once per adapter); it now runs once under
  `tests/.../Adapters/Core/`. Suite: 856 per TFM (down 35 duplicated
  cases), `--filter Adapters` = 35.

## Decisions

- **Channel bytes stay in the shared wire**: they are the header
  format's delivery classes; the *engine* mapping is pinned per adapter
  in the bridge (a doc comment naming `Channel.Reliable = 0, ...` /
  `Channels.Reliable = 0, ...` at the pinned engine version) and checked
  by name presence, like the pinned Transport members. An engine
  renumber must now be re-verified to pass CI.
- **Duplication pin over absence pin**: adapters may keep engine-local
  cores (FishNet's loopback); the lint fails only declarations of the
  shared five types outside the core package — the actual failure mode
  M8.3-M8.6 would compound.
- **noEngineReferences: true on the core asmdef**: engine-freeness is
  enforced by Unity's own reference model, not just review.

## Verification

- Adversarial review round: six findings, all fixed — samples-less
  package.json crashed the lint (both lanes), `record`/`interface`
  forks slipped the duplication pin, a negated `#if !<define>` counted
  as a guard, the UPM core dependency was unpinned, session-log test
  arithmetic (31 → 35), and a dynamically-scoped lint variable. Each
  fix carries a self-test assertion (58 total).
- `dotnet build` + `dotnet test`: green on both TFMs (856/856, three
  consecutive runs; one unrelated first-run flake did not recur).
- `lint-unity-adapter.ps1`: full lane green (shared core compiles
  standalone; duplication pin holds; both bridges member-complete).
- `scripts/tests/run-all.ps1`: all 15 self-test files pass (58
  assertions in the rewritten adapter lint test).
- `lint-conventions.ps1`, `lint-file-sizes.ps1`,
  `lint-llm-instructions.ps1`, `sync-unity-package.ps1 -Check`: clean.

## Follow-ups

- M8.3 Wave 1 NGO + Unity Relay is the next PLAN surface — it now
  builds on the shared core instead of cloning it.
- M8.7 live validation runbook covers the packages (licensed seat).
- Watch item: one first-run test flake (net8.0, did not recur in three
  full-suite reruns); if it surfaces in CI, RCA there.
- Dependency lane: dependabot now ignores NUnit ≥ 5.0.0 — FsCheck.NUnit
  3.4.0 (latest) pins NUnit < 5.0.0, so bump PR #86 cannot go green.
  RCA + re-enable steps in #88; coverlet 10.1.0 rides the next group.
