---
name: api-design
description: Design the public C# API surface of the SignalFish.Client library. Use when adding or changing public types, methods, struct events, drain patterns, async signatures, disposal semantics, or anything visible to consumers of the NuGet package.
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

- Events are **readonly structs**: no event classes, no delegates per
  event. `readonly struct PlayerJoinedEvent { ... }`
- Delivered through the internal bounded queue (`IBoundedQueue`,
  channel-free) with a documented backpressure policy — see
  [async-threading](../async-threading/SKILL.md).
- Consumers drain via the **`DrainEvents` pattern**:
  `while (client.DrainEvents(ref enumerator)) { ... }` — a ref-struct
  enumerator over a ring buffer; zero allocations when idle.
- The polling client is **frame-driven** for Unity `Update()` loops: no
  awaits, budgets per poll, events surfaced only through the drain
  pattern above.
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

- One options type per concern (`SignalFishClientOptions`,
  `PollingClientOptions`, `ReconnectPolicy`), not a god-object.
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
- The library stays `netstandard2.1`; test/bench tooling may target
  `net8.0` (e.g. allocation-gate tests, BenchmarkDotNet). Keep
  tooling-only APIs and TFMs out of the library.

## C# style rules (enforced, not optional)

These are enforced by `.editorconfig` + `EnforceCodeStyleInBuild` (they fail
`dotnet build` repo-wide) — do not fight them, and keep new rules in this
pattern when the team adopts one:

- **No `var`** (IDE0008): every local declares its type explicitly, including
  `foreach` control variables and `out var` arguments.
- **`using` directives go inside the namespace** (IDE0065).
- **Enum value 0 is always a sentinel named `None` (or `Unknown`), never a
  real value.** `default(SomeEnum)` must not be mistakable for valid data.
  Mark the sentinel `[Obsolete]` when constructing it explicitly is a bug
  (e.g. `EnvelopeEventKind.None`); leave it usable without the attribute
  when comparing against it is the sanctioned way to detect "absence"
  (e.g. `MessageKind.None`, `DecodeError.None`).

## When NOT to Use

- Internal refactors with no public surface change.
- Wire-format questions — those live in
  [protocol-messages](../protocol-messages/SKILL.md).

## Related Skills

- [async-threading](../async-threading/SKILL.md) - threading, event queue, Unity main thread
- [error-handling](../error-handling/SKILL.md) - exception hierarchy and codes
- [unity-compatibility](../unity-compatibility/SKILL.md) - platform constraints that shape the API
