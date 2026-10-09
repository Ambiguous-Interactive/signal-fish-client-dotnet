<#
.SYNOPSIS
    Runs the perf gate (PLAN.md M9.4): BenchmarkDotNet vs the committed
    baseline.

.DESCRIPTION
    Runs tests/SignalFish.Client.PerfTests (medium job, JSON export) and
    compares the results against tests/SignalFish.Client.PerfTests/
    baseline.json:

    - Median wall time may grow at most -MaxRegression (default 1.30;
    shared runners are noisy, real regressions are larger). Time ratios
    only compare within the same CPU model: the shared fleet rotates
    EPYC SKUs (9V74, 7763, and 9V45 observed within one week), identical
    code spans ~2.5x across them, and a different-CPU run reports
    skipped time ratios instead of a false regression.
    - Bytes allocated per operation may not grow at all: allocation is
    deterministic and CPU-independent, and the codec budget is
    zero-alloc steady state.
    - The runner architecture must match the baseline's (cross-arch
    medians are not comparable).
    - Baseline entries this run no longer produces (renamed or removed
    benchmarks) fail, so the gate cannot silently rot.
    - New benchmarks are additive and pass with a warning; re-record the
    baseline to fold them in.

    Like the fuzz lane, this never runs on pull requests: PR CI time
    stays flat. -UpdateBaseline records a fresh baseline instead of
    comparing; scheduled runs never update it - the Bench workflow
    uploads the fresh baseline as an artifact and the operator reviews
    and commits it (the audited escape hatch, same shape as the
    api-compat suppression file).

    -SkipBenchmarks -ResultsDir <dir> runs only the compare/update phase
    against existing reports: the self-tests use it, and it re-checks a
    run without re-measuring.

.EXAMPLE
    pwsh -NoProfile -File scripts/run-bench.ps1

.EXAMPLE
    pwsh -NoProfile -File scripts/run-bench.ps1 -UpdateBaseline

.EXAMPLE
    pwsh -NoProfile -File scripts/run-bench.ps1 -SkipBenchmarks -ResultsDir BenchmarkDotNet.Artifacts/results
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,

    # Allowed median growth factor. CI runners are noisy; a tight gate
    # flakes, a loose one hides real regressions. 1.30 sits between them.
    [ValidateRange(1.01, 10.0)]
    [double]$MaxRegression = 1.30,

    [switch]$UpdateBaseline,
    [switch]$SkipBenchmarks,
    [string]$ResultsDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

$projectPath = Join-Path $RepoRoot 'tests/SignalFish.Client.PerfTests/SignalFish.Client.PerfTests.csproj'
$baselinePath = Join-Path $RepoRoot 'tests/SignalFish.Client.PerfTests/baseline.json'
$artifactsRoot = Join-Path $RepoRoot 'BenchmarkDotNet.Artifacts'
if (-not $ResultsDir) {
    $ResultsDir = Join-Path $artifactsRoot 'results'
}

function Read-Results {
    param([string]$Dir)

    if (-not (Test-Path -LiteralPath $Dir)) {
        throw "Benchmark results not found: $Dir. Run the benchmarks first (omit -SkipBenchmarks)."
    }
    $files = @(Get-ChildItem -LiteralPath $Dir -Filter '*-report*.json' -File)
    if ($files.Count -eq 0) {
        throw "No BenchmarkDotNet reports (*-report*.json) under $Dir."
    }

    $architectures = New-Object 'System.Collections.Generic.HashSet[string]'
    $cpus = New-Object 'System.Collections.Generic.HashSet[string]'
    $versions = New-Object 'System.Collections.Generic.HashSet[string]'
    $byFullName = @{}
    foreach ($file in $files) {
        $report = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        # StrictMode makes bare property reads throw before the friendly
        # guards can, so existence is checked via PSObject.Properties.
        foreach ($field in @('HostEnvironmentInfo', 'Benchmarks')) {
            if (-not $report.PSObject.Properties[$field]) {
                throw "Report field missing: $field in $($file.Name)."
            }
        }
        foreach ($field in @('Architecture', 'BenchmarkDotNetVersion')) {
            if (-not $report.HostEnvironmentInfo.PSObject.Properties[$field]) {
                throw "Report field missing: HostEnvironmentInfo.$field in $($file.Name)."
            }
        }
        if (-not $report.Benchmarks) {
            throw "Report has no benchmarks: $($file.Name)"
        }
        $architectures.Add([string]$report.HostEnvironmentInfo.Architecture) | Out-Null
        $versions.Add([string]$report.HostEnvironmentInfo.BenchmarkDotNetVersion) | Out-Null
        # ProcessorName is the CPU model the medians belong to; reports
        # without it degrade to an unknown model (time ratios then skip).
        $cpu = ''
        if ($report.HostEnvironmentInfo.PSObject.Properties['ProcessorName']) {
            $cpu = [string]$report.HostEnvironmentInfo.ProcessorName
        }
        if ($cpu) { $cpus.Add($cpu) | Out-Null }
        else { $cpus.Add('(unknown)') | Out-Null }

        foreach ($bench in $report.Benchmarks) {
            if (-not $bench.PSObject.Properties['FullName'] -or
                -not $bench.PSObject.Properties['Statistics'] -or
                -not $bench.Statistics.PSObject.Properties['Median']) {
                throw "Benchmark entry has no timing statistics in $($file.Name) (FullName / Statistics.Median missing)."
            }
            if (-not $bench.PSObject.Properties['Memory'] -or
                -not $bench.Memory.PSObject.Properties['BytesAllocatedPerOperation']) {
                throw "Benchmark '$($bench.FullName)' has no MemoryDiagnoser data in $($file.Name). Add [MemoryDiagnoser] to it."
            }
            $entry = [pscustomobject]@{
                FullName                   = [string]$bench.FullName
                MedianNanoseconds          = [double]$bench.Statistics.Median
                BytesAllocatedPerOperation = [long]$bench.Memory.BytesAllocatedPerOperation
            }
            if ($byFullName.ContainsKey($entry.FullName)) {
                $existing = $byFullName[$entry.FullName]
                if ($existing.MedianNanoseconds -ne $entry.MedianNanoseconds -or
                    $existing.BytesAllocatedPerOperation -ne $entry.BytesAllocatedPerOperation) {
                    throw "Conflicting results for '$($entry.FullName)' under $Dir (multiple exporters?)."
                }
                continue
            }
            $byFullName[$entry.FullName] = $entry
        }
    }

    if ($architectures.Count -gt 1) {
        throw "Results mix architectures ($($architectures -join ', ')); a run must use one host."
    }
    if ($cpus.Count -gt 1) {
        throw "Results mix CPUs ($($cpus -join ', ')); a run must use one host."
    }
    $entries = @($byFullName.Values | Sort-Object -Property FullName)
    return [pscustomobject]@{
        Architecture           = [string]($architectures | Select-Object -First 1)
        CpuModel               = [string]($cpus | Select-Object -First 1)
        BenchmarkDotNetVersion = [string]($versions | Select-Object -First 1)
        Entries                = $entries
    }
}

function Write-Baseline {
    param([string]$Path, [object]$Run)

    # Recording is the audited escape hatch: a baseline without a CPU
    # model could never time-compare, so refuse instead of pinning ''.
    if (-not $Run.CpuModel) {
        throw "Results do not name a CPU model (HostEnvironmentInfo.ProcessorName missing); refusing to record a baseline whose time gate could never compare."
    }

    $document = [ordered]@{
        schemaVersion          = 1
        job                    = 'medium'
        architecture           = $Run.Architecture
        cpuModel               = $Run.CpuModel
        benchmarkDotNetVersion = $Run.BenchmarkDotNetVersion
        recordedAtUtc          = (Get-Date).ToUniversalTime().ToString(
            'yyyy-MM-ddTHH:mm:ssZ',
            [System.Globalization.CultureInfo]::InvariantCulture)
        entries                = @($Run.Entries | ForEach-Object {
            [ordered]@{
                fullName                   = $_.FullName
                medianNanoseconds          = $_.MedianNanoseconds
                bytesAllocatedPerOperation = $_.BytesAllocatedPerOperation
            }
        })
    }
    $json = ConvertTo-Json -InputObject $document -Depth 5
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Baseline written: $Path ($($Run.Entries.Count) benchmarks, $($Run.Architecture)$(if ($Run.CpuModel) { ", $($Run.CpuModel)" }))."
    Write-Host 'Review the diff and commit it - the gate compares every future run against this file.'
}

function Read-Baseline {
    param([string]$Path)

    $baseline = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    # StrictMode makes bare property reads throw before the friendly
    # guards can, so existence is checked via PSObject.Properties.
    foreach ($field in @('schemaVersion', 'architecture', 'entries')) {
        if (-not $baseline.PSObject.Properties[$field]) {
            throw "Baseline is missing '$field': $Path. Re-record it with -UpdateBaseline."
        }
    }
    if ($baseline.schemaVersion -ne 1) {
        throw "Unsupported baseline schema version '$($baseline.schemaVersion)' in $Path."
    }
    if (-not $baseline.architecture -or -not $baseline.entries) {
        throw "Baseline is missing architecture or entries: $Path. Re-record it with -UpdateBaseline."
    }
    return $baseline
}

function Format-Us {
    param([double]$Nanoseconds)
    return '{0:N2} us' -f ($Nanoseconds / 1000.0)
}

# Compare the run against the baseline; returns rows, failures, warnings.
function Compare-Results {
    param([object]$Baseline, [object]$Run)

    if ($Baseline.architecture -ne $Run.Architecture) {
        throw "Baseline architecture $($Baseline.architecture) != this run's $($Run.Architecture); medians are not comparable across architectures. Re-record the baseline on the target architecture (-UpdateBaseline) as a reviewed change."
    }

    $rows = New-Object 'System.Collections.Generic.List[object]'
    $failures = New-Object 'System.Collections.Generic.List[string]'
    $warnings = New-Object 'System.Collections.Generic.List[string]'

    # A BenchmarkDotNet upgrade can shift medians with no code change;
    # warn so a required re-record has a named cause.
    if ($Baseline.PSObject.Properties['benchmarkDotNetVersion'] -and
        $Baseline.benchmarkDotNetVersion -ne $Run.BenchmarkDotNetVersion) {
        $warnings.Add("BenchmarkDotNet changed since the baseline ($($Baseline.benchmarkDotNetVersion) -> $($Run.BenchmarkDotNetVersion)); medians may shift without a code change. Re-record if the diff is tooling, not regression.") | Out-Null
    }

    # Medians are only comparable on the same CPU model. The shared fleet
    # rotates EPYC SKUs (9V74, 7763, 9V45 observed in one week) and
    # identical code spans ~2.5x across them, so a different or unknown
    # CPU skips the time gate instead of reporting a false regression;
    # allocation and rot protection still gate the run.
    $baselineCpu = ''
    if ($Baseline.PSObject.Properties['cpuModel']) {
        $baselineCpu = [string]$Baseline.cpuModel
    }
    $sameCpu = [bool]($baselineCpu -and $Run.CpuModel -and $baselineCpu -eq $Run.CpuModel)
    if (-not $sameCpu) {
        $warnings.Add("Time ratios skipped: CPU differs (baseline: '$baselineCpu', this run: '$($Run.CpuModel)'). Shared-fleet SKUs are not median-comparable; allocation and rot protection still gate. Re-record the baseline if the fleet settles on one CPU.") | Out-Null
    }

    $runByName = @{}
    foreach ($entry in $Run.Entries) { $runByName[$entry.FullName] = $entry }

    foreach ($base in @($Baseline.entries)) {
        if (-not $runByName.ContainsKey($base.fullName)) {
            $failures.Add("Baseline benchmark no longer exists (renamed or removed): $($base.fullName). Re-record the baseline (-UpdateBaseline) so the gate cannot rot.") | Out-Null
            continue
        }
        $runEntry = $runByName[$base.fullName]
        $ratio = 0.0
        $ratioText = '-'
        $verdict = 'OK'
        if ($base.medianNanoseconds -le 0) {
            $failures.Add("Baseline median must be positive for $($base.fullName).") | Out-Null
            $verdict = 'FAIL'
        }
        elseif ($runEntry.MedianNanoseconds -le 0) {
            $failures.Add("Run median must be positive for $($base.fullName) (corrupt report?); got $($runEntry.MedianNanoseconds) ns.") | Out-Null
            $verdict = 'FAIL'
        }
        elseif ($sameCpu) {
            $ratio = $runEntry.MedianNanoseconds / $base.medianNanoseconds
            $ratioText = '{0:N3}' -f $ratio
            if ($ratio -gt $MaxRegression) {
                $failures.Add("Median regression: $($base.fullName) $(Format-Us $base.medianNanoseconds) -> $(Format-Us $runEntry.MedianNanoseconds) ($('{0:N3}x' -f $ratio) > $('{0:N2}x' -f $MaxRegression) limit).") | Out-Null
                $verdict = 'FAIL'
            }
        }
        else {
            $verdict = 'CPU-SKIP'
        }
        if ($runEntry.BytesAllocatedPerOperation -gt $base.bytesAllocatedPerOperation) {
            $failures.Add("Allocation regression: $($base.fullName) $($base.bytesAllocatedPerOperation) B -> $($runEntry.BytesAllocatedPerOperation) B per op. The budget is exact; re-record only with a reviewed reason.") | Out-Null
            $verdict = 'FAIL'
        }
        $rows.Add([pscustomobject]@{
            FullName = $base.fullName
            Base     = Format-Us $base.medianNanoseconds
            Run      = Format-Us $runEntry.MedianNanoseconds
            Ratio    = $ratioText
            Alloc    = "$($base.bytesAllocatedPerOperation) -> $($runEntry.BytesAllocatedPerOperation) B"
            Verdict  = $verdict
        }) | Out-Null
    }

    foreach ($entry in $Run.Entries) {
        $known = @($Baseline.entries | Where-Object { $_.fullName -eq $entry.FullName }).Count
        if ($known -eq 0) {
            $warnings.Add("New benchmark, not yet gated (additive, passes): $($entry.FullName). Re-record the baseline to gate it.") | Out-Null
            $rows.Add([pscustomobject]@{
                FullName = $entry.FullName
                Base     = '-'
                Run      = Format-Us $entry.MedianNanoseconds
                Ratio    = '-'
                Alloc    = "-> $($entry.BytesAllocatedPerOperation) B"
                Verdict  = 'NEW'
            }) | Out-Null
        }
    }

    return [pscustomobject]@{
        Rows = $rows; Failures = $failures; Warnings = $warnings; SameCpu = $sameCpu
    }
}

function Write-Comparison {
    param([object]$Comparison)

    $table = $Comparison.Rows |
        Format-Table -Property FullName, Base, Run, Ratio, Alloc, Verdict -AutoSize |
        Out-String -Width 200
    Write-Host $table
    foreach ($warning in $Comparison.Warnings) { Write-Host "WARNING: $warning" -ForegroundColor Yellow }

    if ($env:GITHUB_STEP_SUMMARY) {
        $lines = @('| Benchmark | Baseline | This run | Ratio | Allocated | Verdict |', '| --- | --- | --- | --- | --- | --- |')
        foreach ($row in $Comparison.Rows) {
            $lines += "| $($row.FullName) | $($row.Base) | $($row.Run) | $($row.Ratio) | $($row.Alloc) | $($row.Verdict) |"
        }
        foreach ($warning in $Comparison.Warnings) { $lines += "`nWARNING: $warning" }
        foreach ($failure in $Comparison.Failures) { $lines += "`nFAIL: $failure" }
        Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($lines -join "`n")
    }

    if ($Comparison.Failures.Count -gt 0) {
        Write-Host ''
        foreach ($failure in $Comparison.Failures) { Write-Host "FAIL: $failure" -ForegroundColor Red }
        exit 1
    }
    $budget = 'median x{0}, allocation exact' -f $MaxRegression
    if (-not $Comparison.SameCpu) {
        $budget = 'allocation exact; time ratios skipped (CPU differs)'
    }
    Write-Host "Perf gate passed: $($Comparison.Rows.Count) benchmark(s) within budget ($budget)."
}

if (-not $SkipBenchmarks) {
    if (Test-Path -LiteralPath $artifactsRoot) {
        Remove-Item -Recurse -Force $artifactsRoot
    }
    Push-Location $RepoRoot
    try {
        & dotnet run -c Release --project $projectPath -- --filter '*' --job medium --exporters json
        if ($LASTEXITCODE -ne 0) {
            throw "Benchmark run failed (exit $LASTEXITCODE)."
        }
    }
    finally {
        Pop-Location
    }
}

$run = Read-Results -Dir $ResultsDir
$cpuText = if ($run.CpuModel) { " ($($run.CpuModel))" } else { '' }
Write-Host "Bench run: $($run.Entries.Count) benchmark(s) on $($run.Architecture)$cpuText, BenchmarkDotNet $($run.BenchmarkDotNetVersion)."

if ($UpdateBaseline) {
    Write-Baseline -Path $baselinePath -Run $run
    return
}

if (-not (Test-Path -LiteralPath $baselinePath)) {
    throw "Baseline not found: $baselinePath. Record one with -UpdateBaseline (CI: dispatch the Bench workflow with update_baseline), review the diff, and commit it."
}

Write-Comparison -Comparison (Compare-Results -Baseline (Read-Baseline -Path $baselinePath) -Run $run)
