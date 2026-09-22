# Session 021 — Graceful shutdown + manual reconnection (M4.3 + M4.4)

Date: 2026-09-22. Branch: `async-shutdown-reconnect-m4.3-m4.4`. Plan tasks:
M4.3, M4.4.

## What

The async client's lifecycle is complete and the documented manual recovery
procedure is now first-class SDK surface.

- **M4.3 — staged graceful shutdown.** `SignalFishClientOptions` gains
  `ShutdownTimeoutMilliseconds` (default 1 s, matching the Rust client's
  `shutdown_timeout`; 0 aborts immediately). `DisposeAsync` inside a room
  now stages the leave: the role's `LeaveRoom`/`LeaveSpectator` rides the
  same single-writer command queue, the session ends on the typed
  `RoomLeft`/`SpectatorLeft` confirmation (or at the budget), and the
  transport is disposed either way. Two independent bounds back each other
  up: the loop caps its park at the deadline (virtual-clock exact) and
  `DisposeAsync` holds a real-time `Task.Delay` fallback for a loop parked
  outside its own checks (stalled send, undrained event queue). A pending
  directed fence or a full command queue skips the leave stage — the armed
  fence is never clobbered. `Disconnected` stays exactly-once-last on
  every path; sends throw `ObjectDisposedException` from the moment
  disposal starts, even mid-leave.
- **M4.4 — manual reconnection surface** (`Reconnection/`): the Rust
  client's end-to-end recovery policy as data.
  `ReconnectContext` is the persisted seat triple (player, room, token),
  captured from a `ClientSnapshot` via `TryCapture` (player baselines with
  a token only — spectator baselines have none, post-teardown snapshots
  are cleared), token redacted in `ToString`. `ReconnectRecovery.Classify`
  is the `ReconnectionFailed` decision tree verbatim from the Rust docs:
  `RECONNECTION_EXPIRED`/`RECONNECTION_TOKEN_INVALID` → fresh join,
  `PLAYER_ALREADY_CONNECTED` → wait for the seat, anything else → retry
  with backoff (unknown future codes fail safe into backoff). A golden-
  driven integration test walks the whole procedure: join with a token,
  server-typed 4003 death, fresh client + handshake, `SendReconnect` with
  the persisted triple (golden wire bytes), `Reconnected` with membership
  restored and the token rotated, re-capture of the rotated seat.
- SSOT sweep: `PLAYER_ALREADY_CONNECTED` and `RECONNECTION_FAILED` added
  to the protocol quick-reference error-code list (they were missing vs
  the server's error-codes doc).

## Red-green yield

- The two shutdown bounds initially masked each other in tests: every
  shutdown test used the virtual clock without advancing it, so only the
  real-time fallback ever ended sessions. The dedicated
  `IdleLoopClosesItselfAtTheVirtualShutdownDeadline` pins the loop side
  alone — heartbeat park at 60 s, budget 30 s, advance exactly 30 s, and
  a 30 s REAL fallback that cannot fire inside the 10 s event timeout.
  Mutation-checked: deleting the park cap fails it (after the first draft
  of the test, with a 2 s budget, survived that same mutation — the fix
  is the 30 s/30 s split).
- Adversarial review (sub-agent, FIX-FIRST verdict, all findings
  addressed): loop-side budget had zero coverage (fixed as above);
  `ReconnectAction` violated the repo's enum-sentinel rule (`default` was
  a valid action — the most destructive one; now `[Obsolete] None = 0`);
  doc comments claimed a "normal close" that has no wire counterpart (the
  transport disposes the socket — an abrupt close — reworded honestly);
  timing asserts were 50x too slack to pin the budget (tightened);
  skip-path behaviors (pending fence, full queue) were unpinned (two new
  tests).

## Verification

- 499 tests green on net8.0 + net10.0 (21 new: 13 shutdown/lifecycle, 8
  recovery — plus data-driven cases).
- `csharpier check` clean; zero-deps / no-LINQ / member-order /
  comment-form / test-names / file-size lints green (aggregator script
  stalls under nested pwsh in the dev container; each constituent passes
  standalone, CI runs them natively).
- Follow-ups filed: WebSocketTransport close handshake (graceful wire
  close), M4.5 opt-in `ReconnectPolicy` (next round), live-server
  reconnection E2E (needs token-presence verification on the v2 endpoint).
