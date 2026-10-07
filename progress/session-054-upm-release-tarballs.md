# Session 054 — UPM tarballs in the release lane (#106)

Date: 2026-10-06
Branch: `m96-upm-release-tarballs`
PR: #(this session)

## What shipped

**#106 — the release lane now ships the UPM fleet**: every `v*` tag
attaches all nine `.tgz` tarballs (core SDK package + eight engine
adapters) to the GitHub Release next to the `.nupkg`/`.snupkg`, closing
the one gap between the M9 gate's "tag → packages + release
end-to-end" and reality:

- **`scripts/pack-unity-packages.ps1`** — packs the whole fleet into
  one `dist/`: the core through `sync-unity-package.ps1 -Pack`
  (delegated, so the release tarball is exactly what a local `-Pack`
  stages: fresh source mirror + shipped-asmdef graph check), every
  adapter verbatim from its source tree (they are hand-authored source
  packages — the directory is the tarball). Fails the run instead of
  shipping a partial fleet: empty fleet (tree drift — the M9.5
  no-vacuous-pass lesson), manifest without a name/version string,
  invalid JSON, two packages sharing a name (the second tarball would
  silently overwrite the first and one package would vanish from the
  release), or any tar failure. Diagnostics name the manifest with
  forward slashes (the M9.5 platform lesson).
- **`release.yml`** — a "Pack UPM tarballs" step after the dotnet pack,
  and the release-create step attaches `dist/*.tgz` beside the NuGet
  assets.
- **Docs** — `docs/releasing.md`: the pipeline list gains the UPM pack
  step (and the release step names all three artifact kinds); the
  "Future: UPM + NPM distribution" section is folded away — replaced
  by an honest "Future: OpenUPM + NPM listing" (artifacts ship today;
  registry discovery is what remains). `docs/getting-started.md` and
  `README.md` no longer say the tarball lane "lands with the release
  milestone" — it landed; `docs/unity.md`'s Install section gains the
  tarball route. `scripts/sync-unity-package.ps1`'s `-Pack` comment now
  states the release lane packs the same way (the comment claimed the
  opposite until session 053 found it).
- **`scripts/tests/test-pack-unity-packages.ps1`** — 51 fixture-driven
  assertions: core staging content (fresh mirror + asmdef + samples),
  adapter verbatim content (sample and sample-less), `Samples~` demo
  manifests never packed, the partial-fleet failures (missing version,
  invalid JSON, JSON `null`, duplicate name, traversal name,
  non-semver version, empty fleet, missing core, `-OutDir` as a file)
  with manifest-named messages, a drifted core package name, a
  relative `-RepoRoot` still staging the fresh mirror, a seeded stale
  tarball cannot satisfy the drifted-core check, the
  fleet-count summary, and a run against the real repository fleet
  (9 tarballs, versions read from the manifests — never hard-coded,
  so the routine version-coupling PR cannot break the suite).
- **`.gitignore`** gains `dist/` — both pack flows stage there now; a
  local pack run must not dirty the tree.
- **`CHANGELOG.md`** — Unreleased entry: releases ship every Unity
  package as a tarball (consumer-facing).

## Why this shape

One entry point packing all nine, not two steps with a core/adapter
split: the coupling gate (M9.5) already guarantees the fleet releases
as one unit, so the pack is one unit. Delegating the core to the sync
script reuses the only hardened staging logic (fresh mirror +
asmdef-graph pin) instead of copying it. Validation runs as a separate
first pass so a malformed fleet writes zero artifacts.

## Red-green evidence

- The test file was written first and failed (no script). First green
  run caught a **real blocker**: the core was being tarred **verbatim
  as an adapter** — the routing constant was a package directory
  compared against a manifest file path, never equal, so the sync
  staging (fresh mirror + asmdef check) never ran, while every
  success-shaped assertion passed. Only the fixture assertion on the
  artifact the staging branch uniquely produces (`Runtime/Core/A.cs`
  in the core tarball) failed. Debugging lesson recorded in
  `.llm/improvement-log.md` (session 054): instrument the branch
  entry, not the branch body — a plausible wrong-branch result reads
  as a subtle right-branch failure.
- Post-fix and post-review: 52/52. `scripts/tests/run-all.ps1` 18/18
  files.
- The workflow's coupling step was exercised locally under the exact
  CI shell (`bash -eo pipefail`) with the new segment-exact glob:
  green count at 9; a doctored `v0.2.0-rc.1` against the `0.1.0`
  fleet fails (detection intact); a `Samples~Extra` near-miss
  manifest joins the fleet (count 10, mismatch flagged).
- `pack-unity-packages.ps1` run against the real repo: 9 tarballs,
  then removed (and `dist/` ignored so it can never leak into a diff).
- markdownlint (changed files), typos v1.50.3 (whole repo, CI pin),
  `mkdocs build --strict` (full site; changed pages verified in the
  output), yaml-lint on `release.yml`, `lint-llm-instructions`,
  `sync -Check`, `lint-unity-package-versions` (9 in lockstep) — all
  green locally.

## Findings

- The stale `-Pack` comment session 053 corrected ("the tag pipeline
  does not ship UPM tarballs yet") is now doubly obsolete — this round
  makes the docs, the comment, and the pipeline agree.
- The `.gitignore` hole was pre-existing: `sync -Pack -OutDir dist`
  (the documented local flow since M7.1) already wrote an untracked
  `dist/` into the tree; nobody had packed locally since the ignore
  list was last touched.

## Adversarial review round (evidence-first sub-agent; every finding
reproduced before reporting)

Verdict: REQUEST CHANGES — 5 should-fix + 5 nits, all reproduced.
Fixed: (1) a **missing core package** shipped a silent partial fleet —
the packer now fails when no manifest sits at the core path (the
release-lane coupling gate alone could not catch it: a renamed core
just shrinks both gates' counts). (2) a **relative `-RepoRoot`**
silently routed the core down the verbatim adapter branch (Get-ChildItem
returns absolute paths, so every relative-path computation depends on
an absolute root) — the root is now resolved unconditionally. (3) a
**drifted core package name** shipped a tarball whose filename lied
(the sync script names the staged tarball after its own hard-coded id)
— the staged tarball is now checked against the manifest's name+version.
(4) the real-fleet assertions **hard-coded `0.1.0`**, so the repo's own
release-checklist version-coupling PR would have broken the suite —
versions are read from the manifests. (5) the **README** carried the
same stale "tarball lane lands with the release milestone" sentence the
PR fixed in getting-started.md (plus `docs/unity.md`'s Install section
now names the tarball route). Nits also fixed: name/version shape
guards so a hostile manifest can never carry a separator into the
tarball filename (path traversal out of `-OutDir`), a JSON `null`
manifest now fails with the shape message instead of a StrictMode
crash, `-OutDir`-is-a-file fails with the script's message instead of
tar's, and the `release.yml` coupling gate's `Samples~` exclusion is
segment-exact (`*/Samples~/*`), matching the packer — a `Samples~Extra`
near-miss directory is now a fleet member in both gates (hand-probed:
count 10, mismatch flagged). Declined with reason: dropping `shell:
pwsh` from the workflow step (every script cell in `dotnet.yml` uses
the same explicit form — consistency, and exit propagation is
verified); pruning stale tarballs from `-OutDir` (documented in the
script instead — CI runners are fresh and the nupkg flow has the same
semantics). A test-hygiene bug of my own surfaced while adding the
review assertions: an invalid manifest from an earlier failure case
leaked into the missing-core case and the wrong guard fired — fixture
state is now restored between failure modes.

## Deliberate scope cuts

- **No OpenUPM/NPM listing** — discovery is a separate surface
  (#106's notes say it can stay open); the docs section now says
  exactly that.
- **No reproducible-tarball flags** (sorted mtimes/uids) — the NuGet
  artifacts are not bit-reproducible either; UPM resolves content, not
  tarball bytes. Not a demonstrated problem.
- **No core-name refactor** — the sync script hard-codes the core
  tarball name; a first-pass duplicate-name check across all manifests
  (including the core) guards the only drift that matters (a second
  package claiming the core's name now fails the run).
- **`run-all.ps1` unchanged** — it auto-discovers `test-*.ps1`; the
  new suite rides the existing convention.

## Verification round (live-probe sub-agent, round 2)

Verdict: **VERIFIED** — all six fixes reproduced working (exit codes +
messages), both suites green, no real-fleet regression from the new
guards, rc prereleases (`0.1.0-rc.1`, `+build`) still accepted by the
semver guard, and the Samples~ near-miss parity proven in both gates.
Two findings, both addressed or accepted: the LOW one (the
expected-tarball check was a bare `Test-Path`, so a pre-seeded stale
artifact with the expected name could false-pass a drifted core) is
fixed by pre-deleting the expected name before staging, with a
regression assertion; the INFO one (a caught drift leaves sync's
mislabeled tarball on disk in the operator's OutDir) is accepted — the
run fails closed before anything attaches to a release, and CI's dist
is ephemeral.

## Verification

- `scripts/tests/run-all.ps1` 18/18 files (incl. the new 51).
- markdownlint (changed files), typos (v1.50.3 = CI pin), mkdocs
  `--strict`, yaml-lint, `lint-llm-instructions`, `lint-file-sizes`,
  `sync -Check`, version-coupling lint — green.
- Main CI green on `4dad8da` before branching (Docs + e2e finished
  green; dotnet + LLM Context in progress at session start — both
  green on the equivalent PR run #107).
