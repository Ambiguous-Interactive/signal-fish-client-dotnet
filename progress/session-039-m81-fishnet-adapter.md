# Session 039 — M8.1: the FishNet adapter (Wave 1 flagship)

Date: 2026-10-03. Scope: the M8.1 milestone item — the FishNet adapter
package: `SignalFishFishNetTransport : Transport` bridging a FishNet
session onto a Signal Fish room's v3 binary relay lane. Routine drift
check first: local main matched `origin/main` (`b009880`), fetch clean
(fast-forward, one stale remote branch pruned), working tree clean, no
open/draft PRs, one open issue (#80 — explicitly no action until a page
needs the extensions), main CI green on all workflows. No stashes; no
unique work on any local branch (all remotes gone, pre-squash trees).

## Starting state

M0-M7 were complete; the plan's next surface was M8.1 (the M8 section's
Wave-1 flagship): a FishNet `Transport` bridge with authority-side
fanout over per-peer `GameData`, v3 raw binary frames, channel→delivery
mapping, MTU + budget plumbing, host-mode loopback, and a movement-sync
sample. Engine adapters are source packages under `unity/Adapters/` that
never reference the SDK at build time and never compile in CI.

## Design (why it looks like this)

- **The relay is a room broadcast; FishNet is a star.** The adapter
  frames its own 18-byte header — target player UUID (all-zero =
  broadcast), FishNet channel byte, version — ahead of each segment,
  riding the v3 binary lane as one opaque payload. Routing rules turn
  the broadcast into the star: the authority consumes its peers'
  upstream frames; clients consume only the authority's frames addressed
  to them (or broadcast). Everything else drops by named reason.
- **Both FishNet channels ride the reliable lane.** The binary lane has
  no volatile class, and a reliable superset of unreliable semantics
  beats silently dropping frames; the channel byte is preserved
  end-to-end. Cost: the authority fans a `SendToClient` out as one relay
  broadcast per target peer. Documented as a deliberate limitation.
- **CI-compiled core, guarded bridge.** The pure logic (header codec,
  peer router, receive rules, MTU math, host loopback) is engine- and
  FishNet-free C# that runs in the dotnet suite (41 tests) and compiles
  standalone (netstandard2.1, C# 9, warnaserror) in the new
  `lint-fishnet-adapter` CI gate. The bridge itself is `#if
  SIGNALFISH_FISHNET`-guarded and never compiles here — the gate pins
  its member completeness against the pinned FishNet 4.x surface
  (verified against upstream master), the define plumbing, and the
  no-vendored-SDK rule.
- **Define plumbing (post-review).** `versionDefines` keys on FishNet's
  **UPM package** (`com.firstgeargames.fishnet`, ≥4.0.0 — the asmdef
  name is not a valid resource), and `defineConstraints` gate the whole
  assembly so the `FishNet.Runtime` reference can never dangle when
  FishNet is absent. Assets/-vendored FishNet is not detected (issue
  #81).

## Delivered

- **The package** (`unity/Adapters/FishNet/`): `package.json`
  (deps: the SDK package; movement-sync sample), the gated asmdef, five
  core sources, the bridge (`Runtime/FishNet/`), and the
  `Samples~/MovementSync` sample (pose sync over FishNet RPCs; legacy
  Input noted).
- **The bridge**: full abstract `Transport` surface; the Signal Fish
  bootstrap (connect → authenticate → join → v3 binary negotiation →
  authority grant on the host path) inside `StartConnection` with a
  generation-guarded, self-disposing task; host-mode loopback (in-process,
  never touches the relay); staged FishNet events drained on the tick
  thread (FishNet's handlers are not thread-safe — its own transports
  queue exactly like this); per-peer fanout with `RoutedDropped`/
  `OutboundDropped` diagnostics; relay backpressure reads as FishNet
  send backpressure (frame stays at the queue head; never silent loss;
  `BinaryFormatNotNegotiated` tears down loudly); authority migration
  without a running server side tears down loudly instead of silently
  stalling the room.
- **The gate** (`scripts/lint-fishnet-adapter.ps1` + self-test, CI step
  in `dotnet.yml`): compile check of the core; guard/vendoring/define/
  sample-path contract; bridge member completeness; 13 self-test
  assertions over fixture repos plus the real repo full lane.
- **Tests**: 41 new adapter tests, including the joint pin — an
  adapter-encoded UUID/frame decodes through the library's strict
  `BinaryGameDataFrame` reader (`from_player` spelling converges), and
  the full encode→decode→route matrix over data.
- **Docs**: `docs/adapters/fishnet.md` (nav + unity/transport pointers),
  CHANGELOG entry, honest limits and validation status throughout.
- **Follow-up**: issue #81 (Editor define detector for vendored
  FishNet).

## Adversarial review round

A critical sub-agent (full context: local sources + FishNet master)
returned two blockers, three majors, and a tail — all verified against
upstream before acting:

- **Blocker: the version define could never fire.** `versionDefines`
  resources are packages, not asmdefs, and an empty expression is
  invalid — the package would have been a silent no-op in exactly its
  target environment while docs and lint asserted otherwise. Fixed to
  the package key + `4.0.0` + defineConstraints (and the gate now pins
  the corrected shape).
- **Blocker: bootstrap/teardown race.** A `StopConnection` mid-bootstrap
  left the old task running: two live clients, a leaked joined room, and
  a stale player id defeating `DropSelfOrigin`. Fixed with a generation
  counter checked after every await; stale bootstraps dispose their own
  client and never touch shared state.
- **Majors: off-thread FishNet events** (now staged and drained on the
  tick thread, like FishNet's own transports), **unrouted-sender frames
  misattributed to the host's connection 0** (now dropped + counted),
  and **authority migration silently stalling the session** (now a loud
  teardown with the remedy in the message and docs).
- Minors fixed in the same pass: unchecked wait results, the dead
  `docs/unity.md` link, package.json dependencies (the SDK package),
  the sample's never-true define guard removed (samples compile in
  consumer assemblies), Input-backend note, name cosmetics. The
  reviewer also verified the header/UUID codec byte-exact against the
  library's decoder, the MTU math (worst-case relay map ≈ 92 B), and
  the no-self-echo assumption the routing leans on (already pinned by
  the CI E2E suite).

## Verification

- `dotnet build -warnaserror` clean; 856 unit tests green on net8.0 and
  net10.0 (41 new); all six convention lints green; CSharpier clean.
- `lint-fishnet-adapter` full lane green (core compile + contract);
  `lint-webgl-plugin` untouched and green; `sync-unity-package -Check`
  green (the adapter lives outside the mirrored package).
- `scripts/tests/run-all.ps1` green — 15 self-test files including the
  new one; markdownlint clean; PLAN.md size lint green.
- mkdocs strict build, typos, and lychee run in the Docs CI workflow
  (no python toolchain in the dev container); the new page uses only
  links that exist.

## Deferred (tracked)

- M8.7 per-adapter validation: live two-client Unity runs for the
  FishNet bridge (licensed seat; runbook lands with it).
- Issue #81: Editor define detector for Assets/-vendored FishNet.
- Next surface: M8.2 Wave 1 Mirror (same package shape, Mirror
  `Transport` target).
