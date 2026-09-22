# Session 019 — Bidirectional partition drill (conformance item 7)

**Date:** 2026-09-22 · **Issue:** #45 · **Milestone:** M3.6 gate completion

## What and why

The server's client-author checklist item 7 (directional liveness) had one
open remainder after #44: prove each transport direction can fail
independently and the client reacts correctly to a one-way partition, not
just to full silence. This session added the missing man-in-the-middle and
closed the item.

## Changes

- `tests/SignalFish.Client.E2E/PartitionProxy.cs` (new): in-process raw TCP
  proxy — loopback listener forwarding to the live server, with a kill
  switch per direction. A severed direction reads and discards, so local
  sends keep "succeeding" while nothing reaches the far end (true
  partition semantics, not a stall). The WebSocket session is opaque
  payload; the proxy never parses it.
- `E2EHarness`: `E2EEnvironment.BaseUrl` + `ConnectProxiedClientAsync`
  (fresh proxy per client; caller disposes).
- `ServerConformanceTests`, two drill scenarios:
  - **client→server severed**: the server's liveness reaper ends the
    session and its typed close (4003 activity-timeout or 4004
    idle-timeout, whichever reaper the server build arms) reaches the
    client through the still-open reverse direction; the client reaches
    terminal — never half-alive.
  - **server→client severed**: the client's outbound relay still carries
    end-to-end (a directly-connected peer receives it), yet the client's
    own liveness clock declares death (local 1006) instead of trusting
    one-way outbound progress.
- `docs/conformance.md`: item 7 flipped to covered; intro note updated.
- `PLAN.md` (local): M3.6 gate note + plan-status header refreshed.

## Verification

- `dotnet build` clean (0 warnings, `-warnaserror` CI-equivalent analyzers
  in the E2E project: CA2213/CA2025 both surfaced and fixed).
- Unit suites green (441 tests, net8.0 + net10.0); E2E suite skips without
  a live server, as designed.
- `csharpier check`, `lint-file-sizes`, `lint-no-linq` green.
- The drill's first live verification is this PR's `e2e` run (Docker is
  unavailable locally). First live run caught a real miss: the drill
  scenarios skipped the Authenticate handshake and the client's own
  admission fence refused the join (`NotAuthenticated`) — fixed by routing
  the proxied clients through the same handshake as every other scenario
  (`ConnectProxiedAuthenticatedClientAsync`). Second live run: 9/9 green
  (`conformance` on the PR).

## Deferred / follow-ups

- Reconnect-after-partition sequences with M4.4 reconnection (noted in
  `docs/conformance.md`).
- Session scope kept to the issue; M4 (async client) remains the next
  major milestone surface.
