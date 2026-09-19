<#
.SYNOPSIS
    Self-tests for .githooks/pre-commit.ps1.

.DESCRIPTION
    Runs the hook inside a disposable git repo. Regression focus: staged-file
    collection must survive PowerShell's scalar unroll (a single staged file
    used to crash `.Count` under Set-StrictMode and block the commit).
#>

. (Join-Path $PSScriptRoot 'test-common.ps1')

$sourceRoot = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
$repo = New-TestRepo

function Invoke-Hook {
    Push-Location $repo
    try {
        return Invoke-Pwsh -ScriptPath (Join-Path $repo '.githooks/pre-commit.ps1')
    }
    finally {
        Pop-Location
    }
}

try {
    Copy-Item -Recurse -Force -Path (Join-Path $sourceRoot '.githooks') -Destination (Join-Path $repo '.githooks')
    Copy-Item -Recurse -Force -Path (Join-Path $sourceRoot 'scripts') -Destination (Join-Path $repo 'scripts')

    & git -C $repo init | Out-Null
    & git -C $repo config user.email 'self-test@example.com'
    & git -C $repo config user.name 'self-test'

    Write-TestFile -Path (Join-Path $repo 'README.md') -Content 'test'

    $run = Invoke-Hook
    Assert-Equal 0 $run.ExitCode 'no staged files: hook exits 0'

    & git -C $repo add README.md
    $run = Invoke-Hook
    Assert-Equal 0 $run.ExitCode 'single staged non-.llm file: hook exits 0 (scalar-unroll regression)'
    $joined = (@($run.Output) | ForEach-Object { $_.ToString() }) -join "`n"
    Assert-True ($joined -notmatch "Count' cannot be found") 'single staged file: no strict-mode Count error'

    Write-TestFile -Path (Join-Path $repo 'docs/a.md') -Content 'a'
    Write-TestFile -Path (Join-Path $repo 'docs/b.md') -Content 'b'
    & git -C $repo add docs
    $run = Invoke-Hook
    Assert-Equal 0 $run.ExitCode 'multiple staged non-.llm files: hook exits 0'
}
finally {
    Remove-TestRepo -Path $repo
}

Get-ScriptExitSummary
