<#
.SYNOPSIS
    Self-tests for scripts/install-hooks.ps1: hook installation must be
    anchored to the script's own repository, not the caller's current
    directory (the CWD-dependence failure class from the powershell-tooling
    skill).
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'test-common.ps1')

$script = Join-Path (Split-Path -Parent $PSScriptRoot) 'install-hooks.ps1'
$repo = New-TestRepo
$elsewhere = New-TestRepo

try {
    # A real git repo so `git -C <repo> config` has a repository to write to.
    & git -C $repo init --quiet
    if ($LASTEXITCODE -ne 0) {
        throw 'git init failed; cannot run install-hooks self-test.'
    }

    # Mirror the real layout: the script anchors to the repo it belongs to,
    # so the copy under test must live inside the temp repo.
    $scriptCopy = Join-Path (Join-Path $repo 'scripts') 'install-hooks.ps1'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $scriptCopy) | Out-Null
    Copy-Item $script $scriptCopy

    # Launch by absolute path with the caller's CWD OUTSIDE the target repo.
    Push-Location $elsewhere
    try {
        & pwsh -NoProfile -File $scriptCopy > $null
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    Assert-Equal 0 $exitCode 'install-hooks succeeds when launched from outside the repo'

    $hooksPath = & git -C $repo config core.hooksPath
    Assert-Equal '.githooks' "$hooksPath" 'core.hooksPath set on the anchored repo'
}
finally {
    Remove-TestRepo $repo
    Remove-TestRepo $elsewhere
}

Write-Host ('install-hooks self-test: {0} passed, {1} failed' -f $script:passCount, $script:failCount)
if ($script:failCount -gt 0) {
    exit 1
}
exit 0
