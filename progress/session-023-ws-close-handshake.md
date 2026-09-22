# Session 023 — WebSocket close handshake on graceful teardown (#48)

Date: 2026-09-22. Scope: issue debt — issue #48, the M4.3 follow-up that
made the graceful shutdown graceful on the wire, plus the merge of
origin/main (PR #51's post-review fixes) carried forward.

## Delivered

- `WebSocketTransport.DisposeAsync` on a connected transport now performs
  the WebSocket close handshake: close output frame (code 1000, no reason)
  sent under the bounded send-gate wait, then a bounded echo window (500
  ms) while a reader is in flight — its pending receive consumes the
  server's echo and records the observed code. Any failure (held gate,
  dead wire, echo never arrives) falls back to the original aborting
  release. Both clients are covered through their existing quiet
  transport-dispose paths; no new interface surface (the hook-by-capability
  option from the issue was rejected as unnecessary public surface).
- `TransitionToDisposed` now returns the prior state, making the
  connected-check atomic (no check-then-act) and keeping the
  second-dispose fast path synchronous.
- `_handshakeActive` guard keeps `ReleaseResources` from disposing the
  send semaphore while the handshake holds or waits on it (the reader can
  fault mid-handshake); the dispose path re-runs the release after the
  handshake clears the flag.
- Session-level contracts unchanged: `Disconnected` on a live dispose is
  still exactly one clean 0; a server-typed close still wins over a
  synthetic 0 (`FinalizeLocked` pins it before the transport dispose).
- Deduplicated the session-022 progress brief (both merge sides shipped
  one; kept main's `session-022-reconnect-policy-m4.5.md`).

## Verification

- Red-green: 3 new loopback tests fail on the pre-change transport
  (server observes no close frame), pass after — `DisposeSendsTheWebSocket-
  CloseFrameCode1000` (the acceptance), `DisposeWithAParkedReceiveCompletes-
  TheCloseHandshake` (full handshake, echo surfaced as 1000), and
  `DisposeWithoutEchoCompletesPromptly` (bounded wait).
- 521 tests green on net8.0 and net10.0; csharpier + 6 convention lints +
  automation self-tests green.
- Adversarial sub-agent review round: fixed the dead `_state` poll
  condition (Disposed can never become Closed — exit on
  `_closeFrameDelivered` instead), the semaphore-dispose overlap, and the
  TOCTOU on `wasConnected`; rejected-with-rationale: TaskCompletionSource
  signal (KISS — bounded poll is cold-path) and an injectable wait seam.
- Two pre-existing transport races surfaced by the review are filed as
  follow-ups, not introduced by this change.

## Leftovers

- Issue #49 (live-server reconnection E2E) stays blocked: the
  token-presence check needs a live server and Docker is unavailable
  locally; the suite's CI run covers the rest of the checklist.
- M5.1 remainder (password-sealing scenario tests) needs the live server
  too; wire structs, command sends, and fence semantics already exist.
