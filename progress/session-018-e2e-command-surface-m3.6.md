# Session 018 — Command surface + live-server conformance (M3.6)

Date: 2026-09-22. Branch: `e2e-checklist-m3.6`.

## Scope

Advance the plan to M3.6: the polling client's command-send surface (deferred
from M3.4) plus the live-server E2E conformance harness the gate demands.
Docker is unavailable in the devcontainer, so the real-server scenarios are
CI-driven; local validation covers everything up to the wire.

## Delivered

- **Command-send surface (red-green)**: `SignalFishPollingClient` gains
  `SendAuthenticate`/`SendJoinRoom`/`SendJoinAsSpectator`/`SendReconnect`/
  `SendPlayerReady`/`SendStartGame`/`SendLeaveRoom`/`SendLeaveSpectator`/
  `SendGameData`. Admission runs on the calling thread through the existing
  state machine (`TryAdmit`); a refused `CommandSend` (new result struct;
  `default` = accepted) names why and never touches the wire. Accepted
  commands encode to golden wire bytes (pinned against the vendored client
  fixtures), directed operations arm their fence until the typed result,
  and the send dispatch is fire-and-forget — a dead wire folds back into
  the next poll exactly like the heartbeat ping (the two paths now share
  one dispatch helper). Sends before `ConnectAsync` are refused
  `NotConnected` (the machine alone cannot see the connect call).
  `ClientCommand` gains `Authenticate` (Ping-like admission: any live
  phase; whether it may still run is the server's rule).
  `CommandSendTests`: 12 red-green anchors — wire pins, fence arm/release,
  role gates, misuse-throws-before-arm, failure fold-in.
- **Live-server conformance suite**: `tests/SignalFish.Client.E2E` (net8,
  NUnit) drives `ServerConformanceTests` — the client-author checklist
  items 1-7 from the server's `docs/guides/building-a-client.md` — with
  the real `WebSocketTransport` against the real server. Without
  `SIGNALFISH_E2E_URL` the fixture skips, so unit CI stays green with no
  server. Scenario notes: item 1 exercises open-mode
  optional-first-Authenticate; item 3 separates the two rejection rules
  (everyone-ready before the non-authority attempt; unready second seat
  before the authority attempt) so each error code is deterministic; item
  4 pins the stale-`all_ready` advisory contract end-to-end; item 6/7 rely
  on short server liveness timers set by the harness. Stale-broadcast
  races are avoided by waiting on `LobbyStateChanged` with an explicit
  `all_ready` expectation.
- **Runner + CI**: `scripts/run-e2e.ps1` boots a throwaway Docker server
  (open mode, generous rate budgets, 3 s liveness timers), waits for the
  port, runs the suite, and always removes the container; `-ServerUrl`
  targets an already-running server. `e2e.yml` runs the same script
  against a service container on PR + main. `dotnet.yml`'s coverage cell
  now also builds the E2E project so compile breaks fail before e2e.
  `docs/conformance.md` tracks the checklist status (items 1-6 green,
  item 7 green for the silent-client half; the bidirectional partition
  drill is the recorded remainder).

## Verification

- `dotnet test`: 441 tests green on each of net8.0 + net10.0 (12 new
  send-surface anchors); idle-poll allocation gate intact. The E2E suite
  builds and skips cleanly without `SIGNALFISH_E2E_URL` (7 skipped).
- E2E suite builds clean under `-warnaserror`.
- All six convention lints, CSharpier, file-size lint, and the automation
  self-tests: green locally.

## Notes / follow-ups

- The devcontainer has no Docker; the PR's `e2e.yml` run is the first true
  live verification of the seven scenarios (adversarial review fixed the
  two defects it predicted: unauthenticated E2E joins the client itself
  refuses, and the GameStarting roster shape).
- Full directional-liveness proxy drill recorded as the open half of
  checklist item 7 (see `docs/conformance.md`).
- Next: M4 (async client + reconnection) per the execution order.
