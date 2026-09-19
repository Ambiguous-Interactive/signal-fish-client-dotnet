---
name: powershell-tooling
description: PowerShell failure classes proven by real bugs in this repo - strict-mode scalar unroll, array-to-string coercion, Write-Error under Stop inside loops, pwsh -File array binding, and the report-all-then-fail contract.
metadata:
  category: core
---

# PowerShell Tooling Rules

This repo's automation (linters, hooks, self-tests) is PowerShell running
under `$ErrorActionPreference = 'Stop'` and `Set-StrictMode -Version Latest`.
Each rule below was a real production bug in this repository — do not
re-introduce them.

## 1. Scalar unroll kills `.Count` under StrictMode

A PowerShell function returning `@($x)` unrolls single-element results to a
scalar. Under `Set-StrictMode -Version Latest`, `$scalar.Count` throws
"property 'Count' cannot be found".

- **Rule**: wrap any function-result collection at the call site:
  `$staged = @(Get-StagedFiles)`.
- Evidence: `.githooks/pre-commit.ps1` blocked every single-file commit
  (fixed in commit `Fix pre-commit strict-mode crash on a single staged
  file`; regression test `scripts/tests/test-pre-commit.ps1`).

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

## Testing tooling

Self-test scripts with a disposable git repo (`New-TestRepo`): copy the
scripts under test into it, run the real script, assert exit code + output.
See `scripts/tests/test-pre-commit.ps1` for the pattern. New tooling ships
with its self-test in the same change.

## Related Skills

- [address-pr-feedback](../address-pr-feedback/SKILL.md) - sweep-and-fix workflow that surfaces these classes
- [create-test](../create-test/SKILL.md) - self-test authoring conventions
- [manage-skills](../manage-skills/SKILL.md) - skill authoring rules
