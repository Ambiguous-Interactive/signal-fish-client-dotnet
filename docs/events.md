# Events

Both clients surface the same protocol over one event type, `PollEvent` — a
readonly struct carrying the decoded typed payloads of a single inbound
frame. Underneath it sits a second, smaller vocabulary, `SessionEvent`:
the driver-level session facts the state machine reacts to (phase,
membership, fences, negotiation). This page documents both vocabularies,
the two queuing models that deliver them, and the ordering guarantees you
can build on; the client surfaces themselves are documented in
[Async Client](client-api.md) and [Polling Client](polling-client.md).

## Two event models

**Session facts (`SessionEventKind`).** `SignalFish.Client.Core.SessionEvent`
is one state-machine input: a struct (applying it never allocates) with
`Kind`, `Membership`, `ReconnectionToken`, `IsAuthority`,
`NegotiatedProtocolVersion`, and `Plan`. Only the fields meaningful for its
kind are populated. The machine applies facts internally; consumers observe
their consequences as `PollEvent`s and snapshot reads.

**Consumer events (`PollEvent`).** One inbound frame decodes to at most one
`PollEvent`; frames with no event surface (heartbeat replies,
client-to-server echoes, room-operation results) are absorbed silently and
still refresh liveness. A few events are synthetic — they have no wire
frame behind them: `TransportReady` (connect), `Disconnected`
(teardown), and the async client's `Reconnecting` / `ReconnectAbandoned`
markers.

### The async client: a bounded queue that never drops

`SignalFishClient` moves events through a bounded FIFO queue
(`SignalFishClientOptions.EventCapacity`, default 256) built on
`SignalFish.Client.Core.IBoundedQueue<T>`. The contract:

- Events are never dropped. A full queue **pauses the driver loop** — the
  backpressure contract. The pause is what trips server-side slow-consumer
  detection when a consumer stops draining.
- `TryDequeueEvent(out PollEvent)` consumes the oldest event without
  waiting; `DequeueEventAsync(ct)` awaits one. Both return the terminal
  `Disconnected` exactly once, last, even when the queue had no room for it
  (it is synthesized from the teardown close at end-of-stream).
- The queue's dequeue result type is `QueueRead<T>`: `HasValue` plus `Value`,
  where `default(QueueRead<T>)` is the end-of-stream signal — the generic
  analogue of Rust's `Option<T>`.
- `Complete()` ends the stream: further enqueues fail, buffered events stay
  consumable, and consumers drain to empty then observe completion.

### The polling client: a ring drained by the game

`SignalFishPollingClient` stores events in a fixed-capacity ring
(`PollingClientOptions.EventCapacity`, default 256, minimum 3). The
contract:

- The ring never drops events either: when full, `Poll()` stops consuming
  transport frames until the game drains. Two slots are always reserved —
  one for the terminal `Disconnected`, one for the two-event frame a
  delivery violation produces — so a teardown on a full ring still delivers
  the event the game cannot reconstruct from `Snapshot`.
- `DrainEvents()` returns an `EventDrain` ref struct: enumerate it with
  `foreach`, consuming events oldest first with zero allocation. Breaking
  early keeps the unconsumed tail; `Current` is a copy pinned between
  `MoveNext` calls.
- The ring is not thread-safe, like the whole client: one owner (the poll
  loop's thread), and nested drains are not supported.

## Ordering guarantees

- Frames are processed strictly in arrival order; the event stream order is
  deterministic under concurrency (async client) and under poll budgets
  (polling client).
- The machine state leads the event stream: a fact is applied to the state
  machine before its event is surfaced, so by the time you read
  `RoomJoined`, `Snapshot.Membership` already agrees with it.
- A delivery-accountability violation surfaces as a `ProtocolViolation`
  event *ahead of* the close/fact/payload event of the same frame.
- `Disconnected` is terminal and last: after it, the async client's stream
  returns end-of-stream and the polling client's `Poll()` is inert.

## `SessionEventKind` reference

`SignalFish.Client.Core.SessionEventKind` is the full set of session facts.
Unknown server messages never produce a session event, and the `None`
sentinel exists only so the enum default is not a fact.

### Handshake and authentication

| Kind | SessionEvent fields | When it happens |
|---|---|---|
| `TransportReady` | — | The transport handshake completed; the wire is open. Synthetic, per connection. |
| `Authenticated` | — | The server confirmed authentication. The v2 wire carries no player id here — identity is confirmed with the membership at join/reconnect time. |
| `ProtocolInfo` | `NegotiatedProtocolVersion` | The server's negotiation echo. `null` on a v2 negotiation (the extended fields are omitted on that wire). Per-connection: cleared at teardown and re-echoed on every new connection. |

### Room membership

| Kind | SessionEvent fields | When it happens |
|---|---|---|
| `RoomJoined` | `Membership`, `ReconnectionToken`, `IsAuthority` | Confirmed player membership (join or create); the baseline carries the seat, the optional server-issued reconnection token, and the authority flag. |
| `SpectatorJoined` | `Membership`, `IsAuthority` | Confirmed spectator membership; spectator baselines carry no reconnection token. |
| `Reconnected` | `Membership`, `ReconnectionToken`, `IsAuthority` | Membership reclaimed on a fresh, re-authenticated connection; the token rotates. |
| `RoomLeft` | — | Confirmed player exit; the connection stays open. |
| `SpectatorLeft` | — | Confirmed spectator exit; the connection stays open. |

### Failures and liveness

| Kind | SessionEvent fields | When it happens |
|---|---|---|
| `RoomJoinFailed` | — | Typed player-join refusal; releases only a pending `JoinPlayer` fence. |
| `SpectatorJoinFailed` | — | Typed spectator-join refusal; releases only a pending `JoinSpectator` fence. |
| `ReconnectionFailed` | — | Typed reconnect refusal; releases only a pending `ReconnectPlayer` fence. |
| `ServerError` | — | Generic server error envelope. Informational only: it never releases a fence (fail-closed) and never changes phase. |
| `Disconnected` | — | The transport closed or failed; the session is terminal. Synthetic. |

### Authority and session planning

| Kind | SessionEvent fields | When it happens |
|---|---|---|
| `AuthorityChanged` | `IsAuthority` | The room authority moved: the baseline's `is_authority` for join/reconnect kinds, the broadcast's `you_are_authority` for the change itself. |
| `SessionPlan` | `Plan` | The authoritative v3 session plan; it is the admission state for `SendSignal`. |

Lobby broadcasts, player/spectator fan-out, game start, game data, and
delivery/mesh traffic are not session facts — they surface only as
`PollEvent` payloads (below) while the machine stays put.

## `PollEvent` payload reference

Payload properties are meaningful only for their kind; everything else holds
`default`. Which property belongs to which kind is tabulated in
[Polling Client](polling-client.md#polleventkind-reference); the payload
types themselves:

| Payload type | Properties | Carried by |
|---|---|---|
| `AuthenticatedMessage` | `AppName`, `Organization`, `RateLimits` (`PerMinute`, `PerHour`, `PerDay`) | `Authenticated` |
| `ProtocolInfoMessage` | `Capabilities`, `GameDataFormats`, `ProtocolVersion?`, `MinProtocolVersion?`, `MaxProtocolVersion?`, `Transports?`, `MaxOutboundMessageSize?` | `ProtocolInfo` |
| `RoomMembership` | `Role`, `PlayerId`, `RoomId`, `RoomCode`, `IsPresent` | `RoomJoined`, `SpectatorJoined`, `Reconnected` |
| `RoomSnapshot` | `GameName?`, `MaxPlayers`, `SupportsAuthority`, `IsAuthority`, `LobbyState?`, `RelayType?`, `ReadyPlayers?`, `CurrentPlayers`, `CurrentSpectators`, `IceServers` | the membership-confirming kinds; fields the frame omitted stay at their default — the server tailors the snapshot per audience |
| `FailureMessage` | `Reason`, `ErrorCode` | `RoomJoinFailed`, `SpectatorJoinFailed`, `ReconnectionFailed`, `ServerError` |
| `TransportClose` | `Code`, `Kind` | `Disconnected` |
| `ReconnectStatus` | `Attempt`, `BackoffMilliseconds`, `LastReason?` | `Reconnecting`, `ReconnectAbandoned` |
| `LobbyStateChangedMessage` | `LobbyState`, `ReadyPlayers`, `AllReady` | `LobbyStateChanged` |
| `PlayerJoinedMessage` | `Player` (`PlayerInfo`: `Id`, `Name`, `IsAuthority`, `IsReady`, `ConnectedAt?`, `Epoch?`, `Seq?`) | `PlayerJoined` |
| `GameStartingMessage` | `PeerConnections` (`PeerConnection`: `PlayerId`, `PlayerName`, `IsAuthority`, `RelayType`, `ConnectionInfo?`) | `GameStarting` |
| `AuthorityResponseMessage` | `Granted`, `Reason?` | `AuthorityResponse` |
| `AuthorityChangedMessage` | `AuthorityPlayer?`, `YouAreAuthority` | `AuthorityChanged` |
| `IncomingGameData` | `FromPlayer`, `Payload` (verbatim, as UTF-8 bytes), `Class`, `Key`, `Seq?`, `Epoch?` | `GameData` |
| `SpectatorLeftMessage` | `RoomId`, `RoomCode`, `Reason`, `CurrentSpectators` | `SpectatorLeft` |
| `NewSpectatorJoinedMessage` | `Spectator` (`SpectatorInfo`: `Id`, `Name`, `ConnectedAt?`), `CurrentSpectators`, `Reason` | `NewSpectatorJoined` |
| `SpectatorDisconnectedMessage` | `SpectatorId`, `Reason`, `CurrentSpectators` | `SpectatorDisconnected` |
| `DeliveryReportMessage` | `PerClass` (`Reliable`, `Latest`, `Volatile` counters), `Gaps` (`DeliveryGap`: `FromPlayer`, `Epoch`, `FromSeq`, `ToSeq`) | `DeliveryReport` |
| `RelayStatsMessage` | `IntervalMs`, `SentToYou`, `DroppedForYou`, `BackpressureEvents` | `RelayStats` |
| `GoingAwayMessage` | `DeadlineMs`, `RetryAfterSecs` | `GoingAway` |
| `SessionPlanMessage` | `Generation?`, `Topology`, `Transport`, `Host?`, `DirectEndpoint?`, `Peers`, `IceServers`, `Fallback` | `SessionPlan` |
| `NewPeerMessage` | `PeerId` | `NewPeer` |
| `PeerTransportStatusMessage` | `PeerId`, `Transport`, `Connected` | `PeerTransportStatus` |
| `IncomingSignalMessage` | `From`, `Generation`, `Signal` (verbatim, as UTF-8 bytes) | `Signal` |

`ProtocolViolation` carries `Violation` (the offending `MessageKind`;
`default` for transport-level violations) and `Diagnostic` (the
accountability rule the frame broke, or `null`). `UnknownMessage` carries
`TypeText`. `DecodeFailed` carries `Error` and `ErrorOffset` — see
[Error Handling](errors.md).

## Deterministic consumption

The event stream is a contract you can test against: same frames in, same
events out, in order, with nothing dropped. Both clients accept an injected
`ISignalFishClock`, so heartbeat timing, liveness death, and backoff advance
under test control without a single real timer. See [Testing](testing.md)
for the virtual-clock pattern and the drain-loop invariants to assert.
