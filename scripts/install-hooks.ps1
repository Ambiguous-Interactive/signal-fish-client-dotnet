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

# Anchor to the repository the script belongs to, not the caller's current
# directory: the script must work when launched by absolute path.
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

& git -C $repoRoot config core.hooksPath .githooks
if ($LASTEXITCODE -ne 0) {
    Write-Error 'git config core.hooksPath .githooks failed. Is this a git repository?'
    exit 1
}

Write-Host 'Hooks installed: core.hooksPath -> .githooks'
Write-Host 'The pre-commit hook requires pwsh (PowerShell 7+).'
exit 0
