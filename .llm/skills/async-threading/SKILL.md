---
name: async-threading
description: Threading, async pipelines, heartbeats, bounded event channels, and Unity main-thread/frame-loop consumption for the client. Use when writing driver loops, timers, ConfigureAwait decisions, backpressure handling, or the polling API Unity games call from Update().
metadata:
  category: protocol
---

# Async & Threading

The library is fully asynchronous internally and must be consumable from
both classic `async/await` apps and Unity frame loops.

## Library-side rules

1. `ConfigureAwait(false)` on every internal `await`. The library must not
   depend on a `SynchronizationContext`.
2. `CancellationToken` flows end to end: connect, send, receive loops,
   disposal. Graceful shutdown = cancel + bounded drain, never
   fire-and-forget.
3. **No threads of our own** on WebGL (see
   [unity-compatibility](../unity-compatibility/SKILL.md)); timers are
   async (`Task.Delay` with token) or tick-driven from the consumer.
4. Send path: single-writer (channel or semaphore). Receive path: one read
   at a time, dispatched to the event channel.

## Event channel & backpressure

- Events flow through one bounded channel (documented capacity in options;
  default e.g. 256).
- **Full channel = backpressure**: the receive loop stops reading the
  socket, which eventually trips server-side slow-consumer detection
  (close 4002). This is the designed failure mode — never drop events
  silently, never grow unbounded.
- Document: consumers must drain continuously. Provide
  `TryReceiveAll`-style draining for frame loops.

## Polling client (Unity frame-loop shape)

```csharp
// Inside MonoBehaviour.Update()
while (client.TryDequeueEvent(out var evt)) { Handle(evt); }
```

- `SignalFishPollingClient`-style wrapper: no awaits, no allocations when
  idle, safe to call from the Unity main thread.
- Heartbeats are handled internally by the polling client using frame-time
  deltas, not wall-clock timers it doesn't own.

## Heartbeats (mandatory)

- The server idle-times connections; clients must send application `Ping`
  periodically. Defaults to respect: server `idle_timeout_secs` 300,
  `server_ping_interval_secs` 10, `pong_timeout_secs` 5.
- Client default: send `Ping` every ~30 s; if no `Pong` within ~2x the
  interval, treat the connection as dead and enter the
  [reconnection](../reconnection/SKILL.md) flow.
- Never block waiting for `Pong`; track it as outstanding-ping state in the
  driver loop.

## Testing threading behavior

- Deterministic virtual time for timers/backoff (inject a time provider;
  netstandard2.1 has no `TimeProvider` — wrap the concept in an internal
  `ISystemClock`).
- No real `Task.Delay` in tests — see
  [create-test](../create-test/SKILL.md).
- Assert event ORDER under concurrent send/receive stress; flaky ordering
  means a real race, not a flaky test.

## When NOT to Use

- What the events mean — [protocol-messages](../protocol-messages/SKILL.md).
- API shape of the polling client — [api-design](../api-design/SKILL.md).

## Related Skills

- [unity-compatibility](../unity-compatibility/SKILL.md) - WebGL/thread constraints
- [reconnection](../reconnection/SKILL.md) - what happens when heartbeats fail
- [api-design](../api-design/SKILL.md) - async signature rules
