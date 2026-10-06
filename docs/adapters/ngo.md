# NGO + Unity Relay

The Netcode for GameObjects adapter (M8.3) puts an NGO session on a
Signal Fish room: NGO and its Unity Transport keep owning the netcode
and the connection, while the Signal Fish room owns the parts it is
good at — matchmaking, membership, heartbeats, and reconnection-ready
sessions. The package ships `SignalFishRoomCoordinator`, a coordinator
component, not a transport bridge: NGO's transport is Unity Transport,
and binding it to a Relay allocation stays one game-side hook call.

The adapter is a separate UPM package:
[unity/Adapters/Ngo](https://github.com/Ambiguous-Interactive/signal-fish-client-dotnet/tree/main/unity/Adapters/Ngo)
(`com.ambiguous-interactive.signalfish.adapters.ngo`). It never
references NGO sources unless NGO is present: the assembly's
`versionDefines` key off the `com.unity.netcode.gameobjects` package
(pinned at 1.2.0) and toggle `SIGNALFISH_NGO`, and an editor define
detector probes for the compiled `Unity.Netcode.Runtime` assembly (or
the UPM package marker) as the vendored-install fallback — both
mechanisms pin the same define, and without it the package compiles to
nothing. After a fresh install, let the editor compile once; the
detector lights the adapter up on its next pass.

## How the coordinator maps NGO onto a room

- The host joins (or creates) the room, **takes the authority**, and
  allocates a Unity Relay server through a game-supplied hook.
- The Relay **join code travels to members over the room's game-data
  lane** as a tiny JSON envelope (`signal_fish_relay_join_code`). Late
  joiners get a fresh broadcast when they enter the room, so the code
  never needs an out-of-band channel beyond the room code itself.
- A client joins the room by code, binds the received join code to
  Unity Transport through its own hook, and starts the NGO client.
- **Connection approval is membership**: the connecting client presents
  its Signal Fish player id (16 RFC-4122 bytes, byte-identical to the
  relay's own `from_player` spelling) as its NGO connection payload,
  and the host approves a connection only when that id is a live room
  member. NGO sees no connection the room does not know.
- The room's player events keep the roster current while the session
  runs, so approval tracks the room in real time.

## Install and set up

1. Install the packages: `com.unity.netcode.gameobjects` (1.2.0), the
   Signal Fish client `com.ambiguous-interactive.signalfish`, the
   shared adapter core `com.ambiguous-interactive.signalfish.adapters.core`,
   then this adapter, and let Unity compile twice (see the detector
   note above).
2. Add a `NetworkManager` with a `UnityTransport`, and put
   `SignalFishRoomCoordinator` next to it.
3. Fill the session fields: `Endpoint` (the v2 relay floor endpoint;
   v3 negotiates on top), `GameName`, and `PlayerName`.
4. Wire the two Relay hooks (the **Relay Room Coordinator** sample in
   `Samples~` shows the full Unity Gaming Services wiring):
   `RelayAllocationRequest` on the host — sign in, allocate, bind the
   host's Unity Transport, return the join code — and `RelayJoinBinder`
   on clients — allocate the join and bind it.
5. Call `StartHostAsync()` (optionally with a room code to claim) or
   `StartClientAsync(roomCode)`; both may be awaited from any thread
   and complete once the engine start ran on the next coordinator
   tick. `CoordinationFailed` carries live-session failures; the
   start-time join code reaches the game through `RelayJoinBinder`.

## Configuration surface

| Member | Meaning |
| --- | --- |
| `Endpoint` / `GameName` / `PlayerName` | The Signal Fish session identity. |
| `MaxFrameBytes` | The client's inbound frame bound for the room session. |
| `StartTimeoutSeconds` / `JoinCodeTimeoutSeconds` | How long the room handshake / the join-code wait and relay hooks may take before the start fails. The staged engine start itself must run within ten seconds of staging (the coordinator's `Update` ticking). |
| `AppId` / `ConnectToken` | Sent on every `Authenticate`; the token is a secret — never log it. |
| `RelayAllocationRequest` | The host's Relay hook; its join code must fit the envelope's charset ([A-Za-z0-9_-], ≤ 128 chars). |
| `RelayJoinBinder` | The client's Relay hook; the NGO client start waits for it to finish. |
| `TransportFactory` | Builds the Signal Fish connection's `ITransport`. WebGL builds must return the package's browser-WebSocket transport here. |
| `CreatePlayerObject` | Whether approved NGO connections spawn the default player object. |
| `JoinedRoomCode` / `RelayJoinCode` / `LocalPlayerId` / `RosterCount` | Live diagnostics. |

## Validation status

Unity is never built in CI (a locked project decision). The
CI-compilable part is the adapter's pure core — the join-code envelope,
the room roster, and the approval payload (through the shared
`AdapterWire` UUID spelling) — which runs in the dotnet test suite and
compiles standalone (netstandard2.1, C# 9, warnings as errors) in the
`lint-unity-adapter` CI gate. The gate also pins the define-guard
contract (no `Unity.Netcode` reference outside `#if SIGNALFISH_NGO`,
the package-pin and detector contracts sharing one define, the core
dependency pin, declared sample paths) and the coordinator's member
completeness against the pinned NGO 1.2.0 `NetworkManager` surface.

The coordinator itself is authored against the NGO 1.2.0 connection
approval contract and the library's conformance suite; **live
two-client validation in Unity runs the [validation
runbook](../unity-validation.md#ngo-and-unity-relay-m83) drill**
(pending a licensed Unity seat). Treat unvalidated behavior as the
honest unknown it is — the same status the other adapters shipped
under.

Known, deliberate limitations in this version: authority moves that
leave an NGO host without its authority tear the coordination down
(NGO 1.x has no host migration — start a new room), a Relay-less host
start is refused loudly rather than half-working, and a fresh install
needs one editor compile before the detector can light the adapter up
(above).
