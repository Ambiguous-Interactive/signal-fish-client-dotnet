<#
.SYNOPSIS
    Packs every Unity UPM package in the fleet into release tarballs.

.DESCRIPTION
    Issue #106: a v* release ships all nine UPM packages (the core SDK
    package plus the engine adapters) as .tgz tarballs attached to the
    GitHub Release next to the NuGet packages, so Unity projects can
    install any package without a UPM registry. The core package is
    packed by scripts/sync-unity-package.ps1 -Pack (fresh source mirror
    plus the shipped-asmdef check); every adapter is a hand-authored
    source package, staged verbatim into the npm pack layout.

    Fails (exit 1) instead of packing a partial fleet: an empty fleet
    (tree drift), a missing core package, a manifest without a
    name/version string (or with a shape unsafe for a filename),
    invalid JSON, two packages sharing a name (the second tarball
    would silently overwrite the first), or any tar failure. Existing
    same-name tarballs are replaced; any other file in -OutDir is left
    alone (a re-pack after a version bump leaves the old tarballs —
    pack into a fresh directory when that matters).

    Every tarball follows the npm pack layout: entries root at a
    'package/' directory (no './' entries) — the shape npm pack emits
    and Unity's tarball installer requires (issue #109).

.PARAMETER OutDir
    Directory the .tgz files are written to. Created when missing.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of the scripts directory.

.EXAMPLE
    pwsh -NoProfile -File scripts/pack-unity-packages.ps1 -OutDir dist
#>
[CmdletBinding()]
param(
    [string]$OutDir = 'dist',
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}
# Resolve even an explicitly relative root: Get-ChildItem returns
# absolute paths, so every relative-path computation below depends on
# $RepoRoot being absolute — a relative root would silently misroute
# the core package to the verbatim adapter branch.
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path.TrimEnd('/', '\')

function Stop-Pack {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Red
    exit 1
}

$unityRoot = Join-Path $RepoRoot 'unity'
if (-not (Test-Path -LiteralPath $unityRoot)) {
    Stop-Pack 'No package manifests found under unity/ - nothing to pack; check the glob.'
}

# Samples~ is Unity's import-hidden sample tree; a demo manifest there
# is not a fleet member. Same discovery as lint-unity-package-versions.
$manifests = [string[]]@(Get-ChildItem -LiteralPath $unityRoot -Recurse -File -Filter 'package.json' |
    Where-Object { $_.FullName -notmatch '[\\/]Samples~[\\/]' } |
    ForEach-Object { $_.FullName })
[System.Array]::Sort($manifests, [System.StringComparer]::Ordinal)

if ($manifests.Count -eq 0) {
    Stop-Pack 'No package manifests found under unity/ - nothing to pack; check the glob.'
}

# Forward slashes: messages must match identically on every OS (the
# self-tests assert on these paths; Windows Get-ChildItem returns
# backslash separators).
function Get-ManifestRelative {
    param([string]$Path)
    return $Path.Substring($RepoRoot.Length + 1) -replace '\\', '/'
}

# First pass: validate every manifest, so a malformed fleet fails before
# any artifact is written.
$semver = '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$'
# UPM reverse-domain ids; the guard exists so name/version can never
# carry a path separator into the tarball filename.
$nameShape = '^[A-Za-z0-9][A-Za-z0-9._-]*$'
$fleet = New-Object 'System.Collections.Generic.List[hashtable]'
$names = @{}
foreach ($path in $manifests) {
    $relative = Get-ManifestRelative -Path $path
    try {
        $json = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    }
    catch {
        Stop-Pack "$relative : package.json is not valid JSON."
    }
    if ($null -eq $json -or $json -isnot [System.Management.Automation.PSCustomObject]) {
        Stop-Pack "$relative : package.json must contain a JSON object."
    }
    $nameProperty = $json.PSObject.Properties['name']
    $versionProperty = $json.PSObject.Properties['version']
    if ($null -eq $nameProperty -or $nameProperty.Value -isnot [string] -or $nameProperty.Value -eq '') {
        Stop-Pack "$relative : package.json must carry a name string."
    }
    if ($null -eq $versionProperty -or $versionProperty.Value -isnot [string] -or $versionProperty.Value -eq '') {
        Stop-Pack "$relative : package.json must carry a version string."
    }
    $name = $nameProperty.Value
    $version = $versionProperty.Value
    # The version shape mirrors lint-unity-package-versions.ps1 (semver)
    # so the tarball filename can never carry a separator.
    if ($name -notmatch $nameShape) {
        Stop-Pack "$relative : package name '$name' is not a UPM id (letters, digits, '.', '_', '-')."
    }
    if ($version -notmatch $semver) {
        Stop-Pack "$relative : version '$version' is not semver (MAJOR.MINOR.PATCH, optional -prerelease/+build)."
    }
    if ($names.ContainsKey($name)) {
        Stop-Pack "$relative : duplicate package name $name (also in $($names[$name])) - the second tarball would overwrite the first."
    }
    $names[$name] = $relative
    $fleet.Add(@{ Relative = $relative; Path = $path; Name = $name; Version = $version })
}

if (-not (Test-Path -LiteralPath $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}
elseif (-not (Get-Item -LiteralPath $OutDir).PSIsContainer) {
    Stop-Pack "pack: -OutDir '$OutDir' exists and is not a directory."
}
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path

# The core package must be part of the fleet: without it the release
# would ship adapters that cannot resolve their SDK dependency.
$coreRelative = 'unity/Packages/com.ambiguous-interactive.signalfish/package.json'
if (-not ($fleet | Where-Object { $_.Relative -eq $coreRelative })) {
    Stop-Pack "pack: no core package manifest at $coreRelative - tree drift; the release would ship an unresolvable adapter set."
}

# Second pass: pack. The core package ships a mirrored library source;
# sync-unity-package -Pack stages a fresh mirror and checks the shipped
# asmdef graph, so the release tarball is exactly what a local -Pack
# stages. Every adapter is verbatim.
$syncScript = Join-Path $PSScriptRoot 'sync-unity-package.ps1'
foreach ($package in $fleet) {
    if ($package.Relative -eq $coreRelative) {
        # The sync script derives the tarball name from the core
        # manifest itself; a drifted core id would otherwise ship a
        # misnamed tarball silently. Pre-delete the expected name so
        # the existence check below can only pass on a fresh stage.
        $expected = Join-Path $OutDir "$($package.Name)-$($package.Version).tgz"
        if (Test-Path -LiteralPath $expected) {
            Remove-Item -LiteralPath $expected -Force
        }
        & pwsh -NoProfile -File $syncScript -Pack -OutDir $OutDir -RepoRoot $RepoRoot
        if ($LASTEXITCODE -ne 0) {
            Stop-Pack "pack: core package staging failed (sync-unity-package.ps1 -Pack exited $LASTEXITCODE)."
        }
        if (-not (Test-Path -LiteralPath $expected)) {
            Stop-Pack "pack: the staged core tarball is $((Get-ChildItem -LiteralPath $OutDir -File -Filter '*.tgz' | Sort-Object LastWriteTime -Descending | Select-Object -First 1).Name), expected $($package.Name)-$($package.Version).tgz - check the core package name."
        }
        continue
    }

    $tarball = Join-Path $OutDir "$($package.Name)-$($package.Version).tgz"
    if (Test-Path -LiteralPath $tarball) {
        Remove-Item -LiteralPath $tarball -Force
    }
    # npm pack layout: entries root at package/ (required by Unity's
    # tarball installer; see .DESCRIPTION for the full contract).
    $stage = Join-Path ([System.IO.Path]::GetTempPath()) ("upmstage-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    try {
        Copy-Item -LiteralPath (Split-Path -Parent $package.Path) -Destination (Join-Path $stage 'package') -Recurse -Force
        tar -czf $tarball -C $stage package
        if ($LASTEXITCODE -ne 0) {
            Stop-Pack "pack: tar failed for $($package.Relative) with exit code $LASTEXITCODE."
        }
        if (-not (Test-Path -LiteralPath $tarball) -or (Get-Item -LiteralPath $tarball).Length -eq 0) {
            Stop-Pack "pack: tar produced no artifact for $($package.Relative)."
        }
        Write-Host "pack: $tarball"
    }
    finally {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "pack: packed $($fleet.Count) UPM tarballs into $OutDir"
