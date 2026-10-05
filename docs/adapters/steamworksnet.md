# Steamworks.NET

The Steamworks.NET adapter (M8.6) puts Steam P2P sockets on a Signal
Fish room. Steam has no matchmaking to lean on, so the room itself is
the fence: the Signal Fish room owns membership, heartbeats, and
reconnection-ready sessions, while Steam's relay network carries the
game traffic. The package ships
`SignalFishSteamIdentityBootstrap`, a bootstrap component, not a
transport bridge: it establishes and fences the Steam connections, and
the game owns what flows over them. The only thing exchanged between
the two is a pair of SteamId64s, published by role over the room's
game-data lane.

The adapter is a separate UPM package:
[unity/Adapters/SteamworksNet](https://github.com/Ambiguous-Interactive/signal-fish-client-dotnet/tree/main/unity/Adapters/SteamworksNet)
(`com.ambiguous-interactive.signalfish.adapters.steamworksnet`). It
never references Steamworks.NET sources unless Steamworks.NET is
present: Steamworks.NET ships as a UPM package
(`com.rlabrecque.steamworks.net`, UPM since 20.0.0), so the adapter
asmdef's `versionDefines` own the `SIGNALFISH_STEAMWORKSNET` define
for package installs. An included editor define detector fills the
vendored gap (Steamworks.NET copied into `Assets/`, which no version
define can see): it probes the package marker or the compiled
assembly and toggles the same define across every build target. Either
mechanism lights the adapter up; without the define the package
compiles to nothing.

## How the bootstrap maps Steam onto a room

The relay lane is a room broadcast, so one identity key would let a
client read another client's id as the host's. The envelope is
role-scoped instead, and the codec (`SteamIdentityEnvelope`) keeps
each lane to its key:

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
open Steam listen socket
publish host SteamId64 (GameData) ────────► the host id arrives
publish local id, ready + StartGame         publish own id
                                            ConnectP2P(host id)
incoming Steam connections:                 │
  accept iff the peer's id was ▼            connection established
  published on the lane (grace window) ◄────┘
SteamPeerConnected / SteamHostIdReceived
```

The fence, precisely: Steam's rendezvous authenticates
`SteamNetworkingIdentity` (the claim is Steam's, not the peer's), so
the host only checks that the connecting identity was advertised on
the room's lane. An incoming connection whose id has not arrived yet
waits in a grace window (`AcceptGraceSeconds`, default 5 s) and is
refused with an app-range end reason if the advertisement never
comes. No ConnectionInfo user data is involved — the authenticated
identity carries the claim, and the room carries the membership.

## Install and set up

1. Install the packages: Steamworks.NET 20.0.0 or newer (the UPM
   package — via OpenUPM or a git URL — or a vendored copy, which the
   detector picks up on its next editor pass), the Signal Fish client
   `com.ambiguous-interactive.signalfish`, the shared adapter core
   `com.ambiguous-interactive.signalfish.adapters.core`, then this
   adapter, and let Unity compile.
2. Initialize the Steamworks API first (your `SteamManager` calls
   `SteamAPI.Init()` and pumps or lets the bootstrap pump
   `SteamAPI.RunCallbacks`); the bootstrap fails a start loudly when
   Steam is not available.
3. Add `SignalFishSteamIdentityBootstrap` and fill the session fields:
   `Endpoint` (the v2 relay floor endpoint; v3 negotiates on top),
   `GameName`, and `PlayerName`.
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
| `CoordinationFailed` | Live-session failures after the start completed (a closed room connection, the host Steam connection closing, authority leaving the host, the host's published id changing mid-session). |
| `SteamHostIdReceived` | The published host id, as the client's wait received it. Informational — the bootstrap dials by itself. |
| `SteamPeerConnected` / `SteamPeerDisconnected` | A fenced peer's Steam connection came up or closed (host side). The game owns the traffic from here. Handlers run on the bootstrap's tick; return promptly and never block. |

## What this tier does not do

The bootstrap establishes and fences connections; it is not a data
plane. There is no poll group, no send helper, and no channel
mapping — the game takes the established Steam connections and speaks
its own protocol over them (the same division as the PUN2 and Fusion
bootstraps, where the engine's transport keeps owning the game
traffic). Membership enforcement beyond the Steam accept fence (room
leave/rejoin races, the host leaving while peers hold connections)
stays the game's job, on the same honest terms as the other Wave 2
adapters.

## Validation status

Unity is never built in CI (a locked project decision). The
CI-compilable part is the adapter's pure core — the identity envelope —
which runs in the dotnet test suite and compiles standalone
(netstandard2.1, C# 9, warnings as errors) in the `lint-unity-adapter`
CI gate. The gate also pins the define-guard contract (no `Steamworks`
reference outside `#if SIGNALFISH_STEAMWORKSNET`, the detector
contract, the UPM package pin, the core dependency pin, declared
sample paths) and the bootstrap's member completeness against the
pinned Steamworks.NET surface.

The bootstrap is authored against the Steamworks.NET 2025.165.0
sources (`SteamNetworkingSockets` P2P listen/connect, the
`SteamNetConnectionStatusChangedCallback_t` dispatcher, the
`ESteamNetworkingConnectionState` machine, `SteamUser.GetSteamID`),
pinned into the CI compile lane; **live two-client validation in
Unity with the Steam client running is the M8.7 runbook item** (needs
a licensed Unity seat). Treat unvalidated behavior as the honest
unknown it is — the same status the other adapters shipped under.

Known, deliberate limitations in this version: authority moves that
leave the host tear the coordination down (the fence would have no
one to honor it — start a new room), the advertised-membership set
only grows during a session (a member that leaves keeps its entry
until the session ends), and a fresh import needs one editor compile
before the detector path can light the adapter up (the
`versionDefines` path needs none).
