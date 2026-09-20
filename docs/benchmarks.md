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
