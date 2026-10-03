# Async Client

`SignalFish.Client.Async.SignalFishClient` is the event-driven client: one
background driver loop multiplexes command sends, frame receives, and
heartbeat timing over an [`ITransport`](transport.md) while your code queues
commands and consumes events from any thread. Commands are admitted
synchronously against a session state machine (a refused command never
touches the wire), and events surface through a bounded queue that never
drops — a full queue pauses the loop instead. This page is the reference for
the client itself, its options, and its lifecycle; the event stream is
documented in [Events](events.md) and failure modes in
[Error Handling](errors.md).

## Construction

```csharp
public SignalFishClient(
    SignalFish.Client.Transport.ITransport transport,
    SignalFish.Client.Core.ISignalFishClock clock,
    SignalFishClientOptions? options = null
)
```

The transport must be unconnected — `ConnectAsync` performs the handshake.
Pass `SystemClock.Instance` for production; tests inject a virtual
`ISignalFishClock` so no test waits on a real timer (see
[Testing](testing.md)).

### Thread model

The client is thread-safe: a single driver loop owns the transport, and
`lock`-guarded accessors keep state reads coherent. Commands queue from any
thread, and frames are processed strictly in arrival order, so the event
stream order is deterministic under concurrency. Heartbeats are automatic:
the loop pings every `HeartbeatIntervalMilliseconds` and declares the session
dead — tearing down with close code 1006 (`Abnormal`) — when no server frame
arrives within `HeartbeatTimeoutMilliseconds`.

### `SignalFishClientOptions`

Every parameter has a documented default; all are read-only properties on
the options object after construction.

| Option | Type | Default | Meaning |
|---|---|---|---|
| `eventCapacity` | `int` | `256` | Event-queue capacity. A full queue pauses the driver loop until drained; events are never dropped. |
| `commandCapacity` | `int` | `1024` | Command-queue capacity. A full queue fails fast sends (`SendBufferFull`); `SendGameDataReliableAsync` waits for a slot instead. |
| `maxFrameBytes` | `int` | `65536` (64 KiB) | Maximum inbound frame size in bytes; larger frames are protocol violations. |
| `heartbeatIntervalMilliseconds` | `int` | `30000` | Ping cadence (elapsed since the last ping sent). |
| `heartbeatTimeoutMilliseconds` | `int` | `60000` | Liveness timeout: no server frame within this window tears the session down. |
| `commandsPerWake` | `int` | `64` | Maximum queued commands sent per driver-loop wake. |
| `shutdownTimeoutMilliseconds` | `int` | `1000` | Graceful shutdown budget: disposing inside a room first sends the role's leave and waits up to this long for the typed confirmation. Zero aborts immediately. |
| `reconnectPolicy` | `ReconnectPolicy?` | `null` | Opt-in automatic reconnection; `null` keeps recovery fully manual (see [Reconnection](#reconnection)). |
| `appId` | `string?` | `null` | Public app ID sent on every `Authenticate`; `null` omits it (open deployments accept the handshake without one). |
| `connectToken` | `string?` | `null` | Optional tenant credential sent on every `Authenticate`. A secret: `ToString()` redacts it; rotate by issuing a fresh token and building new options. |
| `sdkVersion` | `string` | `SignalFishClientInfo.SdkVersion` | SDK version string sent on every `Authenticate`. |
| `platform` | `string` | `SignalFishClientInfo.Platform` | Platform token (`"dotnet"`) sent on every `Authenticate`. |
| `protocolVersion` | `uint?` | `null` | Highest protocol version advertised on every automatic-reconnect `Authenticate`; `null` keeps the v2 floor. Mirror your explicit first `SendAuthenticate` here — a revived round advertising less would renegotiate down. See [Protocol Versioning](protocol-versioning.md). |
| `supportedTransports` | `IReadOnlyList<string>?` | `null` | Data-path transport tokens advertised on reconnect handshakes; must include `"relay"` when set. |
| `supportedTopologies` | `IReadOnlyList<string>?` | `null` | Session topology tokens advertised on reconnect handshakes. |
| `requestedCapabilities` | `IReadOnlyList<string>?` | `null` | Additive capability tokens; unknown tokens are ignored by the server. |
| `violationPolicy` | `DeliveryViolationPolicy` | `DeliveryViolationPolicy.Quarantine` | Response to delivery-accountability violations (see [Delivery](delivery.md)). |
| `gameDataFormat` | `string?` | `null` | Preferred game-data encoding token; `null` or `"json"` means the JSON text default. Binary sends (`SendBinaryGameData`) require a non-JSON encoding the server advertises. |

Invalid values throw `ArgumentOutOfRangeException` at construction — a
malformed token list fails fast instead of killing a reconnect round later.

## State accessors

| Member | Type | Meaning |
|---|---|---|
| `Phase` | `ConnectionPhase` | `Connecting`, `TransportReady`, `Authenticated`, `InRoom`, or `Terminal`. |
| `IsConnected` | `bool` | The session is live (not terminal). |
| `IsAuthenticated` | `bool` | The server confirmed this connection's handshake. |
| `Membership` | `RoomMembership` | Confirmed `Role`, `PlayerId`, `RoomId`, and `RoomCode` — set together by a confirmed join, cleared together by a confirmed exit. Absent (`IsPresent == false`) outside a room. |
| `Snapshot` | `ClientSnapshot` | One coherent snapshot of all of the above plus the reconnection token, authority, negotiation, and quarantine state. Prefer it whenever multiple fields must describe the same instant. |
| `PendingOperation` | `PendingRoomOperation` | The in-flight directed room operation, if any. |
| `PendingEventCount` | `int` | Events waiting to be dequeued. |
| `SendCapacity` | `int` | How many more commands can queue before fail-fast sends report full. |
| `MaxSendCapacity` | `int` | The configured command-queue capacity. |

`ClientSnapshot` properties:

| Property | Type | Meaning |
|---|---|---|
| `Connected` | `bool` | True until the session goes terminal. |
| `TransportReady` | `bool` | Handshake observed on the current connection (sticky until teardown). |
| `Authenticated` | `bool` | Server confirmed authentication on this connection. |
| `Role` | `RoomRole?` | Confirmed room role; `null` outside a confirmed room. |
| `PlayerId` | `Guid?` | This client's player or spectator id; `null` outside a room. |
| `RoomId` | `Guid?` | Server-assigned room id; `null` outside a room. |
| `RoomCode` | `string?` | Human-shareable room code; `null` outside a room. |
| `ReconnectionToken` | `string?` | Latest server-issued reconnection token. A bearer seat credential: never log it — `ToString()` redacts it. |
| `IsAuthority` | `bool` | This connection is the confirmed room authority. |
| `NegotiatedProtocolVersion` | `uint?` | The server's cap-down echo; `null` before `ProtocolInfo` or on a v2 negotiation. |
| `Quarantined` | `bool` | The quarantine policy is suppressing the room's game data after a delivery violation. |

## Lifecycle

### Connect

```csharp
public async Task ConnectAsync(Uri endpoint, CancellationToken ct = default)
```

Connects the transport, marks the session transport-ready (enqueuing a
`TransportReady` event), and starts the driver loop. It may be called once
per client instance — a second call throws `InvalidOperationException`, and
a failure leaves the session unusable: construct a fresh client over a fresh
transport to retry.

### Send commands

Every send returns `CommandSend` synchronously: `Accepted` is true when the
command passed admission and holds a queue slot; otherwise `Refusal` names
why (`AdmissionError`, see [Error Handling](errors.md)). Admission and
queuing are one atomic step, so a refused command never reaches the wire and
never wedges a fence.

| Method | Payload | Answer events |
|---|---|---|
| `SendAuthenticate(in AuthenticateMessage)` | App identity + v3 advertisement | `Authenticated`, `ProtocolInfo` |
| `SendJoinRoom(in JoinRoomMessage)` | Game name, player name, optional room code / limits / password | `RoomJoined` or `RoomJoinFailed` |
| `SendJoinAsSpectator(in JoinAsSpectatorMessage)` | Game name, room code, spectator name, optional password | `SpectatorJoined` or `SpectatorJoinFailed` |
| `SendReconnect(in ReconnectMessage)` | Player id, room id, auth token | `Reconnected` or `ReconnectionFailed` |
| `SendPlayerReady()` | — | `LobbyStateChanged` |
| `SendAuthorityRequest(bool becomeAuthority)` | Become / relinquish | `AuthorityResponse`, and `AuthorityChanged` on a move |
| `SendStartGame()` | — | `GameStarting` fan-out when the server accepts; a refusal arrives as `ServerError` |
| `SendLeaveRoom()` | — | `RoomLeft` |
| `SendLeaveSpectator()` | — | `SpectatorLeft` |
| `SendGameData(in GameDataMessage)` | Verbatim JSON payload + optional v3 class | relayed to peers (`GameData` events) |
| `SendBinaryGameData(ReadOnlyMemory<byte> payload)` | One raw binary frame, verbatim | relayed as reliable game data |
| `SendSignal(in SignalMessage)` | Opaque WebRTC signal for one plan peer | `Signal` on the recipient |
| `SendTransportStatus(in TransportStatusMessage)` | Local data-path state | `PeerTransportStatus` fan-out |
| `SendProvideConnectionInfo(in ProvideConnectionInfoMessage)` | Self-declared engine connection info | repeated to peers (the raw material a `host` + `direct` plan is projected from) |

Notes:

- In allowlist mode `SendAuthenticate` must be the first message; in open
  mode it is optional but must precede every application message when used.
  The server, not this client, rejects a late or repeated handshake.
- `SendJoinRoom`, `SendJoinAsSpectator`, `SendReconnect`, `SendLeaveRoom`,
  and `SendLeaveSpectator` are directed operations: admission arms a fence
  (`PendingOperation`) and every other command is refused with
  `RoomOperationPending` until the typed result or session teardown releases
  it. Only the heartbeat and an `Authenticate` handshake bypass the fence.
  The fence arms only after the command holds a queue slot, so a full
  queue can never wedge it.
- `SendGameData` fails fast with `SendBufferFull` when the command queue is
  full — nothing is queued and nothing is dropped. The backpressure-aware
  counterpart is:

  ```csharp
  public async Task<CommandSend> SendGameDataReliableAsync(
      GameDataMessage message,
      CancellationToken ct = default
  )
  ```

  It waits for a slot instead of failing fast, pacing the caller to actual
  transport throughput — the recommended shape for high-rate payloads. If
  the session ends while waiting, the verdict is `NotConnected`.
- `SendBinaryGameData` and `SendSignal` are negotiated-v3 sends: the first
  requires a non-JSON game-data encoding (`BinaryFormatNotNegotiated`
  otherwise), the second requires a live `SessionPlan` targeting a plan peer
  (`SessionPlanUnavailable` / `StaleSessionGeneration` otherwise). See
  [Protocol Versioning](protocol-versioning.md).

### Consume events

```csharp
public bool TryDequeueEvent(out PollEvent pollEvent)
public async ValueTask<PollEvent?> DequeueEventAsync(
    CancellationToken ct = default
)
```

`TryDequeueEvent` returns `false` when nothing is buffered right now.
`DequeueEventAsync` awaits the next event and returns `null` once the
session is terminal and every event was consumed — including the terminal
`Disconnected`, which is delivered exactly once, last. Consumers must drain
continuously: the driver loop pauses on a full event queue, so an abandoned
consumer eventually stalls the session's outbound work (which is what trips
server-side slow-consumer detection).

### Dispose

```csharp
public async ValueTask DisposeAsync()
```

Idempotent; callable from any thread. Disposing inside a room stages a
graceful shutdown within `shutdownTimeoutMilliseconds`: the role's leave
goes out first and the teardown waits for its typed confirmation (a pending
directed operation or a full command queue skips the stage). The transport
is disposed either way. A zero budget, an already-terminal session, and a
client never connected all tear down immediately. Sends after this throw
`ObjectDisposedException` from the moment disposal starts.

## Reconnection

Recovery is **fully manual by default**. The procedure:

1. Persist the seat triple with `ReconnectContext.TryCapture(snapshot,
   out ReconnectContext context)` right after every `RoomJoined` and
   `Reconnected` — it succeeds only for a confirmed player baseline
   carrying a token (spectators cannot reconnect). The captured context
   holds `PlayerId`, `RoomId`, and `Token`; the token is a bearer seat
   credential that `ToString()` redacts.
2. On an unexpected `Disconnected`, build a fresh transport and client,
   connect, and authenticate.
3. Call `SendReconnect(new ReconnectMessage(playerId, roomId, token))` and
   wait for `Reconnected` or `ReconnectionFailed`. Persist the rotated
   token from the new baseline.
4. Classify a failure with `ReconnectRecovery.Classify(failure.ErrorCode)`:
   `ReconnectAction.FreshJoin` (`RECONNECTION_EXPIRED`,
   `RECONNECTION_TOKEN_INVALID`), `ReconnectAction.WaitForSeat`
   (`PLAYER_ALREADY_CONNECTED`), or `ReconnectAction.RetryWithBackoff`
   (everything else, including unknown future codes).

### Automatic reconnection (opt-in)

Supply a `ReconnectPolicy` to automate the transport-and-authentication
core:

```csharp
public ReconnectPolicy(
    Func<ITransport> transportFactory,
    int initialBackoffMilliseconds = 500,
    int maxBackoffMilliseconds = 8000,
    double multiplier = 2.0,
    int maxAttempts = 5
)
```

After a retryable disconnect the driver emits `Reconnecting` (carrying the
attempt number and the deterministic backoff), waits, opens a fresh
transport from the factory, re-authenticates with the configured
credentials, and reclaims a retained player seat automatically. There is no
jitter — the SDK carries no RNG; de-synchronize retry storms by varying the
initial delay per client or add jitter inside the factory. The attempt
budget resets whenever a connection reaches the authenticated phase. When
the budget runs out the driver emits `ReconnectAbandoned` and the session
ends. `policy.WithTerminalCloseCodes(params int[])` derives a policy that
ends the session instead of retrying when a close carries a listed code
(a kick, for example); unlisted codes — notably 4000, a server going away —
keep reconnecting. A `DeliveryViolationPolicy.Disconnect` teardown is never
reconnected. See [Error Handling](errors.md) for the close codes and
[Delivery](delivery.md) for the violation policy.

The polling client is caller-driven and never reconnects automatically; see
[Polling Client](polling-client.md).

## Minimal session

```csharp
using SignalFish.Client.Async;
using SignalFish.Client.Core;
using SignalFish.Client.Protocol;
using SignalFish.Client.Transport;
using System.Text;

var client = new SignalFishClient(
    new WebSocketTransport(), SystemClock.Instance);
await client.ConnectAsync(new Uri("ws://localhost:3536/v2/ws"));

client.SendAuthenticate(new AuthenticateMessage(appId: "my-app"));
client.SendJoinRoom(new JoinRoomMessage("my-game", "Alice"));

while (await client.DequeueEventAsync() is PollEvent ev)
{
    switch (ev.Kind)
    {
        case PollEventKind.RoomJoined:
            // ev.Membership carries the confirmed seat.
            break;
        case PollEventKind.GameData:
            Console.WriteLine(
                $"{ev.GameData.FromPlayer}: "
                + Encoding.UTF8.GetString(ev.GameData.Payload.Span));
            break;
        case PollEventKind.Disconnected:
            // Terminal: ev.Close carries the close code; the stream ends.
            break;
    }
}

await client.DisposeAsync();
```

Real call patterns live in [Getting Started](getting-started.md) and
[Examples](examples.md); deterministic consumption is covered in
[Testing](testing.md).
