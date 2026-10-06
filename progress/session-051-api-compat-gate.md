# Session 051 — API-compat gate (M9.3) + the v0.1.0 baseline

Date: 2026-10-06
Branch: `m93-api-compat-gate`

## What shipped

**M9.3 — the API-compat gate**, plus the first release tag it needs:

- **`v0.1.0` cut** (prep: PR #103 finalized the changelog section per
  the runbook). `release.yml` packed and published the package end-to-end:
  GitHub Packages, GitHub Release with `.nupkg`/`.snupkg` assets,
  nuget.org skipped (no `NUGET_API_KEY`). The released package is now
  the durable API baseline consumers can hold the project to.
- **`scripts/check-api-compat.ps1`** (the gate): packs the working
  tree's library and compares it with Microsoft.DotNet.ApiCompat.Tool
  (pinned 10.0.401 in `.config/dotnet-tools.json`) against the latest
  GitHub Release's `SignalFish.Client.*.nupkg`, downloaded with
  `gh release download` (workflow token in CI; gh auth locally). The
  baseline is the same bits consumers download — no feed auth, no
  committed surface snapshot to go stale.
- **Red-green verified locally before wiring CI**, each behavior on a
  scratch working-tree edit against the stand-in-then-real baseline:
  - identical surface → green;
  - removed public const → red with `CP0002` naming the member;
  - added public const → green (additive stays free, per the
    additive-wire philosophy).
  Also verified the two failure paths (pack failure; missing release)
  produce named remedies, and that a breaking change that compiles
  packs and still trips the gate — the compile-clean red case is the
  one the gate exists for.
- **`dotnet.yml`**: the gate runs on the coverage cell after the other
  lints. A missing release fails the step (never skips) so the gate
  cannot silently rot; `-Suppression` is the documented escape hatch
  for audited, intentional breaks.
- **Parameter renames + attribute mismatches are checked** via
  `--enable-rule-cannot-change-parameter-name` and
  `--enable-rule-attributes-must-match` — both are source-breaking
  for C# consumers while binary-compatible, so the default rule set
  alone would miss them.
- Docs: `docs/releasing.md` gains the gate section (what fails, the
  audited-suppression flow for intentional breaks, stale-entry
  hygiene); `.llm/references/project-decisions.md` CI-shape row
  updated.

## Deliberate scope cuts

- **No suppression file committed**: none needed today; the
  `.config/apicompat-suppressions.xml` convention (picked up
  automatically when present) and the `-Suppression` param exist so
  the first intentional break is an audit diff, not new plumbing.
- **No script self-test**: the gate needs a GitHub release and a
  packed tool; CI is the test. `run-e2e.ps1`/`fast-check.ps1` set the
  same precedent.
- **No strict mode**: additions must stay free in 0.x; strict
  (equality) mode is a 1.0+ policy decision, not a tooling one.

## Verification

- Adversarial review round (sub-agent, evidence-based): confirmed the
  shallow-checkout pack path, the CP0017 rename catch, the
  suppression flow end-to-end, and StrictMode/quoting safety. Four
  findings, all fixed: the committed-suppression path now works in CI
  without a workflow edit (convention path default), the attribute
  claim scoped to the tool's default exclusions (`[Obsolete]` passes
  by design), the session record's tool-restore claim corrected to
  the coverage cell, and the zero-asset error message made explicit.
- `pwsh scripts/check-api-compat.ps1` green against the live `v0.1.0`
  release assets (the same command CI runs).
- Full local suite green (`fast-check.ps1`); the gate touches no
  library code, but the tool-manifest edit rides `dotnet tool restore`
  on the coverage cell (the only cell that restores pinned tools).
- PR CI: all cells green including the new gate step; the e2e, docs,
  and fuzz lanes unaffected.
