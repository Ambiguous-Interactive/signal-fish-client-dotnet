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
    source package, so its directory is the tarball, verbatim.

    Fails (exit 1) instead of packing a partial fleet: an empty fleet
    (tree drift), a manifest without a name/version string, invalid
    JSON, two packages sharing a name (the second tarball would
    silently overwrite the first), or any tar failure.

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
$RepoRoot = $RepoRoot.TrimEnd('/', '\')

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
    $nameProperty = $json.PSObject.Properties['name']
    $versionProperty = $json.PSObject.Properties['version']
    if ($null -eq $nameProperty -or $nameProperty.Value -isnot [string] -or $nameProperty.Value -eq '') {
        Stop-Pack "$relative : package.json must carry a name string."
    }
    if ($null -eq $versionProperty -or $versionProperty.Value -isnot [string] -or $versionProperty.Value -eq '') {
        Stop-Pack "$relative : package.json must carry a version string."
    }
    $name = $nameProperty.Value
    if ($names.ContainsKey($name)) {
        Stop-Pack "$relative : duplicate package name $name (also in $($names[$name])) - the second tarball would overwrite the first."
    }
    $names[$name] = $relative
    $fleet.Add(@{ Relative = $relative; Path = $path; Name = $name; Version = $versionProperty.Value })
}

if (-not (Test-Path -LiteralPath $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path

# Second pass: pack. The core package ships a mirrored library source;
# sync-unity-package -Pack stages a fresh mirror and checks the shipped
# asmdef graph, so the release tarball is exactly what a local -Pack
# stages. Every adapter is verbatim.
$syncScript = Join-Path $PSScriptRoot 'sync-unity-package.ps1'
$coreRelative = 'unity/Packages/com.ambiguous-interactive.signalfish/package.json'
foreach ($package in $fleet) {
    if ($package.Relative -eq $coreRelative) {
        & pwsh -NoProfile -File $syncScript -Pack -OutDir $OutDir -RepoRoot $RepoRoot
        if ($LASTEXITCODE -ne 0) {
            Stop-Pack "pack: core package staging failed (sync-unity-package.ps1 -Pack exited $LASTEXITCODE)."
        }
        continue
    }

    $tarball = Join-Path $OutDir "$($package.Name)-$($package.Version).tgz"
    if (Test-Path -LiteralPath $tarball) {
        Remove-Item -LiteralPath $tarball -Force
    }
    tar -czf $tarball -C (Split-Path -Parent $package.Path) .
    if ($LASTEXITCODE -ne 0) {
        Stop-Pack "pack: tar failed for $($package.Relative) with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $tarball) -or (Get-Item -LiteralPath $tarball).Length -eq 0) {
        Stop-Pack "pack: tar produced no artifact for $($package.Relative)."
    }
    Write-Host "pack: $tarball"
}

Write-Host "pack: packed $($fleet.Count) UPM tarballs into $OutDir"
