# Session 022 — Opt-in ReconnectPolicy (M4.5 + M4 gate)

Date: 2026-09-22. Scope: the last M4 milestone — automatic reconnection for
the async client, red-green, plus the M4 gate (Rust-doc reconnection
semantics test-parity + concurrency order stress).

## Delivered

- `Reconnection/ReconnectPolicy`: transport factory, deterministic exp
  backoff `min(initial * multiplier^(n-1), max)` (no jitter), `MaxAttempts`,
  `WithTerminalCloseCodes` classification. Validation at construction.
- `Reconnection/ReconnectStatus` + `PollEventKind.Reconnecting` (27) /
  `ReconnectAbandoned` (28) + `PollEvent.Reconnect` payload field.
- `SignalFishClientOptions(reconnectPolicy: ...)`: null (default) keeps
  recovery fully manual — legacy behavior verified unchanged.
- Driver rework (`SignalFishClient`): session loop (rounds + reconnect
  orchestration) around the untouched per-connection loop. Round markers
  (`Disconnected` per death, `Reconnecting`, `TransportReady`) ride the
  event queue's own backpressure — never dropped. Fresh machine per round
  (Phase reads `Connecting` between rounds, never `Terminal`);
  `AdmissionRefusal` refuses sends while severed. Auto-authenticate +
  auto-reconnect per round; attempt budget resets on `Authenticated`.
  Seat retention via `ReconnectContext` capture at sever (OR-semantics:
  pre-auth deaths keep the seat, issued-but-unanswered reclaims lose it —
  the documented Rust gap). Voluntary leave discards the seat (membership
  cleared ⇒ capture fails).
- `FakeTransport`: `DoomWithClose` (connects then dies) and
  `FailHeldSends` (wire death while a send is parked mid-flight).

## Verification

- `dotnet build` clean (0 warnings, `-warnaserror`); 517 tests green on
  net8.0 and net10.0 (11 new reconnect tests + policy tests).
- 6 convention lints + csharpier clean.
- Three adversarial sub-agent review rounds; fixed: marker drops under
  event-queue backpressure (blocking enqueues), Phase reading `Terminal`
  between rounds (machine swap at sever), seat clobbered by a seatless
  sever (OR-semantics), per-round `_disconnectedDelivered` reset,
  reliable-send sever verdict, driver-thread `ObjectDisposedException`
  guard, drain stop on severed.

## Findings / decisions

- Markers are regular queued events: a policy session's stream is
  `... Disconnected, Reconnecting(n, delay), TransportReady, ...` — one
  `Disconnected` per connection death, `ReconnectAbandoned` last.
- The synthesized end-of-stream `Disconnected` remains a legacy-only
  fallback (and the fallback when a round's marker is lost to a
  concurrent finalization); `FinalizeLocked` prefers an observed death
  close over the synthetic 0.
- `DisposeAsync` during a backoff wait is bounded by the shutdown budget
  (queue completion breaks the marker enqueues; the backoff delay rides
  the shutdown token).

## Leftover (tracked as issues)

- #49 (E2E live-server reconnection scenario) — still open; v2
  token-presence check needs a live server run.
- #48 (WebSocket close handshake on graceful teardown) — untouched this
  session; a separate transport surface.
