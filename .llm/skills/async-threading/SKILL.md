---
name: async-threading
description: Threading, async pipelines, heartbeats, the bounded IBoundedQueue event queue, struct-event draining, and Unity main-thread/frame-loop consumption for the client. Use when writing driver loops, timers, ConfigureAwait decisions, backpressure handling, or the polling API Unity games call from Update().
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
4. Send path: single-writer (semaphore-guarded). Receive path: one read at
   a time, dispatched to the event queue.

## Event queue & backpressure (IBoundedQueue)

- Events flow through one internal bounded queue behind `IBoundedQueue` —
  a channel-free, zero-dependency abstraction (no
  `System.Threading.Channels`; see
  [unity-compatibility](../unity-compatibility/SKILL.md)). Capacity is
  documented in options (default e.g. 256).
- Events are **structs**: no event classes, no delegates per event. Consumers
  drain them with a ref-struct enumerator (the `DrainEvents` pattern — API
  shape in [api-design](../api-design/SKILL.md)).
- **Full queue = backpressure**: the receive loop pauses reading the
  socket (never drops events, never grows unbounded), which eventually
  trips server-side slow-consumer detection (close 4002). Fail-fast sends
  report `SendBufferFull`; `*Reliable` variants await capacity.
- Consumers must drain continuously; the polling client exposes the
  `DrainEvents` enumerator for frame loops.

## Polling client (Unity frame-loop shape)

```csharp
// Inside MonoBehaviour.Update()
while (client.DrainEvents(ref enumerator)) { Handle(enumerator.Current); }
```

- `SignalFishPollingClient`-style wrapper: no awaits, struct events via a
  ref-struct ring-buffer enumerator, zero allocations when idle, safe to
  call from the Unity main thread.
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

## Driver-loop liveness

- **Never restart an iteration on a condition the loop itself re-arms.**
  Restore-on-refusal plus an unconditional `continue`/re-check is a
  livelock: the restarted iteration re-triggers on the same state and
  never reaches the code that would clear it (drain, receive, heartbeat).
  A refused automatic action keeps its retry state but falls through, so
  retries are paced by external progress (a frame, a wake, a deadline).
- Gate a re-attempt on the *actual blocker*, not just the trigger flag:
  e.g. a directed operation is futile while a fence is held
  (`PendingOperation != default`), so wait for the fence instead of
  refusing every iteration.
- When a confirmed foreign state supersedes the trigger (a retained
  reclaim seat vs. a deliberate application join), clear the trigger
  instead of retrying forever.
- Red-green a liveness fix with a refusal that is deterministic and
  persistent (e.g. a capacity-1 command queue refilled before the retry):
  the red state must visibly fail to progress, not just differ.

## Testing threading behavior

- Deterministic virtual time for timers/backoff (inject `ISignalFishClock`;
  netstandard2.1 has no `TimeProvider`, so the concept is an internal
  clock interface).
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
