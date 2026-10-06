# Session 050 — MinVer version stamp (M9.1)

Date: 2026-10-06
Branch: `minver-version-stamp-m9.1`

## What shipped

**M9.1 — the tag-to-assembly version loop**, closing the last core
piece of the M9.2 packaging story:

- MinVer 8.0.0 stamps the git-tag version into the client library:
  package version, file version, and `InformationalVersion`
  (`AssemblyVersion` stays at the stable major line, the recommended
  practice for a library).
  Wired in `Directory.Build.targets` conditioned on
  `PackageId == SignalFish.Client` with `PrivateAssets="all"` —
  build-time only, so the zero-dependency lock holds (no
  `src/*.csproj` carries a PackageReference;
  `lint-zero-dependencies` untouched and green). Tag prefix `v` is
  pinned explicitly (MinVer 8 with an unset prefix only matches bare
  semver tags and silently ignored `v*` tags — verified with the
  minver CLI before pinning). `MinVerMinimumMajorMinor 0.1` keeps
  untagged checkouts (PR CI, local branches, shallow clones)
  building deterministically at a `0.1.0-alpha.0` pre-release
  (height-suffixed when history depth exists) instead of
  failing.
- `SignalFishClientInfo.SdkVersion` is now a static property reading
  the stamped `InformationalVersion` at type init (strips the `+sha`
  source revision the SDK appends in CI builds), falling back to the
  `0.1.0` floor when the assembly carries no stamp or Unity's
  default `0.0.0` — Unity compiles the mirrored sources with no
  MinVer, and that path is unchanged. The `const` is gone; the
  default-parameter use in `SignalFishClientOptions` now passes
  `null` and resolves at construction (the `?? ` fallback was
  already there). Consumers no longer bake a stale version into
  their call sites at compile time — that inlining was the exact
  drift M9.1 exists to kill.
- `release.yml`: checkout fetches full history (MinVer cannot see
  the pushed tag through the default shallow clone) and the pack
  step pins `-p:MinVerVersionOverride` (the documented override — a
  CLI `-p:Version` is overwritten by MinVer). Verified end-to-end
  with local data: tag `v0.2.0-rc.1` → assembly
  `0.2.0-rc.1+c72a79b…` → `SdkVersion` `0.2.0-rc.1`; release-lane
  pack with the override → `SignalFish.Client.0.2.0.nupkg` whose DLL
  carries `0.2.0+…`; untagged → `0.1.0-alpha.0.51+c72a79b…`.
- Tests: `SignalFishClientInfoTests` pins `SdkVersion` against the
  assembly stamp sans metadata and the semver-core shape; the two
  reconnect frames that asserted a literal `"0.1.0"` now source
  `sdk_version` from the same property the encoder uses (the frame
  assertions stay byte-exact but no longer couple to the build's
  stamped value). Unity mirror re-synced.

## Why this shape

Reflection over generated-source stamping: one small, AOT-safe
method with a null/unstamped guard beats MSBuild file generation
(timing coupling with MinVer, incremental-build edges, a generated
file Unity must not see). The Unity fallback is the honest behavior
for a source distribution — the version pipeline is a dotnet-build
artifact.

## Validation

- Full unit suite 933/933 on net8.0 and net10.0 (Release);
  E2E/PerfTests/FuzzTests build clean with `-warnaserror`.
- `lint-conventions` (all 6), CSharpier check, Unity mirror `-Check`
  lane green; `docs/releasing.md` runbook and CHANGELOG updated.

## Open issues triage

- #88 (FsCheck.NUnit pins NUnit < 5): re-checked 2026-10-06 —
  FsCheck.NUnit is still 3.4.0 on nuget.org; the NUnit 5 bump stays
  blocked on upstream (dependabot already ignores NUnit ≥ 5).
- #80 (mkdocs sibling markdown extensions when first used): no new
  markdown extensions introduced; no-action by its own terms.

## Follow-ups

- M9.3: API-compat gate in `dotnet.yml` — needs the first real tag
  (`v0.1.0`) cut so the released baseline exists; the gate then
  fails any PR that breaks the public surface.
- M9.4 (scheduled bench) and M9.5 (release checklist with adapter
  package version coupling) after the gate.
- First release drill: push `v0.1.0`, confirm the Release workflow
  ships a package whose `sdk_version` echoes the tag (the runbook
  now documents the MinVer loop).
