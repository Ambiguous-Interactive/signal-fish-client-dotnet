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

    # --- src/ branches: LINQ ban + CSharpier -------------------------------
    # A clean src/ file must pass both new gates. The disposable repo has no
    # .config manifest, so the CSharpier step reports the clear
    # not-restored guidance instead of a raw dotnet error.
    Write-TestFile -Path (Join-Path $repo 'src/clean.cs') -Content 'namespace X { internal static class A { } }'
    & git -C $repo add src/clean.cs
    $run = Invoke-Hook
    $joined = (@($run.Output) | ForEach-Object { $_.ToString() }) -join "`n"
    Assert-True ($joined -match 'enforcing the LINQ ban') 'staged src .cs triggers the LINQ lint'
    Assert-True ($joined -match 'checking C# formatting') 'staged src .cs triggers the CSharpier check'
    Assert-True ($joined -match 'CSharpier is not restored') 'missing tool manifest produces restore guidance'

    # A LINQ-using src/ file must be blocked with the ban message.
    Write-TestFile -Path (Join-Path $repo 'src/linq.cs') -Content 'namespace X { using System.Linq; internal static class A { } }'
    & git -C $repo add src/linq.cs
    $run = Invoke-Hook
    $joined = (@($run.Output) | ForEach-Object { $_.ToString() }) -join "`n"
    Assert-True ($run.ExitCode -ne 0) 'LINQ-using src file blocks the commit'
    Assert-True ($joined -match 'System\.Linq reference found') 'block message names the LINQ ban'
}
finally {
    Remove-TestRepo -Path $repo
}

Get-ScriptExitSummary
