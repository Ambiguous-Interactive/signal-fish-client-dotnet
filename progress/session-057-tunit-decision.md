# Session 057 — #111 decided: stay on NUnit (MTP)

Date: 2026-10-07
Branch: `m111-tunit-decision`
PR: (this session)

## Drift check

- origin/main merged clean at `caa3417` (#112); working tree was clean;
  no open or draft PRs from earlier sessions (the two stale local
  branches `m111-nunit-mtp-runner` / `m97-nunit5-fscheck-decouple`
  remain, their remote halves deleted at merge).
- Main CI green on #112: dotnet, Docs, e2e, LLM Context all success.
- Dependabot weekly ran 2026-10-07, both groups green, nothing open.
- Open issues at start: #111 (this session's surface), #109 (future
  discovery work: lane unpicked; npm path needs a registry token),
  #80 (dormant: enable on the first page that needs one). M7.4 Unity
  validation and the M8.7 live drills stay blocked on a licensed seat.

## Decision

**#111 is declined: the suite stays on NUnit 5 riding
Microsoft.Testing.Platform — the `EnableNUnitRunner` shape #112
landed.** The evaluation the issue asked for is complete; closing with
the reasoning is the issue's own declared decline path.

Why, from the issue's own research plus the #110/#112 outcomes:

- Both original motivators are gone. The FsCheck.NUnit pin died with
  #110 (NUnit 5.0.0), and the runner-generation goal (MTP) landed in
  #112 with zero test changes — 933/933 on both TFMs.
- The remaining benefit is seconds. TUnit's published wins are real,
  but the suite runs ~23 s, and a test runner is invisible to an SDK
  whose public surface is frozen under the api-compat gate.
- The remaining cost is 2-4 engineer-days: 2,552 auto-rewritten asserts
  each needing review (the fixer had real bugs), plus 26 hand
  conversions. Pure churn in maintenance mode.
- Supply risk: ~20 releases in 30 days and no LTS, against the house
  "pin exactly, ride bumps deliberately" discipline, for no gain.
- Reversible: the MTP base #112 shipped is exactly what a later
  conversion needs (fixer + review, no platform flip, no TRX/coverage
  replumbing).

## Shipped

- `.llm/references/project-decisions.md` — the tooling-defaults row now
  names NUnit-on-MTP and records the declined TUnit evaluation.
- This file; PLAN.md collapsed (no actionable surface remains; the
  blocked and dormant items keep their pointers).
- #111 closed as completed with this rationale and the PR link.

## Verification

- No code changed; `dotnet build` / `dotnet test` not applicable (last
  green: #112, 933/933 on net8.0 and net10.0, main CI fully green).
- `lint-file-sizes` (PLAN.md and `.llm` budgets) and
  `lint-llm-instructions` green; skills index untouched (no skill
  change).
