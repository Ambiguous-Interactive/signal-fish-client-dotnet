# Session 052 — Scheduled bench gate (M9.4)

Date: 2026-10-06
Branch: `m94-scheduled-bench`
PR: #105

## What shipped

**M9.4 — the weekly perf gate**, closing the promise the baseline docs
made in session 008 ("regression checks run … on the scheduled bench
workflow"):

- **`scripts/run-bench.ps1`** (the gate): runs
  `tests/SignalFish.Client.PerfTests` (BenchmarkDotNet, medium job,
  JSON export) and compares every benchmark against the committed
  machine baseline `tests/SignalFish.Client.PerfTests/baseline.json`.
  Fails on: median growth beyond `-MaxRegression` (default 1.30),
  *any* growth in bytes allocated per operation (allocation is
  deterministic; the codec budget is zero-alloc), a baseline benchmark
  the run no longer produces (renames/removals fail — the gate cannot
  silently rot), or a runner architecture different from the
  baseline's (Arm64 vs x64 medians are not comparable — the schema
  pins it). New benchmarks pass with a warning until folded in.
  `-UpdateBaseline` records a reviewed baseline instead; the schema
  carries `schemaVersion`, job, architecture, BDN version, timestamp.
  A run summary table lands in `$GITHUB_STEP_SUMMARY` when present.
- **`.github/workflows/bench.yml`**: Mondays 05:00 UTC (after the fuzz
  lane) + `workflow_dispatch` with `update_baseline`. Never on pull
  requests — PR CI time stays flat (fuzz-lane policy). Missing
  baseline fails the run with the remedy spelled out (never skips).
  Artifacts (reports + baseline) upload on every outcome for triage.
- **`tests/SignalFish.Client.PerfTests/baseline.json`**: first x64
  baseline, recorded on CI hardware (ubuntu-latest, AMD EPYC,
  MediumRun) from this PR's branch, so the gate is live at merge — no
  post-merge bootstrap, no Arm64-devcontainer numbers that could never
  compare.
- **`scripts/tests/test-run-bench.ps1`**: 33 fixture-driven assertions
  over the compare logic (no benchmarks needed): record/distill
  correctness, at-limit vs over-limit ratios, allocation growth and
  decrease, architecture mismatch, orphaned baseline entries, new
  benchmark warnings, missing-results/missing-baseline remedies, and
  report-all-then-fail.
- **`docs/benchmarks.md`**: the gate section — what fails, why the
  thresholds are what they are, and the re-record flow (dispatch →
  download artifact → review diff → commit; the audited escape hatch,
  same shape as the api-compat suppression file).

## Why these thresholds

- **Median ×1.30**: ubuntu-latest runners are noisy. The bootstrap run
  gave one real sample of run-to-run drift on identical code: max
  median ratio 1.148 (ChannelsTryRoundtrip), most ≤1.014. 1.30 keeps
  headroom above observed noise while real regressions (algorithmic
  changes) exceed it comfortably. The gate also uses BDN's Median
  (robust to the outlier runs visible in the logs).
- **Allocation exact**: `BytesAllocatedPerOperation` is deterministic
  for identical code; any growth is either a real regression or a
  runtime change — both deserve a reviewed baseline update.
- **Architecture pinned in the artifact**: the devcontainer is Arm64,
  CI is x64; medians differ ~2x between them. A locally recorded
  baseline would have failed every scheduled run; the pin turns that
  failure mode into a named message.

## Red-green evidence

- Self-tests red first: the first run failed 6 assertions (a test-harness
  bug: PowerShell argument mode binds `[regex]::Escape($x)` as the
  literal string `[regex]::Escape` in a named-parameter argument and
  shifts `$x` into the next positional — parens fix it). Green after
  the fix; the review round then grew the suite to 44 assertions.
- Real pipeline locally (Arm64): `-UpdateBaseline` recorded 6
  benchmarks in 4m24s; compare green against it; a doctored baseline
  (median 20 us vs 37.65 us actual) went red with the named benchmark
  and exit 1.
- CI (run 37517712684): the record step produced the x64 baseline; the
  shim's second step then ran a full compare of run-2 against run-1's
  baseline — **green on real hardware** (max ratio 1.148). The shim
  was removed in the same PR; its race is the interesting finding
  below.

## Findings

- **`hashFiles` in a step-`if` evaluates at step start, not workflow
  parse.** A step that creates the hashed file flips a later
  `hashFiles`-gated step to true: the "record if absent" shim ran the
  record step, whose `baseline.json` then made the compare step's
  condition true, so both ran (9m10s total). Benign here (the extra
  compare validated the fresh baseline in place), but any conditional
  pair that mutates the workspace needs the condition captured before
  the mutation (e.g., into `$GITHUB_ENV`), not re-derived at the step.
- **`gh workflow run <file>` 404s until the workflow exists on the
  default branch**, so a new scheduled lane cannot be dispatched from
  its own PR. The temporary push trigger + record-if-absent shim is
  the workaround: the first baseline gets recorded on the target
  hardware and the shim leaves with the same PR.
- **PowerShell argument mode does not evaluate `[type]::Method(args)`
  in named-parameter position** — it binds the literal prefix string
  and shifts the rest into positionals, silently. Expressions need
  parens.

## Deliberate scope cuts

- **No PR-time bench**: scheduled/dispatch only; the fuzz lane sets
  the precedent and `docs/benchmarks.md` says why.
- **No automatic baseline updates from CI**: a baseline is a reviewed
  change; the dispatch + artifact + commit flow is the audit trail.
- **No per-benchmark thresholds or step-summary charts**: one global
  ratio budget + exact allocation is data-backed and simple;
  per-benchmark tuning is unwarranted until the 1.30 budget flakes in
  practice (watch the first scheduled runs).
- **No new benchmark classes**: the codec + bounded-queue surfaces are
  the ones the plan's budgets name.

## Verification

- **Adversarial review round** (sub-agent, evidence-first): 7 findings,
  all fixed — the reflect-improve log was not yet on the branch and
  would have broken the 300-line lint once committed (pruned five
  graduated entries to fit); a zero or negative *run* median passed
  the gate (only the baseline side was guarded); `Set-StrictMode`
  turned every missing-field diagnostic into a raw property-not-found
  throw (guards are now `PSObject.Properties` existence checks);
  culture-sensitive baseline timestamp (now InvariantCulture);
  BenchmarkDotNet version drift unchecked (now a warning — the
  2026-10-19 ubuntu-26 image migration makes a no-code-change shift
  plausible); two exercised behaviors had no fixtures (duplicate
  conflict, zero median — added); load-bearing baseline/artifact paths
  got a sync-pointer comment. Finding 8 (align the input-guard idiom
  with the fuzz lane) was skipped deliberately: the reviewer verified
  the current form safe and marked the change optional.
- **Second-round verification sub-agent**: all 8 items VERIFIED with
  live probes (ran the script with a negative median; re-ran the lint
  and the full self-test suite).
- `pwsh scripts/tests/test-run-bench.ps1`: 44/44 assertions.
  `scripts/tests/run-all.ps1`: all 16 files pass.
- Real run + compare + doctored-red locally (Arm64), full CI record +
  compare green on x64 (run 37517712684); the reviewed script
  reproduces that CI compare exactly on the downloaded artifact (max
  ratio 1.148).
- `lint-file-sizes` (PLAN.md + log budget) and `lint-llm-instructions`
  green.
- PR CI: one red check after the review commit (markdownlint MD012,
  double trailing blank in the pruned log) — fixed; all checks green.
- Checked issue #88 (NUnit 5): still blocked upstream — FsCheck.NUnit
  3.4.0 remains latest on nuget.org (verified this session); no action
  possible. Issue #80 trigger (first use of a new mkdocs extension)
  did not occur.
- Live validation watch items: the first scheduled run (Monday
  2026-10-12) is the gate's first unsupervised execution, and the
  second (2026-10-19) rides the `ubuntu-latest` → Ubuntu 26 image
  migration (runner-images#14748) — a no-code-change median shift
  there may need a baseline re-record, and the new BenchmarkDotNet
  version-drift warning names that cause when tooling, not code,
  moved.
