# Session 014 — Issue-debt round: server-spec adoption, style gates, CI trim

Date: 2026-09-21. Branch: `issue-debt-conventions`.

## Scope

Close issue debt with small, fully-verified deliverables (#34 new server
spec, #26 style directives, #20 comment forms) while reducing CI time and
holding code coverage flat. No PLAN milestone tasks this round by design
(M3.4 stays next).

## Delivered

- **#34 closed with evidence (correctness)**: the "new spec" (server
  v0.9.2, released 2026-09-21T03:20Z) is exactly our fixture pin
  `07a6fd08` — session 013 had already vendored it. Verified byte-identical
  via the sync script's verify mode; full-corpus route/decode tests green.
  Upstream main has no commits beyond the pin. No client changes needed.
- **#26 underscore ban executed (style)**: owner directive (2026-09-20)
  was "do all of the stuff"; the sibling repos were surveyed first —
  unity-helpers documents "PascalCase, NO underscores" for test names
  (lint rule UNH004) and DoxReloaded's tests follow the same shape.
  Renamed 111 test methods (mechanical underscore strip; `nameof`/
  `TestCaseSource` members were already clean); 320 x 2 TFM green with
  discovery count unchanged. New `scripts/lint-test-names.ps1` keeps it
  that way (also pins `TestName`/`SetName` display names to dot notation).
  `.llm` naming table + `create-test` skill updated as the SSOT.
- **#20 closed — comment forms enforced (style)**: ported the
  unity-helpers comment-form rule as `scripts/lint-comment-form.ps1`
  (C#-only, string-aware lexer: raw/verbatim/interpolated strings do not
  leak `//`; trailing comments never join runs; depth-from-indentation per
  CSharpier guarantees; `///` docs exempt; tool-directive runs exempt).
  Converted all 42 multi-line `//` runs to single `/* */` blocks (all were
  already rationale-only from prior sessions' minimal-comment policy —
  form was the gap, not content).
- **CI time down, coverage flat**: dotnet.yml matrix 4 -> 3 cells
  (windows+net10 dropped — OS assurance carried by windows+net8, TFM
  assurance by ubuntu+net10, coverage collected on ubuntu+net8 regardless).
  Measured from the last PR run: the dropped cell took 202s and windows+net8
  (the expected new long pole) 124s vs the old 3m26s wall; expect the PR
  wall around ~2m. Two new lint steps add ~10s to the coverage cell.
- **Sibling-analyzer decision recorded**: unity-helpers' WUH analyzers are
  UnityEngine-object-coupled (null-patterns, serialization, lifecycle) —
  poor fit for a netstandard2.1 protocol library. The adoptable pieces are
  the lint scripts; both new gates follow that pattern (CI/local only,
  nothing shipped).

## Red-green evidence

- RED 1: `lint-test-names.ps1` run against the pre-rename suite reported
  111 violations; rename turned it green (build + 320 x 2 TFM confirm).
- RED 2: `lint-comment-form.ps1` reported 42 runs; conversion -> 0.
- Self-tests: `test-lint-test-names.ps1` (8 asserts) and
  `test-lint-comment-form.ps1` (10 asserts) — includes the edge cases the
  implementation got wrong first: out-of-repo absolute paths (Substring
  crash), PS7 ternary line-break parse, string-literal `//` leaks.
- GREEN: `dotnet build -warnaserror` 0 warnings; 320 x 2 TFM; tooling
  projects build; CSharpier clean; all lints green; 11/11 self-test files.

## CI

Matrix trimmed (above). New steps on the coverage cell:
`lint-comment-form.ps1`, `lint-test-names.ps1`. Pre-commit hook gained the
same two gates for staged paths. run-all self-tests auto-discovered the
two new test files (9 -> 11).

## Verification

`pwsh scripts/tests/run-all.ps1` 11/11; all six lint scripts green;
`dotnet test` 320 x {net8.0, net10.0} green; CSharpier clean; fixtures
byte-identical at pin `07a6fd08`.

## Left for later

- DoxReloaded member-ordering lint (rule exists in their context.md;
  needs a small C# member-order checker) — follow-up issue filed.
- TUnit spike (after the polling client lands) — follow-up issue filed.
- M3.4 polling client (next PLAN surface; carries #7's idle-poll gate).
