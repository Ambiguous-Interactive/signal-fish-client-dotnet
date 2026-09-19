# Session 002 — PR feedback round 1 + feedback-workflow skill

Date: 2026-09-18. Branch: `scaffold-governance-m0` (PR #1).

## Feedback fetched

Cursor Bugbot reviewed commit `026d9e1` with 3 findings (no human feedback
yet). All 3 verified valid; all 3 fixed; classes swept repo-wide.

| Finding | Severity | Verification | Disposition |
| --- | --- | --- | --- |
| Linters abort after first error | Medium | `Write-Error` under EAP=Stop in loops (`lint-file-sizes.ps1`, `lint-llm-instructions.ps1`) | Fixed: report-all-then-fail; tests assert TWO violations are BOTH reported |
| IPv6 hosts produce invalid URIs | Low | `DefaultServerUri("::1")` threw `UriFormatException` (3 RED cases) | Fixed: bracket `:`-containing hosts; idempotent for pre-bracketed |
| Test helper flattens file content | Low | `Write-TestFile([string])` space-joined arrays; fixtures were single-line | Fixed: `[object]$Content` + explicit join; helper regression test added |

## Extra bug found while going green

`pwsh -File` sends surplus tokens after a named parameter to positional
parameters (never appends to the named `string[]`); three invocation
variants failed before landing on `pwsh -Command` + array literal (the
pattern `pre-commit.ps1` already used). Added `Invoke-PwshCommand` helper.

## Sweeps (class elimination)

- In-loop `Write-Error`/`throw`: only single-shot sites remain
  (`run-all.ps1`, `install-hooks.ps1` usage errors) — class eliminated.
- String-interpolated URIs: single site, now IPv6-safe — class eliminated.
- Narrowly-typed content params: only `Write-TestFile` — fixed.

## Knowledge capture (reflect-improve)

- New skill `address-pr-feedback`: mechanical gh fetch -> triage
  (AUTO-FIX/ASK/REJECT) -> verify every claim -> class sweep -> red-green ->
  reply map -> knowledge capture. Synthesized from this round plus
  unity-helpers' `review-code-changes` (two-pass, fix-first heuristic) and
  `ship-changes` (gated workflow) — unity-helpers had no fetch-feedback
  procedure itself.
- New skill `powershell-tooling`: the five PS failure classes proven by
  real bugs in this repo (scalar unroll/`.Count`, array->string coercion,
  terminating errors in loops, `pwsh -File` array binding, exit-code
  contract), each with evidence and regression-test pointers.
- Improvement log entry `2026-09-18 - PR feedback round 1`.

## Verification

- `dotnet build -c Release -warnaserror`: 0 warnings (both TFMs).
- `dotnet test`: 10/10 on net8.0 and net10.0 (6 new IPv6/bracket cases).
- `scripts/tests/run-all.ps1`: 6/6 self-test files (new:
  `test-test-helpers.ps1`; report-all cases in the two lint tests).
- Both `.llm` linters green; skills index regenerated (13 skills).

## Leftovers

- CI must be green after push; then PR comment with the finding->fix map.
- unity-helpers mining follow-up (optional): none beyond what was ported —
  its review/ship skills assume a local `npm` validation stack this repo
  does not have; the portable parts are folded into `address-pr-feedback`.
