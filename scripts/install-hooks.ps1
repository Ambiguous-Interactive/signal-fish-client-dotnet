<#
.SYNOPSIS
    Activates the repository git hooks (sets core.hooksPath to .githooks).

.EXAMPLE
    pwsh -NoProfile -File scripts/install-hooks.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

& git config core.hooksPath .githooks
if ($LASTEXITCODE -ne 0) {
    Write-Error 'git config core.hooksPath .githooks failed. Are you inside the repository?'
    exit 1
}

Write-Host 'Hooks installed: core.hooksPath -> .githooks'
Write-Host 'The pre-commit hook requires pwsh (PowerShell 7+).'
exit 0
