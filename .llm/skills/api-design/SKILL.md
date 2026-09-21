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
- **Enum value 0 is always a non-valid `None` sentinel, marked
  `[Obsolete]` — project-wide, no exceptions** (`EnvelopeEventKind` set the
  pattern). Reference the sentinel only as `default(TEnum)`; a named
  reference fails the build (`-warnaserror` CS0618), which is the point:
  `default(...)` vs valid values are compiler-distinguishable. Shape public
  APIs so callers never need the sentinel's name — return `bool` + `out`
  value (`TryAdmit`) instead of a "None means OK" status enum; refuse
  default-carrying payloads at encode time (`GameDataMessage`) instead of
  encoding them.
- **No `this.` qualification; private fields are `_camelCase`.** The
  underscore prefix is what makes unqualified access unambiguous. Note:
  Roslyn's `EnforceCodeStyleInBuild` does NOT enforce IDE0003 or naming
  rules (IDE1006) — those are enforced IDE-side by `.editorconfig` and at
  build time by `scripts/lint-no-this-qualification.ps1` (hook + CI).
- **Linter precedent for banned constructs:** when an analyzer package would
  break the zero-PackageReference rule or Roslyn cannot enforce a rule at
  build time, add a PowerShell linter (`lint-no-linq.ps1`,
  `lint-no-this-qualification.ps1`) with a self-test, hook step, and CI
  step — the repo's established enforcement shape.

## When NOT to Use

- Internal refactors with no public surface change.
- Wire-format questions — those live in
  [protocol-messages](../protocol-messages/SKILL.md).

## Related Skills

- [async-threading](../async-threading/SKILL.md) - threading, event queue, Unity main thread
- [error-handling](../error-handling/SKILL.md) - exception hierarchy and codes
- [unity-compatibility](../unity-compatibility/SKILL.md) - platform constraints that shape the API
