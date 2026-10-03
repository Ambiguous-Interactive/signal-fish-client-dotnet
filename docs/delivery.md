# Delivery & Backpressure

What happens to a message between `SendGameData` on one client and the
event another client drains: the v3 delivery classes, the server stamps
and gap reports the client validates, where delivery surfaces on each
client, and every bounded queue between your code and the wire.

## Delivery classes

JSON `GameData` carries an optional v3 classification
(`SignalFish.Client.Protocol.GameDataClass`, wire tokens
`reliable`/`latest`/`volatile`; the server answers
`INVALID_DELIVERY_CLASS`/`INVALID_INPUT` for anything else):

| Class | Value | Wire behavior | Intended use |
|-------|-------|---------------|--------------|
| `GameDataClass.Reliable` | 1 | The default: omits the metadata entirely and reproduces the v2 relay-floor wire form. Preserve the message or close the slow recipient loudly. | Control and state transitions that must not vanish. |
| `GameDataClass.Latest` | 2 | Coalescing: the server retains only the newest queued value per sender-defined `key`. | Replaceable state such as position or aim. |
| `GameDataClass.Volatile` | 3 | Opportunistic: dropped under pressure rather than backlogged; never paces the sender. | Ephemeral effects and frequent samples. |

Raw binary game data (`SendBinaryGameData`) carries no class metadata:
it is **always reliable** and shares the sender's per-room sequence
stream with their JSON frames (`BinaryGameDataFrame`).

## Sequence stamps and accountability

On a negotiated-v3 connection the server stamps every relayed frame with
the sender's `seq` (per-room relay sequence, `ulong`) and `epoch`
(incarnation, `uint`): inbound JSON frames expose them as
`IncomingGameData.Seq`/`IncomingGameData.Epoch` (`null` on the v2 wire),
and the binary envelope carries them as mandatory non-zero fields. The
v2 wire omits both.

`DeliveryGate` and `DeliveryAccountability` validate the stamp stream
per connection: sender baselines from the room/reconnect snapshots
(`SenderBaseline`: `PlayerId`, `Epoch`, `Seq`), joined/left/reconnected
lifecycle boundaries, exact gap coverage, stale-incarnation suppression,
monotonic `RelayStats`, and unsupported-format advisory causality. A
sequence hole is authorized only by matching prior `DeliveryReport`
gap ranges; counters are diagnostics, not authorization. The engine
also bounds its own state: one report carries at most
`DeliveryAccountability.DeliveryReportMaxGaps` (256) gap ranges, with
16 announced epochs and 16 departed incarnations retained per sender and
1024 total pending gaps.

Every frame is also labeled for the application:
`GameDataDisposition.Apply` means the payload belongs to the visible
incarnation; `GameDataDisposition.Stale` means it is trailing data of a
departed or superseded incarnation — the accounting still advances but
the payload is not surfaced.

## Delivery reports

The server's `DeliveryReport` frame carries per-class counters for this
connection plus the ranges its retention policies withheld
(`DeliveryReportMessage`):

- `PerClass` (required): `Reliable` (`Delivered`, `Abandoned`,
  `UnsupportedFormat`), `Latest` (`Delivered`, `Superseded`,
  `DroppedFull`, `Abandoned`, `UnsupportedFormat`), and `Volatile`
  (`Delivered`, `Dropped`, `Abandoned`, `UnsupportedFormat`) counters.
- `Gaps` (optional): `DeliveryGap` values — `FromPlayer`, `Epoch`,
  `FromSeq`..`ToSeq` (inclusive), and the `DeliveryGapReason` that
  withheld them (`LatestSuperseded`, `LatestDroppedFull`,
  `VolatileDropped`, `UnsupportedFormat`).

How reports surface on each client:

- **Async client:** `PollEventKind.DeliveryReport` events from
  `TryDequeueEvent`/`DequeueEventAsync`; the payload rides
  `PollEvent.DeliveryReport`.
- **Polling client:** the same `PollEventKind.DeliveryReport` event,
  produced by `Poll()` and consumed in the `DrainEvents()` loop.

Per-interval relay accounting surfaces alongside it as
`PollEventKind.RelayStats` (`RelayStatsMessage`: `IntervalMs`,
`SentToYou`, `DroppedForYou`, `BackpressureEvents`). v2 connections
never receive `DeliveryReport` at all — one arriving there is itself a
delivery-accountability violation.

## Violation policy

When a decoded frame violates the delivery contract (a bad baseline, an
unauthorized gap, a non-monotonic report), the violation is always
surfaced as a `PollEventKind.ProtocolViolation` event with its
`PollEvent.Diagnostic` text; the configured policy decides what happens
to the session (`SignalFish.Client.Core.DeliveryViolationPolicy`):

| Policy | Value | Behavior |
|--------|-------|----------|
| `DeliveryViolationPolicy.Quarantine` | 1 (default) | Emit the violation and suppress subsequent room game data until the next authoritative rebaseline (`RoomJoined`/`SpectatorJoined`/`Reconnected`) or session end; the session stays usable. The latched state is exposed as `ClientSnapshot.Quarantined`. |
| `DeliveryViolationPolicy.Disconnect` | 2 | Emit the violation and tear the signaling connection down. |
| `DeliveryViolationPolicy.Observe` | 3 | Emit the violation and keep the documented delivery behavior — except a baseline that fails validation is still refused, because a broken roster must never replace the client's cursors. |

Both clients expose it as a constructor option, defaulting to
quarantine: `SignalFishClientOptions(violationPolicy: ...)` and
`PollingClientOptions(violationPolicy: ...)`.

## Backpressure

Every hop is bounded, and no hop drops silently:

- **Send cap:** both transports cap one frame at the server inbound
  limit — `WebSocketTransport.DefaultOutboundCapBytes` and
  `SignalFishWebGLTransport.DefaultOutboundCapBytes`, both 64 KiB.
  Oversized sends throw before the wire.
- **Frame bound:** the inbound per-frame bound defaults to the same
  64 KiB (`SignalFishClientOptions.MaxFrameBytes` /
  `PollingClientOptions.MaxFrameBytes`, default `64 * 1024`); a larger
  frame is a protocol violation.
- **Command queue (async client):** a bounded ring, default
  `SignalFishClientOptions.DefaultCommandCapacity` (1024).
  `SendGameData` and `SendBinaryGameData` fail fast with
  `AdmissionError.SendBufferFull` when it is full — nothing is queued
  and nothing is dropped silently. `SendGameDataReliableAsync` (async
  client only) waits for a slot instead, pacing the caller to transport
  throughput. *Queued is not delivered:* work still queued when the
  connection ends is discarded with it.
- **Event queue:** the async client's event queue defaults to
  `DefaultEventCapacity` (256); the polling client's event ring
  (`PollingClientOptions.EventCapacity`, minimum 3) pauses frame
  consumption when full until you drain. A consumer that stops
  draining eventually stops the socket being read — the server then
  sees *you* as the slow consumer.
- **Server-side pressure** surfaces as data, not silence: the gap
  reasons (`LatestDroppedFull`, `VolatileDropped`, `UnsupportedFormat`),
  the per-class `Abandoned` counters, and
  `RelayStats.BackpressureEvents` name exactly what was withheld and
  why.

## Choosing a class

| You are sending | Use |
|-----------------|-----|
| State transitions, chat, roster changes | `GameDataClass.Reliable` |
| High-rate replaceable state (position, aim, timer) | `GameDataClass.Latest` with a stable key per stream |
| Fire-and-forget effects, per-tick samples | `GameDataClass.Volatile` |
| Pre-encoded binary payloads (a negotiated non-JSON encoding) | `SendBinaryGameData` (always reliable) |

```csharp
using System.Text;
using SignalFish.Client.Protocol;

var client = /* SignalFishClient or SignalFishPollingClient */;

// Reliable — the default class, the v2 relay-floor wire form.
CommandSend sent = client.SendGameData(new GameDataMessage(
    Encoding.UTF8.GetBytes("{\"move\":17}")));

// Latest — keyed coalescing: only the newest value per key survives.
sent = client.SendGameData(new GameDataMessage(
    Encoding.UTF8.GetBytes("{\"pos\":[12.5,-3.0]}"),
    GameDataClass.Latest,
    key: 42));

// Binary game data — always reliable; requires a negotiated v3
// connection with a non-JSON game-data encoding.
sent = client.SendBinaryGameData(payload);
if (!sent.Accepted)
{
    // sent.Refusal is AdmissionError.ProtocolUnsupported (v3 was not
    // negotiated) or AdmissionError.BinaryFormatNotNegotiated (the
    // session negotiated the JSON encoding). Nothing reached the wire.
}
```

Payloads are validated at construction (`GameDataMessage` requires a
verbatim UTF-8 JSON value no deeper than 128 containers), so a malformed
payload is refused before any send is attempted. On the async client,
prefer `SendGameDataReliableAsync` for high-rate reliable payloads: it
applies the same admission verdicts but waits for queue space instead of
refusing.

## See also

- [Protocol Versioning](protocol-versioning.md) — how v3 is negotiated
  and what stays gated on v2.
- [Client API](client-api.md) — the send and event-drain methods.
- [Polling client](polling-client.md) — the single-threaded drain loop.
- [Events](events.md) — `DeliveryReport`, `RelayStats`, and
  `ProtocolViolation` events.
- [Errors](errors.md) — `AdmissionError` verdicts.
