<#
.SYNOPSIS
    Fails when the packed library breaks the public surface of the latest
    released package (M9.3 API-compat gate).

.DESCRIPTION
    Packs src/SignalFish.Client and compares it with Microsoft.DotNet.
    ApiCompat.Tool against the latest GitHub Release's SignalFish.Client
    .nupkg. Breaking changes (removed or reshaped public API) fail;
    additions are compatible and pass. The released package is the
    baseline, so the gate stays honest without a feed: the same bits a
    consumer can download are the bits the PR is compared against.

    Parameter renames and attribute mismatches are checked too - both
    are source-breaking for C# consumers even though binary-compatible.
    Attributes the tool excludes by default (ObsoleteAttribute,
    AttributeUsageAttribute) are exempt: adding [Obsolete] is an
    additive deprecation and passes.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of the scripts directory.

.PARAMETER Baseline
    Path to a baseline SignalFish.Client .nupkg. Defaults to the latest
    GitHub Release asset (downloaded with gh; CI authenticates with the
    workflow token, locally gh must be logged in).

.PARAMETER Dist
    Directory the candidate package is packed into. Defaults to
    <RepoRoot>/artifacts/api-compat. Created fresh on each run.

.PARAMETER Suppression
    Path to an ApiCompat suppression file. Intentional breaking changes
    are listed there (run the tool with --generate-suppression-file to
    draft one) and reviewed in the PR that breaks the surface. Rare by
    design: 0.x ships breaks via a minor bump, 1.0+ via a major bump.
    Defaults to .config/apicompat-suppressions.xml when that file
    exists, so a committed suppression rides CI without a workflow
    edit.

.EXAMPLE
    pwsh -NoProfile -File scripts/check-api-compat.ps1

.EXAMPLE
    pwsh -NoProfile -File scripts/check-api-compat.ps1 -Baseline ~/Downloads/SignalFish.Client.0.1.0.nupkg
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$Baseline,
    [string]$Dist,
    [string]$Suppression
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

$projectPath = Join-Path $RepoRoot 'src/SignalFish.Client/SignalFish.Client.csproj'

if (-not $Dist) {
    $Dist = Join-Path $RepoRoot 'artifacts/api-compat'
}

if (-not $Suppression) {
    $conventionalSuppression = Join-Path $RepoRoot '.config/apicompat-suppressions.xml'
    if (Test-Path $conventionalSuppression) {
        $Suppression = $conventionalSuppression
    }
}

function Get-ReleaseNupkgPath {
    param([string]$WorkDir)

    $downloadDir = Join-Path $WorkDir 'baseline'
    New-Item -ItemType Directory -Force -Path $downloadDir | Out-Null
    gh release download --repo Ambiguous-Interactive/signal-fish-client-dotnet --pattern 'SignalFish.Client.*.nupkg' --dir $downloadDir --clobber
    if ($LASTEXITCODE -ne 0) {
        throw "gh release download failed (exit $LASTEXITCODE). The API-compat gate needs the latest release as its baseline; cut the first v* tag per docs/releasing.md or pass -Baseline."
    }

    $nupkg = @(Get-ChildItem -Path $downloadDir -Filter 'SignalFish.Client.*.nupkg')
    if ($nupkg.Count -ne 1) {
        $found = if ($nupkg.Count -eq 0) { 'none - the latest release has no matching asset' } else { $nupkg.Name -join ', ' }
        throw "Expected exactly one SignalFish.Client .nupkg release asset, found: $found"
    }
    return $nupkg[0].FullName
}

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Force -Path $workDir | Out-Null
try {
    if (-not $Baseline) {
        $Baseline = Get-ReleaseNupkgPath -WorkDir $workDir
    }
    if (-not (Test-Path $Baseline)) {
        throw "Baseline package not found: $Baseline"
    }
    Write-Host "Baseline package: $Baseline"

    if (Test-Path $Dist) {
        Remove-Item -Recurse -Force $Dist
    }
    New-Item -ItemType Directory -Force -Path $Dist | Out-Null
    dotnet pack $projectPath -c Release -o $Dist
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack failed (exit $LASTEXITCODE)"
    }

    $candidate = @(Get-ChildItem -Path $Dist -Filter 'SignalFish.Client.*.nupkg')
    if ($candidate.Count -ne 1) {
        throw "Expected exactly one packed SignalFish.Client .nupkg, found $($candidate.Count): $($candidate.Name -join ', ')"
    }

    $apicompatArgs = @(
        'tool', 'run', 'apicompat', '--', 'package', $candidate[0].FullName,
        '--baseline-package', $Baseline,
        '--enable-rule-cannot-change-parameter-name',
        '--enable-rule-attributes-must-match'
    )
    if ($Suppression) {
        if (-not (Test-Path $Suppression)) {
            throw "Suppression file not found: $Suppression"
        }
        $apicompatArgs += @('--suppression-file', (Resolve-Path $Suppression).Path)
    }

    dotnet @apicompatArgs
    if ($LASTEXITCODE -ne 0) {
        throw "API compatibility check failed (exit $LASTEXITCODE): the candidate package breaks the released surface above."
    }

    Write-Host "API-compatible with $Baseline."
}
finally {
    Remove-Item -Recurse -Force $workDir -ErrorAction SilentlyContinue
}
