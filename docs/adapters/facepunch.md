# Facepunch Steamworks

The Facepunch Steamworks adapter (M8.6) puts Steam P2P sockets on a
Signal Fish room. Steam has no matchmaking to lean on, so the room
itself is the fence: the Signal Fish room owns membership, heartbeats,
and reconnection-ready sessions, while Steam's relay network carries
the game traffic. The package ships
`SignalFishFacepunchSteamIdentityBootstrap`, a bootstrap component, not
a transport bridge: it establishes and fences the Steam connections,
and the game owns what flows over them. The only thing exchanged
between the two is a pair of SteamId64s, published by role over the
room's game-data lane. This is the second half of M8.6 — the same
envelope contract as the Steamworks.NET half, byte-identical lane keys,
so a room could host mixed-binding peers in principle (each seat runs
one binding).

The adapter is a separate UPM package:
[unity/Adapters/Facepunch](https://github.com/Ambiguous-Interactive/signal-fish-client-dotnet/tree/main/unity/Adapters/Facepunch)
(`com.ambiguous-interactive.signalfish.adapters.facepunch`). It never
references Facepunch Steamworks sources unless Facepunch Steamworks is
present. Facepunch Steamworks ships as an asset — precompiled platform
assemblies (`Facepunch.Steamworks.Win32`/`.Win64`/`.Posix`) plus native
binaries, not a UPM package the Package Manager knows — so the included
editor define detector is the `SIGNALFISH_FACEPUNCH` define's sole
owner (the Mirror/PUN2/Fusion pattern; there is no `versionDefines`
path). It probes the loaded Facepunch assembly and toggles the define
across every build target; without the define the package compiles to
nothing. After a fresh import, let the editor compile once; the
detector lights the adapter up on its next pass.

## How the bootstrap maps Steam onto a room

The wire is the same role-scoped identity envelope the Steamworks.NET
half carries (`SteamIdentityEnvelope`):

- `signal_fish_steam_host` — the **host's** SteamId64. The host
  publishes it when its session goes live and re-publishes on every
  later join, so late joiners never depend on timing. Clients consume
  it: it is the address their Steam connection dials.
- `signal_fish_steam_peer` — a **client's** SteamId64. A client
  publishes its own id when its session goes live (and re-publishes on
  every join). The host consumes these into the accept fence.

The values are decimal strings (1-20 digits, no leading zero), so a
64-bit SteamId survives every JSON decoder losslessly — a raw JSON
number would not.

The flow, end to end:

```text
host                                        client
----                                        ------
join room ────────────────────────────────► join room (by code)
take authority                              mark ready
open Steam relay socket (CreateRelaySocket)
publish host SteamId64 (GameData) ────────► the host id arrives
publish local id, ready + StartGame         publish own id
                                            ConnectRelay(host id)
incoming Steam connections:                 │
  accept iff the peer's id was ▼            connection established
  published on the lane (grace window) ◄────┘
SteamPeerConnected / SteamHostIdReceived
```

The fence, precisely: Steam's rendezvous authenticates the connecting
identity (the claim is Steam's, not the peer's), so the host only
checks that the connecting identity was advertised on the room's lane.
Facepunch's `SocketManager` auto-accepts every connection by default —
the bootstrap overrides that: an incoming connection whose id has not
arrived yet waits in a grace window (`AcceptGraceSeconds`, default 5 s)
and is refused with an app-range end reason (`NetConnectionEnd.App_Min`)
if the advertisement never comes. No `ConnectionInfo` user data is
involved — the authenticated identity carries the claim, and the room
carries the membership.

On Facepunch, status changes dispatch from `SteamClient.RunCallbacks`,
which the bootstrap pumps on its own tick — and this is a hard
requirement, not a preference: the bootstrap needs Facepunch's manual
callback mode (`SteamClient.Init(appId, asyncCallbacks: false)`),
because Facepunch's default async mode pumps callbacks on a background
thread, which would put fence decisions (and every event handler) off
the Unity main thread. With the pump on the tick, double-pumping (the
game pumping too) is a harmless extra drain. The bootstrap's
host/client sides derive from Facepunch's
`SocketManager`/`ConnectionManager` — the client's own dial posts a
Connecting status that Facepunch's manager state machine already
absorbs, and the bootstrap additionally scopes every manager callback
to the manager instance its own session created, so a stale dispatch
through Facepunch's static registries can never land in a new
session's fence.

## Install and set up

1. Install the packages: Facepunch Steamworks 2.5.2 or newer (the
   Unity asset — dropped into the project per its install guide, which
   the detector picks up on its next editor pass), the Signal Fish
   client `com.ambiguous-interactive.signalfish`, the shared adapter
   core `com.ambiguous-interactive.signalfish.adapters.core`, then
   this adapter, and let Unity compile.
2. Initialize the Facepunch Steamworks API first (your `SteamManager`
   calls `SteamClient.Init(appId, asyncCallbacks: false)` — manual
   callback mode is required, because the bootstrap pumps
   `SteamClient.RunCallbacks` on its own tick and Facepunch's default
   async mode would dispatch fence decisions on a background thread);
   the bootstrap fails a start loudly when Steam is not available or
   no user is logged on.
3. Add `SignalFishFacepunchSteamIdentityBootstrap` and fill the session
   fields: `Endpoint` (the v2 relay floor endpoint; v3 negotiates on
   top), `GameName`, and `PlayerName`.
4. Call `StartHostAsync()` (optionally with a room code to claim) or
   `StartClientAsync(roomCode)`; both may be awaited from any thread
   and complete once the host's exchange is live (or, on the client,
   once the Steam connection to the host is established).
   `CoordinationFailed` carries live-session failures.

## Configuration surface

| Member | Meaning |
| --- | --- |
| `Endpoint` / `GameName` / `PlayerName` | The Signal Fish session identity. |
| `StartTimeoutSeconds` / `SteamStartTimeoutSeconds` | How long the room handshake / the host-id wait and the Steam connect may take before the start fails. |
| `AcceptGraceSeconds` | How long the host waits for an incoming peer's id to arrive on the lane before refusing. Zero refuses everyone not already advertised. |
| `ListenVirtualPort` | The Steam P2P virtual port the host listens on; 0 is Steam's default. |
| `TransportFactory` | Builds the Signal Fish connection's `ITransport`. WebGL builds must return the package's browser-WebSocket transport here. |
| `JoinedRoomCode` / `LocalSteamId` / `HostSteamId` / `IsCoordinating` | Live diagnostics. |
| `LiveSocketManager` / `LiveConnectionManager` | The live Facepunch managers once a start completes — the game's traffic plug. Assign an `ISocketManager`/`IConnectionManager` to the manager's `Interface` (its `OnMessage` receives the game's messages) and call `Receive` per frame to pump them. |
| `CoordinationFailed` | Live-session failures after the start completed (a closed room connection, the host Steam connection closing, authority leaving the host, the host's published id changing mid-session). |
| `SteamHostIdReceived` | The published host id, as the client's wait received it. Informational — the bootstrap dials by itself. |
| `SteamPeerConnected` / `SteamPeerDisconnected` | A fenced peer's Steam connection came up or closed (host side). The game owns the traffic from here. Handlers run on the bootstrap's tick; return promptly and never block. |

## What this tier does not do

The bootstrap establishes and fences connections; it is not a data
plane. The bootstrap never pumps messages, never sends one, and maps
no channels — accepted host connections land on Facepunch's poll group
and the game plugs its own message pump into the exposed managers:
assign an `ISocketManager`/`IConnectionManager` to the manager's
`Interface` (that is the only receive path on Facepunch) and call
`Receive` per frame. One asymmetry to know: the host-side interface
also sees `OnConnected` (the poll-group assignment rides with it),
while `OnConnecting`/`OnDisconnected` stay bootstrap-owned — track
connection lifetime through `SteamPeerConnected`/
`SteamPeerDisconnected`, not the interface. Membership enforcement
beyond the Steam accept fence (room leave/rejoin races, the host leaving while peers
hold connections) stays the game's job, on the same honest terms as
the other Wave 2 adapters.

## Validation status

Unity is never built in CI (a locked project decision). The
CI-compilable part is the adapter's pure core — the identity envelope —
which runs in the dotnet test suite and compiles standalone
(netstandard2.1, C# 9, warnings as errors) in the `lint-unity-adapter`
CI gate. The gate also pins the define-guard contract (no `Steamworks`
reference outside `#if SIGNALFISH_FACEPUNCH`, the detector contract,
the asset-ship honesty — no `versionDefines` for an asset — the core
dependency pin, declared sample paths) and the bootstrap's member
completeness plus a full type-check against the pinned Facepunch
surface.

The bootstrap is authored against the Facepunch.Steamworks 2.5.2
sources (`SteamNetworkingSockets.CreateRelaySocket`/`ConnectRelay`, the
`SocketManager`/`ConnectionManager` state machines and their virtual
hooks, `Connection.Accept`/`Close`, `ConnectionInfo.State`/`Identity`/
`EndReason`, `NetConnectionEnd.App_Min`, `SteamClient.RunCallbacks`/
`IsValid`/`SteamId`), pinned into the CI compile lane; **live
two-client validation in Unity with the Steam client running is the
M8.7 runbook item** (needs a licensed Unity seat). Treat unvalidated
behavior as the honest unknown it is — the same status the other
adapters shipped under.

Known, deliberate limitations in this version: authority moves that
leave the host tear the coordination down (the fence would have no
one to honor it — start a new room), the advertised-membership set
only grows during a session (a member that leaves keeps its entry
until the session ends), a fresh import needs one editor compile
before the detector path can light the adapter up, and Facepunch's
connection-side registry never removes entries (the bootstrap clears
the socket-side entry and scopes every callback to its own session's
manager, so stale dispatches are absorbed — but the residue is
Facepunch's, and re-hosting in the same process keeps growing it
until the process ends).
