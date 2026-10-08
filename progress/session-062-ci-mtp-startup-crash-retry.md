# Session 062 — dotnet lane: retry the transient MTP startup crash

Date: 2026-10-08
Branch: `ci-mtp-startup-crash-retry`
PR: (this session)
Issue: #120

## Drift check

- origin/main already merged (working tree clean at `066263a`, #119);
  stale local branch `protocol-info-010-parity` deleted (squash-merged,
  remote branch gone).
- **Main was red**: the `dotnet` workflow failed on the #119 merge push
  (`066263a`) — the only red among the last 40 runs. Open issues: #115
  (operator-blocked: npm names + `NPM_TOKEN`), #80 (dormant by its own
  text). No open or draft PRs.

## RCA: the new red was the MTP migration's startup crash, not the merge

Failure signature (coverage cell `ubuntu/net8.0`, the only cell running
coverlet.MTP): `Zero tests ran`, exit **139** (SIGSEGV),
`BadImageFormatException: Index not found (0x80131124)` on threadpool
threads, then MTP's process-monitor named pipe `Connection reset by
peer` in `ConnectToTestHostProcessMonitorIfAvailableAsync`.

Evidence gathered:

- 100-run scan of `dotnet.yml` history: **0 hits in the 60 runs before
  the MTP migration** (#113, Oct 7), **2 hits in ~11 runs after** —
  2026-10-07 17:27 (`m109-npm-discovery`) and 2026-10-08 04:13 (main).
- The red main run executed a **byte-identical tree** (`e68e0db`) to a
  green PR run two hours earlier, same SDK (8.0.425), same cache keys —
  not code-, SDK-, or cache-deterministic.
- Re-running the failed main job passed **on the first retry**, same
  commit (run `37726470462`, attempt 2 green).
- Upstream footprint matches a launch-path race: testfx#2563 (identical
  `BadImageFormatException: Index not found` signature, infra-level),
  testfx#11184 (Unix pipe RST in MTP's own CI), coverlet#1934
  (coverlet.MTP crashes during instrumentation). No dump exists.

Root cause: a transient race inside the MTP test-host launch path
(process monitor + coverlet.MTP instrumentation) — upstream territory,
not fixable from this repo. Mitigation: bounded retry at the step
level.

## What shipped

- `.github/workflows/dotnet.yml`: both `Test` steps now retry up to
  **3 attempts** (`shell: bash` — the Windows default is pwsh), warn
  via `::warning::` on each non-final attempt, wipe `TestResults/`
  before each attempt, and re-raise the last exit code on exhaustion.
  A repeatable test failure still fails the lane; the suite is ~2-3
  min, so the retry budget fits the 15-min job timeout. YAML-linted;
  loop semantics verified under `bash -e`.
- Issue #120 records the RCA, the run links, and two follow-ups: the
  `e2e` lane uses the same MTP runtime (`run-e2e.ps1`) but has no
  observed occurrence and unverified scenario idempotence (deliberate
  scope-out), and an upstream report if the crash recurs despite
  retries.

## Deliberately not done

- No retry in `run-e2e.ps1` yet: retrying a live-server conformance
  suite re-runs scenarios whose idempotence against a shared server is
  unverified (rate-limit budgets exist, but per-scenario assumptions
  are untested). Tracked in #120.
- No change to coverage flags or MTP/coverlet versions: both current,
  no upstream fix to pick up.

## Verification

- Retry semantics under `bash -e -o pipefail` (what `shell: bash`
  pins on both OSes): exhaust -> last exit code re-raised (139
  preserved); first success -> exit 0; warnings only on non-final
  attempts.
- Workflow YAML validated.
- **Adversarial review caught a blocker in the first cut**: the loop
  was bash but the steps pinned no shell, and GitHub's default on
  Windows is pwsh — the Windows cell failed to parse the loop and never
  ran `dotnet test` (caught red on this PR's own CI). Fixed by pinning
  `shell: bash` on both Test steps; review also drove the exit-code
  preservation, the warning gate on the final attempt, the per-attempt
  `rm -rf TestResults` (no stale TRX/partial cobertura from a crashed
  attempt in the report or artifact), and the "repeatable" wording.
- CI on the PR: all checks green (the retry itself exercised by the
  regular runs).
