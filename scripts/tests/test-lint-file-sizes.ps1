<#
.SYNOPSIS
    Self-tests for scripts/lint-file-sizes.ps1 (300-line hard limit).
#>
. (Join-Path $PSScriptRoot 'test-common.ps1')

$linter = Join-Path (Split-Path -Parent $PSScriptRoot) 'lint-file-sizes.ps1'
$repo = New-TestRepo
try {
    Write-Host 'test-lint-file-sizes'

    # 1. Under the limit passes.
    $small = (Join-Path $repo '.llm') + '/small.md'
    Write-TestFile -Path $small -Content ((1..250 | ForEach-Object { "line $_" }) -join "`n")
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', '.llm/small.md')
    Assert-Equal 0 $run.ExitCode '250-line file passes'

    # 2. Exactly at the limit passes.
    $exact = (Join-Path $repo '.llm') + '/exact.md'
    Write-TestFile -Path $exact -Content ((1..300 | ForEach-Object { "line $_" }) -join "`n")
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', '.llm/exact.md')
    Assert-Equal 0 $run.ExitCode '300-line file passes (limit is inclusive)'

    # 3. Over the limit fails.
    $big = (Join-Path $repo '.llm') + '/big.md'
    Write-TestFile -Path $big -Content ((1..301 | ForEach-Object { "line $_" }) -join "`n")
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', '.llm/big.md')
    Assert-True ($run.ExitCode -ne 0) '301-line file fails with exit 1'
    Assert-OutputContains -Run $run -Pattern 'MUST split' 'over-limit message mentions splitting'

    # 4. Warning at the 270 threshold without failing.
    $warn = (Join-Path $repo '.llm') + '/warn.md'
    Write-TestFile -Path $warn -Content ((1..275 | ForEach-Object { "line $_" }) -join "`n")
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', '.llm/warn.md')
    Assert-Equal 0 $run.ExitCode '275-line file passes but warns'
    Assert-OutputContains -Run $run -Pattern 'consider splitting' 'warning text present'

    # 5. Default scope covers all .llm files and pointer files.
    $pointer = Join-Path $repo 'CLAUDE.md'
    Write-TestFile -Path $pointer -Content ((1..320 | ForEach-Object { "line $_" }) -join "`n")
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo)
    Assert-True ($run.ExitCode -ne 0) 'default scope catches oversized pointer file'

    # 6. Default scope passes when everything is small.
    Remove-Item -LiteralPath $pointer, $big -Force
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo)
    Assert-Equal 0 $run.ExitCode 'default scope passes when all files are within limits'

    # 7. Missing target errors.
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', '.llm/does-not-exist.md')
    Assert-True ($run.ExitCode -ne 0) 'missing path fails'
}
finally {
    Remove-TestRepo $repo
}
Get-ScriptExitSummary
