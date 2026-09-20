<#
.SYNOPSIS
    Runs the codec fuzz lane (PLAN.md M1.5): envelope-reader totality and
    writer roundtrip targets, coverage-guided via SharpFuzz + libfuzzer-dotnet.

.DESCRIPTION
    Publishes tests/SignalFish.Client.FuzzTests, instruments the library
    assemblies with SharpFuzz, and drives each selected target with the
    libfuzzer-dotnet driver. The reader target is seeded with the golden
    wire fixtures (tests/Golden/*.jsonl). Any escaping exception or failed
    invariant crashes the driver, which fails the script and leaves the
    crashing input in .fuzz/crashes.

    The corpus and crash artifacts persist in .fuzz/ (gitignored) so local
    triage survives between runs; only the publish/instrumentation output is
    wiped. The libfuzzer-dotnet driver is pinned by release AND SHA256 (it
    is native code executed in CI). Instrumentation and fuzzing only run on
    Linux; the script fails fast on other platforms. Local runs stay bounded
    (-SecondsPerTarget); long runs belong to the scheduled fuzz workflow.

    Network: downloads the pinned libfuzzer-dotnet driver binary (first run
    only); SharpFuzz.CommandLine comes from the repo tool manifest
    (.config/dotnet-tools.json) via `dotnet tool restore`.

.EXAMPLE
    pwsh -NoProfile -File scripts/fuzz-codec.ps1 -SecondsPerTarget 30

.EXAMPLE
    pwsh -NoProfile -File scripts/fuzz-codec.ps1 -Target reader -SecondsPerTarget 900
#>
[CmdletBinding()]
param(
    # Which fuzz target to run: the envelope reader, the writer, or both.
    [ValidateSet('reader', 'writer', 'both')]
    [string]$Target = 'both',

    # Wall-clock fuzzing budget per target, in seconds. Capped so both
    # targets stay inside the fuzz workflow's job timeout.
    [ValidateRange(1, 1200)]
    [int]$SecondsPerTarget = 900,

    # Pre-provisioned libfuzzer-dotnet driver. When empty, the pinned
    # release binary is downloaded into .fuzz/.
    [string]$DriverPath = '',

    # Repository root. Defaults to the parent of the scripts directory.
    [string]$RepoRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ($PSVersionTable.PSVersion.Major -ge 6 -and $IsLinux))
{
    Write-Error 'The codec fuzz lane requires pwsh 7+ on Linux (libfuzzer-dotnet driver).'
}

if ([string]::IsNullOrEmpty($RepoRoot))
{
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

$driverRelease = 'v2025.05.02.0904'
$driverSha256 = 'C2C2A90D94C409A4AF339A0D4F244E0442C5A5D249BE0F1252BA07871F285958'
$driverUrl =
    "https://github.com/Metalnem/libfuzzer-dotnet/releases/download/$driverRelease/libfuzzer-dotnet-ubuntu"

$project = Join-Path $RepoRoot 'tests/SignalFish.Client.FuzzTests/SignalFish.Client.FuzzTests.csproj'
$publishDir = Join-Path $RepoRoot 'tests/SignalFish.Client.FuzzTests/bin/fuzz'
$projectDll = Join-Path $publishDir 'SignalFish.Client.FuzzTests.dll'
$persistenceDir = Join-Path $RepoRoot '.fuzz'
$artifactsDir = Join-Path $persistenceDir 'crashes'
$corpusRoot = Join-Path $persistenceDir 'corpus'

# ---- Publish ---------------------------------------------------------------

# Start clean: publish does not overwrite an already-instrumented dll.
if (Test-Path $publishDir)
{
    Remove-Item -Recurse -Force $publishDir
}

Write-Host "Publishing fuzz host to $publishDir"
dotnet publish $project -c Release -o $publishDir
if ($LASTEXITCODE -ne 0)
{
    Write-Error 'dotnet publish failed.'
}

# ---- Instrument ------------------------------------------------------------

# SharpFuzz.CommandLine is pinned by the repo tool manifest, same as
# csharpier and reportgenerator.
Push-Location $RepoRoot
try
{
    dotnet tool restore
    if ($LASTEXITCODE -ne 0)
    {
        Write-Error 'dotnet tool restore failed.'
    }
}
finally
{
    Pop-Location
}

# The host dll and the fuzzing engine itself must stay uninstrumented.
$excludedPatterns = @(
    'SignalFish.Client.FuzzTests.dll',
    'SharpFuzz*.dll',
    'dnlib.dll',
    'netstandard.dll',
    'System.*.dll',
    'Microsoft.*.dll'
)
$targets =
    Get-ChildItem $publishDir -Filter '*.dll' | Where-Object {
        $name = $_.Name
        -not ($excludedPatterns | Where-Object { $name -like $_ })
    }

if (-not $targets)
{
    Write-Error 'No fuzzing target assemblies found in the publish output.'
}

foreach ($assembly in $targets)
{
    Write-Host "Instrumenting $($assembly.Name)"
    Push-Location $RepoRoot
    try
    {
        # Tool commands resolve the manifest from the current directory, so
        # the tool must run from the repo root even when the script itself
        # was launched from elsewhere.
        dotnet tool run sharpfuzz -- $assembly.FullName
    }
    finally
    {
        Pop-Location
    }

    if ($LASTEXITCODE -ne 0)
    {
        Write-Error "Failed to instrument $($assembly.FullName)."
    }
}

# ---- Driver ----------------------------------------------------------------

if ([string]::IsNullOrEmpty($DriverPath))
{
    New-Item -ItemType Directory -Force -Path $persistenceDir | Out-Null
    $DriverPath = Join-Path $persistenceDir 'libfuzzer-dotnet'
    if (-not (Test-Path $DriverPath))
    {
        $download = "$DriverPath.download"
        Write-Host "Downloading libfuzzer-dotnet driver $driverRelease"
        Invoke-WebRequest -Uri $driverUrl -OutFile $download
        Move-Item -Force $download $DriverPath
    }

    # Native code executed in CI: refuse anything but the pinned bytes —
    # on every run, not just fresh downloads (also catches truncated or
    # partial downloads and a corrupted cache). An explicitly provided
    # -DriverPath (e.g. a locally built arm64 driver) opts out.
    $driverHash = (Get-FileHash -Algorithm SHA256 $DriverPath).Hash
    if ($driverHash -ne $driverSha256)
    {
        Write-Error "Driver SHA256 mismatch: got $driverHash, expected $driverSha256."
    }
}

chmod +x $DriverPath

New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

# Seed the reader corpus with the golden wire fixtures; the writer corpus
# cold-starts (its inputs are driver-derived message recipes, not JSON).
foreach ($name in @('reader', 'writer'))
{
    New-Item -ItemType Directory -Force -Path (Join-Path $corpusRoot $name) | Out-Null
}

$index = 0
Get-ChildItem (Join-Path $RepoRoot 'tests/Golden') -Filter '*.jsonl' | ForEach-Object {
    Get-Content $_.FullName
} | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
    $index++
    [System.IO.File]::WriteAllBytes(
        (Join-Path $corpusRoot "reader/seed-$index.json"),
        [System.Text.Encoding]::UTF8.GetBytes($_)
    )
}

Write-Host "Seeded reader corpus with $index golden frames"

# ---- Fuzz ------------------------------------------------------------------

$selected = if ($Target -eq 'both') { @('reader', 'writer') } else { , $Target }
foreach ($name in $selected)
{
    Write-Host "Fuzzing $name target for ${SecondsPerTarget}s"
    $env:SIGNALFISH_FUZZ_TARGET = $name
    $env:SIGNALFISH_FUZZ_CRASH_DIR = $artifactsDir

    # Argument array + splatting: pwsh native-arg parsing can mangle
    # libFuzzer's -flag=value tokens passed as bare strings.
    $fuzzerArgs = @(
        '-timeout=10'
        '-rss_limit_mb=4096'
        "-max_total_time=$SecondsPerTarget"
        "-artifact_prefix=$artifactsDir/"
        '--target_path=dotnet'
        "--target_arg=$projectDll"
        (Join-Path $corpusRoot $name)
    )
    & $DriverPath @fuzzerArgs

    if ($LASTEXITCODE -ne 0)
    {
        Write-Error (
            "Fuzz target '$name' failed (exit $LASTEXITCODE). " +
            "Crashing inputs were written to $artifactsDir."
        )
    }

    Write-Host "Fuzz target '$name' clean for ${SecondsPerTarget}s"
}

Write-Host 'Fuzz lane green.'
