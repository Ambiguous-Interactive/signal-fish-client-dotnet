# Error Handling

The .NET client reports failure through several bounded, typed channels —
never through prose you have to parse:

- **`CommandSend`** — the synchronous verdict of every send method: admitted
  or locally refused with an `AdmissionError`.
- **`TransportClose` / `TransportClosedException`** — the terminal close of
  the wire, keyed by wire code (`TransportCloseKind`).
- **`DecodeError`** — bounded reasons a malformed inbound frame failed to
  decode, surfaced as a `DecodeFailed` event, never an exception.
- **`FailureMessage`** — the server's typed failures (`reason` +
  `error_code`) carried by the failure event kinds.
- **`DeliveryViolationPolicy`** — the configured response to
  delivery-accountability violations (see [Delivery](delivery.md)).
- **`ReconnectStatus` / `ReconnectAction`** — automatic-reconnection
  scheduling and the manual recovery decision tree.

Nothing in the SDK throws for protocol-level failures; the wire is decoded
totally and every anomaly is classified. Local throws are limited to misuse
(disposed client, double connect, invalid option values) and to malformed
caller payloads — `GameDataMessage` validates its JSON at construction, so
a refusal precedes every send.

## `CommandSend`: local admission

Every send method returns `CommandSend` synchronously. `Accepted` is true
when the command passed admission and holds a queue slot; a refused command
never touches the wire and names why via `Refusal`. `default(CommandSend)`
is the accepted verdict.

```csharp
CommandSend send = client.SendGameData(
    new GameDataMessage(Encoding.UTF8.GetBytes("{\"move\":1}")));
if (!send.Accepted)
{
    Console.WriteLine($"{send.Refusal}"); // e.g. "NotInRoom"
}
```

### `AdmissionError`

| Value | When it happens |
|---|---|
| `NotConnected` | No live session: never connected, terminal, or (async) severed mid-reconnect. Wins first, before every other verdict. |
| `NotAuthenticated` | A directed room operation (`SendJoinRoom`, `SendJoinAsSpectator`, `SendReconnect`, `SendLeaveRoom`, `SendLeaveSpectator`) was sent before the server confirmed authentication. Wait for `Authenticated`. |
| `RoomOperationPending` | A previously admitted directed operation still awaits its typed result. The machine is fenced: every command except the heartbeat and an `Authenticate` handshake is refused until the typed success/failure or session teardown releases it. |
| `AlreadyInRoom` | A join-style command (join or reconnect) was refused because this connection is already a confirmed room member. |
| `NotInRoom` | A room-scoped command (leave, game data, ready, start, authority, ...) was refused because there is no confirmed membership. |
| `WrongRoomRole` | The command requires the other room role (a player-only command as a spectator, or vice versa). |
| `SendBufferFull` | The bounded command queue is full (async client, `commandCapacity`). Fail-fast sends queue nothing — the message is not silently dropped. `SendGameDataReliableAsync` waits for a slot instead. The polling client has no command queue and never reports this. |
| `AuthorityRequired` | `SendAuthorityRequest(false)` was refused: this connection does not hold the authority. |
| `ProtocolUnsupported` | A v3-only send was refused because the connection has not negotiated v3 (either `ProtocolInfo` has not arrived yet, or it negotiated the v2 relay floor). Checked last — membership and role verdicts take precedence. |
| `BinaryFormatNotNegotiated` | A `SendBinaryGameData` was refused because the session negotiated the JSON game-data encoding (or none). Checked after the v3 verdict — the encoding rides a negotiated-v3 connection. |
| `SessionPlanUnavailable` | A `SendSignal` was refused: no `SessionPlan` has been observed on this connection, the selected transport is not webrtc, or the target is not a plan peer. |
| `StaleSessionGeneration` | A `SendSignal` was refused because its generation does not match the latest plan's generation (a re-plan superseded it). Stamp signals from the current plan's generation. |

`None` is a sentinel so the enum default is not an error; branch on
`Accepted`, not on `Refusal == AdmissionError.None`.

### Handling `SendBufferFull`

Four remedies, in order of preference:

1. Pace with `SendGameDataReliableAsync`, which waits for a slot instead of
   failing (and reports `NotConnected` if the session ends while waiting).
2. Retry the next frame/tick; watch `SendCapacity` drain toward
   `MaxSendCapacity`.
3. Raise `commandCapacity` for more burst headroom.
4. Drain events promptly — the command queue only drains while the driver
   loop runs, and the loop pauses when the event queue is full (events are
   never dropped), so a wedged consumer eventually stalls sends too.

```csharp
GameDataMessage input = new GameDataMessage(
    Encoding.UTF8.GetBytes("{\"input\":{\"frame\":1042}}"));

CommandSend send = client.SendGameData(input);
if (!send.Accepted && send.Refusal == AdmissionError.SendBufferFull)
{
    // Queue full: wait for a slot instead of dropping the payload.
    CommandSend retried = await client.SendGameDataReliableAsync(input);
}
```

## Transport closes

The terminal close is data, not prose. `TransportClose` carries the raw
`Code` plus its server-defined meaning, `TransportCloseKind`. It surfaces on
the final `Disconnected` event (`ev.Close`) and inside
`TransportClosedException` for code that touches a dead transport directly.

### `TransportCloseKind`

| Kind | Wire code | Meaning and reaction |
|---|---|---|
| `Normal` | 1000 | Normal closure. On a live connection the transport's dispose performs the WebSocket close handshake with this code; the session-level close the client synthesizes for its own teardown reports code 0 (`None`). |
| `Abnormal` | 1006 | Locally observed abnormal closure — never sent on the wire. The client declares the session dead itself (heartbeat timeout, socket failure) with this code. |
| `MessageTooBig` | 1009 | `outbound_message_too_large`. |
| `ServerShutdown` | 4000 | `server_shutdown` — surface it; retry later. |
| `AuthTimeout` | 4001 | `auth_timeout` — reconnect and authenticate promptly. |
| `SlowConsumer` | 4002 | `slow_consumer` — back off and shrink the send rate. |
| `ActivityTimeout` | 4003 | `activity_timeout` — the heartbeat was missed. |
| `IdleTimeout` | 4004 | `idle_timeout` — same reaction as `ActivityTimeout`. |
| `RoomInactive` | 4005 | `room_inactive` — the room is gone; rejoin from scratch. |
| `InboundRateLimited` | 4006 | `inbound_rate_limited` — honor the `Authenticated` rate-limit budgets. |
| `Kicked` | 4007 | `kicked` — never auto-rejoin; surface it to the user. |
| `Unknown` | — | Any code outside the server-defined table. |

`None` is the sentinel for the struct default (code 0, no close).

### `TransportClosedException`

```csharp
public sealed class TransportClosedException : InvalidOperationException
{
    public TransportClose Close { get; }
}
```

Thrown when the transport is used after its terminal close was already
surfaced, or when an operation races a close and loses. Client code driving
`SignalFishClient` or `SignalFishPollingClient` normally never sees it —
the clients fold a dead wire into the session as a `Disconnected` event —
but transports are public: code owning an `ITransport` directly must expect
it and can key recovery on `ex.Close.Code`.

```csharp
case PollEventKind.Disconnected:
    switch (ev.Close.Kind)
    {
        case TransportCloseKind.RoomInactive:
            // The room is gone: rejoin from scratch.
            break;
        case TransportCloseKind.Kicked:
            // Never auto-rejoin: surface the kick to the user.
            break;
        default:
            // Retryable: schedule a reconnect.
            break;
    }
    break;
```

## Decode failures

The decoder is total: malformed input produces a bounded `DecodeError` plus
a byte offset — never an exception. A failed decode surfaces as a
`DecodeFailed` event carrying `Error` and `ErrorOffset`, with the complete
raw frame in `Raw` for diagnostics. The session state machine stays
fail-closed: nothing is applied for that frame.

### `DecodeError`

JSON envelope lane:

| Value | When |
|---|---|
| `Truncated` | The input ended before a complete frame was read. |
| `NotAnObject` | The frame root is not a JSON object (the envelope contract). |
| `MissingType` | The envelope carries no `type` member. |
| `TypeNotString` | The `type` member is not a JSON string. |
| `EmptyType` | The `type` member is an empty string. |
| `DataNotObject` | The `data` member is present but not a JSON object (JSON `null` is tolerated as absent). |
| `InvalidToken` | A malformed JSON token (bad escape, bad number, invalid UTF-8, wrong delimiter, ...). |
| `DepthExceeded` | Nesting exceeded `EnvelopeReader.MaxDepth` (128). |
| `TrailingContent` | Non-whitespace content follows the envelope object. |

Binary game-data lane (MessagePack):

| Value | When |
|---|---|
| `NotAMap` | The binary frame's root is not a MessagePack map. |
| `UnknownField` | The map carries a key outside the frame's key set. |
| `DuplicateField` | A known map key repeats. |
| `MissingField` | A required map key is absent. |
| `InvalidFieldValue` | A map value violates its field contract (shape, token, or stamp). |

`None` is the sentinel; a `DecodeFailed` event always carries a real error.

Related but distinct: `UnknownMessage` (an unrecognized type discriminator —
forward compatibility, not a failure) and `ProtocolViolation` (a *routed*
frame that could not be honored: session-critical fields malformed, a known
message whose payload broke the wire contract, or a delivery-accountability
violation — see [Delivery](delivery.md)). Steady `DecodeFailed` growth
usually means protocol drift or a corrupting middlebox.

## Server failures: `FailureMessage`

The server's failure family shares one payload: `Reason` (human-readable
prose — the wire names the field `reason`, `message`, or `error` depending
on the message; the decoder accepts all three) and `ErrorCode` (always
`error_code`, a stable machine-readable token). Routing never keys on the
prose text. `error_code` is optional on `Error` and the join failures —
`ErrorCode` is then empty, so handle the refusal by event kind rather than
by code; `AuthenticationError` and `ReconnectionFailed` always carry one.

The family surfaces as four event kinds, each carrying `ev.Failure`:

- `RoomJoinFailed` — the join was refused (room full, bad password, ...).
- `SpectatorJoinFailed` — the spectator join was refused.
- `ReconnectionFailed` — the seat reclaim was refused; classify the code
  with `ReconnectRecovery.Classify` (below).
- `ServerError` — everything else, including the `Error` envelope and
  `AuthenticationError` (its `INVALID_APP_ID` code distinguishes it).

`ErrorCode` values are server-defined strings (the .NET client verifies
tokens such as `RECONNECTION_EXPIRED`, `PLAYER_ALREADY_CONNECTED`, and
`INVALID_APP_ID`, all read verbatim); unknown codes from a newer server are
preserved, not rejected. A `ServerError` is informational: it never releases
a room operation fence and never changes phase — the machine stays
fail-closed until the typed result or teardown.

```csharp
case PollEventKind.ReconnectionFailed:
    switch (ReconnectRecovery.Classify(ev.Failure.ErrorCode))
    {
        case ReconnectAction.FreshJoin:
            // Server no longer knows this seat: join again as a new player.
            break;
        case ReconnectAction.WaitForSeat:
            // Another live connection holds the seat: wait for it to exit.
            break;
        default:
            // Retry with backoff while the room is worth rejoining.
            break;
    }
    break;
```

`ReconnectAction.FreshJoin` covers `RECONNECTION_EXPIRED` and
`RECONNECTION_TOKEN_INVALID`; `WaitForSeat` covers
`PLAYER_ALREADY_CONNECTED`; anything else — transient refusal, drain, ban,
unknown future code — is `ReconnectAction.RetryWithBackoff`.

## Delivery violations

A decoded server frame that breaks the negotiated-v3 delivery
accountability (sender stamps, gap ranges, lifecycle watermarks) always
surfaces as a `ProtocolViolation` event with its diagnostic. What happens
to the session is the configured `DeliveryViolationPolicy`:

| Value | Reaction |
|---|---|
| `Quarantine` (default) | Emit the violation and suppress the room's game data until the next authoritative rebaseline; the session stays usable. The suppressed state is exposed as `Snapshot.Quarantined`. |
| `Disconnect` | Emit the violation and tear the signaling connection down. A policy teardown is never reconnected. |
| `Observe` | Emit the violation and continue; frames keep their documented delivery behavior. Authoritative baselines that fail validation are still refused — a broken roster must never replace the client's cursors. |

`None` is a sentinel so the enum default is not a policy; the options
constructors reject it.

See [Delivery](delivery.md) for the accountability model and
[Protocol Versioning](protocol-versioning.md) for when the contract applies.

## Reconnection outcomes

With an opt-in [ReconnectPolicy](client-api.md#automatic-reconnection-opt-in),
the async client reports its progress on the event stream. Each marker
carries a `ReconnectStatus` (`ev.Reconnect`):

| Property | `Reconnecting` | `ReconnectAbandoned` |
|---|---|---|
| `Attempt` | The 1-based attempt about to start. | The number of attempts spent. |
| `BackoffMilliseconds` | The deterministic wait before the announced attempt. | `0`. |
| `LastReason` | `null`. | Why the budget ran out (the last close description). |

The budget (`maxAttempts`, default 5) resets whenever a connection reaches
the authenticated phase. The markers arrive in a fixed order: the death's
own `Disconnected` marker first, then one `Reconnecting` per attempt, then
`ReconnectAbandoned`, then the stream ends. The manual-recovery decision
tree for `ReconnectionFailed` error codes is `ReconnectRecovery.Classify`,
shown above; the close codes that should skip retrying entirely can be
listed with `ReconnectPolicy.WithTerminalCloseCodes`.

!!! note "Local versus server errors"
    | Channel | Type | When |
    |---|---|---|
    | Send-method return | `CommandSend` | Immediate local refusals: wrong phase, role, fence, format, or a full queue. |
    | Event stream | `DecodeFailed`, `ProtocolViolation`, `ServerError`, failure kinds, `Disconnected` | Asynchronous anomalies and server-reported failures. |

Both layers exist because a refused command never reached the server, while
an event always did.
