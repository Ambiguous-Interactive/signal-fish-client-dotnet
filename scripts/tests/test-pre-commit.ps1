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

    # Bugbot regression: a staged src/ PROJECT file injecting LINQ must
    # reach the linter too (the hook used to forward only *.cs paths, so
    # the commit was blessed locally and failed in CI). Each case resets
    # the index so the staged set isolates the file under test.
    & git -C $repo reset -q | Out-Null
    Write-TestFile -Path (Join-Path $repo 'src/Injected.csproj') -Content '<Project><ItemGroup><Using Include="System.Linq" /></ItemGroup></Project>'
    & git -C $repo add src/Injected.csproj
    $run = Invoke-Hook
    $joined = (@($run.Output) | ForEach-Object { $_.ToString() }) -join "`n"
    Assert-True ($joined -match 'enforcing the LINQ ban') 'staged src .csproj triggers the LINQ lint'
    Assert-True ($joined -match 'System\.Linq reference found') 'LINQ-injecting src .csproj is blocked'

    # Sibling sweep: the size lint enforces every .cursor/rules/*.mdc, so
    # the hook must trigger on them (it used to know only one pointer).
    & git -C $repo reset -q | Out-Null
    Write-TestFile -Path (Join-Path $repo '.cursor/rules/extra.mdc') -Content ((1..301 | ForEach-Object { "line $_" }) -join "`n")
    & git -C $repo add .cursor/rules/extra.mdc
    $run = Invoke-Hook
    $joined = (@($run.Output) | ForEach-Object { $_.ToString() }) -join "`n"
    Assert-True ($joined -match 'MUST split') 'oversized .cursor/rules/*.mdc is blocked by the hook'

    # A clean src/ .csproj runs the gates without tripping either lint.
    & git -C $repo reset -q | Out-Null
    Write-TestFile -Path (Join-Path $repo 'src/Clean.csproj') -Content '<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>'
    & git -C $repo add src/Clean.csproj
    $run = Invoke-Hook
    $joined = (@($run.Output) | ForEach-Object { $_.ToString() }) -join "`n"
    Assert-True ($joined -match 'enforcing the LINQ ban') 'staged clean src .csproj triggers the LINQ lint'
    Assert-True ($joined -notmatch 'System\.Linq reference found') 'clean src .csproj is not blocked by the LINQ ban'

    # --- Unity package mirror gate ------------------------------------------
    # A staged src/SignalFish.Client edit against a stale mirror must
    # re-sync and re-stage the mirror (the commit must never bake a stale
    # Runtime copy).
    & git -C $repo reset -q | Out-Null
    $libraryRoot = Join-Path $repo 'src/SignalFish.Client'
    $packageRoot = Join-Path $repo 'unity/Packages/com.ambiguous-interactive.signalfish'
    Write-TestFile -Path (Join-Path $libraryRoot 'Core/A.cs') -Content @(
        'namespace SignalFish.Client',
        '{',
        '    public static class A { }',
        '}'
    )
    Write-TestFile -Path (Join-Path $packageRoot 'package.json') -Content '{ "name": "com.ambiguous-interactive.signalfish", "version": "0.1.0" }'
    Write-TestFile -Path (Join-Path $packageRoot 'Runtime/Stale.cs') -Content 'stale'
    & git -C $repo add 'src/SignalFish.Client' $packageRoot
    $run = Invoke-Hook
    $joined = (@($run.Output) | ForEach-Object { $_.ToString() }) -join "`n"
    Assert-True ($joined -match 'refreshing the Unity package source mirror') 'staged src .cs triggers the Unity mirror gate'
    # The fixture has no .config/dotnet-tools.json, so the unrelated
    # CSharpier gate always reports its restore guidance; the mirror
    # gate's effects are what this case verifies.
    Assert-True ($joined -match 'CSharpier is not restored') 'the remaining blocker is the known missing-manifest guidance'
    Assert-True ($joined -notmatch 'mirror sync failed') 'the mirror sync itself did not fail'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $packageRoot 'Runtime/Stale.cs'))) 'the mirror sync removed the orphaned Runtime copy'
    $stagedRuntime = @(& git -C $repo diff --cached --name-only -- "$packageRoot/Runtime")
    Assert-True ($stagedRuntime.Count -gt 0) 'the refreshed mirror was re-staged'
    Assert-True ((Test-Path -LiteralPath (Join-Path $packageRoot 'Runtime/Core/A.cs'))) 'the fresh source is mirrored under Runtime'

    # Bugbot regression: an untracked src/ file must also block the sync
    # (the gate reads the working tree; an untracked file would otherwise
    # be mirrored into a commit that never contains it).
    & git -C $repo reset -q | Out-Null
    & git -C $repo add 'src/SignalFish.Client' $packageRoot
    Write-TestFile -Path (Join-Path $repo 'src/SignalFish.Client/Core/ZzUntracked.cs') -Content 'scratch'
    $run = Invoke-Hook
    $joined = (@($run.Output) | ForEach-Object { $_.ToString() }) -join "`n"
    Assert-True ($run.ExitCode -ne 0) 'an untracked src file blocks the commit while the gate is active'
    Assert-True ($joined -match 'unstaged or untracked src/SignalFish\.Client') 'the block message names the unstaged-src guard'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $packageRoot 'Runtime/ZzUntracked.cs'))) 'the untracked file was never mirrored'
}
finally {
    Remove-TestRepo -Path $repo
}

Get-ScriptExitSummary
