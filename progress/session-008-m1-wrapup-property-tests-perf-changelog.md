# Session 008 — M1 wrap-up: property tests, perf baseline, changelog

Date: 2026-09-20. Scope: issue-debt round closing out the open-issue queue
(#15, #9, #7) and advancing PLAN M1 (M1.4 done, M1.6 done, M1.5 open).

## What shipped

- **#15 — CHANGELOG.md + STE user-copy rule**
  - `CHANGELOG.md` (keep-a-changelog, semver): `Unreleased` section lists
    only user-visible additions — envelope decoding and encoding. CI/test/
    tooling churn deliberately excluded (sibling contract: godot repo
    issue #29).
  - User-copy rule added to `.llm/context.md` (rule 16): user-facing text is
    short, simple, STE-style, why-then-how; CHANGELOG stays user-only.

- **#9 — upstream golden fixtures: verified still blocked upstream**
  - `scripts/sync-protocol-fixtures.ps1` verify mode: all 4 files
    byte-identical to `signal-fish-server@eaae1ca3`; the server's default
    branch tip IS the pin, so `GameStarting` / `RoomLeft` / `*Failed` wire
    samples still do not exist upstream. Issue stays open; nothing to sync.
    Status noted on the issue.

- **#7 / M1.6 — perf gate: BenchmarkDotNet + baselines**
  - `tests/SignalFish.Client.PerfTests` (net8.0, explicit `dotnet run`,
    never `dotnet test`): `DecodeFullCorpus` (all 47 golden frames per op,
    baseline) and `EncodeRepresentativeSession` (9-message real session per
    op into a reused, pre-sized buffer).
  - Baselines recorded in `docs/benchmarks.md`: decode 27.5 us/corpus,
    encode 1.6 us/session, **both 0 B allocated steady-state** — matching
    the existing allocation-gate unit tests. Bench lane stays off PR CI;
    scheduled regression runs are M9.4.

- **M1.4 — FsCheck property suite** (`CodecPropertyTests`, 6 properties,
  both TFMs, ~0.25 s):
  - Escape-aware string write→scan→materialize roundtrip, strengthened to
    require the scanner to consume writer output byte-exactly.
  - Value-scan stability over arbitrary JSON trees (AST generator:
    null/bool/number/string/array/object with escape-heavy char pool).
  - Decode totality: arbitrary junk bytes and mutation-spliced corpus
    frames never throw; failures carry a bounded, in-range error code.
  - Known-type routing under arbitrary legal payloads (data slice
    round-trips byte-identically) + forward-compatible unknown-type events.
  - Red-green evidence: planted reader throw → totality property failed;
    planted writer quote-escape drop → the *initial* string property passed
    (its slicing re-cut the framing, hiding the bug) → property
    strengthened to assert exact scanner consumption → now fails red.
    Both fixes kept as permanent test strength.

- **CI time: net decrease, coverage unchanged**
  - `dotnet.yml`: `concurrency` cancel-in-progress (stale runs die);
    NuGet package cache (restore dominates per-cell wall clock); coverage
    collection + ReportGenerator narrowed from both Linux cells to one
    representative cell (library is netstandard2.1 regardless of runner
    TFM — identical data, half the instrumentation); ReportGenerator
    moved from per-run global install to the repo tool manifest.
  - Every cell still runs the full suite on its TFM; measured coverage is
    unchanged.

## Housekeeping

- `.llm/improvement-log.md` condensed back under the 300-line ceiling
  (graduated devcontainer entries compressed to durable-lesson stubs);
  session 008 findings recorded there (lone surrogates in generators,
  ref-struct by-value hazard resurfacing in test helpers, quote-slicing
  property blind spot, `Encoding.ASCII` mangling).
- PLAN.md: M1.4/M1.6 marked done with notes; M1 gate updated (M1.5 open).

## Verification

- `dotnet build -c Release -warnaserror` — 0 warnings, 0 errors.
- `dotnet test -c Release` — 235 passed × {net8.0, net10.0}.
- `scripts/tests/run-all.ps1` — 7/7 self-test files.
- `csharpier -- check .`, `lint-no-linq`, `lint-zero-dependencies`,
  `lint-file-sizes`, `lint-llm-instructions` — clean.
- `sync-protocol-fixtures.ps1` verify — byte-identical at pin.

## Leftovers / next surfaces

- **M1.5 (SharpFuzz lane)** is the only open M1 item; fuzz targets can lean
  on the property-suite generators (junk-byte totality is already a
  property).
- #7 stays open pending the `unity-helpers` buffer-techniques review when
  the polling client (M3) lands; benchmarks + allocation gates (this
  round) and the LINQ ban (session 007) are done.
- #9 stays open upstream; re-run `sync-protocol-fixtures.ps1 -Sync` when
  the server publishes the missing v2 samples.
- M2 (transport) is the next milestone surface.
