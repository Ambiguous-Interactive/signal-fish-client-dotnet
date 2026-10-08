# Session 065 — Live reconnection drill (M6.6's last open item)

Date: 2026-10-08. PR: #126.

## Driver

PLAN.md's "Next surface" left exactly one non-blocked, non-dormant
conformance item: M6.6's "live reconnect drill open" (deferred by
session 034). The reconnection flow had codec + state-machine unit
coverage with fakes, but no test had ever reclaimed a real dropped seat
against the real server. Drift check: server latest release v0.10.0 =
the vendored corpus pin; post-release server commits (#820, #822) are
operator-side (tenant verification keys) — the client treats
`connect_token` as opaque, no client impact. Open issues #122 (ubuntu
image migration, actions start Oct 19), #115 (needs the operator's
`NPM_TOKEN`), #80 (dormant by design) — none actionable in code.

## What shipped

- `ReconnectionConformanceTests` (new fixture, two scenarios):
  1. `DroppedSeatReclaimsItsMembershipWithTheWireToken` — v3 join
     captures `reconnection_token`, the proxy drop severs the socket,
     the anchor's `PlayerLeft` syncs the record registration, a fresh
     authenticated client reclaims with `SendReconnect`, and the drill
     asserts: identical membership, rotated token (never re-issued),
     current roster in the reclaim snapshot (carol joined during the
     gap), the anchor's `PlayerReconnected`, and the reclaimed seat's
     first `GameData` being the fresh frame (gameplay never replays).
  2. `ConsumedTokenIsRefusedAndTheRotatedTokenReclaimsAgain` — the
     consumed token is refused typed (`RECONNECTION_TOKEN_INVALID`) and
     the rotated replacement reclaims on the same connection.
- `PartitionProxy`: `DisposeAsync` now closes **both legs** of every
  relay. Before, the upstream (server-side) leg survived disposal and
  idled until the server's 3 s inbound reaper fired — a proxy drop was
  a timer race, not a disconnect (and the existing drills' wind-down
  leaned on a 5 s give-up).
- `E2EHarness`: `ConnectProxiedV3ClientAsync` (v3 handshake through the
  proxy) + optional `PollingClientOptions` on the v3 helpers; the drill
  anchors ping at 500 ms to stay fed under the CI server's 3 s idle
  timers; `ConnectV3ClientAsync` now disposes the client on any
  handshake failure (was auth-refusal only).

## Evidence / red-green

- Server spec mirrored: signal-fish-server
  `tests/reconnection_replay_e2e.rs::reconnect_succeeds_with_only_the_wire_token`
  (+ `ReconnectionError::error_code()` mapping: TokenMismatch/Invalid →
  `RECONNECTION_TOKEN_INVALID`).
- Adversarial review round 1 returned FIX-FIRST with a Critical the
  local run could never catch (no docker in the sandbox): the proxy's
  zombie upstream leg + the 3 s inbound-idle reaper would have failed
  test 2 deterministically. Both mechanisms verified in the server's
  `src/websocket/connection.rs` read loop, fixed, and re-reviewed to
  SHIP.
- First CI run: e2e `conformance` green in 59 s; both tests discovered
  (20 total, was 18). Unit suite + all six convention lints green
  locally.

## Deliberately not done

- No CHANGELOG entry (test-only; keep-a-changelog policy excludes test
  churn).
- No dependency bumps: the one outdated package
  (`Microsoft.Testing.Extensions.TrxReport` 2.3.3 → 2.5.1) is pinned to
  the MTP host version on purpose (the #120/#121/#123 startup-crash
  saga); `src/` stays at its zero-dep lock.
- The e2e drill still cannot run in this sandbox (no docker); the CI
  e2e lane is its red-green loop. Local verification stops at
  build + unit + lints + test discovery.

## Follow-ups

- Failure-of-a-failure hygiene only: in test 1, if bob2's connect
  throws after carol connected, carol is disposed only at process exit
  (the server reaps her in 3 s). Nested-nullable-locals would close it;
  noise outweighs the risk today.
