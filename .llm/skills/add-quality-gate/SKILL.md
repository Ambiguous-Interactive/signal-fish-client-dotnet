---
name: add-quality-gate
description: Add or modify repo lint/gate tooling (scripts/lint-*.ps1, pre-commit hook steps, CI check steps) with the three-scope contract - keep the linter's enforced set, the hook's trigger set, and CI's trigger set in lockstep so the hook can never bless a commit CI rejects.
metadata:
  category: core
---

# Add Quality Gate

A repo gate (linter, formatter check, dependency ban) is enforced in three
places: the tool itself, the pre-commit hook, and CI. Bugs live in the gaps
between their scopes, not in the tools.

## When to Use

- Adding a new linter/formatter/dependency ban to `scripts/`, the
  pre-commit hook, or a CI workflow.
- Extending an existing gate's accepted inputs (new file type, new rule
  that reads more than before).
- Reviewing a gate change for the scope-mismatch class.

## The three-scope contract

| Scope | Defined by | Must be |
| --- | --- | --- |
| Enforced set | What the linter rejects in default (no `-Paths`) mode | The real policy |
| Hook trigger | The staged-file selectors in `.githooks/pre-commit.ps1` | Superset of every enforced-set input, filtered to staged paths |
| CI trigger | Workflow path filters + default-mode invocations | Superset of the hook trigger (or path-filter-free) |

Invariant: **anything the enforced set can reject, staging it must trigger
the hook, and CI must run the gate on it.** A narrower hook trigger blesses
commits CI rejects — the developer learns at push time what the hook should
have caught. A wider CI-only trigger is at most a delay, never a hole.

## Checklist

1. Write the linter with a **default mode that sweeps the whole enforced
   set** and a `-Paths` mode for staged paths that filters to its own
   accepted inputs (callers may over-pass; out-of-scope paths pass clean).
2. Add the hook step with a selector derived from the accepted inputs —
   not from whatever file types existed on the day it was written.
3. Wire CI to run the gate in default mode (one cell is fine for cheap
   text scans if the matrix is parallel; see `dotnet.yml`).
4. **Self-test every accepted input type through the hook**: the
   `test-pre-commit.ps1` suite must stage at least one violating file per
   accepted type and assert the block message. A selector regression is a
   silent hole otherwise.
5. Reset the git index between hook self-test cases (`git reset -q`);
   staged files accumulate, and a leftover violating file makes later
   cases pass for the wrong reason.
6. If the gate shells out to an installable tool, catch the
   not-restored case and print the restore command — never surface a raw
   `Cannot find a tool in the manifest file` error.
7. Regenerate nothing else, but run `scripts/tests/run-all.ps1` — hook
   self-tests run in CI (`llm-context.yml`).

## Incidents that shaped this

- 2026-09-20 (PR #14, Bugbot): the LINQ ban accepted `.cs` + `.csproj`
  but the hook forwarded only staged `src/**/*.cs`, so a project file
  injecting `<Using Include="System.Linq" />` sailed past the hook and
  failed CI.
- 2026-09-20 (sibling sweep): `lint-file-sizes.ps1` enforces every
  `.cursor/rules/*.mdc`; the hook knew only the one pointer file, so a new
  oversized rule file was hook-invisible.

## Related Skills

- [address-pr-feedback](../address-pr-feedback/SKILL.md) - sweep the class
  after any finding
- [powershell-tooling](../powershell-tooling/SKILL.md) - PS failure classes
  in this repo's tooling
- [create-test](../create-test/SKILL.md) - red-green for gate changes
