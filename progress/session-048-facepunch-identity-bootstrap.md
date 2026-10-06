# Session 048 — Facepunch Steamworks identity bootstrap (M8.6, second half)

Date: 2026-10-06
Branch: `facepunch-bootstrap-m8.6`

## What shipped

**M8.6 Wave 2, Facepunch half** — `SignalFishFacepunchSteamIdentityBootstrap`,
the Facepunch.Steamworks binding of the SteamId64 exchange plus the
fenced Steam P2P socket bootstrap, shipped as the UPM source package
`com.ambiguous-interactive.signalfish.adapters.facepunch`
(`unity/Adapters/Facepunch`):

- The same wire as the Steamworks.NET half: the role-scoped
  `signal_fish_steam_host` / `signal_fish_steam_peer` envelope,
  byte-identical lane keys (the package's envelope copy differs from
  the sibling's by the namespace line only, and the dotnet suite runs
  the full 16-test contract against both copies).
- Host: Signal Fish join → authority →
  `SteamNetworkingSockets.CreateRelaySocket` (Facepunch's auto-accept
  default is overridden) → publish the host SteamId64 over the room's
  game-data lane (re-published on every join) → ready + start the
  game. Incoming Steam connections are accepted only after the peer's
  id arrived on the lane, within a grace window (`AcceptGraceSeconds`),
  and refused with an app-range end reason
  (`NetConnectionEnd.App_Min`) otherwise.
- Client: Signal Fish join by code → ready → wait for the host id on
  the lane → publish its own id → `ConnectRelay` to the published host
  id; the start completes when the Steam connection establishes and
  the connected identity matches the published host id.
- Status changes dispatch from `SteamClient.RunCallbacks`, pumped on
  the bootstrap's tick — Facepunch's manual callback mode
  (`SteamClient.Init(appId, asyncCallbacks: false)`) is a documented
  requirement, because Facepunch's default async mode pumps callbacks
  on a background thread (the adversarial review caught the docs
  instructing the broken default). Message pumping stays the game's
  job: the live `SocketManager`/`ConnectionManager` are exposed
  (`LiveSocketManager`/`LiveConnectionManager`), and the game's
  receive path is Facepunch's own `Interface` hook (an
  `ISocketManager`/`IConnectionManager` whose `OnMessage` gets the
  messages) — the bootstrap owns the lifecycle callbacks, the
  interface only ever sees messages.
- Teardown frees every connection the fence created — pending,
  tracked, and accepted-but-not-yet-established (the adversarial
  review caught the accepted set being left to the listen socket's
  ungraceful sweep) — and nulls the socket's entry in Facepunch's
  static registry (Facepunch never removes entries), so stale
  post-teardown dispatches to the socket become no-ops; the host
  manager's ended-connection close is additionally gated on the
  manager still being the session's current one (a stale dispatch can
  carry a recycled id). Facepunch's connection-side registry residue
  is documented as a known limitation.
- A live-session Steam shutdown is detected (`SteamClient.IsValid`
  checked on the tick — Facepunch's pump is a silent no-op after a
  shutdown and swallows callback exceptions, so the pump's catch is a
  backstop only), a logged-out client fails the start with the right
  diagnosis (nil `SteamId` check, not a publish error), and a refused
  socket/connect surfaces its intended message (Facepunch rejects the
  default handle in its registry setter, so the refusal arrives as an
  `ArgumentException` there, not as a returned default handle).
- Facepunch ships as an asset (precompiled platform DLLs Win32/Win64/
  Posix + native binaries, no UPM package — verified against the
  2.5.2 tag: no `package.json` anywhere in the repo), so the editor
  define detector solely owns `SIGNALFISH_FACEPUNCH` (PUN2/Fusion
  pattern); the asmdef pins the three platform assembly references and
  carries no `versionDefines`.

## How the surface was pinned

Every pinned member was verified against the real Facepunch.Steamworks
2.5.2 sources (cloned from `Facepunch/Facepunch.Steamworks`), not
memory — including the two compile-visible differences from the
Steamworks.NET sibling that memory-based pinning would have missed:

- `ConnectionInfo` exposes no public end-debug string (only
  `State`/`Address`/`Identity`/`EndReason`), so the refusal paths
  report the end reason.
- Neither handle struct exposes a public validity member
  (`Socket.Id` is internal); Facepunch refuses a create/connect by
  handing back the default handle, which its registry setter rejects
  with an `ArgumentException` — so the kicks translate that throw
  into the intended diagnostic.
- The client's own dial posts a Connecting status — Facepunch's
  `ConnectionManager` absorbs it by construction (`Connecting` starts
  true, so the state machine never forwards it as `OnConnecting`); the
  issue's Steamworks.NET own-dial blocker class has no Facepunch
  equivalent, and the bootstrap documents it.
- Facepunch routes status changes by socket/connection id through
  static registries that outlive a session, so every manager callback
  is scoped to the manager instance the current session created
  (stale dispatch / recycled-id guard), teardown nulls the socket's
  registry entry (Facepunch never removes entries) and frees every
  connection the fence created, and the host manager's
  ended-connection close only fires for the current session's
  manager.
- TFMs `netstandard2.1;net6.0;net46` — Unity-compatible via
  netstandard2.1 (2021.2+).

## Validation

- `dotnet test`: 931 green on net8.0 and net10.0 (16 new envelope
  contract tests for the Facepunch envelope copy).
- `lint-unity-adapter`: all eight packages green, including the new
  Facepunch compile + bridge lanes; `test-lint-unity-adapter` extended
  with the Facepunch fixture across the negative loops (128
  assertions, up from 114).
- lint-conventions and the remaining self-test suites green; csharpier
  clean; docs page (`docs/adapters/facepunch.md`) + nav entry added;
  CHANGELOG entry added.

## Open issues triage

- #99 (this deliverable): implemented; close on merge.
- #88 (FsCheck.NUnit pins NUnit < 5): unchanged — external wait, the
  wait-plan stands (FsCheck.NUnit 3.4.0 is still latest).
- #80 (mkdocs extensions when first used): no-action by its own terms;
  the facepunch docs page uses no new extensions.

## Follow-ups

- M8.7 per-adapter validation checklist + runbook skeleton (the next
  PLAN surface; folds in every adapter's live-validation gap, both
  Steamworks halves included).
- The Facepunch advertised-membership set only grows during a session
  (same as the Steamworks.NET half); room-membership-driven expiry is
  the shared candidate refinement if live validation shows it matters.
