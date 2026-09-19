# Session 004 — M1.1 golden protocol fixtures + sync script

Date: 2026-09-19. Branch: `golden-fixtures-m1.1`. Goal: advance PLAN.md to
a green PR. Closes issue #3 (M1.1), opening milestone M1 (protocol core).

## Drift check

- `origin/main` up to date; working tree clean; no open PRs; no draft or
  in-progress PRs from previous sessions; main CI green at session start.
- Open issues triaged by gameplay impact: #3 (M1.1 fixtures — protocol
  correctness, next PLAN milestone) taken; chores #5-#8 (CSharpier,
  analyzers, perf, zero-deps) deferred as lower priority per the ordering.
  Zero-deps (#8) is partially served here: the new corpus test uses only
  inbox System.Text.Json in the test project; library stays dep-free.

## This session's surface: M1.1 (issue #3)

- `tests/Golden/`: 4 JSONL wire-sample files vendored byte-for-byte from
  `signal-fish-server` `.llm/code-samples/protocol/` at pinned commit
  `eaae1ca3` (v2 client/server + v3 client/server; 12/10/9/16 messages).
- `tests/Golden/PROVENANCE.md`: generated provenance (source repo, pin,
  sync date, per-file line counts). The JSONL files themselves stay
  byte-identical to upstream — JSONL has no comment syntax, so a header
  inside the data files would corrupt them; provenance lives beside them.
- `scripts/sync-protocol-fixtures.ps1`: verify-by-default (downloads the
  pinned corpus, compares byte-identical after CRLF normalization, fails on
  content drift or file-set drift with actionable guidance) plus `-Sync`
  re-vendor mode (rewrites fixtures + provenance). Upstream file list is
  discovered via the GitHub contents API, so new upstream samples are
  never silently missed. Listing uses authenticated `gh api` when available
  (5000 req/hr) with an anonymous REST fallback; file bytes always come
  from raw.githubusercontent.com. Deliberately NOT wired into per-PR CI
  (network flake risk; drift matters when codec work consumes the corpus,
  and any contributor can run verify in seconds).
- `GoldenFixtureCorpusTests`: consumption red-green anchor — pinned corpus
  present + every envelope line is a JSON object carrying a non-empty
  `type` discriminator and object `data` when present. Fixtures copy to
  test output via csproj `Content` + `LinkBase` (no fragile path walking).
- Review round 1 also drove: byte-based drift compare (BOM tamper now
  fails), raw-byte downloads (no IRM stringification hazard), invariant
  culture + ordinal sorts, upstream filename guard, single download per
  file, and the `gh api`-first listing (anonymous 60 req/hr burned out
  mid-session — exactly the flake the review predicted).

## Red → green (verified empirically)

- Appending `{"type": 42}` to a fixture → `EveryEnvelopeLine_...` fails
  (non-string discriminator). Tampering a vendored file → verify mode
  exits non-zero with restore/re-sync guidance. Both restored clean after.
- Test initially written against an output-dir-relative walk; switched to
  csproj content copy before it ever ran — deterministic on all TFMs.

## Verification

- `dotnet build -c Release -warnaserror`: 0 w / 0 e.
- `dotnet test -c Release`: 12/12 on net8.0 + net10.0 (2 new tests).
- `scripts/sync-protocol-fixtures.ps1` verify: green at pin (run 3×,
  including after tamper/restore).
- `scripts/tests/run-all.ps1`: 6/6 self-test files.
- `lint-file-sizes.ps1` + `lint-llm-instructions.ps1`: pass.
- Fixture byte sizes match the upstream API listing exactly
  (762/837/1412/5042).

## Notes for future sessions

- M1.2 (EnvelopeReader) should decode every `tests/Golden/*.jsonl` line;
  M1.3 (EnvelopeWriter) round-trips the client-message files. The corpus
  covers what upstream publishes today — `GameStarting`, `RoomLeft`, and
  the `*Failed` family have no upstream samples; tracked as issue #9
  (request upstream, never hand-vendor). Fixture requests belong upstream.
- PLAN.md + GOAL.md remain gitignored local-only docs (unchanged rule).

## Leftovers / follow-ups

- Chore issues #5-#8 remain open, ordered behind M1.2/M1.3 codec work.
- Issue #9 tracks the upstream fixture gap (v2 floor completeness).
- If CI ever needs drift detection, run the sync script in a scheduled
  (not per-PR) workflow to respect rate limits (gh api path mitigates).

## Adversarial review round (sub-agent)

Verdict was FIX-FIRST. Findings and dispositions:

| Finding | Severity | Disposition |
| --- | --- | --- |
| Done-when "covers every v2 message" not literally met (no upstream `GameStarting`/`RoomLeft`/`*Failed` samples); gap disclosed incompletely | MAJOR | Fixed: "Coverage gaps" section generated into PROVENANCE.md; issue #9 filed; PR states the caveat |
| BOM'd hand-edits silently passed verify (ReadAllText strips BOM) — empirically proven | MINOR | Fixed: byte-based compare (ReadAllBytes + CRLF-normalized SequenceEqual); BOM tamper now fails (verified) |
| Invoke-RestMethod stringification: latent self-consistent corruption if upstream adds a JSON-served file | MINOR | Fixed: raw byte download via Invoke-WebRequest -OutFile + ReadAllBytes |
| Culture-dependent date (Hijri repro) + culture-sensitive Sort-Object | MINOR | Fixed: InvariantCulture date, [StringComparer]::Ordinal sorts |
| -Sync downloads each file twice | MINOR | Fixed: line counts from bytes already in hand |
| No validation of upstream file names | NIT | Fixed: `^[A-Za-z0-9._-]+$` guard |
| PROVENANCE source URL not lychee-excluded (server repo) | NIT | Accepted consciously: the server repo is public and SHOULD be link-checked (references elsewhere depend on it); excluding it would hide real breakage |
| Session date 2026-09-19 vs env date 09-18 | NIT | Machine UTC clock reads 09-19; script records UTC — kept |
| Two-segment test names vs Method_Scenario_Expectation | NIT | Fixed: `Corpus_WithPinnedFiles_...`, `Corpus_EveryEnvelopeLine_...` |
| Blank-line JSONL corruption passes structural test | NIT | Fixed: corpus test now fails on blank/whitespace lines |

Reviewer-confirmed solid: byte fidelity of all 4 fixtures vs pin;
.gitattributes eol=lf keeps fresh-clone comparisons stable; csproj Content
include works on both TFMs with LinkBase=Golden\; `return` after Assert.Fail
required for definite assignment; markdownlint 0 issues on generated
PROVENANCE; here-string backtick doubling correct; no injection path.

## Post-fix red-green re-verification

- BOM tamper → verify exit 1 (previously 0 — the exact hole the review
  found, now closed and re-proven).
- `dotnet build -c Release -warnaserror`: 0 w / 0 e; `dotnet test -c
  Release`: 12/12 on net8.0 + net10.0.
- Sync → verify roundtrip green after restore.
- Found+fixed during re-verification: PowerShell array unrolling on
  `return` broke byte[] typing (comma-prefixed returns); expression
  arguments need parens when passed to functions.

## PR feedback round (Cursor Bugbot, commit 93187ff)

Fetched via `gh pr view` + `gh api .../pulls/10/comments`; each claim
verified against the code before acting (per the address-pr-feedback loop):

| Finding | Severity | Verified? | Disposition |
| --- | --- | --- | --- |
| "Array unroll breaks single-file corpus" — name lists returned bare, call sites un-`@()`-wrapped; 1-element return unrolls to `String`, `.Count` throws under StrictMode | Low | Yes — repro'd: unroll real, `.Count` throw real; the "foreach walks characters" claim is false (PS7 iterates once) | Fixed: bare name-list returns + `@()` at every call site (repo convention, per powershell-tooling rule 1). First attempt combined comma-prefix + `@()` which NESTS the array (repro'd: `object[1]`, space-joined rendering, `[string]` binding failure) — caught by the stale-file E2E test before shipping |
| "Sync cannot drop removed fixtures" — `-Sync` overwrites but never deletes; a pin bump removing/renaming a fixture can never converge, and verify-mode guidance dead-ends | Medium | Yes — code reading + E2E (planted stale fixture threw "Fixture set drift (after sync)") | Fixed: `-Sync` now deletes stale managed-scope `*.jsonl` before the postcondition set assert; `PROVENANCE.md` regenerated last |

Sweep for the class across `scripts/`, `scripts/tests/`, `.githooks/`:
- Scalar-unroll class: only this script had un-`@()`-wrapped function-result
  collections (`.githooks/pre-commit.ps1` already follows rule 1 both
  sides; `generate-skills-index.ps1` uses `List[object]` + typed locals).
- Convergence class: this is the repo's only sync/mirror script.
- Bugbot's byte-array comma returns were already correct and untouched.

Verification: parse OK; E2E — planted `zzz-removed-upstream.jsonl` deleted
by `-Sync` (exit 0, previously exit 1), verify green, corpus byte-identical;
`dotnet build -warnaserror` + `dotnet test` (12/12 both TFMs);
`scripts/tests/run-all.ps1` 6/6; linters green; skills index regenerated.

Knowledge capture (reflect-improve): `powershell-tooling` rule 1 extended
(both evidence instances + the comma+`@()` nesting trap); new rule 6
(sync/mirror scripts must converge — deletions included, converge-then-
assert-then-derive ordering); improvement-log entry added. Root cause of
the leak: new tooling wasn't rules-checked against `powershell-tooling` at
write time — now stated in the skill's intro. Self-test for the
network-bound sync script deferred (harness is local-only; acceptable while
the script is manual-run, revisit if it joins CI).
