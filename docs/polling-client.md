# Polling Client

`SignalFish.Client.Polling.SignalFishPollingClient` is the frame-driven
client: every `Poll()` call synchronously consumes a bounded slice of
transport work, applies session facts, and hands you the resulting events.
It has no background loop and no internal locking — construct, connect,
send, poll, and drain on one thread (the game loop's). That makes it the
right client for engines that own the main thread: a Unity `MonoBehaviour`
pumps it in `Update()` and every handler is already main-thread, with no
marshaling. For servers, tools, and anything with an async runtime, prefer
the [Async Client](client-api.md).

## Construction

```csharp
public SignalFishPollingClient(
    SignalFish.Client.Transport.ITransport transport,
    SignalFish.Client.Core.ISignalFishClock clock,
    PollingClientOptions? options = null
)
```

The transport must be unconnected — `ConnectAsync` performs the handshake.
Inject `SystemClock.Instance` for production or a virtual
`ISignalFishClock` in tests (see [Testing](testing.md)).

### `PollingClientOptions`

| Option | Type | Default | Meaning |
|---|---|---|---|
| `maxFramesPerPoll` | `int` | `64` | Maximum transport frames consumed per `Poll()`. Frames above the budget stay queued in the transport for the next poll. |
| `maxFrameBytes` | `int` | `65536` (64 KiB) | Maximum inbound frame size in bytes; larger frames are protocol violations. |
| `eventCapacity` | `int` | `256` | Event-ring capacity (minimum 3: one slot is reserved for the terminal `Disconnected` event, one for the two-event frame a delivery violation produces). A full ring pauses frame consumption until drained. |
| `heartbeatIntervalMilliseconds` | `int` | `30000` | Ping cadence; `Poll()` sends the ping when the cadence elapses. |
| `heartbeatTimeoutMilliseconds` | `int` | `60000` | Liveness timeout: no server frame within this window declares the session dead at the next poll. |
| `violationPolicy` | `DeliveryViolationPolicy` | `DeliveryViolationPolicy.Quarantine` | Response to delivery-accountability violations (see [Delivery](delivery.md)). |

## State accessors

The polling client exposes the same state surface as the async client,
read synchronously with no locking:

| Member | Type | Meaning |
|---|---|---|
| `Phase` | `ConnectionPhase` | `Connecting`, `TransportReady`, `Authenticated`, `InRoom`, or `Terminal`. |
| `IsConnected` | `bool` | The session is live (not terminal). |
| `IsAuthenticated` | `bool` | The server confirmed authentication. |
| `Membership` | `RoomMembership` | The confirmed membership; absent outside a confirmed room. |
| `Snapshot` | `ClientSnapshot` | One coherent snapshot — prefer it whenever multiple fields must describe the same instant. |
| `PendingOperation` | `PendingRoomOperation` | The in-flight directed room operation, if any. |
| `PendingEventCount` | `int` | Events waiting to be drained. |

See [Async Client](client-api.md#state-accessors) for the `ClientSnapshot`
field table; both clients share the type.

## The loop

One frame of work:

1. `Poll()` consumes up to `maxFramesPerPoll` queued transport frames
   (stopping early when the ring approaches capacity), decodes each frame
   to at most one `PollEvent`, applies session facts to the state machine,
   and runs heartbeat/liveness timing on the injected clock. It returns the
   number of frames consumed and is inert once terminal. An idle poll
   allocates nothing: the single outstanding receive is issued once and
   re-issued only when a frame was actually delivered.
2. `DrainEvents()` starts a drain walk over the pending events, oldest
   first. Enumerate it with `foreach`; breaking early keeps the unconsumed
   tail for the next drain.
3. Send commands between polls. Commands are admitted on the calling
   thread — a refused command never touches the wire — then encoded and
   handed to the transport fire-and-forget; a failed send folds back into
   the next poll as a teardown.

The complete shape, mirroring the `PollingDriver` sample shipped with the
Unity package:

```csharp
using System.Text;
using SignalFish.Client.Core;
using SignalFish.Client.Polling;
using SignalFish.Client.Protocol;
using SignalFish.Client.Transport;

var client = new SignalFishPollingClient(
    new WebSocketTransport(), SystemClock.Instance);
await client.ConnectAsync(new Uri("ws://localhost:8080")); // the sample's
// editable default; the SDK default endpoint is ws://localhost:3536/v2/ws

client.SendAuthenticate(new AuthenticateMessage(appId: "unity-sample"));
client.SendJoinRoom(new JoinRoomMessage("sample-game", "player"));

// Once per frame (Unity Update(), Mono game loop, ...):
client.Poll();
foreach (PollEvent ev in client.DrainEvents())
{
    switch (ev.Kind)
    {
        case PollEventKind.RoomJoined:
            Console.WriteLine(
                $"joined {ev.Membership.RoomCode} as {ev.Membership.Role}");
            break;
        case PollEventKind.GameData:
            Console.WriteLine(
                $"{ev.GameData.FromPlayer}: "
                + Encoding.UTF8.GetString(ev.GameData.Payload.Span));
            break;
        case PollEventKind.ServerError:
        case PollEventKind.RoomJoinFailed:
            Console.WriteLine(
                $"{ev.Kind}: {ev.Failure.Reason} ({ev.Failure.ErrorCode})");
            break;
        case PollEventKind.Disconnected:
            // Terminal: construct a fresh client to reconnect.
            break;
    }
}
```

Commands admitted after `RoomJoined` relay to the room:

```csharp
CommandSend send = client.SendGameData(
    new GameDataMessage(Encoding.UTF8.GetBytes("{\"sample\":\"hello\"}")));
if (!send.Accepted)
{
    Console.WriteLine($"command refused: {send.Refusal}");
}
```

`DisposeAsync()` tears the session down (idempotent) and disposes the
transport. The polling client never reconnects on its own; capture
`ReconnectContext.TryCapture` baselines and drive
[manual reconnection](client-api.md#reconnection) yourself.

## `PollEventKind` reference

Every `PollEvent` carries a `Kind` plus payload properties that are
meaningful only for that kind (`default` otherwise). One inbound frame
yields at most one event; frames with no event surface (heartbeat replies,
client-to-server echoes, room-operation results) are absorbed silently and
still refresh liveness. `Raw` carries the complete frame as received for
advanced inspection (empty for synthetic events).

| Kind | Payload | Meaning |
|---|---|---|
| `TransportReady` | — | The transport handshake completed; the wire is open. Synthetic (no wire frame behind it). |
| `Authenticated` | `Authenticated` | Server confirmed authentication; carries the rate-limit budgets. |
| `RoomJoined` | `Membership`, `Snapshot` | Confirmed player membership (join or create). |
| `SpectatorJoined` | `Membership`, `Snapshot` | Confirmed spectator membership. |
| `Reconnected` | `Membership`, `Snapshot` | Membership reclaimed after reconnection. |
| `RoomLeft` | — | Confirmed player exit; the connection stays open. |
| `SpectatorLeft` | `SpectatorLeft` | Confirmed spectator exit. |
| `RoomJoinFailed` | `Failure` | Typed player-join refusal. |
| `SpectatorJoinFailed` | `Failure` | Typed spectator-join refusal. |
| `ReconnectionFailed` | `Failure` | Typed reconnect refusal. |
| `ServerError` | `Failure` | Generic server error envelope, including `AuthenticationError` (its `INVALID_APP_ID` code distinguishes it). |
| `ProtocolViolation` | `Violation`, `Diagnostic` | A routed frame could not be honored: malformed session-critical fields, a known message whose payload broke the wire contract, or a delivery-accountability violation. The state machine stays fail-closed; nothing is applied for the frame. `Diagnostic` carries the accountability rule (null for transport-level violations such as an oversized or unadmitted binary frame). |
| `Disconnected` | `Close` | The transport closed or died; the session is terminal. Synthetic — delivered exactly once, last. |
| `ProtocolInfo` | `ProtocolInfo` | Server capabilities, limits, and the negotiated version. |
| `LobbyStateChanged` | `Lobby` | Lobby/readiness broadcast: state, ready players, all-ready flag. |
| `PlayerJoined` | `PlayerJoined` | A player joined the room. |
| `PlayerLeft` | `LeftPlayerId` | A player left the room. |
| `PlayerReconnected` | `LeftPlayerId` | A player's liveness was restored. |
| `GameStarting` | `GameStart` | The authority started the game; carries peer connections. |
| `AuthorityResponse` | `AuthorityResponse` | Authority grant/deny answer. |
| `AuthorityChanged` | `AuthorityChanged` | The authority moved (new authority + self flag). |
| `GameData` | `GameData` | Relayed game data: sender id + verbatim payload. |
| `NewSpectatorJoined` | `NewSpectator` | A spectator joined (spectator + roster). |
| `SpectatorDisconnected` | `SpectatorDisconnected` | A spectator dropped without leaving (id + reason + roster). |
| `UnknownMessage` | `TypeText` | An unrecognized (forward-compatible) type discriminator. |
| `DecodeFailed` | `Error`, `ErrorOffset` | The frame was malformed; carries the bounded decode error. |
| `Reconnecting` | `Reconnect` | Async client only: the opt-in policy scheduled an attempt. The polling client is caller-driven and never emits it. |
| `ReconnectAbandoned` | `Reconnect` | Async client only: the attempt budget ran out; the session ends after it. |
| `DeliveryReport` | `DeliveryReport` | Per-class delivery accounting + gap report (v3). |
| `RelayStats` | `RelayStats` | Per-interval relay accounting (v3). |
| `GoingAway` | `GoingAway` | Server draining notice: deadline + optional retry-after. |
| `SessionPlan` | `SessionPlan` | The per-recipient authoritative session plan (v3). |
| `NewPeer` | `NewPeer` | An additive WebRTC peer directive (v3). |
| `PeerTransportStatus` | `PeerTransportStatus` | A peer's reported data-path transport state (v3). |
| `Signal` | `Signal` | A peer's verbatim WebRTC signal relay (v3). |

`PollEventKind.None` exists only so the enum default is not an event; do
not branch on it (it is marked `[Obsolete]`, so comparing against it
warns).

## How the v3 lanes surface

- **Binary game data** rides physical binary frames and surfaces as a
  regular `GameData` event — always `GameDataClass.Reliable`, no class
  metadata, gated through the same delivery engine as JSON game data. It
  requires a negotiated-v3 connection with a non-JSON encoding; the
  delivery gate refuses any binary frame outside that contract before it
  can decode into game data.
- **Delivery accounting** arrives as `DeliveryReport` (per-class counters
  plus withheld ranges) and `RelayStats` (per-interval counters). A
  delivery-accountability violation surfaces as a `ProtocolViolation` event
  *ahead of* the frame's own event; what happens to the session is the
  configured `ViolationPolicy` — see [Delivery](delivery.md).
- **Mesh events** follow the negotiated `SessionPlan`: `NewPeer` adds a
  peer, `PeerTransportStatus` reports data-path state, and `Signal` relays
  a peer's verbatim WebRTC payload. Signals racing a plan replacement are
  absorbed as a benign relay-ordering race.

## Overflow behavior

The event ring never drops events. When the ring is full, `Poll()` stops
consuming transport frames until the game drains — cooperative
backpressure, so a stalled consumer shows up as transport backlog rather
than lost gameplay. Two ring slots are always reserved so a teardown on a
full ring still delivers the one event the game cannot reconstruct from
`Snapshot`: the terminal `Disconnected`. See [Events](events.md) for the
full consumption contract and [Error Handling](errors.md) for failure
handling.
