<#
.SYNOPSIS
    Enforces release version coupling across the Unity UPM package fleet.

.DESCRIPTION
    PLAN.md M9.5: the nine UPM packages (the core SDK package plus the
    adapter packages) release as one unit from this repository, so their
    versions must move in lockstep with the tagged `SignalFish.Client`
    release. A tag that bumps the .NET package while the UPM manifests
    lag behind ships a silent mismatch: the Unity source distribution
    would report the old version while `SdkVersion` reports the new one,
    and exact dependency pins would stop resolving. This linter fails
    when any `unity/**/package.json`:

      - carries a version that is not semver (UPM rejects anything else),
      - drifts from the fleet version every other manifest carries,
      - pins a sibling fleet package at anything but that package's
        current exact version (range syntax or a typo'd name included).

    External (engine SDK) dependencies are out of scope: adapters detect
    those assets at editor time and never resolve them through UPM.
    docs/releasing.md carries the checklist step that pairs with this
    gate: set every manifest to the tag version before pushing it.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of the scripts directory.

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-unity-package-versions.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}
$RepoRoot = $RepoRoot.TrimEnd('/', '\')

$unityRoot = Join-Path $RepoRoot 'unity'
if (-not (Test-Path -LiteralPath $unityRoot)) {
    Write-Host 'No package manifests found under unity/ - nothing to enforce; check the glob.' -ForegroundColor Red
    exit 1
}
# Samples~ is Unity's import-hidden sample tree; a demo manifest there is
# not a fleet member.
$manifests = [string[]]@(Get-ChildItem -LiteralPath $unityRoot -Recurse -File -Filter 'package.json' |
    Where-Object { $_.FullName -notmatch '[\\/]Samples~[\\/]' } |
    ForEach-Object { $_.FullName })
[System.Array]::Sort($manifests, [System.StringComparer]::Ordinal)

if ($manifests.Count -eq 0) {
    Write-Host 'No package manifests found under unity/ - nothing to enforce; check the glob.' -ForegroundColor Red
    exit 1
}

$semver = '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$'
$problems = New-Object 'System.Collections.Generic.List[string]'

# First pass: read name/version and validate the shapes.
$fleet = @{}
foreach ($path in $manifests) {
    $json = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $nameProperty = $json.PSObject.Properties['name']
    $versionProperty = $json.PSObject.Properties['version']
    $relative = $path.Substring($RepoRoot.Length + 1)
    if ($null -eq $nameProperty -or $nameProperty.Value -isnot [string] -or $nameProperty.Value -eq '') {
        $problems.Add("$relative : package.json must carry a name string.")
        continue
    }
    if ($null -eq $versionProperty -or $versionProperty.Value -isnot [string] -or $versionProperty.Value -eq '') {
        $problems.Add("$relative : package.json must carry a version string.")
        continue
    }
    $name = $nameProperty.Value
    $version = $versionProperty.Value
    if ($fleet.ContainsKey($name)) {
        $problems.Add("$relative : duplicate package name $name (also in $($fleet[$name].relative)).")
        continue
    }
    if ($version -notmatch $semver) {
        $problems.Add("$relative : version '$version' is not semver (MAJOR.MINOR.PATCH, optional -prerelease/+build).")
        continue
    }
    $fleet[$name] = @{ version = $version; relative = $relative }
}

# Second pass: lockstep versions across the fleet.
$versions = @($fleet.Values | ForEach-Object { $_.version } | Sort-Object -Unique)
if ($versions.Count -gt 1) {
    foreach ($name in ($fleet.Keys | Sort-Object)) {
        $entry = $fleet[$name]
        if ($entry.version -ne $versions[0]) {
            $problems.Add("$($entry.relative) : version $($entry.version) drifts from the fleet version $($versions[0]) - release coupling requires one version across unity/.")
        }
    }
}

# Third pass: internal dependency pins must be exact and current.
foreach ($path in $manifests) {
    $json = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $nameProperty = $json.PSObject.Properties['name']
    if ($null -eq $nameProperty) { continue }
    $name = [string]$nameProperty.Value
    if (-not $fleet.ContainsKey($name)) { continue }
    $dependenciesProperty = $json.PSObject.Properties['dependencies']
    if ($null -eq $dependenciesProperty) { continue }
    $relative = $fleet[$name].relative
    if ($null -eq $dependenciesProperty.Value -or
        $dependenciesProperty.Value -isnot [System.Management.Automation.PSCustomObject]) {
        $problems.Add("$relative : dependencies must be an object of package -> version pins (got null/array/scalar).")
        continue
    }
    foreach ($property in @($dependenciesProperty.Value.PSObject.Properties)) {
        $dependency = [string]$property.Name
        if (-not $dependency.StartsWith('com.ambiguous-interactive.signalfish')) { continue }
        if ($dependency -eq $name) {
            $problems.Add("$relative : depends on itself - a package can never resolve its own dependency.")
            continue
        }
        if ($null -eq $property.Value -or $property.Value -isnot [string]) {
            $problems.Add("$relative : pins $dependency with a non-string value - pins must be version strings.")
            continue
        }
        $pin = $property.Value
        if (-not $fleet.ContainsKey($dependency)) {
            $problems.Add("$relative : pins '$dependency', which is not a fleet package in this repository - a renamed or typo'd member would never resolve.")
            continue
        }
        $current = $fleet[$dependency].version
        if ($pin -ne $current) {
            $problems.Add("$relative : pins $dependency $pin, but the fleet version is $current - internal pins must be exact and current.")
        }
    }
}

if ($problems.Count -gt 0) {
    Write-Host 'Unity package version coupling FAILED:' -ForegroundColor Red
    foreach ($problem in $problems) {
        Write-Host "    $problem" -ForegroundColor Red
    }
    Write-Host 'Release coupling: set every unity/**/package.json to the release version and re-pin internal dependencies (docs/releasing.md, release checklist).' -ForegroundColor Red
    exit 1
}

Write-Host "package-versions: $($fleet.Count) UPM packages in lockstep at $($versions[0]); internal pins exact."
exit 0
