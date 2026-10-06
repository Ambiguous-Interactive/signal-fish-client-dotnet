<#
.SYNOPSIS
    Self-tests for scripts/run-bench.ps1 (M9.4 perf gate): baseline
    recording, median/allocation/architecture comparison, rot protection.
#>
. (Join-Path $PSScriptRoot 'test-common.ps1')

$scriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'run-bench.ps1'
$repo = New-TestRepo
try {
    Write-Host 'test-run-bench'

    $resultsDir = Join-Path $repo 'results'
    New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null
    $projectDir = Join-Path $repo 'tests/SignalFish.Client.PerfTests'
    New-Item -ItemType Directory -Force -Path $projectDir | Out-Null
    $baselinePath = Join-Path $projectDir 'baseline.json'

    function New-Entry {
        param([string]$FullName, [double]$Median, [long]$Alloc)
        return [pscustomobject]@{
            FullName = $FullName
            Statistics = [pscustomobject]@{ Median = $Median }
            Memory = [pscustomobject]@{ BytesAllocatedPerOperation = $Alloc }
        }
    }

    function Write-Report {
        param(
            [object[]]$Benchmarks,
            [string]$Architecture = 'X64',
            [string]$Name = 'PerfTests-report-full-compressed.json',
            [string]$Version = '0.15.8'
        )
        $report = [ordered]@{
            HostEnvironmentInfo = [ordered]@{
                Architecture           = $Architecture
                BenchmarkDotNetVersion = $Version
            }
            Benchmarks = @($Benchmarks)
        }
        Write-TestFile -Path (Join-Path $resultsDir $Name) -Content ($report | ConvertTo-Json -Depth 6)
    }

    $codec = 'SignalFish.Client.PerfTests.CodecBenchmarks.DecodeFullCorpus'
    $queue = 'SignalFish.Client.PerfTests.BoundedQueueBenchmarks.HandRolledTryRoundtrip'

    # 1. Missing results dir fails with a clear message.
    $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo, '-SkipBenchmarks', '-ResultsDir', (Join-Path $repo 'missing'))
    Assert-True ($run.ExitCode -ne 0) 'missing results dir fails'
    Assert-OutputContains -Run $run -Pattern 'Benchmark results not found' 'missing results message names the remedy'

    # 2. Results without a committed baseline fail (never silently pass).
    Write-Report -Benchmarks @((New-Entry -FullName $codec -Median 55000 -Alloc 0))
    $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo, '-SkipBenchmarks', '-ResultsDir', $resultsDir)
    Assert-True ($run.ExitCode -ne 0) 'missing baseline fails'
    Assert-OutputContains -Run $run -Pattern 'Baseline not found' 'missing baseline message'

    # 3. -UpdateBaseline distills the reports into the committed baseline.
    Write-Report -Benchmarks @(
        (New-Entry -FullName $codec -Median 55000 -Alloc 0),
        (New-Entry -FullName $queue -Median 66000 -Alloc 0)
    )
    $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo, '-SkipBenchmarks', '-ResultsDir', $resultsDir, '-UpdateBaseline')
    Assert-Equal 0 $run.ExitCode 'baseline recording passes'
    Assert-True (Test-Path -LiteralPath $baselinePath) 'baseline file written'
    $baseline = Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json
    Assert-Equal 1 $baseline.schemaVersion 'baseline schema version'
    Assert-Equal 'X64' $baseline.architecture 'baseline records architecture'
    Assert-Equal 'medium' $baseline.job 'baseline records the job'
    Assert-Equal 2 @($baseline.entries).Count 'baseline entries count'
    Assert-Equal $queue $baseline.entries[0].fullName 'baseline entries sorted by full name'
    Assert-Equal 66000 $baseline.entries[0].medianNanoseconds 'baseline median distilled'
    Assert-Equal $codec $baseline.entries[1].fullName 'baseline second entry'
    Assert-Equal 55000 $baseline.entries[1].medianNanoseconds 'baseline median distilled'
    Assert-Equal 0 $baseline.entries[1].bytesAllocatedPerOperation 'baseline allocation distilled'

    # 4. An unchanged run passes.
    $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo, '-SkipBenchmarks', '-ResultsDir', $resultsDir)
    Assert-Equal 0 $run.ExitCode 'unchanged run passes'
    Assert-OutputContains -Run $run -Pattern 'Perf gate passed' 'pass message'

    # 5. Exactly at the median limit passes; above it fails.
    Write-Report -Benchmarks @(
        (New-Entry -FullName $codec -Median (55000 * 1.30) -Alloc 0),
        (New-Entry -FullName $queue -Median 66000 -Alloc 0)
    )
    $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo, '-SkipBenchmarks', '-ResultsDir', $resultsDir)
    Assert-Equal 0 $run.ExitCode 'exactly-at-limit ratio passes'
    Write-Report -Benchmarks @(
        (New-Entry -FullName $codec -Median (55000 * 1.31) -Alloc 0),
        (New-Entry -FullName $queue -Median 66000 -Alloc 0)
    )
    $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo, '-SkipBenchmarks', '-ResultsDir', $resultsDir)
    Assert-True ($run.ExitCode -ne 0) 'above-limit ratio fails'
    Assert-OutputContains -Run $run -Pattern ([regex]::Escape($codec)) 'median regression names the benchmark'

    # 6. Any allocation growth fails; allocation decrease passes.
    Write-Report -Benchmarks @(
        (New-Entry -FullName $codec -Median 55000 -Alloc 8),
        (New-Entry -FullName $queue -Median 66000 -Alloc 0)
    )
    $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo, '-SkipBenchmarks', '-ResultsDir', $resultsDir)
    Assert-True ($run.ExitCode -ne 0) 'allocation growth fails'
    Assert-OutputContains -Run $run -Pattern 'Allocation regression' 'allocation message'
    Write-Report -Benchmarks @(
        (New-Entry -FullName $codec -Median 55000 -Alloc 0),
        (New-Entry -FullName $queue -Median 66000 -Alloc 0)
    )
    $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo, '-SkipBenchmarks', '-ResultsDir', $resultsDir)
    Assert-Equal 0 $run.ExitCode 'allocation at baseline passes'

    # 7. Architecture mismatch fails instead of comparing noise.
    Write-Report -Benchmarks @(
        (New-Entry -FullName $codec -Median 55000 -Alloc 0),
        (New-Entry -FullName $queue -Median 66000 -Alloc 0)
    ) -Architecture 'Arm64'
    $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo, '-SkipBenchmarks', '-ResultsDir', $resultsDir)
    Assert-True ($run.ExitCode -ne 0) 'architecture mismatch fails'
    Assert-OutputContains -Run $run -Pattern 'Arm64.*X64|X64.*Arm64' 'architecture message names both'

    # 8. A BenchmarkDotNet version drift warns but passes.
    Write-Report -Benchmarks @(
        (New-Entry -FullName $codec -Median 55000 -Alloc 0),
        (New-Entry -FullName $queue -Median 66000 -Alloc 0)
    ) -Architecture 'X64' -Version '0.16.0'
    $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo, '-SkipBenchmarks', '-ResultsDir', $resultsDir)
    Assert-Equal 0 $run.ExitCode 'benchmarkdotnet version drift passes'
    Assert-OutputContains -Run $run -Pattern 'BenchmarkDotNet changed since the baseline' 'version drift warns with both versions'

    # 9. A zero or negative run median fails instead of dividing noise.
    Write-Report -Benchmarks @(
        (New-Entry -FullName $codec -Median 0 -Alloc 0),
        (New-Entry -FullName $queue -Median 66000 -Alloc 0)
    ) -Architecture 'X64'
    $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo, '-SkipBenchmarks', '-ResultsDir', $resultsDir)
    Assert-True ($run.ExitCode -ne 0) 'zero run median fails'
    Assert-OutputContains -Run $run -Pattern 'Run median must be positive' 'zero median message names the cause'

    # 10. Two report files that disagree on one benchmark fail loudly.
    Write-Report -Benchmarks @(
        (New-Entry -FullName $codec -Median 55000 -Alloc 0),
        (New-Entry -FullName $queue -Median 66000 -Alloc 0)
    ) -Architecture 'X64'
    Write-Report -Benchmarks @((New-Entry -FullName $codec -Median 99000 -Alloc 0)) `
        -Architecture 'X64' -Name 'PerfTests-report-brief.json'
    $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo, '-SkipBenchmarks', '-ResultsDir', $resultsDir)
    Assert-True ($run.ExitCode -ne 0) 'conflicting duplicate results fail'
    Assert-OutputContains -Run $run -Pattern 'Conflicting results' 'conflict message names the benchmark'

    # 11. A report without the expected fields fails with the friendly message.
    Write-TestFile -Path (Join-Path $resultsDir 'PerfTests-report-full-compressed.json') -Content '{"HostEnvironmentInfo": {}}'
    $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo, '-SkipBenchmarks', '-ResultsDir', $resultsDir)
    Assert-True ($run.ExitCode -ne 0) 'field-less report fails'
    Assert-OutputContains -Run $run -Pattern 'Report field missing' 'missing-field message is the friendly one'

    # 12. A baseline missing the entries field fails with the friendly message.
    Remove-Item -Path (Join-Path $resultsDir '*') -Force
    Write-Report -Benchmarks @((New-Entry -FullName $codec -Median 55000 -Alloc 0)) -Architecture 'X64'
    Write-TestFile -Path $baselinePath -Content '{"schemaVersion": 1, "architecture": "X64"}'
    $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo, '-SkipBenchmarks', '-ResultsDir', $resultsDir)
    Assert-True ($run.ExitCode -ne 0) 'field-less baseline fails'
    Assert-OutputContains -Run $run -Pattern "Baseline is missing 'entries'" 'baseline missing-field message'

    # 13. A benchmark the run no longer produces fails (rot protection).
    Write-Report -Benchmarks @(
        (New-Entry -FullName $codec -Median 55000 -Alloc 0),
        (New-Entry -FullName $queue -Median 66000 -Alloc 0)
    ) -Architecture 'X64'
    $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo, '-SkipBenchmarks', '-ResultsDir', $resultsDir, '-UpdateBaseline')
    Assert-Equal 0 $run.ExitCode 'baseline re-recorded after the corrupt-baseline test'
    Write-Report -Benchmarks @((New-Entry -FullName $codec -Median 55000 -Alloc 0)) -Architecture 'X64'
    $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo, '-SkipBenchmarks', '-ResultsDir', $resultsDir)
    Assert-True ($run.ExitCode -ne 0) 'orphaned baseline entry fails'
    Assert-OutputContains -Run $run -Pattern ([regex]::Escape($queue)) 'orphan message names the missing benchmark'

    # 14. A new benchmark is additive: passes with a warning.
    Write-Report -Benchmarks @(
        (New-Entry -FullName $codec -Median 55000 -Alloc 0),
        (New-Entry -FullName $queue -Median 66000 -Alloc 0),
        (New-Entry -FullName 'SignalFish.Client.PerfTests.CodecBenchmarks.EncodeRepresentativeSession' -Median 1600 -Alloc 0)
    ) -Architecture 'X64'
    $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo, '-SkipBenchmarks', '-ResultsDir', $resultsDir)
    Assert-Equal 0 $run.ExitCode 'new benchmark passes'
    Assert-OutputContains -Run $run -Pattern 'New benchmark, not yet gated' 'new benchmark warns'

    # 15. Every failure is reported, not just the first.
    Write-Report -Benchmarks @(
        (New-Entry -FullName $codec -Median (55000 * 2) -Alloc 16),
        (New-Entry -FullName $queue -Median (66000 * 2) -Alloc 0)
    ) -Architecture 'X64'
    $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo, '-SkipBenchmarks', '-ResultsDir', $resultsDir)
    Assert-True ($run.ExitCode -ne 0) 'double regression fails'
    Assert-OutputContains -Run $run -Pattern ([regex]::Escape($codec)) 'first failure reported'
    Assert-OutputContains -Run $run -Pattern ([regex]::Escape($queue)) 'second failure reported'
    Assert-OutputContains -Run $run -Pattern 'Allocation regression' 'allocation failure still reported'
}
finally {
    Remove-TestRepo $repo
}
Get-ScriptExitSummary
