---
name: create-test
description: Write, run, and organize tests for the SignalFish.Client library (NUnit, fake transports, golden wire fixtures, virtual clock, SharpFuzz and FsCheck lanes). Use when adding tests, fixing a flaky test, setting up fixtures, fuzzing the codec, or verifying protocol conformance.
metadata:
  category: testing
---

# Create Tests

## Stack and layout

- **NUnit** — chosen because the Unity Test Framework is NUnit-based, so
  test knowledge and some fixtures transfer to Unity projects.
- Runner: `tests/SignalFish.Client.Tests/` multi-targeting `net8.0;net10.0` (the library
  itself stays `netstandard2.1` — see
  [api-design](../api-design/SKILL.md)); CI runs `dotnet test` (see
  `.github/workflows/dotnet.yml`).
- One file per type under test: `SignalFishClientTests.cs`,
  `EnvelopeDecoderTests.cs`.

## Naming and structure

- Class: `<Type>Tests`. Method: PascalCase, no underscores - scenario and
  expectation read as one name: `JoinRoomWithoutRoomCodeSendsCreateRoomRequest`.
  NUnit display names use dot notation: `TestName = "Input.Null.Throws"`.
- One behavior per test; arrange/act/assert in that order; no
  multiple-assert megatests (helper `Assert.Multiple` is fine for related
  assertions).

## The fake transport is the backbone

Protocol and client tests never open real sockets:

```csharp
var transport = new FakeTransport();          // implements ITransport
transport.QueueServerFrame("{\"type\":\"Authenticated\",\"data\":{...}}");
await client.JoinRoomAsync(JoinRoomParams.Create("my-game", "Alice"));
Assert.That(transport.SentFrames.Single(), Does.Contain("\"type\":\"JoinRoom\""));
```

- Script server behavior by queueing frames; assert on sent frames and
  emitted events (order-sensitive, see
  [async-threading](../async-threading/SKILL.md)).
- Disconnect/reconnect scenarios: script a close code, then assert the
  [reconnection](../reconnection/SKILL.md) event sequence.

## Determinism rules

1. **No real time**: inject `ISignalFishClock`; virtual time for heartbeats
   and backoff. A test that sleeps to pass is a bug.
2. **No network, no filesystem, no environment dependence.**
3. No `Thread.Sleep` synchronization — use tasks/events the code exposes.
4. Seeded randomness only.

## Golden wire fixtures (red-green source of truth)

Codec tests are driven by golden wire fixtures under `tests/Golden/`,
vendored from the server repo with a provenance header (source commit) and
a resync script — never hand-edited. They are the RED step for every codec
change:

1. Add/refresh the fixture from the server repo at a pinned commit
   (`scripts/sync-protocol-fixtures.ps1`), cited in
   [protocol-quick-reference](../../references/protocol-quick-reference.md).
2. Write the failing test first: inbound — the fixture decodes to the typed
   struct event; outbound — our writer emits byte-identical frames.
3. Implement the minimum codec change to go GREEN (see
   [json-serialization](../json-serialization/SKILL.md)).
4. Cover at least: one happy path, one unknown-field (forward compat), one
   unknown `type`, one malformed/truncated frame (bounded decode error),
   and one payload-less message per area — see
   [protocol-messages](../protocol-messages/SKILL.md).

Never edit a golden fixture to make a test pass — fixtures mirror the
server; the client adapts. Resync only from the pinned server commit.

## Fuzz and property lanes

- **SharpFuzz** entries (`tests/SignalFish.Client.FuzzTests/`) target the
  codec invariants: the reader is total (arbitrary bytes yield a bounded
  error event, never an unbounded throw) and the writer never emits
  invalid UTF-8.
- **FsCheck** properties cover roundtrip stability (decode(encode(x)) == x
  for representative structs) and malformed-input totality.
- **Generators must sample enums by meaning, not by raw ordinal.** Mapping
  bytes to enums with `value % N` silently changes meaning when a sentinel
  is inserted at 0 (it samples the sentinel and drops the last member) —
  use `1u + (byte % N)` over the valid range, and re-check every numeric
  enum construction after any sentinel insertion.
- **Deterministic red-green without the driver**: the fuzz host runs
  standalone with a seed file —
  `SIGNALFISH_FUZZ_TARGET=writer dotnet <FuzzTests.dll> seed.bin`
  (crash exits non-zero, ~134). Craft the seed from the cursor's
  consumption order; a hand-built 5-byte file can prove a generator bug
  red-green in seconds instead of a 15-minute instrumented run.
- **Repro seeds and crash artifacts stay under `.fuzz/`** (gitignored;
  `crash-*.bin` is ignored repo-wide) — never write them to the repo root,
  where a `git add .` commits them (a 5-byte crash seed was). If a seed is
  worth keeping permanently, graduate it into a golden fixture instead.
- When a fuzz/property run finds a real bug, graduate it into a golden
  fixture + regression test before fixing.
- Long fuzz runs belong to scheduled CI, not the unit test suite — keep
  local runs bounded.

## Running

```text
dotnet test                                        # everything
dotnet test --filter "FullyQualifiedName~Reconnect"  # one area
```

## When NOT to Use

- Performance microbenchmarks — out of unit-test scope; propose a bench
  project instead.

## Related Skills

- [json-serialization](../json-serialization/SKILL.md) - codec totality and fuzz invariants
- [async-threading](../async-threading/SKILL.md) - virtual time, event ordering
- [error-handling](../error-handling/SKILL.md) - error path test matrix
- [manage-skills](../manage-skills/SKILL.md) - documenting test procedures as skills
