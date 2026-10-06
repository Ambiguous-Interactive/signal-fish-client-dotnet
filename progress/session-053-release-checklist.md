# Session 053 — Release checklist + UPM version coupling (M9.5)

Date: 2026-10-06
Branch: `m95-release-checklist`
PR: #107

## What shipped

**M9.5 — the release checklist, completed by making the UPM fleet's
version coupling machine-checked**, closing the last open release &
ops surface in PLAN.md:

- **`scripts/lint-unity-package-versions.ps1`** — fails any
  `unity/**/package.json` that (a) drifts from the fleet version all
  other manifests carry, (b) pins a sibling fleet package at anything
  but that package's current exact version (range syntax and typo'd
  names included), or (c) carries a non-semver version. External
  engine-SDK dependencies are out of scope (adapters detect those
  assets; they never resolve through UPM). Wired into the `dotnet.yml`
  lint cell next to `lint-unity-adapter`.
- **Tag-side re-check in `release.yml`** — before packing, every
  manifest must carry the tag's version exactly: an rc tag requires
  the fleet at the same rc version, and only build metadata (`+...`)
  is ignored. The PR-side lint keeps the manifests consistent with
  each other; only the release lane can catch tagging a `main` whose
  manifests lag the tag, which is the one hole a PR-time gate cannot
  close. The step counts the manifests it checked (a tree rename that
  empties the glob fails the run instead of passing vacuously) and
  skips `Samples~` (sample trees are not fleet members).
- **`docs/releasing.md`** — "Cutting a release" (3 steps) is now a real
  checklist (before the tag / tag / after the run) whose step 3 is the
  coupling step with both gates named; the "what happens on a tag push"
  pipeline list includes the coupling check; the "Future: UPM + NPM"
  section and `sync-unity-package.ps1 -Pack`'s comment now state that
  UPM tarballs do **not** ship in the tag pipeline (they never did —
  the old comment claimed it was "wired up in M9.2").
- **`scripts/tests/test-lint-unity-package-versions.ps1`** — 26
  fixture-driven assertions: lockstep pass/fail, stale/range/missing/
  self/non-string pins, semver shape, malformed shapes (`dependencies:
  null`, array-valued version and dependencies — violations, never
  crashes or silent passes), prerelease lockstep, empty-fleet remedy,
  and a run against the real repository fleet (9 packages, lockstep at
  0.1.0).

## Why this shape

The .NET version needs no file edit (MinVer reads the tag), so the
coupling lives entirely in hand-maintained manifests — a rule without a
gate rots on the first release that skips the doc. Two gates cover the
two directions of drift: PR-time (manifests vs each other) and
tag-time (manifests vs the tag). Doc-only was rejected for the same
reason; the release lane alone was rejected because PRs should fail
before merge, not after tagging.

## Red-green evidence

- Self-tests written first: 8 failed against the missing/stubbed lint;
  14/14 after. The real-fleet assertion runs the lint against the
  checkout (default `-RepoRoot`).
- The `release.yml` guard logic was exercised locally with `jq` +
  `find` against the real fleet: green at release `0.1.0`; a doctored
  `v0.2.0-rc.1` fails naming all nine manifests.
- First lint draft had two real bugs caught by its own self-tests:
  the empty-fleet guard used `Write-Error` (rendered as a
  console-wrapped record no assertion could match), and the guard ran
  after a `Get-ChildItem` that threw on a missing `unity/` root.
  Both fixed; the Write-Error lesson folded into powershell-tooling
  rule 3 (`.llm/improvement-log.md`, session 053 entry).
- **Adversarial review round** (evidence-first sub-agent, reproduced
  every finding): 1 blocker + 2 should-fix + 9 nits + 1 optional.
  All fixed except the two noted below. The blocker: the `release.yml`
  step read `find` through process substitution, whose exit code does
  not propagate under `bash -eo pipefail` — a tree drift that emptied
  the glob passed the gate vacuously (the exact failure mode the gate
  exists for). The should-fix set: `dependencies: null` crashed the
  lint with a wrapped terminating error; array-valued `version` and
  `dependencies` false-passed (`[string]` coercion flattens
  single-element arrays; `.PSObject.Properties` on an array enumerates
  `Length`/`Rank`); and the two gates contradicted each other on
  prerelease tags (the lane stripped the rc suffix while the lint
  accepted rc fleets — the lane now compares the full version and
  strips only `+build`, so the fleet ships exactly what the tag
  ships).
- **Second verification round** (live-probe sub-agent): every claimed
  fix reproduced (six shell scenarios, three hand-probes beyond the
  suite); verdict approve. It caught one new defect the fix had
  introduced — a stale `$relative` reporting the wrong manifest in the
  dependencies-shape message — fixed, and the suite now asserts the
  file path in every shape-case message (the pattern-only assertions
  had let it through).
- **Windows CI caught what Linux could not**: the lint's diagnostic
  paths came from `Get-ChildItem` verbatim, so on Windows the messages
  carried backslash separators and the path-prefixed self-test
  assertions failed (devcontainer is Linux; only the windows-latest
  cell exercises this). The lint now normalizes diagnostics to forward
  slashes — the sync script's mirror-plan convention — and the suite
  is green in both Lint cells. Corollary of session 052's "platform
  gates shape bootstrap paths": the platform also shapes which
  assertions can fail.

## Findings

- **The release lane never shipped UPM tarballs.** `-Pack`'s comment
  claimed "the release lane attaches it to GitHub Releases (wired up
  in M9.2)" — M9.2 predates the Unity package (M7.1); the claim was
  aspirational and stale. Fixed the comment + docs to the truth and
  opened #106 to actually ship them (extend staging to the eight
  adapters, attach in `release.yml`, fold docs into the checklist).
- **The coupling gap was invisible to every existing gate**:
  `lint-unity-adapter` checks adapter contracts, `sync-unity-package
  -Check` checks the source mirror, but nothing compared manifest
  versions. All nine packages happened to agree at `0.1.0` — the lint
  turns that coincidence into an invariant.
- `pwsh -File` + `Write-Error`: terminating errors render wrapped and
  defeat message-text assertions even outside loops (rule 3 now says
  the `Write-Host` + `exit 1` form applies everywhere).

## Deliberate scope cuts

- **No UPM tarball shipping** in this round — it needs adapter staging
  (`-Pack` is core-only today) and release-lane steps; #106 carries the
  full design. The checklist documents the honest state.
- **No fleet version ↔ tag version check in the PR lint** — a PR lint
  has no tag to compare against; MinVer's `0.1.0-alpha.N` fallback
  would false-fail. The tag-time guard is the right place.
- **No adapter `dependencies` loosening** (e.g. ranges instead of
  exact pins) — exact pins are the shipped contract; loosening them is
  a consumer-facing decision, not a tooling cleanup.
- **`Write-Error` sweep limited to the touched script**: sync-unity-package.ps1
  converted (six sites, self-test asserts exit codes + the
  `Write-Host` remedy lines). `fuzz-codec.ps1` and `install-hooks.ps1`
  keep their terminating errors for now — the fuzz lane needs the
  libfuzzer driver to exercise its paths locally, so converting them
  unverified would trade a cosmetic inconsistency for an untested
  failure path. Follow-up when either lane is next touched.

## Verification

- Self-test 26/26; `scripts/tests/run-all.ps1` 17/17 files.
- The `release.yml` step exercised locally under the exact CI shell
  (`bash -eo pipefail`), five scenarios: green plain tag (9 counted);
  red rc mismatch naming all nine; missing `unity/` tree fails (no
  vacuous pass); consistent rc fleet green; `Samples~` demo manifest
  ignored.
- The lint re-verified against the real fleet after every fix.
- markdownlint (65 files), typos (whole repo, v1.50.3 = CI pin),
  `mkdocs build --strict` green locally.
- `lint-llm-instructions` green after the skill edit + index
  regeneration.
- Issue #88 (NUnit 5) re-checked: FsCheck.NUnit 3.4.0 is still latest
  on nuget.org (verified this session); remains blocked upstream.
  Issue #80 trigger (first mkdocs extension use) did not occur.
- Main CI green on `3ca2ed2` before branching (dotnet, e2e, docs; LLM
  Context finished green).
