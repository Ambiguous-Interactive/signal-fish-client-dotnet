# Session 067 — Diagnostics uploads can no longer fail a green lane

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
  retried `dotnet test` exit code already decided the run.
- `bench.yml`: conditional. In compare mode (the weekly gate) the
  artifact is diagnostics, so upload noise cannot fail the gate; in
  `update_baseline` record mode the artifact *is* the deliverable the
  operator downloads and commits, so a failed upload stays loud.
- `fuzz.yml` untouched: its crash upload runs only on failure — see the
  sweep below.

`continue-on-error` was chosen over a retry wrapper: one line, covers
the whole failure mode (persistent failures included, which a 2-attempt
retry does not), and a failed step still shows as a visible warning for
diagnostics loss. The issue listed `continue-on-error` as an acceptable
durable fix. Under `continue-on-error`, dotnet.yml's
`if-no-files-found: error` became decorative, so it is now `warn` — the
honest setting for a warning-only step.

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
  triggers on schedule/dispatch only). Expression semantics verified by
  review against the documented `inputs` behavior (empty on schedule →
  non-blocking; `false` → non-blocking; `true` → blocking) and a
  compare-mode dispatch on this branch (run 37870208725 — the gate ran
  under the new conditional); the first scheduled run after merge is
  the empty-inputs proof (tracked below).

## Deliberately not done

- No CHANGELOG entry: CI-only churn is excluded by the
  keep-a-changelog policy.
- No retry machinery for the upload steps: new infrastructure for a
  failure mode `continue-on-error` already eliminates.
- #122, #115, #80 remain open by design (watch / operator-blocked /
  dormant).

## Follow-ups

- Check the first scheduled `Bench` run after 2026-10-12: it is the
  first exercise of the new conditional's empty-`inputs` path.
- #122: after 2026-10-19, check the first post-migration `bench` run
  and the `dotnet`/`e2e` lanes for tool-level breakage; re-baseline if
  the bench gate trips (documented remedy, not a threshold tweak).
