# Photon PUN2

The Photon PUN2 adapter (M8.5) puts a PUN2 room on a Signal Fish room:
PUN2 and Photon's cloud keep owning the game connection, while the
Signal Fish room owns the parts it is good at — matchmaking, membership,
heartbeats, and reconnection-ready sessions. The package ships
`SignalFishPun2Bootstrap`, a bootstrap component, not a transport
bridge: the only thing exchanged between the two is the PUN room name,
published by the host over the room's game-data lane. This is the
honest tier for a cloud-hosted netcode — there is no host endpoint to
publish and no accept path to gate, so the bootstrap exchanges exactly
what the room can carry: the name of the room to join.

The adapter is a separate UPM package:
[unity/Adapters/Pun2](https://github.com/Ambiguous-Interactive/signal-fish-client-dotnet/tree/main/unity/Adapters/Pun2)
(`com.ambiguous-interactive.signalfish.adapters.pun2`). It never
references PUN2 sources unless PUN2 is present: PUN2 ships as an asset
(no UPM package), so the included editor define detector is the define's
sole owner — it probes the compiled `PhotonUnityNetworking` assembly and
toggles `SIGNALFISH_PUN2` across every build target, and without the
define the package compiles to nothing. After a fresh import, let the
editor compile once; the detector lights the adapter up on its next
pass.

## How the bootstrap maps PUN2 onto a room

- The **host** joins (or creates) the Signal Fish room, takes the
  authority, connects through `ConnectUsingSettings`, and creates the
  PUN room with `JoinOrCreateRoom`. Only after the PUN room exists does
  it publish the PUN room name over the room's game-data lane as a tiny
  JSON envelope (`signal_fish_pun2_room`), then marks ready and starts
  the game. Every later join re-publishes the name, so late joiners
  never depend on timing.
- A **client** joins the room by code, marks itself ready (the host's
  start needs an all-ready lobby), and waits for the published name on
  the same lane. When it arrives, the client connects and joins the PUN
  room by that name — the bootstrap performs the PUN calls itself.
- The host's PUN room name defaults to the Signal Fish room code (both
  sides can trust a name neither chose), or any configured name that
  fits the envelope's charset ([A-Za-z0-9_-], at most 128 chars) — the
  host validates before it publishes and fails loudly rather than
  running an exchange that can never carry the name.

The flow, end to end:

```text
host                                  client
----                                  ------
join room ──────────────────────────► join room (by code)
take authority                        mark ready
PUN connect + JoinOrCreateRoom
publish PUN room name (GameData) ───► the name arrives
ready + StartGame                     PUN connect + JoinOrCreateRoom(name)
```

## Install and set up

1. Install the packages: Photon PUN 2 (imported into the project), the
   Signal Fish client `com.ambiguous-interactive.signalfish`, the
   shared adapter core `com.ambiguous-interactive.signalfish.adapters.core`,
   then this adapter, and let Unity compile twice (see the detector
   note above).
2. Run PUN 2's setup wizard: the `PhotonServerSettings` asset supplies
   the AppId and region for `ConnectUsingSettings`.
3. Add `SignalFishPun2Bootstrap` and fill the session fields:
   `Endpoint` (the v2 relay floor endpoint; v3 negotiates on top),
   `GameName`, and `PlayerName`.
4. Call `StartHostAsync()` (optionally with a room code to claim) or
   `StartClientAsync(roomCode)`; both may be awaited from any thread
   and complete once the PUN room was joined (or the start failed).
   `CoordinationFailed` carries live-session failures; the client-side
   room name reaches the game through `PunRoomNameReceived`.

## Configuration surface

| Member | Meaning |
| --- | --- |
| `Endpoint` / `GameName` / `PlayerName` | The Signal Fish session identity. |
| `RoomName` | The host's PUN room name; empty derives it from the room code. Clients ignore this — their PUN room name is the one the host published. |
| `MaxPlayers` | The host's PUN room capacity (`RoomOptions.MaxPlayers`); 0 means no limit. |
| `StartTimeoutSeconds` / `PunStartTimeoutSeconds` | How long the room handshake / the room-name wait and the cloud-bound PUN connect and join may take before the start fails. |
| `TransportFactory` | Builds the Signal Fish connection's `ITransport`. WebGL builds must return the package's browser-WebSocket transport here. |
| `JoinedRoomCode` / `PhotonRoomName` / `LocalPlayerId` / `IsCoordinating` | Live diagnostics. |
| `CoordinationFailed` | Live-session failures after the start completed (a closed room connection, a closed PUN connection, authority leaving the host). |
| `PunRoomNameReceived` | The published PUN room name, as the client's wait received it. Informational — the bootstrap joins by itself. |

## Membership is not the gate here

The BYOS template's echo gate and the NGO coordinator's approval
protect a **host accept path** — a direct connection the host can
refuse. A PUN room on Photon's cloud has no accept path: anyone with
the room name can join it. That is the tier this adapter is honest
about — the Signal Fish room's part is matchmaking plus the name
exchange, and membership enforcement (if the game needs it) belongs to
the game's own room logic on the PUN side.

## Validation status

Unity is never built in CI (a locked project decision). The
CI-compilable part is the adapter's pure core — the room-name envelope —
which runs in the dotnet test suite and compiles standalone
(netstandard2.1, C# 9, warnings as errors) in the `lint-unity-adapter`
CI gate. The gate also pins the define-guard contract (no `Photon`
reference outside `#if SIGNALFISH_PUN2`, the detector contract as the
define's sole owner, the core dependency pin, declared sample paths)
and the bootstrap's member completeness against the pinned PUN 2 surface.

The bootstrap itself is authored against the PUN 2.31 matchmaking
contract (`ConnectUsingSettings`, `JoinOrCreateRoom`, the
`MonoBehaviourPunCallbacks` virtuals) and the library's conformance
suite; **live two-client validation in Unity runs the [validation
runbook](../unity-validation.md#pun2-m85) drill** (pending a licensed
Unity seat). Treat unvalidated behavior as the honest unknown it is —
the same status the other adapters shipped under.

Known, deliberate limitations in this version: authority moves that
leave the host tear the coordination down (the published name would
have no one to honor it — start a new room), the host's `StartGame`
refusal when a member has not readied yet surfaces as a failure rather
than a retry, a fresh import needs one editor compile before the
detector can light the adapter up (above), and both sides must run the
**same PhotonServerSettings** (AppId and region): the client only
*joins* the host's room, so a settings mismatch surfaces as the
client's join failure rather than a silent split-brain room.
