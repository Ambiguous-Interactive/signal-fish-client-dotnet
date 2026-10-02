# Session 032 — Issue #68: fuzz the MessagePack binary frame decoder

Date: 2026-10-02. Scope: the single open issue (#68) — bring the v3
binary game-data decoder under the coverage-guided fuzz lane — plus the
routine drift check. M6.5 mesh stays the next focused surface.

## Starting state

- PR #69 (M6.4 binary game data) had landed as a squash merge; the
  local `binary-game-data-m6.4` branch and its remote were obsolete.
  New branch `msgpack-frame-fuzz` cut from origin/main (f1974b7).
- origin/main CI fully green; one open issue (#68, fuzz the MessagePack
  binary frame decoder); no open or draft PRs.

## Delivered

- **`msgpack-frame` fuzz target** (`tests/SignalFish.Client.FuzzTests/
  Program.cs`): drives `BinaryGameDataFrame.TryDecode` on every input
  under both `protocolV3` modes. Oracles: never throws; every failure
  carries a structured `DecodeError` at an offset in `[0, length]`; the
  strict member-count gate (five on v3, three on v2) lets at most one
  mode accept a frame; a success pins a consistent field set (a real
  encoding token — never the `default` sentinel — and, on v3, the
  non-zero server stamps). Allocation stays covered by the existing
  `BinaryRelayHotPathAllocatesNothing` unit gate, not the fuzzer.
- **Committed corpus** (`tests/SignalFish.Client.FuzzTests/Corpus/
  msgpack-frame/`, 25 `.bin` seeds): happy paths (canonical shape,
  map16/str8/bin16 width variants, key permutations, every seq integer
  width, the `json` token, empty payload, the v2 shape) plus every
  malformed class (unknown/duplicate/missing key, zero stamps, short
  UUID, string payload, non-map root, truncation, trailing bytes,
  unknown encoding, v3 key on v2, negative/wide declared lengths,
  beyond-uint32 epoch, empty input). Seeds carry their expectation in
  the name: `accept-v3-`/`accept-v2-` decode on exactly that mode,
  `reject-` on neither.
- **Corpus-contract test** (`BinaryGameDataTests.
  CommittedMsgPackCorpusDecodesPerNameContract`): walks the committed
  corpus and pins the name contract plus the same bounded-failure and
  one-mode-wins invariants — the seeds cannot rot silently. The corpus
  rides the test output via a `Content` include (like the golden
  fixtures).
- **Lane wiring**: `scripts/fuzz-codec.ps1` grew the `msgpack-frame`
  target (seeds copied into `.fuzz/corpus` next to the golden-derived
  reader corpus) and an `all` default replacing `both`;
  `-SecondsPerTarget` re-capped at 600 so three targets stay inside the
  workflow timeout. `fuzz.yml`: 3 × 10-minute targets (same 30-minute
  total budget), dispatch default and comments updated.
- **SSOT**: the create-test skill's fuzz section records the third
  target and names the corpus as the graduation home for binary-frame
  seeds; skills index regenerated (unchanged).

## Verification

- Corpus-contract test green on net8.0 + net10.0; full suite green
  (726 tests per TFM; E2E skipped locally as usual).
- All 25 seeds replay clean through the new target standalone
  (`SIGNALFISH_FUZZ_TARGET=msgpack-frame dotnet … <seed>`, no driver);
  reader/writer targets and the bad-target usage path re-verified.
- `fast-check`, all six convention lints, lint-llm-instructions,
  lint-file-sizes, and all 12 automation self-tests pass.
- The instrumented lane cannot run in this arm64 devcontainer (the
  pinned libfuzzer-dotnet release is x86-64); per the session-009/029
  pattern, a short `workflow_dispatch` fuzz run on the PR branch proves
  the lane green on CI x86-64.

## Notes

- The generator that emitted the seeds was one-time (its `Str8` helper
  initially dropped the length byte; the corpus-contract test caught it
  on the first run — red-green working as intended).
- `.fuzz/libfuzzer-dotnet` (x86-64, gitignored) downloaded during the
  local attempt stays cached; harmless on x86-64 hosts, and `-DriverPath`
  remains the arm64 escape hatch.

## Follow-ups

- M6.5 mesh is the next focused surface (PLAN updated).
- Scheduled fuzz lane picks up the new target on its next weekly run;
  the PR-branch dispatch covers it before merge.
