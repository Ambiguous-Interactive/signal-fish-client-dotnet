# FishNet Transport

The FishNet adapter (M8.1) puts a FishNet session on a Signal Fish room:
the package ships `SignalFishFishNetTransport`, a FishNet
`Transport` implementation whose wire is the room's v3 binary game-data
lane. FishNet keeps owning the game netcode; Signal Fish owns the parts
it is good at — rooms, membership, heartbeats, reconnection, and the
relay.

The adapter is a separate UPM package:
[unity/Adapters/FishNet](https://github.com/Ambiguous-Interactive/signal-fish-client-dotnet/tree/main/unity/Adapters/FishNet)
(`com.ambiguous-interactive.signalfish.transport.fishnet`). It never
references FishNet sources: the assembly's `versionDefines` key off
FishNet's UPM package (`com.firstgeargames.fishnet`, 4.x) when it is
installed as a package, an editor define detector probes for the
compiled `FishNet.Runtime` assembly (the vendored `Assets/` layout) and
toggles the same `SIGNALFISH_FISHNET` define, and the asmdef's
`defineConstraints` skip the whole assembly when the define is absent —
so the package is inert without FishNet, and the `FishNet.Runtime`
assembly reference can never dangle. After a fresh FishNet install,
let the editor compile once; the detector lights the adapter up on its
next pass.

## How the bridge maps FishNet onto a room

FishNet is a star: clients talk only to the server. The Signal Fish
relay is a room broadcast. The adapter reconciles the two with roles:

- The room **authority** plays the FishNet **server**; every other
  member plays a FishNet **client**.
- Each relayed frame carries an 18-byte adapter header — target player,
  FishNet channel, version — ahead of the FishNet segment, which rides
  verbatim.
- The authority consumes its peers' upstream frames into its server
  feed; clients consume only the authority's frames addressed to them
  (or broadcast). Everything else is dropped by rule, which is what
  makes a broadcast relay behave like a star.
- **Host mode loops back in-process**: the host's own client traffic
  never touches the relay.
- `GetMTU` reports the client's inbound frame budget minus the adapter
  wire reserve, so a full MTU frame still decodes on every recipient.

Both FishNet channels ride the relay's **reliable** binary lane in this
version. The channel byte is preserved end-to-end — FishNet sees
`Channel.Unreliable` traffic arrive on the channel it expects — but
delivery is reliable: the relay floor has no volatile binary class yet,
and a reliable superset of unreliable semantics (everything arrives, in
order) beats silently dropping frames a game marked reliable. Downstream
cost to know about: the authority fans `SendToClient` out as one relay
broadcast per target peer.

## Install and set up

1. Install the packages: FishNet (its git URL UPM install or a vendored
   `Assets/` install — both are detected), then
   `com.ambiguous-interactive.signalfish`, then the shared adapter core
   `com.ambiguous-interactive.signalfish.adapters.core`, then this
   adapter, and let Unity compile twice (the define detector lights the
   adapter up after FishNet's first compile).
2. Add a `NetworkManager` and put `SignalFishFishNetTransport` under its
   transport list.
3. Fill the session fields: `Endpoint` (the v2 relay floor endpoint;
   v3 is negotiated on top), `GameName`, `PlayerName`, and — for
   guests — `RoomCode`.
4. Start host or client through FishNet as usual. The adapter joins the
   room, negotiates the v3 binary format, and requests the authority on
   the host path; the FishNet side reports `Started` once the room is
   live. Read `JoinedRoomCode` to share the room code.

The movement-sync sample (package **Samples~**) shows the smallest
end-to-end loop: an owning client feeds input, the server rebroadcasts
the pose, observers apply it.

### Authority moves

The room can hand the authority to another member (for example when the
host leaves). FishNet's server half cannot appear by itself, so the
bridge **tears the session down loudly** if this machine becomes the
authority without a running server side: re-host (join with an empty
`RoomCode`) and start FishNet's server to become the hub again.

## Configuration surface

| Member | Meaning |
| --- | --- |
| `Endpoint` / `GameName` / `PlayerName` / `RoomCode` | The Signal Fish session identity; empty `RoomCode` creates a room (the host path). |
| `AppId` / `ConnectToken` | Sent on every `Authenticate`; the token is a secret — never log it. |
| `MaxFrameBytes` | The client's inbound frame bound; the basis of the reported MTU. |
| `LoopbackCapacity` | Host-mode loopback frames per direction; overflow drops and counts. |
| `TransportFactory` | Builds the Signal Fish connection's `ITransport`. WebGL builds must return the package's browser-WebSocket transport here. |
| `JoinedRoomCode` | The joined room's shareable code. |
| `OutboundDropped` / `LoopbackDropped` / `PeerCount` | Live diagnostics. |

Relay backpressure reads as FishNet send backpressure: when the Signal
Fish command queue is full, frames wait at the adapter's queue head and
retry on the next iterate — never silent loss; a `BinaryFormatNotNegotiated`
refusal tears the session down loudly instead of queuing forever.

## Validation status

Unity is never built in CI (a locked project decision). The
CI-compilable part is the adapter's pure core — the wire header, peer
router, receive rules, MTU math, and host loopback — which runs in the
dotnet test suite and compiles standalone (netstandard2.1, C# 9,
warnings as errors) in the `lint-unity-adapter` CI gate. The gate also
pins the define-guard contract (no FishNet reference outside
`#if SIGNALFISH_FISHNET`, no vendored SDK sources, the `versionDefines`
pin, the detector contract, declared sample paths) and the bridge's
member completeness against the pinned FishNet 4.x `Transport` surface.

The bridge itself is authored against the FishNet 4.x transport
contract and the library's conformance suite; **live two-client
validation in Unity is the M8.7 runbook item** (needs a licensed Unity
seat). Treat unvalidated behavior as the honest unknown it is — the
same status the WebGL transport shipped under before M7.4 validation.

Known, deliberate limitations in this version: both FishNet channels
ride the reliable relay lane (above), authority migration without a
running server side tears down instead of silently stalling (above), an
authority-side `SendToClient` costs one relay broadcast per target peer,
and a fresh FishNet install needs one editor compile before the detector
can light the adapter up (above).
