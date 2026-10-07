# Session 058 — #109: the UPM fleet ships to npm (discovery)

Date: 2026-10-07
Branch: `m109-npm-discovery`
PR: (this session)

## Drift check

- origin/main merged clean at `1e83b00` (#113); working tree clean; no
  stash; no open or draft PRs from earlier sessions.
- Main CI green on #113: dotnet, Docs, e2e, LLM Context all success.
- Dependabot weekly ran 2026-10-07, both groups green, nothing open.
- Open issues at start: #109 (this session's surface — discovery lane
  unpicked), #80 (dormant: enable on the first page that needs one).

## The surface: #109 — list the UPM packages on OpenUPM/NPM

#106 ships all nine UPM tarballs as Release assets; #109 asks for
**discovery** — Unity users resolving the packages from the package
manager UI. The issue left the lane open: OpenUPM's upstream resolver
and/or an npm publish step in `release.yml`.

## The bug found on the way (red-green)

Probing the npm lane surfaced a layout defect: every shipped tarball
rooted its entries at `./` (`tar -C <pkg-dir> .`), not at the
`package/` directory `npm pack` emits. npm 10 tolerated the dot-root
by luck; the naive `./pkg/` hybrid shape fails `npm publish` outright
(verified locally: ENOENT on the manifest), and npm-pack layout is the
contract npm publishing and UPM's tarball installer are built around.
The tarballs also predate any Unity tarball-install validation (M7.4
is blocked on the Unity seat), so nothing had ever pinned the shape.

Fix (both packers): stage the package contents under `package/` and
`tar -C $stage package` — portable (no GNU-only `--transform`, the
scripts must keep working in Windows PowerShell). Self-tests now pin
the layout: entries root at `package/package.json`, no `./` entries
(test-pack-unity-packages 43→57 assertions, test-sync-unity-package
+1). All nine real tarballs verified with `npm publish --dry-run` +
manifest read-back.

## What shipped

- `scripts/sync-unity-package.ps1`, `scripts/pack-unity-packages.ps1`:
  npm pack layout for every tarball.
- `release.yml`: an always-on `npm publish --dry-run` gate (fails the
  release on a non-publishable tarball, no auth needed) and an opt-in
  publish step — `NPM_TOKEN` secret, same shape as `NUGET_API_KEY`;
  skips versions npm already has (immutable, like NuGet).
- `docs/releasing.md`: "Future: OpenUPM + NPM listing" replaced by the
  picked lanes — npm (set `NPM_TOKEN`; from the next tag on the fleet
  publishes under its UPM ids) and OpenUPM (one-time manual submission
  at openupm.com; the resolver rides the tag tree, no repo change).
  New `NPM_TOKEN` row in the setup table, npm 403 troubleshooting
  bullet, pipeline steps renumbered.
- `CHANGELOG.md`: the layout change (Changed) and the npm channel
  (Added).

## Lanes picked, deliberately

- npm is the primary lane: fully automatable in-repo, opt-in by one
  secret, exercised (dry-run) on every release even while absent.
- OpenUPM stays a one-time manual submission — their flow needs a
  browser login; nothing in-repo can or should automate it.
- `docs/unity.md` does not advertise npm resolution yet: the channel
  is live only after `NPM_TOKEN` + a tag. Flip it in the same PR as
  the first npm-published release.

## Adversarial review round (sub-agent)

The reviewer verified the suites and probed npm behavior with
regression-shape tarballs; no blockers, four accepted findings:

- **Gate gap (should-fix)**: `npm publish --dry-run` *passes* the old
  `./`-rooted layout on npm 10 — the gate as first written only caught
  publish-fatal shapes, not the layout regression this PR migrates
  from. Fixed: the release gate now greps each tarball for
  `package/package.json` at the root and no `./` entries before the
  dry-run (verified red on `dot-root.tgz` and the `./pkg/` hybrid,
  green on all nine real tarballs).
- **npm token guidance (should-fix)**: Granular tokens select
  packages from existing names only — there is no glob input, so the
  original instruction was a wall for a fresh publisher. Fixed: point
  at a classic Automation token (publishes from CI, bypasses 2FA),
  scope later once the names exist.
- **npm 403 causes (should-fix)**: the troubleshooting entry named
  only the publish race; permission and 2FA causes added.
- **Wording accuracy**: npm tolerates several shapes; **Unity's
  installer** is the party that requires `package/` — comments,
  docs, and CHANGELOG now say exactly that. The "registry read is
  anonymous" claim dropped (the lookup runs with the token present).
- Link Check (CI) caught the two new links: the npmjs.com package
  page 403s bots (link dropped), and `openupm.com/repos/add/` is not
  a URL — replaced with the real docs page
  (openupm.com/docs/adding-upm-package.html), whose monorepo section
  also corrected the runbook: OpenUPM submissions are one package per
  entry, submitted one by one.

## Verification

- `scripts/tests/run-all.ps1`: all 18 self-test files pass (red first:
  14 + 7 failures before the layout fix).
- Real-fleet pack + `npm publish --dry-run` on all nine: OK.
- `release.yml` YAML parse OK; `lint-file-sizes` / `lint-llm-instructions`
  green. No C# changes, so no dotnet build/test delta.

## Leftover

- Operator: create the npm account/org owning `com.ambiguous-interactive.*`,
  mint a Granular read-write token, set `NPM_TOKEN`, cut a tag. Then
  update `docs/unity.md`'s Install section with npm resolution.
- OpenUPM submission is a maintainer click once npm names are owned.
