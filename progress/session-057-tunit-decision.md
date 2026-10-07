# Session 057 — #111 decided: stay on NUnit (MTP)

Date: 2026-10-07
Branch: `m111-tunit-decision`
PR: (this session)

## Drift check

- origin/main merged clean at `caa3417` (#112); working tree was clean;
  no open or draft PRs from earlier sessions. Pruned the five stale
  local branches from merged sessions (`m94-scheduled-bench`,
  `m95-release-checklist`, `m96-upm-release-tarballs`,
  `m97-nunit5-fscheck-decouple`, `m111-nunit-mtp-runner`) — each landed
  via squash (#104-#112 range) and its remote half was deleted at
  merge.
- Main CI green on #112: dotnet, Docs, e2e, LLM Context all success.
- Dependabot weekly ran 2026-10-07, both groups green, nothing open.
- Open issues at start: #111 (this session's surface), #109 (future
  discovery work: lane unpicked; npm path needs a registry token),
  #80 (dormant: enable on the first page that needs one). M7.4 Unity
  validation and the M8.7 live drills stay blocked on a licensed seat.

## Decision

**#111 is declined** — the owner call, exercised this session under
GOAL.md's open-issue mandate: **the suite stays on NUnit 5 riding
Microsoft.Testing.Platform — the `EnableNUnitRunner` shape #112
landed.** The evaluation the issue asked for is complete; closing with
the reasoning is the issue's own declared decline path.

Why, from the issue's own research plus the #110/#112 outcomes:

- Both original motivators are gone. The FsCheck.NUnit pin died with
  #110 (NUnit 5.0.0), and the runner-generation goal (MTP) landed in
  #112 with zero test changes — 933/933 on both TFMs.
- The remaining benefit is seconds, and the direction is already
  banked. The issue's "real argument" — MTP as Microsoft's stated
  direction (deterministic, reflection-free, AOT-capable) — is
  captured by #112: NUnit rides the same platform, so TUnit's marginal
  delta over NUnit-on-MTP is the rewrite itself. The speed gap is
  seconds on a ~23 s suite (23.4/22.8 s per session 056), invisible to
  an SDK whose public surface is frozen under the api-compat gate.
- The remaining cost is 2-4 engineer-days: 2,552 auto-rewritten asserts
  each needing review (the fixer had real bugs), plus 23 hand
  conversions (13 `[TestCaseSource]`, 6 `[Values]`, 4
  `[OneTimeSetUp]`) and the FsCheck wiring. Pure churn in maintenance
  mode.
- Supply risk: ~20 releases in 30 days and no LTS, against the house
  "pin exactly, ride bumps deliberately" discipline, for no gain.
- Reversible: the MTP base #112 shipped is exactly what a later
  conversion needs (fixer + review, no platform flip, no TRX/coverage
  replumbing).

## What shipped

- `.llm/references/project-decisions.md` — the tooling-defaults row now
  names NUnit-on-MTP and records the declined TUnit evaluation.
- This file; PLAN.md collapsed (no actionable surface remains; the
  blocked, parked, and dormant items keep their pointers). The
  reflect-improve loop ran as a lightweight pass (announcement-only;
  entry in `.llm/improvement-log.md`).
- #111 closed as completed with the rationale comment and this PR's
  link.

## Verification

- No code changed; `dotnet build` / `dotnet test` not applicable (last
  green: #112, 933/933 on net8.0 and net10.0, main CI fully green).
- `lint-file-sizes` (PLAN.md and `.llm` budgets) and
  `lint-llm-instructions` green; skills index untouched (no skill
  change).
