# Session 055 — NUnit 5 through the FsCheck pin (#88)

Date: 2026-10-07
Branch: `m97-nunit5-fscheck-decouple`
PR: #110 (closes #88)

## What shipped

**#88 — the suite takes NUnit 5.0.0 by shedding FsCheck.NUnit**: the
3.4.0 adapter pinned `NUnit < 5.0.0` and blocked every framework bump;
while the session ran, the ecosystem moved under it — NUnit 5.0.0
shipped 2026-09-27 and FsCheck.NUnit stayed at 3.4.0, so waiting had no
end date. Three commits, each independently buildable:

- **FsCheck without an adapter** — `CodecPropertyTests` drops the
  `FsCheck.NUnit` attribute integration; the six properties run FsCheck
  core through its own runner inside plain `[Test]`s
  (`Config.QuickThrowOnFailure.WithMaxTest(n).WithQuietOnSuccess(true)`
  via `.Check(...)`), the exact documented framework-agnostic pattern.
  Generators untouched. With no `FsCheck.<framework>` package, no test
  framework can be version-pinned again — this survives any future
  runner decision. The `[Property]`-attribute shape is preserved in a
  4-line `PropertyConfig` helper.
- **NUnit 4.6.1 → 5.0.0** (Tests + E2E). NUnit 5's one breaking change
  this suite hits: `Assert.ThrowsAsync`/`CatchAsync`/
  `DoesNotThrowAsync` return a `Task` that must be awaited — all 13
  sites fixed (the NUnit2059 analyzer enumerated them; compiler-enforced,
  nothing silent). The two sync tests housing them become `async Task`
  (rule 3 shape), and their pre-cancel `Cancel()` calls become awaited
  `CancelAsync()` under CA1849. The rest of the 5.0 breaking-change
  list (TestDelegate removal, legacy Assert namespace moves, `Is.SameAs`
  object overloads, `[Platform]` renames, `[Order]`, CollectionTally)
  was swept against the whole repo: zero uses.
- **Dependabot un-ignores NUnit** — the `ignore` block added when the
  pin landed is gone; the weekly grouped bump carries NUnit again.
  Plus two drift fixes found in passing: `docs/attributions.md` names
  `FsCheck` (the package actually referenced), and `.llm/context.md`'s
  status line no longer claims the public surface lacks a tagged freeze
  (`v0.1.0` + the api-compat gate exist since M9.3).

## Red-green evidence

- Baseline before the change: 933/933 on net8.0 in 36 s wall / 15 s run.
- Red check on the new path: falsified one property
  (`materialized != value`) — the test fails via FsCheck's throwing
  runner with the shrunk counterexample and replay seed, then reverted.
- After: 933/933 on net8.0 **and** net10.0, same counts (no test lost
  in the attribute conversion), same 15 s run duration.
- Equivalence verified against FsCheck 3.4.0 sources
  (`FsCheckPropertyAttribute.fs`, `Runner.fs`): the old attribute built
  from `Config.Default` — which in 3.4.0 *is* `Config.Quick` — so
  `Config.QuickThrowOnFailure` (= `Quick` + throwing runner) plus the
  same two overrides reproduces the previous numeric config exactly
  (StartSize 1, EndSize 100, MaxRejected 1000). One honest behavioral
  delta: a falsified property now surfaces as NUnit `Error` (thrown
  exception) instead of `Failed` (the adapter recorded it as a failed
  assertion). Message text is byte-identical; CI verdict unchanged; no
  tooling keys off that distinction.
- Convention lints (all six), CSharpier, YAML lint on dependabot,
  `lint-llm-instructions`, `lint-file-sizes` — green locally.
- Commit 1 verified green in isolation (fresh worktree at 53c2eb4:
  build + 933/933).

## Findings

- **FsCheck's C# docs are stale**: the RunningTests page still teaches
  `Configuration.Quick` for C#, but the type does not exist in 3.4.0 —
  reflection over the shipped DLL showed the real surface is
  `FsCheck.Config` (F# record, `With*` members callable from C#) +
  `CheckExtensions.Check(Property, Config)`. Do not trust the docs
  table; trust the package.
- **The NUnit 5 break was the friendly kind**: the analyzer (NUnit2059)
  and the compiler flag every un-awaited async assert, so the sweep was
  mechanically verifiable — 13 sites, zero judgement calls.
- **The `E2E` project needed nothing** for NUnit 5 beyond the version
  bump: no async asserts, no removed APIs.

## Adversarial review round (evidence-first sub-agent)

Verdict: **APPROVE** — zero BLOCKER/SHOULD-FIX; nine INFO findings, all
reproduced: the Failed→Error ResultState delta (accepted, cosmetic),
the config equivalence proof from FsCheck sources, an off-by-one in my
commit message ("three" sync conversions; it is two — fixed via reword
+ force-push on the unmerged branch), the adapter's missing official
NUnit 5 statement (accepted: no statement exists, 933/933 green through
it on both TFMs, noted in the PR), the clean breaking-change sweep,
`.Check` exception containment (exceptions inside property lambdas fold
into the falsification message, never escape), and dependabot/CI/api-
compat confirmations (the diff never touches `src/`, so the gate is
inert).

## The research half (owner's question, answered but not acted on)

The owner asked on #88 whether TUnit/xUnit make more sense than NUnit.
Researched with current data: **TUnit is the strongest long-term fit**
(1.x stable 11 months, MTP-native, automated NUnit migration via the
`TUNU0001` fixer, first-party `TUnit.FsCheck`), **xUnit v3 solves
nothing we need** (no converter; 2,552 constraint asserts by hand), and
the honest counterweights are that this suite's absolute perf stake is
seconds and TUnit's release cadence is hot. Full comparison + effort
estimate (2-4 days) + decision gate filed as #111 for the owner call.
Deliberately not migrated in this session: one coherent deliverable,
and the runner-generation decision is not mine to make.

## Deliberate scope cuts

- **No TUnit/MTP move** — see above; #111 holds the decision.
- **No CHANGELOG entry** — rule 16: test/tooling churn is never
  user-visible.
- **No PLAN.md in the PR** — gitignored by design.
- **No NUnit-on-MTP flip** (`EnableNUnitRunner`) — real option, but it
  entangles the coverlet→MTP-coverage swap with this round; belongs
  with the #111 decision.

## Verification

- `dotnet build` (solution, both TFMs) and `dotnet test` (933/933 × 2
  TFMs) green at HEAD.
- `scripts/lint-conventions.ps1` 6/6, CSharpier check, dependabot YAML
  parse, `lint-llm-instructions` — green.
- Main CI green at session start (`ab5b893`, all four workflows);
  branch pushed; PR CI to be watched to green before merge.
