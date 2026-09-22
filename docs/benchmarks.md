# Performance baselines

Recorded baselines for the codec hot paths (PLAN.md M1.6). Benchmarks live in
`tests/SignalFish.Client.PerfTests`; run locally:

```sh
dotnet run -c Release --project tests/SignalFish.Client.PerfTests -- --filter *
```

One `DecodeFullCorpus` operation decodes every frame in `tests/Golden/` (47
frames). One `EncodeRepresentativeSession` operation encodes a representative
9-message client session (authenticate, join, ready, start, classified relay
payload, v3 signal, transport status, leave, ping) into a reused buffer.
Steady-state zero allocations are additionally enforced by allocation-gate
unit tests on both directions; the `Allocated` column below is the same truth
on benchmark hardware.

## Baseline — 2026-09-20 (session 008)

Environment: BenchmarkDotNet 0.15.8, .NET SDK 10.0.401 / .NET 8.0.31 runtime,
Ubuntu 24.04 container, Arm64 (RyuJIT armv8.0-a).

| Method                      | Mean      | Allocated |
| --------------------------- | --------: | --------: |
| DecodeFullCorpus            | 27.537 us | 0 B       |
| EncodeRepresentativeSession | 1.586 us  | 0 B       |

Derived budgets (approximate, same environment):

- Decode: ~0.59 us/frame across the mixed corpus.
- Encode: ~0.18 us/message (~5.7 M messages/s; ~0.4 GB/s of wire output for
  the representative session) — far above signaling traffic needs.

Budget policy: codec hot paths must stay **zero-alloc steady-state** (gate
tests fail the build otherwise) and within ~2x of these means. Regression
checks run locally or on the scheduled bench workflow (PLAN.md M9.4) — not on
PR CI, to keep CI time flat.

## Baseline — 2026-09-22 (session 020, M4.1 bounded-queue spike)

Environment: BenchmarkDotNet 0.15.8, .NET SDK 10.0.401 / .NET 8.0.31 runtime,
Ubuntu 24.04 container, Arm64 (RyuJIT armv8.0-a).

The spike pits the shipped hand-rolled `BoundedQueue<int>` against a
`System.Threading.Channels` adapter on the same `IBoundedQueue<T>` interface
(the adapter lives in the bench project only — the library stays
zero-dependency). One `TryRoundtrip` operation is 1000 enqueue+dequeue pairs;
one `AwaitHandoff` operation is 200 park-a-producer-then-free-a-slot
handoffs.

| Method                 | Mean     | Allocated |
| ---------------------- | -------- | --------: |
| HandRolledTryRoundtrip | 65.78 us | 0 B       |
| ChannelsTryRoundtrip   | 65.83 us | 0 B       |
| HandRolledAwaitHandoff | 23.55 us | 72 B      |
| ChannelsAwaitHandoff   | 23.69 us | 72 B      |

Decision (plan M4.1): the hand-rolled queue is at parity with Channels on
both the fail-fast and awaiting paths, so the zero-dependency default costs
nothing; the `IBoundedQueue<T>` abstraction keeps a Channels swap-in honest
(the adapter proves it compiles and performs against the same contract).

Budget policy: fail-fast enqueue/dequeue must stay 0 B (allocation-gate test
`FailFastRoundtripAllocatesNothing`); means within ~2x of the table above.
