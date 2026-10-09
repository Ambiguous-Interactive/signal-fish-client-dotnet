# Session 067 — Green lanes stay green: upload noise and CPU-SKU benches

Date: 2026-10-09. PR: #131.

## Driver

Main was green with no open PRs, so the session started from the open
issues: #122 waits on the Oct 19 runner migration, #115 needs the
operator's `NPM_TOKEN`, #80 dormant by design. That left #129 — the e2e
lane died once with `ECONNRESET` in the artifact-upload step *after* the
suite passed 20/20 (run 37863039994): infra noise after the gate had
already decided the run. The issue asked for a durable fix.

## The finding

`upload-artifact` steps with `if: always()` run after the real gate; a
transient upload error there fails the whole job even though the gate
passed. The class, not just the e2e instance: every upload step whose
artifact is diagnostics-only carries the same false-red risk.

## What shipped

- `e2e.yml`: the results upload is `continue-on-error: true` — the
  suite's exit code is the gate; the artifact is diagnostics (#129).
- `dotnet.yml`: same for the test-results + coverage upload — the
  retried `dotnet test` exit code already decided the run, and
  `if-no-files-found` drops to `warn` to match the warning-only step.
- `bench.yml`: conditional. In compare mode (the weekly gate) the
  artifact is diagnostics, so upload noise cannot fail the gate; in
  `update_baseline` record mode the artifact *is* the deliverable the
  operator downloads and commits, so a failed upload stays loud.
- `fuzz.yml` untouched: its crash upload runs only on failure — see the
  sweep below.
- Bench gate CPU pinning (#132): the compare now records the CPU model
  (`HostEnvironmentInfo.ProcessorName`) in the baseline as `cpuModel`
  and gates time ratios only within the same CPU; a different-CPU run
  reports `CPU-SKIP` rows and a named warning instead of a false
  regression. Allocations (exact) and rot protection still gate every
  run. The committed baseline was re-recorded (same measured data as
  run 37871948849, now carrying `cpuModel`).

`continue-on-error` was chosen over a retry wrapper: one line, covers
the whole failure mode (persistent failures included, which a 2-attempt
retry does not), and a failed step still shows as a visible warning for
diagnostics loss. The issue listed `continue-on-error` as an acceptable
durable fix.

## Same-class sweep

- `fuzz.yml` crash-artifact upload: runs only
  `if: failure() || cancelled()`, so it can never false-red a green
  run. Left as is.
- `docs.yml` pages upload + deploy and `release.yml`'s `gh release
  create`: the deliverable, not diagnostics — failures there are real
  reds. Left blocking.
- `dotnet.yml` "Generate coverage report" (post-gate) and
  `devcontainer-build.yml`'s buildx `cacheTo`: local deterministic
  tooling, not runner-infra noise; a failure there is a real signal.
  Left blocking.
- `actions/cache` save failures are warnings by default — never a
  false red.

## Evidence / red-green

- All eight workflow files parse (js-yaml). The PR's `e2e`/`dotnet`
  checks prove the edited lanes still run end to end; the actual
  `continue-on-error` path only fires on runner-infra noise, which
  cannot be reproduced on demand.
- The bench conditional is not exercised by any PR check (the lane
  triggers on schedule/dispatch only), so compare-mode dispatches on
  this branch exercised it for real (runs 37870208725, 37871248919):
  the gate decided the run and the upload could not have added noise.
  They also exposed the stale-baseline class below. The first
  scheduled run after the merge is the empty-inputs proof (tracked
  below).
- Bench gate: red = the two CI compare runs above (false 1.77x/1.49x
  regressions); green = the same 7763 reports pass against the new
  baseline with `CPU-SKIP` rows, same-CPU compares still enforce the
  median gate, and 54 assertions pass in the extended
  `test-run-bench.ps1` (all 18 self-test files green).

## The bench surprise: the first compare run ever failed

The bench lane has run exactly once before: the Oct 6 recording of the
committed baseline (#105). This session's two compare dispatches are
its first comparisons, and both failed the same codec rows with stable
ratios — which looked like a real regression until the re-record
landed on a *faster than baseline* instance and broke that theory:

| Run | CPU | DecodeFullCorpus | Encode |
| --- | --- | --- | --- |
| baseline recording (Oct 6) | EPYC 9V74 2.60GHz | 16.90 us | 1.01 us |
| compare 1 + 2 (Oct 9) | EPYC 7763 2.45GHz | 29.99 / 29.68 us | 1.49 / 1.50 us |
| re-record (Oct 9) | EPYC 9V45 2.60GHz | 11.87 us | 0.70 us |

RCA, data-backed: **the shared fleet rotates CPU SKUs** — three in one
week — and identical code spans ~2.5x in medians across them
(allocations byte-identical in every run). The 1.30x limit is
meaningless across SKUs, so *no* committed baseline can survive the
rotation: the first scheduled run (2026-10-12) would have hit the
lottery regardless of which baseline was committed. The BDN report
already carries the truth (`HostEnvironmentInfo.ProcessorName`); the
gate just ignored it. Same genre as #122 (environment shifts
masquerading as regressions) but present before the migration; filed
as #132 and crossed into #122.

Remedy per the lane's own cross-architecture principle ("medians are
not comparable; re-record as a reviewed change"), one level down:
time ratios gate only within the same CPU model. The baseline gains
`cpuModel`; a different-CPU run gets `CPU-SKIP` rows plus a named
warning, while allocations (exact) and rot protection still gate.
Verified red-green with the real CI reports: the exact 7763 reports
that failed twice now pass with skipped ratios; same-CPU compares
still enforce the full median gate (54 assertions across the extended
`test-run-bench.ps1`; all 18 self-test files green). The committed
baseline is the 9V45 re-record — same measured data, now with its CPU
pinned.

## Deliberately not done

- No CHANGELOG entry: CI-only churn is excluded by the
  keep-a-changelog policy.
- No retry machinery for the upload steps: new infrastructure for a
  failure mode `continue-on-error` already eliminates.
- No per-SKU baseline map or threshold widening: the within-CPU gate
  covers the class; a baseline per SKU doubles the maintenance for a
  rotation the fleet does not expose in advance, and a wider limit
  would mute real regressions.
- #115, #80 remain open by design (operator-blocked / dormant); #122
  stays a watch with the SKU evidence commented.

## Follow-ups

- Check the first scheduled `Bench` run after 2026-10-12: it is the
  first exercise of the new conditional's empty-`inputs` path and of
  the within-CPU gate on a scheduled run.
- #122: after 2026-10-19, check the first post-migration `bench` run
  and the `dotnet`/`e2e` lanes for tool-level breakage.
