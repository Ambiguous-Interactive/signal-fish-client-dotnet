---
name: create-test
description: Write, run, and organize tests for the SignalFish.Client library (NUnit, fake transports, golden wire samples, deterministic time). Use when adding tests, fixing a flaky test, setting up test fixtures, or verifying protocol conformance.
metadata:
  category: testing
---

# Create Tests

## Stack and layout

- **NUnit** — chosen because the Unity Test Framework is NUnit-based, so
  test knowledge and some fixtures transfer to Unity projects.
- Runner: `tests/SignalFish.Client.Tests/` targeting `net8.0`; CI runs
  `dotnet test` (see `.github/workflows/dotnet.yml`).
- One file per type under test: `SignalFishClientTests.cs`,
  `EnvelopeDecoderTests.cs`.

## Naming and structure

- Class: `<Type>Tests`. Method: `Method_Scenario_Expectation`.
  `JoinRoom_WithoutRoomCode_SendsCreateRoomRequest`
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

1. **No real time**: inject `ISystemClock`; virtual time for heartbeats and
   backoff. A test that sleeps to pass is a bug.
2. **No network, no filesystem, no environment dependence.**
3. No `Thread.Sleep` synchronization — use tasks/events the code exposes.
4. Seeded randomness only.

## Wire conformance (golden samples)

When message shapes change or a new message type is added:

1. Add/refresh a JSON sample of the exact server wire format in the test
   project (copy from the server repo's docs/spec, cited in
   [protocol-quick-reference](../../references/protocol-quick-reference.md)).
2. Assert deserialization into our typed record AND reserialization
   matches field-for-field (order-insensitive).
3. Cover at least: one happy path, one unknown-field (forward compat), one
   payload-less message per area — see
   [json-serialization](../json-serialization/SKILL.md) and
   [protocol-messages](../protocol-messages/SKILL.md).

Never edit a golden sample to make a test pass — samples mirror the
server; the client adapts.

## Running

```
dotnet test                                        # everything
dotnet test --filter "FullyQualifiedName~Reconnect"  # one area
```

## When NOT to Use

- Performance microbenchmarks — out of unit-test scope; propose a bench
  project instead.

## Related Skills

- [async-threading](../async-threading/SKILL.md) - virtual time, event ordering
- [error-handling](../error-handling/SKILL.md) - error path test matrix
- [manage-skills](../manage-skills/SKILL.md) - documenting test procedures as skills
