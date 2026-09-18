---
name: api-design
description: Design the public C# API surface of the SignalFish.Client library. Use when adding or changing public types, methods, events, async signatures, disposal semantics, or anything visible to consumers of the NuGet package.
metadata:
  category: core
---

# API Design

Rules for the public surface of `SignalFish.Client`. The library is consumed
by game code (Unity or plain .NET), so the API must be safe under frame
loops, cheap when idle, and honest about asynchrony.

## Async surface

- Public operations return `Task` (or `ValueTask` on hot paths) and take a
  trailing `CancellationToken`:
  `Task JoinRoomAsync(JoinRoomParams params, CancellationToken ct = default)`
- **No sync-over-async**: never expose `XxxSync()` wrappers, never call
  `.Result`, `.Wait()`, or `GetAwaiter().GetResult()` in library code.
- No `async void`, ever. Event handlers are the consumer's business.
- Commands that can only run in certain states (e.g. `JoinRoomAsync` before
  `Authenticated`) fail fast with a typed exception, not by silently
  queueing forever.

## Lifecycle

- Construction is cheap and cannot throw for protocol reasons; connect via
  an explicit `ConnectAsync`/`StartAsync`.
- Implement `IAsyncDisposable`; `DisposeAsync` performs a graceful close
  (send `LeaveRoom` when in a room, close the socket with a normal close
  code, drain pending events within a bounded timeout).
- Re-entry: calling any method after disposal throws
  `ObjectDisposedException`.

## Events

- Events are **immutable records**: `record PlayerJoined(string PlayerId, string PlayerName);`
- Delivered through a bounded channel (`Channel<T>`-style) with a documented
  backpressure policy — see [async-threading](../async-threading/SKILL.md).
- The event stream is a single ordered sequence; consumers drain it
  continuously. Document per-event timing guarantees
  ([event timing is part of the contract](../protocol-messages/SKILL.md)).

## Errors

- One exception hierarchy rooted at `SignalFishException` — see
  [error-handling](../error-handling/SKILL.md). Never throw raw
  `InvalidOperationException` for protocol conditions.
- Server rejections (`Error` / `*Failed` with `error_code`) surface as
  typed failures carrying the code — not string messages.

## Options and configuration

- One options record per concern (`SignalFishConfig`, `ReconnectPolicy`,
  `HeartbeatOptions`), not a god-object.
- Config records are immutable with `with`-style builders or factory
  methods; validate in the factory and throw `ArgumentException` with the
  offending parameter name.

## Compatibility discipline

- Never rename or remove a public member once released; add overloads or new
  types instead. The wire protocol is additive — so is the API.
- `nullable` annotations are enabled and part of the contract.
- Keep the Unity frame-loop consumer in mind: nothing on the hot path may
  allocate per frame when idle; provide the polling client shape from
  [async-threading](../async-threading/SKILL.md).

## When NOT to Use

- Internal refactors with no public surface change.
- Wire-format questions — those live in
  [protocol-messages](../protocol-messages/SKILL.md).

## Related Skills

- [async-threading](../async-threading/SKILL.md) - threading, channels, Unity main thread
- [error-handling](../error-handling/SKILL.md) - exception hierarchy and codes
- [unity-compatibility](../unity-compatibility/SKILL.md) - platform constraints that shape the API
