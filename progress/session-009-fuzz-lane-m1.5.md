# Session 009 — Codec fuzz lane (M1.5) + CI time trim

Date: 2026-09-20. Scope: complete PLAN M1 by landing the SharpFuzz lane
(M1.5, scheduled CI only so PR CI time stays flat) and reduce `dotnet.yml`
runner time with unchanged test coverage. Issue debt: both open issues
(#9, #7) checked; nothing actionable unblocked upstream.

## What shipped

- **M1.5 — `tests/SignalFish.Client.FuzzTests`** (net8.0, SharpFuzz 2.3.0,
  explicit driver run, never `dotnet test`):
  - `reader` target: envelope-decode totality — arbitrary bytes never
    throw; `DecodeFailed` events carry a real reason and an in-range byte
    offset; routing stays coherent (Message events name a real kind and
    never carry type text; UnknownMessage events always do).
  - `writer` target: encode accepts only documented misuse refusals
    (`ArgumentException`); every accepted encode emits valid UTF-8 that
    decodes back to the same message kind with the payload struct
    roundtripping byte-for-byte (Ping checked structurally).
  - The harness persists crashing inputs itself
    (`SIGNALFISH_FUZZ_CRASH_DIR`): the libfuzzer-dotnet parent exits on a
    dead child without writing artifacts. Failure messages include the
    frame text for one-look triage.
- **`scripts/fuzz-codec.ps1`** — publish → SharpFuzz-instrument → pinned
  libfuzzer-dotnet driver; reader corpus seeded from the golden fixtures
  (47 frames). Used by CI and locally (`-SecondsPerTarget` keeps local runs
  bounded). Requires Linux; arm64 devcontainers pass `-DriverPath` (the
  pinned release binary is x86-64). Corpus + crashes persist in `.fuzz/`
  (gitignored) so triage survives re-runs; the driver is pinned by release
  AND SHA256; sharpfuzz comes from the repo tool manifest.
- **`.github/workflows/fuzz.yml`** — weekly schedule + manual dispatch
  only. Two 15-minute targets = the 30-minute M1 gate. Crash artifacts
  upload on failure. PR CI untouched. Dispatch input passes through `env:`
  (never interpolated into the command line).
- **`dotnet.yml` trims (coverage unchanged):**
  - One SDK install per cell (each cell builds/tests only its matrix
    framework); explicit per-project restores (slnx restore needs a newer
    SDK than some cells install).
  - Non-coverage cells build/restore only the test project at their
    framework (no PerfTests, no second TFM); the coverage cell compiles
    PerfTests + FuzzTests as their PR-time build gate.
  - Zero-deps / LINQ-ban lints (OS-independent) run once on the coverage
    cell instead of four times; explicit test-project path; job timeout
    added for stuck-run safety; `Directory.Build.props`/`targets`/
    `.editorconfig` added to the paths filter (all-C# compile gate).

## Red-green evidence

- Reader RED: planted `throw` on empty input → target crashes (exit 134).
- Writer RED: planted dropped backslash on `"` in `WriteEscaped` →
  roundtrip assertion catches it (the same writer-bug class the M1.4
  FsCheck property was strengthened for).
- GREEN: 235 tests × 2 TFMs, 7 script self-tests, CSharpier, fixtures
  byte-identical to `signal-fish-server@eaae1ca3`, and both targets clean
  under the real driver (reader 883k / writer 841k execs at 150 s each;
  re-verified after the adversarial-review fixes at 15 s/target).

## Adversarial review round (12 findings, all addressed or noted)

- Fixed: corpus/crash persistence outside the wiped publish dir; explicit
  per-project restores (bare `dotnet restore` needs a slnx-capable SDK);
  dispatch input via `env:`; driver SHA256 pin with mismatch refusal;
  sharpfuzz moved into `.config/dotnet-tools.json` (kills version skew);
  `Directory.Build.props`/`targets`/`.editorconfig` added to the dotnet.yml
  paths filter; standalone no-arg usage guard (SharpFuzz would throw
  IndexOutOfRange); best-effort crash dump (cannot mask the original
  exception); improvement-log entry reshaped to Trigger/Evidence/
  Findings/Applied/Open; PS 5.1-safe `$IsLinux` guard; docstring crash-path
  wording; dead using removed.
- Not done (deliberate): a `test-fuzz-codec.ps1` glob-only self-test —
  low value vs. bloat; the real lane runs prove the behavior weekly.
- Second adversarial round (7 findings, SHIP verdict): fixed the non-trivial
  ones — per-TFM restore (`-p:TargetFramework=`) so net8-only cells do not
  depend on runner-image SDK drift; PS 5.1 fail-fast that actually fires;
  the downloaded driver is hash-verified on every run (explicit
  `-DriverPath` opts out for local arm64 builds); valid seed-payload
  refusals now crash the writer target (a writer regression can no longer
  masquerade as a documented misuse); fuzz.yml uploads crash artifacts on
  cancel too and the per-target budget is capped (1200 s) to stay inside
  the job timeout; session-009 log entry moved above session-008 (newest
  first). Skipped: orphan-seed pruning in the persistent corpus (harmless
  — libFuzzer treats them as plain inputs).

## Fuzz-lane finds (both harness-side, library verified correct)

1. `Authenticate` with no `data` member decodes to an empty `Data` slice;
   payload `TryDecode`'s contract covers data objects only, so the
   roundtrip assertion now skips the empty-slice case.
2. Padded payloads cannot roundtrip bytes: the writer emits the verbatim
   payload while the reader slices the bare value token (JSON whitespace is
   insignificant). The harness now synthesizes whitespace-trimmed payloads.

## CI-time accounting

- Fuzz lane: scheduled only — PR and main-push runner time unchanged.
- `dotnet.yml`: per-cell work strictly reduced (one TFM build, no duplicate
  lints, no PerfTests outside the coverage cell); matrix breadth (os × TFM)
  and the measured coverage data are identical.
- Measured on the PR: dotnet critical path 2m10s → 1m30s (windows cells);
  ubuntu cells 1m10s/55s → 58s/34s. (A "one SDK per cell" trim was attempted
  and reverted with RCA: restore scoping is graph-global, so the dual-TFM
  test project needs both SDKs everywhere — see improvement-log finding 6.)

## Left for later

- M2 transport (`ITransport` contract tests next) — unblocks M3, which is
  the last open item on #7 (unity-helpers buffer review).
- #9 remains blocked: fixtures byte-identical at the pin; upstream has not
  published `GameStarting` / `RoomLeft` / `*Failed` samples.
