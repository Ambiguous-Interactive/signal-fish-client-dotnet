# Session 066 — Feed the drill anchor at every phase boundary

Date: 2026-10-08. PR: #128.

## Driver

Session 065 left the plan with no known conformance gaps, so the session
started from a fresh driver hunt. Checked in order: open PRs (none), open
issues (#122 waits on the Oct 19 runner migration, #115 needs the
operator's `NPM_TOKEN`, #80 dormant by design), server drift past the
v0.10.0 corpus pin (new commits #822/#824 harden connect-token keys and
claim shapes — minter/operator-side; the client passes `connect_token`
as an opaque string, no client impact), and unaddressed review feedback
on merged PRs. The hunt found one: a Cursor Bugbot thread on PR #126
(filed at merge time, unresolved) — "Anchor goes silent during setup".

## The finding

The reconnection drills' anchor pings only while the harness polls it
(`Poll` runs the heartbeat), and `WaitForEventAsync` polls only its
target client. Between the anchor's waits, each drill runs two fresh
handshakes plus a reclaim; on a slow runner that stretch can cross the
e2e server's 3 s inbound reaper (`SIGNAL_FISH__WEBSOCKET__
IDLE_TIMEOUT_SECS=3` in both `e2e.yml` and `run-e2e.ps1`) and the anchor
would die before witnessing the reclaim. The 500 ms `AnchorOptions`
comment claimed the pings "keep it fed no matter how a slow runner
stretches a phase" — structurally untrue, since no poll means no ping.

## What shipped

- Drill 1 waits for the mid-gap joiner's `PlayerJoined` right after
  carol joins — a real assertion (the roster broadcast reaches the
  surviving seats) that splits the double-handshake silent stretch in
  two.
- Drill 2 waits for the first reclaim's `PlayerReconnected` — the same
  witness semantics, splitting the proxied-handshake stretch before the
  re-drop.
- The `AnchorOptions` comment now states the actual invariant: every
  phase boundary waits on the anchor, and the 500 ms cadence feeds it
  through a stalled phase.

## Same-class sweep

A mechanical sweep of all three e2e fixtures for "client unpolled across
another client's handshake" flagged the two drills (structural:
assertion-bearing clients across two-handshake stretches) plus four
older spots that are safe on inspection: `StartGameRejections...`
(alice/bob are never asserted after the gap — disposable on death),
`SealedRoomGate...` and `V3Negotiation...` (single-handshake windows,
ambient-stall risk only), and the mesh drills' interleaved waits. No
other surface needs the interleaved-wait shape today.

## Evidence / red-green

- Server side verified before relying on the new wait: joining
  broadcasts `ServerMessage::PlayerJoined` to the room except the joiner
  (`room_service.rs`, `broadcast_to_room_except_if_with_hook`), so the
  anchor provably receives carol's join.
- Bugbot round 2 (on this PR) caught that the drill-1 wait still sat
  after *both* handshakes — carol and bob2 connected back-to-back. The
  reorder fixes the real invariant: carol connects, joins, the anchor
  witnesses the join, and only then bob2 connects and reclaims, so
  every handshake is separated by an anchor poll.
- Bugbot thread on #126 answered with the fix link; the fixture-level
  comment documents the invariant for the next editor.
- Local: fast-check green (982 tests), all six convention lints clean.
  The drills need the CI e2e lane (no docker in the sandbox) — the
  `conformance` check on PR #128 is their red-green loop.

## Deliberately not done

- No CHANGELOG entry (test-only; keep-a-changelog policy excludes test
  churn).
- No background-pump harness for the anchor (a thread-safe concurrent
  `Poll` pump was considered and rejected: new infrastructure for a
  window that interleaved meaningful waits already bounds).
- Dependency bumps still intentionally skipped (`TrxReport` pinned to
  the MTP host; `src/` at its zero-dep lock).

## Follow-ups

- If the e2e lane ever shows an anchor-side reaping failure again, the
  fix pattern is established: hand the anchor a meaningful wait at the
  new phase boundary (not a sleep, not a pumper).
