---
name: powershell-tooling
description: PowerShell failure classes proven by real bugs in this repo - strict-mode scalar unroll, comma-plus-@() array nesting, array-to-string coercion, Write-Error under Stop inside loops, pwsh -File array binding, CWD-dependent tool invocations in scripts, sync scripts that cannot converge, and the report-all-then-fail contract.
metadata:
  category: core
---

# PowerShell Tooling Rules

This repo's automation (linters, hooks, self-tests) is PowerShell running
under `$ErrorActionPreference = 'Stop'` and `Set-StrictMode -Version Latest`.
Each rule below was a real production bug in this repository — do not
re-introduce them. When writing new tooling, check it against this list
BEFORE shipping, not only when a reviewer finds a violation.

## 1. Scalar unroll kills `.Count` under StrictMode

A function returning a collection unrolls single-element results to a scalar
at the call site. Under `Set-StrictMode -Version Latest`, `$scalar.Count`
throws "property 'Count' cannot be found". `foreach` over the scalar is
safe, so the crash surfaces later, at a `.Count` or property access — far
from the cause.

- **Rule**: bare-`return` the collection and wrap EVERY consuming call site:
  `$staged = @(Get-StagedFiles)`. That holds for all arities: 1 element ->
  1-element array, 0 -> empty, N -> unchanged. Never ALSO comma-prefix a
  return consumed via `@()`: comma-prefixing exists for binary buffers read
  by plain assignment, and comma + `@()` NESTS the array into
  `object[1]`, which then space-joins inside string interpolation and fails
  `[string]` parameter binding.
- Evidence: `.githooks/pre-commit.ps1` blocked every single-file commit
  (fixed in commit "Fix pre-commit strict-mode crash on a single staged
  file"; regression test `scripts/tests/test-pre-commit.ps1`); Bugbot
  finding "Array unroll breaks single-file corpus" on
  `scripts/sync-protocol-fixtures.ps1` (PR #10). The first fix attempt
  combined comma-prefix with `@()` call sites and nested the list —
  reproduced in isolation before shipping.

## 2. `[string]` params silently flatten arrays

Binding an object array to a `[string]` parameter joins elements with
spaces — no error, wrong data. Multi-line fixtures became one line and
line-oriented lint checks ran against vacuous input.

- **Rule**: type content-shaped parameters `[object]` and join explicitly
  with `` `n `` (see `Write-TestFile` in `scripts/tests/test-common.ps1`).
- Evidence: Bugbot finding "Test helper flattens file content";
  regression test `scripts/tests/test-test-helpers.ps1`.

## 3. No terminating errors inside loops — report all, then fail

`Write-Error` under `$ErrorActionPreference = 'Stop'` terminates on first
use. Linters that emit it per violation stop at finding #1 and hide the
rest, which turns every fix round into a one-violation-per-CI-run loop.

- **Rule** (report-all-then-fail): collect violations, print each on its
  own `Write-Host` line, then one summary line and `exit 1`. Never call
  `Write-Error` inside a loop.
- Evidence: Bugbot finding "Linters abort after first error";
  regression tests assert TWO oversized files / TWO lint errors are BOTH
  reported (`test-lint-file-sizes.ps1`, `test-lint-llm-instructions.ps1`).

## 4. `pwsh -File` cannot receive array values

Tokens after a named parameter fall through to the next *positional*
parameter (or fail type transformation); `-Switch v1 -Switch v2` does not
append either. Array arguments only work via expression parsing.

- **Rule**: to pass multiple values, invoke with `pwsh -Command` and an
  array literal, e.g. `& script.ps1 -Paths @('a','b')` — exactly what
  `.githooks/pre-commit.ps1` does. In self-tests use
  `Invoke-PwshCommand` from `test-common.ps1`.
- Evidence: `test-lint-file-sizes.ps1` case 7 failed three different ways
  before landing on `-Command` (bare token leaked into `$MaxLines`).

## 5. Exit-code contract

Scripts communicate failure by `exit 1` with human-readable, one-violation-
per-line output on stdout; hooks and CI only read the exit code and the
captured output. Keep the two channels consistent — assert exit code AND
message text in self-tests.

## 6. Sync/mirror scripts must converge — deletions included

A sync that only overwrites files present in the source cannot complete a
legitimate source-side removal or rename: stale local files trip the
set-equality check forever, and the error message tells users to re-run the
very mode that dead-ends.

- **Rule**: a sync converges first (write the source set, delete stale
  locals within its managed scope), THEN asserts set equality as a
  postcondition, THEN regenerates derived files. Never assert before the
  state is converged, and never leave derived output stale after a failed
  run.
- Evidence: Bugbot finding "Sync cannot drop removed fixtures" on
  `scripts/sync-protocol-fixtures.ps1` (PR #10) — a pin bump removing a
  fixture could never finish. Fixed: stale fixtures are deleted before the
  postcondition assert; `PROVENANCE.md` is regenerated last.

## 7. Scripts must not depend on the caller's current directory

Repo scripts are launched three ways: `pwsh -NoProfile -File scripts/x.ps1`
from the root (CI), by absolute path from anywhere (agents, hooks), and via
`git hooks` (CWD = repo root, but only by luck). Any command that resolves
against the current directory breaks the second launch shape — and breaks
it silently only when someone launches from elsewhere, i.e. exactly when
it is hardest to debug. Two sub-cases seen in this repo:

- **Tool manifests**: `dotnet tool restore/run` find
  `.config/dotnet-tools.json` by walking UP from the current directory.
  Running the command outside the repo tree fails even when the script
  derived `$RepoRoot` correctly.
- **Git**: bare `git config`/`git add` act on whatever repo the CWD lands
  in — or none. The failure message ("are you inside the repository?")
  blames the caller for the script's own assumption.

- **Rule**: derive `$RepoRoot` from `$PSScriptRoot` (never `$PWD`), build
  all paths from it, and wrap CWD-resolving tool invocations in
  `Push-Location $RepoRoot` / `try` / `finally { Pop-Location }` — or pass
  explicit scope flags (`git -C`). Verify by launching the script by
  absolute path from outside the repository; pin that launch shape in a
  self-test.
- Evidence: Bugbot finding "SharpFuzz ignores repository root" on
  `scripts/fuzz-codec.ps1` (PR #18) — `dotnet tool restore` was anchored
  but the later `dotnet tool run` was not; siblings found by sweep and
  fixed in the same change: `scripts/install-hooks.ps1` bare `git config`
  and `scripts/install-hooks.sh` (same class, different language — check
  shell siblings too). Regression test:
  `scripts/tests/test-install-hooks.ps1`.

## Testing tooling

Self-test scripts with a disposable git repo (`New-TestRepo`): copy the
scripts under test into it, run the real script, assert exit code + output.
See `scripts/tests/test-pre-commit.ps1` for the pattern. New tooling ships
with its self-test in the same change.

## Related Skills

- [address-pr-feedback](../address-pr-feedback/SKILL.md) - sweep-and-fix workflow that surfaces these classes
- [create-test](../create-test/SKILL.md) - self-test authoring conventions
- [manage-skills](../manage-skills/SKILL.md) - skill authoring rules
