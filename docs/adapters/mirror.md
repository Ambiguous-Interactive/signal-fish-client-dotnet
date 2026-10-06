# Mirror Transport

The Mirror adapter (M8.2) puts a Mirror session on a Signal Fish room:
the package ships `SignalFishMirrorTransport`, a Mirror `Transport`
implementation whose wire is the room's v3 binary game-data lane. Mirror
keeps owning the game netcode; Signal Fish owns the parts it is good at —
rooms, membership, heartbeats, reconnection, and the relay.

The adapter is a separate UPM package:
[unity/Adapters/Mirror](https://github.com/Ambiguous-Interactive/signal-fish-client-dotnet/tree/main/unity/Adapters/Mirror)
(`com.ambiguous-interactive.signalfish.transport.mirror`). It never
references Mirror sources: an editor define detector probes for the
compiled `Mirror` assembly and toggles `SIGNALFISH_MIRROR` across build
targets, and the assembly's `defineConstraints` skip it when the define
is absent — the package is inert without Mirror, and the `Mirror`
assembly reference can never dangle. (Mirror ships as an asset — Asset
Store or a zip under `Assets/` — with no UPM package, so unlike the
FishNet adapter there is no `versionDefines` pin: the detector is the
define's only owner.) After a fresh Mirror install, let the editor
compile once; the detector lights the adapter up on its next pass.

## How the bridge maps Mirror onto a room

Mirror is a star: clients talk only to the server. The Signal Fish relay
is a room broadcast. The adapter reconciles the two with roles:

- The room **authority** plays the Mirror **server**; every other
  member plays a Mirror **client**.
- Each relayed frame carries an 18-byte adapter header — target player,
  Mirror channel, version — ahead of the Mirror segment, which rides
  verbatim.
- The authority consumes its peers' upstream frames into its server
  feed; clients consume only the authority's frames addressed to them
  (or broadcast). Everything else is dropped by rule, which is what
  makes a broadcast relay behave like a star.
- **Host mode needs no loopback**: Mirror's local connection delivers
  the host's own client traffic in-process, never through the transport.
- `GetMaxPacketSize` reports the client's inbound frame budget minus the
  adapter wire reserve, so a full-size message still decodes on every
  recipient.

Both Mirror channels ride the relay's **reliable** binary lane in this
version. The channel id is preserved end-to-end — Mirror sees
`Channels.Unreliable` traffic arrive on the channel it expects — but
delivery is reliable: the relay floor has no volatile binary class yet,
and a reliable superset of unreliable semantics (everything arrives, in
order) beats silently dropping frames a game marked reliable.

## Install and set up

1. Install the packages: `com.ambiguous-interactive.signalfish`, the
   shared adapter core `com.ambiguous-interactive.signalfish.adapters.core`,
   the Mirror asset, then this adapter, and let Unity compile twice (see
   the detector note above).
2. Add a `NetworkManager` and put `SignalFishMirrorTransport` on the same
   GameObject, under the transport list.
3. Fill the session fields: `Endpoint` (the v2 relay floor endpoint;
   v3 is negotiated on top), `GameName`, and `PlayerName`.
4. Start host or client through Mirror as usual. The transport joins the
   room, negotiates the v3 binary format, and requests the authority on
   the host path; Mirror sees connections only once the room is live.
   For a client, the NetworkManager's **address is the room code**.
5. Or wire `SignalFishRoomManager` (shipped in the package) and call
   `StartHost()` / `StartClient(roomCode)` — the thinnest glue between
   Mirror's API and the room, with `JoinedRoomCode` for sharing the code.

The late-join and reconnect sample (package **Samples~**) shows the
smallest end-to-end loop: one machine hosts, guests join by code at any
time, and a dropped guest rejoins automatically.

### Authority moves and failed starts

The room can hand the authority to another member (for example when the
host leaves). Mirror's server half cannot appear by itself, so the bridge
**tears the session down loudly** if this machine becomes the authority
without a running server side: re-host to become the hub again. Mirror
has no "server failed to start" callback, so a failed bootstrap drops the
server's active flag, disconnects every staged peer, and surfaces the
reason on the transport's `StartupError` — poll it (or log it in the
RoomManager) rather than guessing.

## Configuration surface

| Member | Meaning |
| --- | --- |
| `Endpoint` / `GameName` / `PlayerName` | The Signal Fish session identity. |
| `RoomCode` | The room code the **server side** creates-or-joins; empty creates a room. Client joins take the code from the NetworkManager address instead. |
| `AppId` / `ConnectToken` | Sent on every `Authenticate`; the token is a secret — never log it. |
| `MaxFrameBytes` | The client's inbound frame bound; the basis of the reported packet size. |
| `TransportFactory` | Builds the Signal Fish connection's `ITransport`. WebGL builds must return the package's browser-WebSocket transport here. |
| `JoinedRoomCode` | The joined room's shareable code. |
| `OutboundDropped` / `RoutedDropped` / `PeerCount` | Live diagnostics. |

Relay backpressure reads as Mirror send backpressure: when the Signal
Fish command queue is full, frames wait at the adapter's queue head and
retry on the next late update — never silent loss; a
`BinaryFormatNotNegotiated` refusal tears the session down loudly instead
of queuing forever.

## Validation status

Unity is never built in CI (a locked project decision). The
CI-compilable part is the adapter's pure core — the wire header, peer
router, receive rules, and MTU math — which runs in the dotnet test
suite and compiles standalone (netstandard2.1, C# 9, warnings as errors)
in the `lint-unity-adapter` CI gate. The gate also pins the
define-guard contract (no Mirror reference outside
`#if SIGNALFISH_MIRROR`, no vendored SDK sources, the define-ownership
pin, the detector contract, declared sample paths) and the bridge's
member completeness against the pinned Mirror 96.9.x `Transport`
surface.

The bridge itself is authored against the Mirror 96.9.x transport
contract and the library's conformance suite; **live two-client
validation in Unity runs the [validation
runbook](../unity-validation.md#mirror-m82) drill** (pending a
licensed Unity seat). Treat unvalidated behavior as the honest unknown
it is — the same status the WebGL transport shipped under before M7.4
validation.

Known, deliberate limitations in this version: both Mirror channels ride
the reliable relay lane (above), authority migration without a running
server side tears down instead of silently stalling (above), an
authority-side `ServerSend` costs one relay broadcast per target peer,
and a fresh Mirror install needs one editor compile before the detector
can light the adapter up (above).
