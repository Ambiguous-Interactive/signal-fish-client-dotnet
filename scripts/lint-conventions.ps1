<#
.SYNOPSIS
    Runs every C# source-convention lint in one pwsh process (CI entry point).

.DESCRIPTION
    Consolidates the six convention lints that used to be six separate CI
    steps into one step: zero-dependencies, no-linq, no-this-qualification,
    comment-form, test-names, and member-order. Each linter is invoked
    in-process with `&` (its `exit` sets $LASTEXITCODE and returns control),
    so the step costs one pwsh startup instead of six and its wall time
    stays flat as lints are added. Every linter always runs so one run
    reports every violated convention; the failure summary names each
    failing script.

    The .llm/file-size and instruction-system lints stay separate: they
    gate documentation and the hook, not C# sources.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of the scripts directory.

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-conventions.ps1
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

$linters = @(
    'lint-zero-dependencies.ps1',
    'lint-no-linq.ps1',
    'lint-no-this-qualification.ps1',
    'lint-comment-form.ps1',
    'lint-test-names.ps1',
    'lint-member-order.ps1'
)

$failed = New-Object 'System.Collections.Generic.List[string]'
foreach ($linter in $linters) {
    Write-Host "==> $linter"
    $scriptPath = Join-Path $RepoRoot (Join-Path 'scripts' $linter)
    if (-not (Test-Path -LiteralPath $scriptPath)) {
        Write-Host "lint-conventions: script not found: scripts/$linter" -ForegroundColor Red
        $failed.Add($linter) | Out-Null
        continue
    }
    & $scriptPath -RepoRoot $RepoRoot
    if ($LASTEXITCODE -ne 0) {
        $failed.Add($linter) | Out-Null
    }
}

if ($failed.Count -gt 0) {
    Write-Host ''
    Write-Host "lint-conventions FAILED: $($failed.Count) lint(s) failed:" -ForegroundColor Red
    foreach ($linter in $failed) {
        Write-Host "  - scripts/$linter" -ForegroundColor Red
    }
    exit 1
}

Write-Host ''
Write-Host "lint-conventions passed: all $($linters.Count) lints clean."
exit 0
