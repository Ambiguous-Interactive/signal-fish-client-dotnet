# Releasing — operator runbook

How a `SignalFish.Client` release ships, what you must set up, and what to
check afterwards. The pipeline is **fully automatic once you push a tag**;
nuget.org publishing is the only opt-in (one secret).

## What happens when you push a `v*` tag

The [`release.yml`](../.github/workflows/release.yml) workflow (tag pushes
only — it never runs on branches or PRs):

1. Validates the tag is semver (`vMAJOR.MINOR.PATCH`, optional `-suffix`).
2. Packs the library with the tag's version (reproducible CI build):
   `SignalFish.Client.<version>.nupkg` + `.snupkg` (symbols).
3. Publishes the `.nupkg` to **GitHub Packages** (always).
4. Publishes the `.nupkg` + `.snupkg` to **nuget.org** (only if
   `NUGET_API_KEY` is set — see below).
5. Creates a **GitHub Release** with generated notes and attaches the
   `.nupkg`/`.snupkg` — the no-auth fallback consumers can download
   directly.

## One-time setup

| Secret | Required | Purpose |
| --- | --- | --- |
| *(none)* | yes | `GITHUB_TOKEN` is provided by Actions automatically; it publishes to GitHub Packages and creates the Release. Nothing to configure. |
| `NUGET_API_KEY` | optional | Enables nuget.org publishing. Create a key at [nuget.org/account/apikeys](https://www.nuget.org/account/apikeys) (package owner account, push scope), then add it under **Settings → Secrets and variables → Actions** ([guide](https://docs.github.com/en/actions/security-guides/using-secrets-in-github-actions)). While absent, the nuget.org step skips itself; no failure, no other change. |

## Cutting a release

1. Ensure `main` is green and the `CHANGELOG.md` `Unreleased` section is
   final; rename it to the new version.
2. Tag and push:

   ```sh
   git tag vX.Y.Z
   git push origin vX.Y.Z
   ```

3. Watch the **Release** workflow run on the Actions tab.

## After the run

- The GitHub Release exists with `.nupkg` + `.snupkg` attached.
- GitHub Packages shows the new version (consumers authenticate with a PAT
  per [GitHub's package auth rules](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry)).
- If `NUGET_API_KEY` was set, the package is live on nuget.org within a
  few minutes of the push step.

## Troubleshooting

- **Tag rejected, no run started** — the tag must match
  `vMAJOR.MINOR.PATCH` (optionally with `-preview.N` or `.build`). Retag
  correctly; a bad tag never triggers anything.
- **"409 Conflict" on push** — the version already exists on that feed.
  The workflow uses `--skip-duplicate`, so this is reported but not fatal;
  NuGet package versions are immutable — never reuse a tag for different
  bits. Fix the version and cut a new tag.
- **snupkg missing from GitHub Packages** — by design: GitHub Packages has
  no symbol endpoint. Symbols ride the Release assets (and nuget.org when
  enabled).
- **Need to re-release** — delete the git tag and the GitHub Release,
  fix the problem, and push a fresh tag with a **new** patch version if
  anything already left the building (published packages cannot be
  replaced).

## Future: UPM + NPM distribution

Unity (UPM/OpenUPM) and NPM publishing need the Unity package from PLAN
M7.1; they fold into this pipeline at M9.2 (see `PLAN.md`). Nothing to
configure today.
