# Releasing — operator runbook

How a `SignalFish.Client` release ships, what you must set up, and what to
check afterwards. The pipeline is **fully automatic once you push a tag**;
nuget.org and npm publishing are the only opt-ins (one secret each).

## What happens when you push a `v*` tag

The `release.yml` workflow (`.github/workflows/release.yml` in the repo
root; tag pushes only — it never runs on branches or PRs):

1. Validates the tag is semver (`vMAJOR.MINOR.PATCH`, optional `-suffix`).
2. Checks the Unity fleet is version-coupled with the tag: every
   `unity/**/package.json` must carry the release version exactly — an
   rc tag requires the fleet at the same rc version; only build
   metadata (`+...`) is ignored. A lagging manifest fails the run
   before anything publishes.
3. Packs the library with the tag's version (reproducible CI build):
   `SignalFish.Client.<version>.nupkg` + `.snupkg` (symbols). MinVer
   stamps that version into the package and the assembly — and the
   client reports it to the server as `Authenticate.sdk_version`
   (`SignalFishClientInfo.SdkVersion`), so the tag, the package, and
   the wire identity always agree. Builds without a tag (local
   branches, PR CI) fall back to a `0.1.0-alpha.0` pre-release
   (suffixed `.<commit-height>` when the checkout has history depth)
   instead of failing.
4. Packs all nine Unity UPM packages into `.tgz` tarballs
   (`scripts/pack-unity-packages.ps1`): the core SDK package through
   the sync script's staging (fresh source mirror + shipped-asmdef
   check), every adapter verbatim from its source tree. A manifest
   without a name/version, a duplicated package name, or an empty
   fleet fails the run instead of shipping a partial set. Every
   tarball follows the npm pack layout (entries root at `package/`).
5. Checks every UPM tarball is npm-ready before anything publishes:
   the npm pack layout (entries root at `package/`, no `./` entries —
   required by Unity's tarball installer) and an auth-free
   `npm publish --dry-run`.
6. Publishes the `.nupkg` to **GitHub Packages** (always).
7. Publishes the `.nupkg` + `.snupkg` to **nuget.org** (only if
   `NUGET_API_KEY` is set — see below).
8. Publishes the nine UPM tarballs to the **npm registry** (only if
   `NPM_TOKEN` is set — see below). Versions npm already has are
   skipped.
9. Creates a **GitHub Release** with generated notes and attaches the
   `.nupkg`/`.snupkg` and the nine UPM `.tgz` tarballs — the no-auth
   fallback consumers can download directly (NuGet feed or Unity
   package, no registry or PAT needed).

## One-time setup

| Secret | Required | Purpose |
| --- | --- | --- |
| *(none)* | yes | `GITHUB_TOKEN` is provided by Actions automatically; it publishes to GitHub Packages and creates the Release. Nothing to configure. |
| `NUGET_API_KEY` | optional | Enables nuget.org publishing. Create a key at [nuget.org/account/apikeys](https://www.nuget.org/account/apikeys) (package owner account, push scope), then add it under **Settings → Secrets and variables → Actions** ([guide](https://docs.github.com/en/actions/security-guides/using-secrets-in-github-actions)). While absent, the nuget.org step skips itself; no failure, no other change. |
| `NPM_TOKEN` | optional | Enables npm registry publishing (issue #109). Create a [Granular access token](https://docs.npmjs.com/creating-and-viewing-access-tokens): **Packages and scopes → All packages** (specific packages can only be picked after the names exist on npm), **Permissions → Read and write**, and **Allow this token to bypass two-factor authentication** checked — CI cannot answer an OTP prompt. Add it as the `NPM_TOKEN` Actions secret. While absent, the npm step skips itself; no failure, no other change. npm deprecates bypass-2FA token publishing in January 2027 in favor of trusted publishing (OIDC) — revisit this lane then. |

## Cutting a release

Work the checklist top to bottom. The .NET version needs no file edit —
MinVer reads the tag — but the Unity manifests are hand-maintained, so
version coupling is a reviewed change that lands **before** the tag.

### Before the tag (one PR to `main`)

1. `main` is green (dotnet, e2e, docs lanes) and the latest weekly
   fuzz run is green; open dependency bumps are incorporated or
   explicitly deferred.
2. The `CHANGELOG.md` `Unreleased` section is final; rename it to the
   new version (keep-a-changelog). The GitHub Release notes are
   generated from commit history, so the changelog is the human-facing
   record — write it for the consumer, not the committer.
3. **Version-couple the UPM fleet** (`unity/**/package.json`): set all
   nine package manifests to the tag's version — for an rc tag, the rc
   version; the fleet ships exactly what the tag ships — and re-pin
   every internal dependency to it. The adapters depend on the core
   SDK package and on each other with exact pins — a core bump without
   the adapters leaves consumers unresolvable, and a lagging manifest
   ships a Unity source distribution that reports the old version
   while `SdkVersion` reports the new one. CI enforces both halves:
   `scripts/lint-unity-package-versions.ps1` keeps the manifests in
   lockstep on every PR, and the release lane re-checks each manifest
   against the tag before packing — tagging a `main` that skipped this
   step fails the run.
4. Merge the PR. `main` is now the release candidate.

### Tag

5. From the merged `main`:

   ```sh
   git tag vX.Y.Z
   git push origin vX.Y.Z
   ```

6. Watch the **Release** workflow run on the Actions tab.

### After the run

- The GitHub Release exists with `.nupkg` + `.snupkg` + the nine UPM
  `.tgz` tarballs attached.
- GitHub Packages shows the new version (consumers authenticate with a PAT
  per [GitHub's package auth rules](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry)).
- If `NUGET_API_KEY` was set, the package is live on nuget.org within a
  few minutes of the push step.
- If `NPM_TOKEN` was set, the nine UPM packages are live on npmjs.com
  within a few minutes; Unity users resolve them through the package
  manager UI (see "Discovery: npm + OpenUPM" below).
- The released package is now the **API-compat baseline**: every PR's
  packed library is compared against it in CI
  (`scripts/check-api-compat.ps1`), so a breaking change fails its own
  PR instead of a consumer's build.

## API-compat gate (M9.3)

`dotnet.yml` packs the PR's library and compares it with
[Microsoft.DotNet.ApiCompat](https://learn.microsoft.com/dotnet/fundamentals/apicompat/overview)
against the latest release's package — the same bits consumers
download. Removals and signature changes fail the PR; additions are
compatible and pass. Parameter renames and attribute mismatches are
checked too (both are source-breaking for C#), minus the attributes
the tool excludes by default: adding `[Obsolete]` is an additive
deprecation and passes.

Intentional breaks are audited, not silent: draft a suppression file
(run the tool's `--generate-suppression-file`), commit it as
`.config/apicompat-suppressions.xml` (the gate picks it up
automatically; `-Suppression <file>` tests other paths locally), and
treat its diff as the review record. The version bump follows
semver (minor in 0.x, major from 1.0). Once the release containing the
break ships, remove the stale entries — the new baseline covers them.

## Troubleshooting

- **Tag rejected, no run started** — the tag must match
  `vMAJOR.MINOR.PATCH` (optionally with `-preview.N` or `+build`).
  Retag correctly; a bad tag never triggers anything.
- **"409 Conflict" on push** — the version already exists on that feed.
  The workflow uses `--skip-duplicate`, so this is reported but not fatal;
  NuGet package versions are immutable — never reuse a tag for different
  bits. Fix the version and cut a new tag.
- **npm publish fails with 403** — the exact version already exists on
  npm (versions are immutable; the workflow skips those via a registry
  lookup first, so a 403 usually means something else): the token lacks
  publish permission for that package name (typical on the very first
  publish — check the token's packages and permissions; see
  "One-time setup").
- **npm publish fails with EOTP** — the account enforces 2FA and the
  token is not allowed to bypass it. Create the token with "bypass
  two-factor authentication" enabled (see "One-time setup").
- **snupkg missing from GitHub Packages** — by design: GitHub Packages has
  no symbol endpoint. Symbols ride the Release assets (and nuget.org when
  enabled).
- **Need to re-release** — delete the git tag and the GitHub Release,
  fix the problem, and push a fresh tag with a **new** patch version if
  anything already left the building (published packages cannot be
  replaced).

## Discovery: npm + OpenUPM

The Unity packages ship as release artifacts: every `v*` tag attaches
all nine UPM `.tgz` tarballs to the GitHub Release next to the NuGet
packages (see the pipeline above), and
`scripts/sync-unity-package.ps1 -Pack` stages the same core tarball by
hand. Tarballs alone are not **discovery** — this is how Unity users
find and resolve the packages:

- **npm registry** (issue #109): every tarball follows the npm pack
  layout, so the release lane can publish the fleet to npmjs.com
  directly. Opt-in: set the `NPM_TOKEN` secret (see "One-time setup");
  from the next tag on, each package publishes under its UPM id (for
  example `com.ambiguous-interactive.signalfish`) and Unity's package
  manager resolves it by name.
- **OpenUPM**: one-time, manual, per package — a submission adds one
  package name, and a multi-package repo like this one submits them
  one by one ([adding a UPM package](https://openupm.com/docs/adding-upm-package.html);
  signed in to GitHub). OpenUPM's build pipeline then serves the
  packages from this repo's tag tree — no token, no workflow change —
  and its rule that a tag's version must match each `package.json` is
  already enforced here by the version-coupling gates.
