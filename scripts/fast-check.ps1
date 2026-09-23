<#
.SYNOPSIS
    Fast red-green loop for C# changes: one framework, no restore, no
    tooling projects.

.DESCRIPTION
    The full local validation (`dotnet build` + `dotnet test` over the
    solution) costs ~45-70 s per iteration across both test target
    frameworks. Typical agent iterations change only C# sources and need
    the unit suite once: this script builds just the test project (which
    builds the library) for one framework in Debug without restore, then
    runs the suite from the built binaries. Measured cost on a warm tree:
    ~10-15 s end to end; parallelized fixtures finish in ~7 s.

    This is the iteration aid, not the gate: run the full `dotnet test`,
    `scripts/lint-conventions.ps1`, and `dotnet tool run csharpier -- check .`
    before declaring done (hooks and CI run them anyway).

.PARAMETER Filter
    Optional NUnit test filter passed to `dotnet test --filter`.

.PARAMETER Framework
    Target framework to build and test. Defaults to net8.0.

.PARAMETER Configuration
    Build configuration. Defaults to Debug (iteration speed; CI gates
    Release).

.EXAMPLE
    pwsh -NoProfile -File scripts/fast-check.ps1

.EXAMPLE
    pwsh -NoProfile -File scripts/fast-check.ps1 -Filter "FullyQualifiedName~GameData"
#>
[CmdletBinding()]
param(
    [string]$Filter,
    [string]$Framework = 'net8.0',
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repoRoot 'tests/SignalFish.Client.Tests/SignalFish.Client.Tests.csproj'

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host "== Building $Framework ($Configuration, no restore) =="
$buildArgs = @(
    'build', $testProject,
    '--no-restore',
    '-c', $Configuration,
    '-f', $Framework,
    '--nologo', '-v', 'q'
)
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0)
{
    Write-Host 'Build without restore failed; restoring once and retrying...'
    & dotnet restore $testProject
    if ($LASTEXITCODE -ne 0)
    {
        throw 'dotnet restore failed.'
    }

    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0)
    {
        throw 'dotnet build failed.'
    }
}

$testArgs = @(
    'test', $testProject,
    '--no-build',
    '-c', $Configuration,
    '-f', $Framework,
    '--nologo', '-v', 'q'
)
if ($Filter)
{
    $testArgs += @('--filter', $Filter)
}

Write-Host "== Testing $Framework =="
& dotnet @testArgs
if ($LASTEXITCODE -ne 0)
{
    throw 'dotnet test failed.'
}

$stopwatch.Stop()
$elapsed = $stopwatch.Elapsed.TotalSeconds.ToString(
    'F1',
    [System.Globalization.CultureInfo]::InvariantCulture
)
Write-Host "== fast-check passed in ${elapsed}s =="
