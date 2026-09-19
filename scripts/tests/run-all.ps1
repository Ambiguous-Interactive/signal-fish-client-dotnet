<#
.SYNOPSIS
    Runs every LLM tooling self-test. Exits non-zero if any test fails.

.EXAMPLE
    pwsh -NoProfile -File scripts/tests/run-all.ps1
#>
[CmdletBinding()]
param(
    [string]$Test
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$tests = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'test-*.ps1' | Sort-Object -Property Name)
if ($Test) {
    $tests = @($tests | Where-Object { $_.Name -like "*$Test*" })
    if ($tests.Count -eq 0) {
        Write-Error "no test file matches '$Test'."
        exit 1
    }
}

$failed = New-Object 'System.Collections.Generic.List[string]'
foreach ($testFile in $tests) {
    Write-Host "==> $($testFile.Name)"
    $output = & pwsh -NoProfile -File $testFile.FullName 2>&1
    if ($LASTEXITCODE -ne 0) {
        $failed.Add($testFile.Name) | Out-Null
        Write-Host "    FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
        foreach ($line in @($output)) { Write-Host "    | $line" }
    }
}

Write-Host ''
if ($failed.Count -gt 0) {
    Write-Host "SELF-TESTS FAILED: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "All $($tests.Count) self-test file(s) passed."
exit 0
