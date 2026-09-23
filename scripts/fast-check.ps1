<#
.SYNOPSIS
    Fast red-green loop for C# changes: one framework, no restore, no
    tooling projects, no analyzer work.

.DESCRIPTION
    The full local validation (`dotnet build` + `dotnet test` over the
    solution) costs ~45-70 s per iteration across both test target
    frameworks. Typical agent iterations change only C# sources and need
    the unit suite once: this script builds just the test project (which
    builds the library) for one framework in Debug without restore and
    without Roslyn analyzers (the gate and CI still run them), then runs
    the suite via `dotnet vstest` on the built assembly (no msbuild in the
    test step).

    By default the `TransportLoopback` fixture (real-socket WebSocket
    round-trips, ~5 s of the suite) skips itself via a fast-lane marker
    variable; pass -IncludeLoopback to run it. Everything always runs in
    the full `dotnet test` gate.

    Measured cost on a warm tree: ~9-12 s end to end (~24 s before).

    This is the iteration aid, not the gate: run the full `dotnet test`,
    `scripts/lint-conventions.ps1`, and `dotnet tool run csharpier -- check .`
    before declaring done (hooks and CI run them anyway).

.PARAMETER Filter
    Optional test-case filter expression appended after the default
    category exclusion (vstest TestCaseFilter syntax, e.g.
    "FullyQualifiedName~GameData").

.PARAMETER Framework
    Target framework to build and test. Defaults to net8.0.

.PARAMETER Configuration
    Build configuration. Defaults to Debug (iteration speed; CI gates
    Release).

.PARAMETER IncludeLoopback
    Include the real-socket TransportLoopback category.

.EXAMPLE
    pwsh -NoProfile -File scripts/fast-check.ps1

.EXAMPLE
    pwsh -NoProfile -File scripts/fast-check.ps1 -Filter "FullyQualifiedName~GameData"
#>
[CmdletBinding()]
param(
    [string]$Filter,
    [string]$Framework = 'net8.0',
    [string]$Configuration = 'Debug',
    [switch]$IncludeLoopback
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repoRoot 'tests/SignalFish.Client.Tests/SignalFish.Client.Tests.csproj'

if (-not $IncludeLoopback)
{
    # The NUnit vstest adapter's TestCaseFilter negation is unreliable
    # (TestCategory!=X runs everything), so the fast lane signals the
    # loopback fixture to skip itself instead.
    $env:SIGNALFISH_FAST_LANE = '1'
}
else
{
    # A previous fast-check run in this shell may have left the marker set.
    Remove-Item Env:SIGNALFISH_FAST_LANE -ErrorAction SilentlyContinue
}

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host "== Building $Framework ($Configuration, no restore, no analyzers) =="
$buildArgs = @(
    'build', $testProject,
    '--no-restore',
    '-c', $Configuration,
    '-f', $Framework,
    '-p:RunAnalyzers=false',
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

$testDll = Join-Path `
    $repoRoot `
    "tests/SignalFish.Client.Tests/bin/$Configuration/$Framework/SignalFish.Client.Tests.dll"
if (-not (Test-Path $testDll))
{
    throw "Built test assembly not found: $testDll"
}

$testArgs = @('vstest', $testDll, '--nologo')
if ($Filter)
{
    $filterArg = "--TestCaseFilter:$Filter"
    $testArgs += $filterArg
}

Write-Host "== Testing $Framework =="
& dotnet @testArgs
if ($LASTEXITCODE -ne 0)
{
    throw 'dotnet vstest failed.'
}

$stopwatch.Stop()
$elapsed = $stopwatch.Elapsed.TotalSeconds.ToString(
    'F1',
    [System.Globalization.CultureInfo]::InvariantCulture
)
Write-Host "== fast-check passed in ${elapsed}s =="
