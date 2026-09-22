# Session 020 — Async client foundation (M4.1 + M4.2)

Date: 2026-09-22. Branch: `async-client-m4`. Plan tasks: M4.1, M4.2.

## What

The async consumption shape now exists: `SignalFishClient` runs one
background driver loop that multiplexes command sends, frame receives, and
heartbeat timing over any `ITransport`, with Rust-parity backpressure on
both directions.

- **M4.1 — bounded queue, data decides.** `IBoundedQueue<T>` (Core) +
  `BoundedQueue<T>` (Async): channel-free, zero-dependency, single-lock
  ring buffer with fail-fast (`TryEnqueue`/`TryDequeue`, 0 B, gated) and
  awaiting (`EnqueueAsync`/`DequeueAsync`) operations, FIFO-exact waiter
  handoff, and cancellation that can never steal a slot or item. The M4.1
  spike benchmarked it against a `System.Threading.Channels` adapter on the
  same interface: parity (65.8 us vs 65.8 us / 1000 try-roundtrips, both
  0 B; 23.6 us vs 23.7 us / 200 await-handoffs, both 72 B) — recorded in
  `docs/benchmarks.md`; the Channels adapter lives in the bench project
  only, proving the abstraction swap-in without adding a dependency.
- **M4.2 — the driver loop.** `SignalFishClient` + `SignalFishClientOptions`
  (256 event capacity, 1024 command capacity, ~30 s ping / 2x liveness, per
  the Rust client's defaults). Fail-fast sends report `SendBufferFull`
  (added last in `AdmissionError` precedence, matching Rust);
  `SendGameDataReliableAsync` waits for a slot. A full event queue parks
  the loop (never drops). Admission and queuing are one atomic step per
  send on a single gate. Events dequeue via `DequeueEventAsync` (null after
  terminal) or `TryDequeueEvent`. `ISignalFishClock` gains `DelayAsync` so
  heartbeat/liveness run on the injected clock (virtual time stays
  deterministic); `CommandSend` moved to Core (one verdict type for both
  clients).

## Refactor (SSOT)

The polling client's private frame-routing pipeline (~300 lines:
close/oversize/decode routing, session-fact mapping, payload surfacing) is
extracted into one internal `FramePipeline` used by both clients — the two
clients cannot drift on decode policy. Existing 441 tests stayed green
across the move.

## Red-green yield (the reason this session worked)

- The bounded queue's waiter-grant path initially passed a wrong expectation
  I wrote (grant-at-head); the 50-frames-into-8-slots client test exposed
  the real FIFO bug it had been masking. A granted producer's item is
  *newer* than everything buffered — it belongs at the tail (which, the
  queue having been full, is the freed slot). The queue test was corrected,
  not just the code.
- The driver loop's park/sweep/refresh wake protocol had a genuine
  lost-wakeup: the surplus-signal sweep (`Wait(0)` loop) could absorb a
  release whose command was still queued, parking the loop forever with a
  non-empty queue. Reproduced in a 60-line standalone harness (2 stalls /
  300 rounds), fixed by deleting the sweep — the persistent wake waiter
  plus the semaphore's count invariant is complete without it (0 stalls /
  300 rounds after). Confirmed by 12 clean full-suite runs after the fix.
- The teardown path initially leaked the transport on server-initiated
  closes (dispose only ran on user `DisposeAsync`); teardown now disposes
  the transport quietly exactly once, mirroring the polling client.
- Pre-connect sends reported `NotAuthenticated` instead of `NotConnected`
  (the machine starts "constructed-live"; only the client knows the connect
  call) — `AdmissionRefusal` now answers `NotConnected` first, matching the
  polling client's admission order and the Rust error precedence.

## Test summary

- 474 tests total at HEAD (net8.0 + net10.0): +14 bounded queue (order,
  capacity, waiter hygiene, completion, cancellation, two-producer order
  stress, 0 B gate), +17 driver loop (transport-ready, golden handshake
  wire, refusal order, ordered session events, stalled-wire
  `SendBufferFull`, reliable await, event-queue backpressure without
  drops, virtual-time heartbeat + cadence boundary + liveness 1006,
  server close 4000, receive fault 4003, dispose semantics, FIFO relay
  order, concurrent relays, terminal-under-full-queue), +2 clock
  (`DelayAsync` completes on advance; cancellation wins over the clock).

## Verification

- `dotnet build -warnaserror` clean (0 warnings) on netstandard2.1.
- `dotnet test` green on net8.0 and net10.0 (456 each), including the new
  suites; run repeatedly to confirm the flake class is gone.
- CSharpier, all six convention lints, file-size/LLM-instruction lints,
  automation self-tests: green.
- Benchmarks recorded (`docs/benchmarks.md`): queue spike at parity;
  codec budgets untouched.

## Adversarial review (three rounds to zero findings)

Round 1 found two contract-breaking defects (canceled-receive NRE that
could kill the loop; terminal `Disconnected` droppable under a full
event queue) plus three minors and two nits. Round 2 verified all fixes
and found five residuals (teardown/loop tail races, the still-tautological
connect test, a torn long read, a doc overclaim). Round 3 verified the
residuals, proved the gate-ordering and TOCTOU safety of the new code,
and returned SHIP with no findings. The terminal-delivery design that
survived review: the terminal event is never enqueued — it is
synthesized one-shot at end-of-stream, so it is exactly-once and last
even when the queue is full at teardown.

## Leftovers / follow-ups

- M4.3 (staged graceful shutdown: leave-room-if-in-room, bounded terminal
  delivery, `Disconnected` exactly once under a full queue), M4.4 (manual
  reconnection), M4.5 (opt-in `ReconnectPolicy`).
- Shutdown timeout option arrives with M4.3's bounded drain (the Rust
  client's 1 s default).
