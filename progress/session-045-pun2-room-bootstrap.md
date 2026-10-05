# Session 045 — PUN2 room bootstrap (M8.5, the PUN2 half)

Date: 2026-10-05
Branch: `fusion-pun2-bootstrap-m8.5`

## What shipped

**M8.5 Wave 2, PUN2 half** — `SignalFishPun2Bootstrap`, the room-name
exchange, shipped as the UPM source package
`com.ambiguous-interactive.signalfish.adapters.pun2`
(`unity/Adapters/Pun2`):

- Host: Signal Fish join → authority → PUN `ConnectUsingSettings` →
  `JoinOrCreateRoom` → publish
  `{"signal_fish_pun2_room": "<name>"}` over the room's game-data lane
  → ready + start the game. Every later join re-publishes, so late
  joiners never depend on timing.
- Client: Signal Fish join by code → ready → wait for the published
  name on the same lane → PUN connect → join the PUN room by that name.
  The bootstrap performs every PUN call itself (staged onto the main
  thread and driven by a `MonoBehaviourPunCallbacks` phase machine).
- The host's PUN room name defaults to the Signal Fish room code, so a
  host that configures nothing exchanges a name neither side chose.
- Honest tier, stated in the docs: a Photon cloud room has no host
  accept path to gate, so this adapter is matchmaking plus the name
  exchange — membership enforcement stays the game's job. This is why
  the package carries no roster/approval machinery.

## How the surface was pinned

Every pinned engine member was verified against the PUN 2.31 source
(public mirrors), not memory: `JoinOrCreateRoom` returns `bool`,
`RoomOptions.MaxPlayers` is a byte field, `TypedLobby.Default` is a
static reference, PUN2 sets its own defines through an editor script
(so our detector owns `SIGNALFISH_PUN2` and the asmdef carries no
`versionDefines` — the Mirror shape), and the callbacks are virtuals on
`MonoBehaviourPunCallbacks`. The lint's new `Pun2` shape-stub compile
lane type-checks the bootstrap against that surface; its first catch in
this package was a real nullable-flow error the dotnet build could not
see (`RoomMembership.RoomCode` flowing into a non-nullable parameter).

## Validation

- `dotnet test`: 887 passed (net8.0; net10.0 in CI) — 11 new envelope
  contract tests (round-trip, byte-exact shape, foreign payloads decode
  as absent, charset bounds, rescan after a malformed value).
- `lint-unity-adapter`: all five packages green, including the new PUN2
  bridge-compile lane; `test-lint-unity-adapter` self-tests extended
  with the PUN2 fixture across all negative loops (86 assertions).
- lint-conventions, CSharpier, pre-commit hooks green; docs page
  (`docs/adapters/pun2.md`) + nav entry added; CHANGELOG entry added.

## Not carried (deliberate)

- The Fusion half of M8.5 is the next surface (PLAN.md): same
  choreography, pinned against Fusion's verified surface before any
  stub is written.
- #88 (NUnit 5) stays blocked on an FsCheck.NUnit release that allows
  NUnit ≥ 5.0.0 (re-checked nuget.org this session: 3.4.0 is still
  latest).
- #80 (mkdocs markdown extensions) stays "enable on first use" — the
  new page uses none.
