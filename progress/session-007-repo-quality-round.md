# Session 007 — Repo quality round: analyzers, LINQ ban, CSharpier, devcontainer self-test

Date: 2026-09-20. Scope: issue-debt round addressing open issues #5, #6, #7
(partial), #12, with a net CI-time decrease and no coverage loss.

## What shipped

- **#6 (max warnings, warnings as errors, analyzers)**
  - `Directory.Build.props`: `WarningLevel=9999`, `TreatWarningsAsErrors`,
    `EnableNETAnalyzers`, `AnalysisLevel=latest-all`,
    `EnforceCodeStyleInBuild`, `Nullable=enable` — repo-wide, matching and
    exceeding the CI `-warnaserror` gate.
  - Genuine fixes in src/: `ArgumentException` `paramName` misuse (CA2208,
    8 sites in `EnvelopeWriter` helpers now use `nameof(param)`),
    `string.Contains` without `StringComparison.Ordinal` (CA1307), two
    `JsonScanner` members made static (CA1822, call sites updated).
  - Scoped suppressions with rationale: test assemblies (CA1707 underscore
    test names, CA1062 framework-guaranteed args, CA1515 public classes) via
    `Directory.Build.targets` (a `.props` condition cannot see csproj-body
    properties — lesson recorded); perf-intentional rules in `.editorconfig`
    (CA1028 byte-backed wire enums, CA1815 non-compared hot-path event
    struct).
  - Test-side real fixes: constant arrays hoisted to `static readonly`
    (CA1861), `ThrowIfGreaterThan` throw helper (CA1512), redundant switch
    fallback arms collapsed (CA1508).

- **#7 (zero-allocation enforcement) — partial; benchmarks remain open**
  - `scripts/lint-no-linq.ps1`: bans `using System.Linq` / `System.Linq.`
    references under `src/` (tests exempt). Chosen over
    BannedApiAnalyzers because any `PackageReference` in `src/` violates the
    locked zero-dependency rule. Wired into CI (`dotnet.yml`) and the
    pre-commit hook; self-tested (`scripts/tests/test-lint-no-linq.ps1`,
    14 assertions).
  - New allocation gate: `Decode_SteadyStateFullCorpus_AllocatesNothing` —
    whole golden corpus, min delta over 4 passes, must be 0 B. Red-checked
    with a planted allocation. The writer gate already existed.

- **#5 (CSharpier)**
  - `.config/dotnet-tools.json` (csharpier 1.3.0); codebase formatted
    (src + tests + csproj/props/targets/slnx); `check .` wired into CI
    (single matrix cell — ubuntu/net8.0 — to keep matrix cost flat) and the
    pre-commit hook for every staged file CSharpier formats (`*.cs`,
    `*.csproj`, `*.props`, `*.targets`, `*.slnx` — must match CI's `.` scope
    so the hook cannot bless a commit CI rejects).

## Adversarial review round (post-implementation)

Findings fixed: hook formatting scope widened to CI's scope; tool manifest
staged with the change set; missing-manifest case now prints restore
guidance (and is self-tested); allocation gate asserts an all-known-message
corpus (fixture-sync coupling is now a named failure) and uses 8 passes;
lint-no-linq covers the parented-namespace using form, csproj
`<Using Include="System.Linq" />`, and `ImplicitUsings` (22 self-test
assertions); `.editorconfig` CA1028/CA1815 suppressions scoped to
`src/SignalFish.Client/Protocol/`; CI conditions unified on `matrix.os`;
self-test ELF check gained a `\x7fELF` magic guard; README format command
matches CI scope; src prefix guard keeps sibling `src-*/` dirs out of
scope.

- **#12 (devcontainer nested-invocation self-test)**
  - `self-test.sh`: real nested `& pwsh` invocation check (the exact path
    `.githooks/pre-commit.ps1` uses) + ELF `e_machine` assertion (od,
    offset 18) that the resolved pwsh binary matches `uname -m`. Verified
    live on arm64 (the incident's host class). Zero CI cost: the
    devcontainer workflow only runs on `.devcontainer/**` changes.

- **CI time: planned net decrease.** Coverage collection moves from all 4
  matrix cells to the two Linux cells (coverage was only ever *reported* on
  Linux; Windows cells paid instrumentation cost for data nobody read), so
  the Linux cells still report coverage for both TFMs — measured coverage
  is unchanged. Added steps are seconds-scale: the repo-wide lints mirror
  the existing zero-dependencies step; the CSharpier check runs in a single
  matrix cell and hides inside the parallel matrix wall clock.

## Verification

- `dotnet build -c Release` — 0 warnings, 0 errors (analyzers + style gate).
- `dotnet test -c Release` — 229 passed x {net8.0, net10.0}.
- `pwsh -NoProfile -File scripts/tests/run-all.ps1` — 7/7 self-test files
  pass (incl. the new no-linq suite).
- `scripts/lint-no-linq.ps1`, `scripts/lint-zero-dependencies.ps1` — clean.
- `dotnet tool run csharpier -- check .` — clean.
- `bash -n .devcontainer/scripts/self-test.sh` + live nested/ELF checks.

## PR feedback round (post-push)

Cursor Bugbot (medium): the pre-commit hook forwarded only staged
`src/**/*.cs` to the LINQ linter, so a staged src `.csproj` injecting
`<Using Include="System.Linq" />` was blessed locally and failed in CI.
Verified, fixed red-green, and swept the class:

- Sibling found and fixed: the size lint enforces every
  `.cursor/rules/*.mdc` but the hook knew only the `signal-fish.mdc`
  pointer.
- All six gates audited against the three-scope contract (linter enforced
  set / hook trigger / CI trigger); the other four were already aligned.
- 4 new hook self-test assertions (with index resets between cases —
  staged files accumulate and had masked the .csproj case on the first
  red run); 15/15 pass.
- Knowledge captured: new skill `.llm/skills/add-quality-gate/`
  (three-scope contract, checklist, incidents) + sweep-table row in
  `address-pr-feedback`; improvement-log entry added.

## Leftovers / next surfaces

- #7 stays open: BenchmarkDotNet baselines + `docs/benchmarks.md` are M1.6;
  `unity-helpers` buffer techniques to review when the polling client (M3)
  lands.
- #9 (upstream golden fixtures) unchanged: upstream still lacks
  GameStarting/RoomLeft/*Failed samples at its tip.
- M1.4 (FsCheck property tests) is the next milestone task.
