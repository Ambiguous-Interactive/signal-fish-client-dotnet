# Session 056 — NUnit on Microsoft.Testing.Platform (#111)

Date: 2026-10-07
Branch: `m111-nunit-mtp-runner`
PR: (this session)

## What shipped

**#111's no-rewrite middle path**: the whole test estate moves onto
Microsoft.Testing.Platform (MTP) while staying NUnit. The suite is
untouched — same 933 tests, same attributes, same filters. Only the
runner generation changes, which is the direction both #111 outcomes
share: if the owner later green-lights TUnit, the MTP plumbing (global
.json mode, TRX/coverage extensions, direct-exec fast lane) is already
in place; if declined, the repo still lands on Microsoft's stated
runner direction without a 2,552-assert review.

- **`global.json`** — `"test": { "runner": "Microsoft.Testing.Platform" }`
  selects the .NET 10 SDK's native MTP mode of `dotnet test` (not the
  bridge mode flagged for removal). The e2e lane's SDK bumps 8.0.x →
  10.0.x; it still builds/runs the net8.0 E2E project.
- **Tests + E2E projects** — `<OutputType>Exe</OutputType>` +
  `<EnableNUnitRunner>true</EnableNUnitRunner>` (NUnit3TestAdapter
  6.3.0, which rides MTP 2.3.3 and handles the kept
  Microsoft.NET.Test.Sdk coexistence — VSTest IDE runners keep working).
  `Microsoft.Testing.Extensions.TrxReport` pinned to **2.3.3 exactly**
  (the adapter's platform version; "pin exactly, ride bumps
  deliberately").
- **Coverage** — `coverlet.collector` → **`coverlet.MTP` 10.1.0**: the
  same engine and version line the repo already used, native to MTP,
  and open-source (avoids the closed-source-licensed
  `Microsoft.Testing.Extensions.CodeCoverage`). Still emits cobertura;
  the ReportGenerator step only widens its glob
  (`coverage.cobertura*.xml` — MTP adds a timestamp segment).
- **`fast-check.ps1`** — drops the `dotnet vstest` console entirely and
  executes the built MTP test application directly (`dotnet exec` +
  `--no-banner`). `-Filter` keeps the vstest TestCaseFilter syntax:
  the adapter's MTP mode registers a `--filter` option that translates
  it, so every documented filter keeps working.
- **`run-e2e.ps1`** — `--logger trx` → `-- --report-trx` (MTP shape);
  `-DotNetTestArgs` now documents that args go after `--`.
- **Docs** — `docs/attributions.md` names the new dev-time packages;
  `.llm/context.md` and the script header carry the re-measured warm
  lane timings. The `dotnet test --filter` teaching in
  `create-test`/`testing.md` needed no change: the native `dotnet test`
  parser forwards unrecognized options to the test application.

## Red-green evidence

- **Red**: with only `global.json` in place, `dotnet test` fails with
  "global.json defines test runner to be Microsoft.Testing.Platform.
  All projects must use that test runner" — proof the mode is real
  before any project flip.
- **First build trap**: adding PackageReferences while reusing a stale
  restore let `dotnet build --no-restore` "succeed", then the run died
  with MTP exit 5 (unregistered option = extension DLLs missing). An
  explicit `dotnet restore` fixed it; the run then failed on
  `--coverlet-output`, which coverlet.MTP does not register (its help
  output enumerated the real option set).
- **Green**: 933/933 on net8.0 (23.4 s run) and net10.0 (22.8 s),
  TRX + cobertura produced in `TestResults/`. E2E discovery listed its
  fixtures under MTP (live run is the e2e lane's job in CI). Fast lane
  re-measured warm: ~22 s full / ~8 s with `-Filter`.
- **CI catch, swept**: the first PR run failed only the e2e lane —
  `run-e2e.ps1`'s bare `--nologo` is forwarded by the native
  `dotnet test` to the test app, which (at the pinned MTP 2.3.3)
  registers only `--no-banner` and rejects the alias with exit 5.
  Local bisect isolated it (no-args → 2 = real failures; `--nologo` →
  5; `--report-trx` → 2). Fixed by moving banner suppression after
  `--` as `--no-banner`; swept the repo — every other `--nologo` sits
  on `dotnet build`, which owns the flag. `--report-trx` verified
  producing the TRX artifact locally.
- Convention lints (all six), CSharpier, `lint-llm-instructions`,
  `lint-file-sizes`, tooling builds, all 18 script self-tests — green
  locally.
