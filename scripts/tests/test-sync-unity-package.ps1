<#
.SYNOPSIS
    Self-tests for scripts/sync-unity-package.ps1 (UPM source mirror).
#>
. (Join-Path $PSScriptRoot 'test-common.ps1')

$sync = Join-Path (Split-Path -Parent $PSScriptRoot) 'sync-unity-package.ps1'
$repo = New-TestRepo
try {
    Write-Host 'test-sync-unity-package'

    $libraryRoot = Join-Path $repo 'src/SignalFish.Client'
    $packageRoot = Join-Path $repo 'unity/Packages/com.ambiguous-interactive.signalfish'
    $runtimeRoot = Join-Path $packageRoot 'Runtime'

    # Fixture: two real sources, bin/obj noise, and a stale mirror entry.
    Write-TestFile -Path (Join-Path $libraryRoot 'Core/A.cs') -Content @(
        'namespace SignalFish.Client',
        '{',
        '    public static class A { }',
        '}'
    )
    Write-TestFile -Path (Join-Path $libraryRoot 'Sub/B.cs') -Content @(
        'namespace SignalFish.Client',
        '{',
        '    public static class B { }',
        '}'
    )
    Write-TestFile -Path (Join-Path $libraryRoot 'obj/X.cs') -Content 'generated'
    Write-TestFile -Path (Join-Path $libraryRoot 'bin/Y.cs') -Content 'generated'
    Write-TestFile -Path (Join-Path $packageRoot 'package.json') -Content @(
        '{',
        '    "name": "com.ambiguous-interactive.signalfish",',
        '    "version": "0.1.0"',
        '}'
    )
    Write-TestFile -Path (Join-Path $packageRoot 'link.xml') -Content '<linker />'
    Write-TestFile -Path (Join-Path $packageRoot 'Samples~/PollingDriver/README.md') -Content '# sample'
    Write-TestFile -Path (Join-Path $runtimeRoot 'SignalFish.Client.asmdef') -Content '{}'
    Write-TestFile -Path (Join-Path $runtimeRoot 'Stale.cs') -Content 'stale'
    Write-TestFile -Path (Join-Path $runtimeRoot 'Old/Z.cs') -Content 'stale'

    # 1. Sync lays down an exact mirror: sources in, noise and orphans out.
    $run = Invoke-Pwsh -ScriptPath $sync -Arguments @('-RepoRoot', $repo)
    Assert-Equal 0 $run.ExitCode 'sync succeeds'
    $mirroredA = Join-Path $runtimeRoot 'Core/A.cs'
    Assert-True (Test-Path -LiteralPath $mirroredA) 'sync copies nested sources'
    Assert-True (Test-Path -LiteralPath (Join-Path $runtimeRoot 'Sub/B.cs')) 'sync copies subfolder sources'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $runtimeRoot 'obj/X.cs'))) 'sync excludes obj/'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $runtimeRoot 'bin/Y.cs'))) 'sync excludes bin/'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $runtimeRoot 'Stale.cs'))) 'sync removes orphaned mirror files'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $runtimeRoot 'Old'))) 'sync prunes emptied directories'
    Assert-True (Test-Path -LiteralPath (Join-Path $runtimeRoot 'SignalFish.Client.asmdef')) 'sync leaves the asmdef alone'

    # 1b. Every mirrored file carries the nullable pragma (Unity ignores
    # the csproj), and -Check accepts exactly that shape.
    $mirroredText = [System.IO.File]::ReadAllText($mirroredA)
    Assert-True $mirroredText.StartsWith("#nullable enable`n") 'mirror carries the nullable pragma prefix'
    $run = Invoke-Pwsh -ScriptPath $sync -Arguments @('-RepoRoot', $repo, '-Check')
    Assert-Equal 0 $run.ExitCode 'fresh mirror (pragma included) passes -Check'

    # 1c. A source that already enables nullable is mirrored verbatim.
    $selfEnabling = Join-Path $libraryRoot 'Core/Self.cs'
    Write-TestFile -Path $selfEnabling -Content @('#nullable enable', 'namespace X;')
    $run = Invoke-Pwsh -ScriptPath $sync -Arguments @('-RepoRoot', $repo)
    Assert-Equal 0 $run.ExitCode 'sync succeeds with a self-enabling source'
    $selfText = [System.IO.File]::ReadAllText((Join-Path $runtimeRoot 'Core/Self.cs'))
    Assert-Equal 1 (@($selfText -split "#nullable enable").Count - 1) 'self-enabling source is not double-prefixed'
    Remove-Item -LiteralPath $selfEnabling -Force
    $run = Invoke-Pwsh -ScriptPath $sync -Arguments @('-RepoRoot', $repo)
    Assert-Equal 0 $run.ExitCode 'sync cleans up the temporary source'

    # 2. An edited source makes -Check fail with the remedy.
    Write-TestFile -Path (Join-Path $libraryRoot 'Core/A.cs') -Content @(
        'namespace SignalFish.Client',
        '{',
        '    public static class A { public const int V = 2; }',
        '}'
    )
    $run = Invoke-Pwsh -ScriptPath $sync -Arguments @('-RepoRoot', $repo, '-Check')
    Assert-True ($run.ExitCode -ne 0) 'stale mirror fails -Check'
    Assert-OutputContains -Run $run -Pattern 'stale: Core/A\.cs' 'stale entry is named'
    Assert-OutputContains -Run $run -Pattern 'sync-unity-package\.ps1' 'the remedy command is printed'

    # 4. A deleted source is treated as an orphan by sync.
    Remove-Item -LiteralPath (Join-Path $libraryRoot 'Sub/B.cs') -Force
    $run = Invoke-Pwsh -ScriptPath $sync -Arguments @('-RepoRoot', $repo)
    Assert-Equal 0 $run.ExitCode 'sync after source deletion succeeds'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $runtimeRoot 'Sub/B.cs'))) 'sync removes the deleted source from the mirror'

    # 5. Pack stages skeleton plus fresh mirror into a tarball.
    $dist = Join-Path $repo 'dist'
    $run = Invoke-Pwsh -ScriptPath $sync -Arguments @('-RepoRoot', $repo, '-Pack', '-OutDir', $dist)
    Assert-Equal 0 $run.ExitCode 'pack succeeds'
    $tarball = Join-Path $dist 'com.ambiguous-interactive.signalfish-0.1.0.tgz'
    Assert-True (Test-Path -LiteralPath $tarball) 'tarball is named after the package and version (.tgz for UPM)'
    $listing = (tar -tzf $tarball) -join "`n"
    Assert-True ($listing -match 'package\.json') 'tarball contains package.json'
    Assert-True ($listing -match 'link\.xml') 'tarball contains link.xml'
    Assert-True ($listing -match 'Runtime/Core/A\.cs') 'tarball contains the fresh mirror'
    Assert-True ($listing -notmatch 'Sub/B\.cs') 'tarball never resurrects removed sources'
    Assert-True ($listing -match 'Samples~/PollingDriver/README\.md') 'tarball contains the samples'
}
finally {
    Remove-TestRepo -Path $repo
}

Get-ScriptExitSummary
