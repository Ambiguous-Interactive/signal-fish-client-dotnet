# Photon Fusion

The Photon Fusion adapter (M8.5) puts a Fusion session on a Signal Fish
room: Fusion and Photon's cloud keep owning the game connection, while
the Signal Fish room owns the parts it is good at — matchmaking,
membership, heartbeats, and reconnection-ready sessions. The package
ships `SignalFishFusionBootstrap`, a bootstrap component, not a
transport bridge: the only thing exchanged between the two is the
session name, published by the host over the room's game-data lane.
This is the honest tier for a cloud-hosted netcode — there is no host
endpoint to publish and no accept path to gate, so the bootstrap
exchanges exactly what the room can carry: the name of the session to
start.

The adapter is a separate UPM package:
[unity/Adapters/Fusion](https://github.com/Ambiguous-Interactive/signal-fish-client-dotnet/tree/main/unity/Adapters/Fusion)
(`com.ambiguous-interactive.signalfish.adapters.fusion`). It never
references Fusion sources unless Fusion is present: Fusion 2 ships as
an asset — precompiled assemblies under `Assets/Photon/Fusion`, not a
UPM package the Package Manager knows — so the included editor define
detector is the define's sole owner. It probes the loaded
`Fusion.Runtime` assembly and toggles `SIGNALFISH_FUSION` across every
build target; without the define the package compiles to nothing.
After a fresh import, let the editor compile once; the detector lights
the adapter up on its next pass.

## How the bootstrap maps Fusion onto a room

- The **host** joins (or creates) the Signal Fish room, takes the
  authority, and starts the Fusion session with `NetworkRunner.StartGame`
  (`GameMode.Host`) and the chosen `SessionName`. Only after the session
  exists does it publish the session name over the room's game-data lane
  as a tiny JSON envelope (`signal_fish_fusion_session`), then marks
  ready and starts the game. Every later join re-publishes the name, so
  late joiners never depend on timing.
- A **client** joins the room by code, marks itself ready (the host's
  start needs an all-ready lobby), and waits for the published name on
  the same lane. When it arrives, the client starts its runner with
  `GameMode.Client` and that name — a mode Fusion's own matchmaking
  contract keeps on the join path: a client with a specific session
  name joins and never creates the session, so a client whose host
  vanished fails its start instead of sitting in a lone session
  reporting success. The bootstrap performs the Fusion calls itself.
- The host's session name defaults to the Signal Fish room code (both
  sides can trust a name neither chose), or any configured name that
  fits the envelope's charset ([A-Za-z0-9_-], at most 128 chars —
  Fusion's own GUID session names fit) — the host validates before it
  publishes and fails loudly rather than running an exchange that can
  never carry the name.

The flow, end to end:

```text
host                                  client
----                                  ------
join room ──────────────────────────► join room (by code)
take authority                        mark ready
StartGame (Host, session name)
publish session name (GameData) ────► the name arrives
ready + StartGame                     StartGame (Client, that name)
```

## Install and set up

1. Install the packages: Photon Fusion 2 (imported into the project),
   the Signal Fish client `com.ambiguous-interactive.signalfish`, the
   shared adapter core `com.ambiguous-interactive.signalfish.adapters.core`,
   then this adapter, and let Unity compile twice (see the detector
   note above).
2. Run Fusion's Hub setup: the `FusionAppSettings` asset supplies the
   AppId and region for the runner's cloud start.
3. Add `SignalFishFusionBootstrap` and fill the session fields:
   `Endpoint` (the v2 relay floor endpoint; v3 negotiates on top),
   `GameName`, and `PlayerName`.
4. Call `StartHostAsync()` (optionally with a room code to claim) or
   `StartClientAsync(roomCode)`; both may be awaited from any thread
   and complete once the Fusion session was started (or the start
   failed). `CoordinationFailed` carries live-session failures; the
   client-side session name reaches the game through
   `SessionNameReceived`.

The runner lives on its own `SignalFishFusionRunner` GameObject the
bootstrap creates and tears down; no scene manager is passed to
`StartGame` (Fusion falls back to its default provider itself), and
scene and simulation ownership stay with the game.

## Configuration surface

| Member | Meaning |
| --- | --- |
| `Endpoint` / `GameName` / `PlayerName` | The Signal Fish session identity. |
| `SessionName` | The host's Fusion session name; empty derives it from the room code. Clients ignore this — their session name is the one the host published. |
| `MaxPlayers` | The host's session capacity (`StartGameArgs.PlayerCount`); 0 means no explicit limit. |
| `StartTimeoutSeconds` / `FusionStartTimeoutSeconds` | How long the room handshake / the session-name wait and the cloud-bound runner start may take before the start fails. |
| `TransportFactory` | Builds the Signal Fish connection's `ITransport`. WebGL builds must return the package's browser-WebSocket transport here. |
| `JoinedRoomCode` / `FusionSessionName` / `LocalPlayerId` / `IsCoordinating` | Live diagnostics. |
| `CoordinationFailed` | Live-session failures after the start completed (a closed room connection, a shut-down runner, authority leaving the host, a host migration). |
| `SessionNameReceived` | The published session name, as the client's wait received it. Informational — the bootstrap starts by itself. |

## Membership is not the gate here

The BYOS template's echo gate and the NGO coordinator's approval
protect a **host accept path** — a direct connection the host can
refuse. A Fusion session on Photon's cloud has no accept path: anyone
with the session name can join it. That is the tier this adapter is
honest about — the Signal Fish room's part is matchmaking plus the
name exchange, and membership enforcement (if the game needs it)
belongs to the game's own session logic on the Fusion side.

## Validation status

Unity is never built in CI (a locked project decision). The
CI-compilable part is the adapter's pure core — the session-name
envelope — which runs in the dotnet test suite and compiles standalone
(netstandard2.1, C# 9, warnings as errors) in the `lint-unity-adapter`
CI gate. The gate also pins the define-guard contract (no `Fusion`
reference outside `#if SIGNALFISH_FUSION`, the detector contract as the
define's sole owner, the core dependency pin, declared sample paths)
and the bootstrap's member completeness against the pinned Fusion
surface.

The bootstrap itself is authored against the Fusion 2 matchmaking
contract (`NetworkRunner.StartGame`, the `StartGameArgs` fields, the
`INetworkRunnerCallbacks` registration surface, pinned against the
Fusion 2 runtime source) and the library's conformance suite; **live
two-client validation in Unity runs the [validation
runbook](../unity-validation.md#fusion-m85) drill** (pending a
licensed Unity seat). Treat unvalidated behavior as the honest unknown
it is — the same status the other adapters shipped under.

Known, deliberate limitations in this version: authority moves that
leave the host and host migrations tear the coordination down (the
published name would have no one to honor it — start a new room), the
host's `StartGame` refusal when a member has not readied yet surfaces
as a failure rather than a retry, a fresh import needs one editor
compile before the detector can light the adapter up (above), and both
sides must run the **same FusionAppSettings** (AppId and region): the
client only *joins* the host's session, so a settings mismatch
surfaces as the client's start failure rather than a silent
split-brain session.
