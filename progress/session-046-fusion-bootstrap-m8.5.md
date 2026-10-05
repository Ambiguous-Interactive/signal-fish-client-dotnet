# Session 046 — Fusion session bootstrap (M8.5, the Fusion half)

Date: 2026-10-05
Branch: `fusion-bootstrap-m8.5`

## What shipped

**M8.5 Wave 2, Fusion half** — `SignalFishFusionBootstrap`, the
session-name exchange, shipped as the UPM source package
`com.ambiguous-interactive.signalfish.adapters.fusion`
(`unity/Adapters/Fusion`), closing issue #95 and completing M8.5:

- Host: Signal Fish join → authority → `NetworkRunner.StartGame`
  (`GameMode.Host`, session name, `PlayerCount`) → publish
  `{"signal_fish_fusion_session": "<name>"}` over the room's game-data
  lane → ready + start the game. Every later join re-publishes, so late
  joiners never depend on timing.
- Client: Signal Fish join by code → ready → wait for the published
  name on the same lane → `NetworkRunner.StartGame` (`GameMode.Client`,
  `EnableClientSessionCreation = false`) with that name. The bootstrap
  performs every Fusion call itself (Unity calls staged onto the main
  thread; Fusion's awaitable start collapses PUN2's phase machine into
  one staged kick + task continuation).
- The host's session name defaults to the Signal Fish room code.
- Split-brain guard is Fusion's own matchmaking contract, verified in
  the docs: `GameMode.Client` with a specific session name joins and
  never creates; the args name it explicitly anyway.
- Honest tier, stated in the docs: a Photon cloud session has no host
  accept path to gate, so this adapter is matchmaking plus the name
  exchange — membership enforcement stays the game's job.
- The runner lives on a bootstrap-owned `SignalFishFusionRunner`
  GameObject; teardown shuts down only what the bootstrap created.
  Start failures settle on Fusion worker threads and are marshalled to
  the captured `SynchronizationContext` before any Unity teardown.

## How the surface was pinned

Every pinned engine member was verified against real Fusion 2 sources,
not memory: the doc API (`StartGameArgs` field list, Fusion 2.0.12), a
decompiled Fusion 2 runtime dump (`GameMode`, `StartGameResult`,
`INetworkRunnerCallbacks`, `ShutdownReason`, `NetDisconnectReason`,
`NetConnectFailedReason`, `NetworkRunner.StartGame`/`Shutdown`
signatures), and a real SDK import (`Assets/Photon/Fusion`: precompiled
DLLs with `isExplicitlyReferenced: 0`, the SDK's own
`FusionBootstrap.InitializeNetworkRunner` call shape, `package.json`
inside Assets — proving `versionDefines` cannot fire). Facts that
shaped the adapter:

- `StartGame` returns `Task<StartGameResult>` (`Ok`, `ErrorMessage`) —
  failures need no callback phase machine.
- `args.SceneManager == null` falls back inside `StartGame` (reflection
  default or `NetworkSceneManagerDummy`) — the bootstrap passes none.
- Precompiled plugin DLLs do not appear in
  `CompilationPipeline.GetAssemblies()`, so the detector probes
  `AppDomain.CurrentDomain.GetAssemblies()` for `Fusion.Runtime`.
- The adapter namespace shadows the engine namespace
  (`SignalFish.Client.Adapters.Fusion` vs `Fusion`), so `using
  global::Fusion;` is load-bearing — the same bug class the lane caught
  in #92, caught again by the lane on first compile.

## Validation

- `dotnet test`: 12 new envelope contract tests (round-trip, byte-exact
  shape, foreign payloads decode as absent, charset bounds, Fusion GUID
  session names); full suite green locally on net8.0.
- `lint-unity-adapter`: all six packages green, including the new
  Fusion bridge-compile lane; `test-lint-unity-adapter` self-tests
  extended with the Fusion fixture across all negative loops (100
  assertions).
- lint-conventions and the remaining self-test suites green; docs page
  (`docs/adapters/fusion.md`) + nav entry added; CHANGELOG entry added.

## Open issues triage

- #88 (FsCheck.NUnit pins NUnit < 5): re-verified the upstream
  constraint this session — no FsCheck.NUnit release with an NUnit 5
  constraint yet; the issue's wait-plan stands, no repo change.
- #80 (mkdocs extensions when first used): explicitly no-action by its
  own terms; the fusion docs page uses no new extensions.

## Follow-ups

- M8.7 per-adapter validation runbook gains its Fusion entry when the
  licensed Unity seat lands (M7.4 block).
- Fusion SDK version drift: the pin is the 2.x surface verified
  2026-10-05; a Fusion upgrade that grows `INetworkRunnerCallbacks`
  breaks the editor compile (an added interface member has no
  implementation) — the runbook item records re-pinning the stub lane
  with it.
