<#
.SYNOPSIS
    Self-tests for scripts/pack-unity-packages.ps1 (release UPM tarballs).
#>
. (Join-Path $PSScriptRoot 'test-common.ps1')

$pack = Join-Path (Split-Path -Parent $PSScriptRoot) 'pack-unity-packages.ps1'
$repo = New-TestRepo
try {
    Write-Host 'test-pack-unity-packages'

    # Fixture: the core SDK package (with a mirrorable library source) and
    # two adapters -- one with samples, one without.
    Write-TestFile -Path (Join-Path $repo 'src/SignalFish.Client/Core/A.cs') -Content @(
        'namespace SignalFish.Client',
        '{',
        '    public static class A { }',
        '}'
    )
    $coreRoot = Join-Path $repo 'unity/Packages/com.ambiguous-interactive.signalfish'
    Write-TestFile -Path (Join-Path $coreRoot 'package.json') -Content @(
        '{',
        '    "name": "com.ambiguous-interactive.signalfish",',
        '    "version": "0.1.0"',
        '}'
    )
    Write-TestFile -Path (Join-Path $coreRoot 'Runtime/SignalFish.Client.asmdef') -Content @('{"name": "Test.Runtime"}')
    Write-TestFile -Path (Join-Path $coreRoot 'Samples~/Demo/README.md') -Content '# sample'

    $adapters = @(
        @{ Dir = 'Adapters/Core'; Name = 'com.ambiguous-interactive.signalfish.adapters.core' },
        @{ Dir = 'Adapters/FishNet'; Name = 'com.ambiguous-interactive.signalfish.transport.fishnet' }
    )
    foreach ($adapter in $adapters) {
        $root = Join-Path $repo (Join-Path 'unity' $adapter.Dir)
        Write-TestFile -Path (Join-Path $root 'package.json') -Content @(
            '{',
            "    `"name`": `"$($adapter.Name)`",",
            '    "version": "0.1.0"',
            '}'
        )
        Write-TestFile -Path (Join-Path $root 'Runtime/Adapter.cs') -Content 'namespace X;'
    }
    Write-TestFile -Path (Join-Path $repo 'unity/Adapters/Core/Samples~/Echo/README.md') -Content '# sample'

    $dist = Join-Path $repo 'dist'

    # 1. Packs the core through the sync staging and every adapter verbatim.
    $run = Invoke-Pwsh -ScriptPath $pack -Arguments @('-RepoRoot', $repo, '-OutDir', $dist)
    Assert-Equal 0 $run.ExitCode 'pack succeeds'
    $coreTarball = Join-Path $dist 'com.ambiguous-interactive.signalfish-0.1.0.tgz'
    $coreAdapterTarball = Join-Path $dist 'com.ambiguous-interactive.signalfish.adapters.core-0.1.0.tgz'
    $fishnetTarball = Join-Path $dist 'com.ambiguous-interactive.signalfish.transport.fishnet-0.1.0.tgz'
    Assert-True (Test-Path -LiteralPath $coreTarball) 'core tarball is named after the package and version'
    Assert-True (Test-Path -LiteralPath $coreAdapterTarball) 'adapter tarball is named after the package and version'
    Assert-True (Test-Path -LiteralPath $fishnetTarball) 'second adapter tarball is named after the package and version'

    # 2. The core tarball is the sync -Pack staging: fresh mirror, hand-
    #    written asmdef, samples.
    $listing = (tar -tzf $coreTarball) -join "`n"
    Assert-True ($listing -match 'package\.json') 'core tarball contains package.json'
    Assert-True ($listing -match 'Runtime/Core/A\.cs') 'core tarball stages the fresh mirror'
    Assert-True ($listing -match 'Runtime/SignalFish\.Client\.asmdef') 'core tarball ships the hand-written Runtime asmdef'
    Assert-True ($listing -match 'Samples~/Demo/README\.md') 'core tarball contains the samples'

    # 3. An adapter tarball is the package directory verbatim: manifest,
    #    runtime sources, samples (UPM requires package.json at the root).
    $listing = (tar -tzf $coreAdapterTarball) -join "`n"
    Assert-True ($listing -match 'package\.json') 'adapter tarball contains package.json at the root'
    Assert-True ($listing -match 'Runtime/Adapter\.cs') 'adapter tarball contains the runtime sources'
    Assert-True ($listing -match 'Samples~/Echo/README\.md') 'adapter tarball contains the samples'
    $listing = (tar -tzf $fishnetTarball) -join "`n"
    Assert-True ($listing -match 'Runtime/Adapter\.cs') 'sample-less adapter tarball contains the runtime sources'
    Assert-True ($listing -notmatch 'Samples~') 'sample-less adapter ships no sample tree'

    # 4. A demo manifest under Samples~ is not a fleet member.
    Write-TestFile -Path (Join-Path $repo 'unity/Adapters/FishNet/Samples~/Demo/package.json') -Content @('{"name": "demo", "version": "9.9.9"}')
    $run = Invoke-Pwsh -ScriptPath $pack -Arguments @('-RepoRoot', $repo, '-OutDir', $dist)
    Assert-Equal 0 $run.ExitCode 'pack ignores Samples~ manifests'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $dist 'demo-9.9.9.tgz'))) 'Samples~ demo is never packed'
    Remove-Item -LiteralPath (Join-Path $repo 'unity/Adapters/FishNet/Samples~/Demo/package.json') -Force

    # 5. A partial or drifted fleet fails loudly instead of packing.
    # 5a. Missing version string.
    Write-TestFile -Path (Join-Path $repo 'unity/Adapters/FishNet/package.json') -Content @(
        '{',
        '    "name": "com.ambiguous-interactive.signalfish.transport.fishnet"',
        '}'
    )
    $run = Invoke-Pwsh -ScriptPath $pack -Arguments @('-RepoRoot', $repo, '-OutDir', $dist)
    Assert-True ($run.ExitCode -ne 0) 'a manifest without a version fails the pack'
    Assert-OutputContains -Run $run -Pattern 'unity/Adapters/FishNet/package\.json : package\.json must carry a version string\.' 'the failing manifest is named with forward slashes'
    # 5b. Invalid JSON.
    Write-TestFile -Path (Join-Path $repo 'unity/Adapters/FishNet/package.json') -Content @('{ "name": ')
    $run = Invoke-Pwsh -ScriptPath $pack -Arguments @('-RepoRoot', $repo, '-OutDir', $dist)
    Assert-True ($run.ExitCode -ne 0) 'invalid JSON fails the pack instead of crashing'
    Assert-OutputContains -Run $run -Pattern 'unity/Adapters/FishNet/package\.json' 'invalid JSON names the manifest'
    # 5c. Two packages sharing a name: the second tarball would silently
    #     overwrite the first and one package would vanish from the release.
    Write-TestFile -Path (Join-Path $repo 'unity/Adapters/FishNet/package.json') -Content @(
        '{',
        '    "name": "com.ambiguous-interactive.signalfish.adapters.core",',
        '    "version": "0.1.0"',
        '}'
    )
    $run = Invoke-Pwsh -ScriptPath $pack -Arguments @('-RepoRoot', $repo, '-OutDir', $dist)
    Assert-True ($run.ExitCode -ne 0) 'a duplicate package name fails the pack'
    Assert-OutputContains -Run $run -Pattern 'duplicate package name' 'the duplicate is named'
    # 5d. Tree drift: an emptied fleet fails instead of passing vacuously.
    $run = Invoke-Pwsh -ScriptPath $pack -Arguments @('-RepoRoot', (New-TestRepo), '-OutDir', $dist)
    Assert-True ($run.ExitCode -ne 0) 'an empty fleet fails the pack'
    Assert-OutputContains -Run $run -Pattern 'No package manifests found under unity/' 'the empty-fleet remedy names the glob'
    # Restore the valid fleet.
    Write-TestFile -Path (Join-Path $repo 'unity/Adapters/FishNet/package.json') -Content @(
        '{',
        '    "name": "com.ambiguous-interactive.signalfish.transport.fishnet",',
        '    "version": "0.1.0"',
        '}'
    )
    $run = Invoke-Pwsh -ScriptPath $pack -Arguments @('-RepoRoot', $repo, '-OutDir', $dist)
    Assert-Equal 0 $run.ExitCode 'pack recovers once the fleet is valid again'

    # 6. The summary counts the packed fleet.
    Assert-OutputContains -Run $run -Pattern 'packed 3 UPM tarballs' 'the summary counts the fleet'

    # 7. The real repository fleet: nine packages in lockstep at 0.1.0.
    $realDist = Join-Path ([System.IO.Path]::GetTempPath()) ("upmpack-" + [System.Guid]::NewGuid().ToString('N'))
    try {
        $run = Invoke-Pwsh -ScriptPath $pack -Arguments @('-OutDir', $realDist)
        Assert-Equal 0 $run.ExitCode 'pack succeeds against the real fleet'
        $tarballs = @()
        if (Test-Path -LiteralPath $realDist) {
            $tarballs = @(Get-ChildItem -LiteralPath $realDist -File -Filter '*.tgz')
        }
        Assert-Equal 9 $tarballs.Count 'every fleet package is packed'
        Assert-Equal 9 @(Get-ChildItem -LiteralPath $realDist -File -Filter '*-0.1.0.tgz').Count 'every tarball carries the fleet version'
        Assert-True ($null -ne ($tarballs | Where-Object { $_.Name -eq 'com.ambiguous-interactive.signalfish-0.1.0.tgz' })) 'the core tarball is present'
        foreach ($name in @(
                'com.ambiguous-interactive.signalfish.adapters.core-0.1.0.tgz',
                'com.ambiguous-interactive.signalfish.adapters.facepunch-0.1.0.tgz',
                'com.ambiguous-interactive.signalfish.adapters.fusion-0.1.0.tgz',
                'com.ambiguous-interactive.signalfish.adapters.ngo-0.1.0.tgz',
                'com.ambiguous-interactive.signalfish.adapters.pun2-0.1.0.tgz',
                'com.ambiguous-interactive.signalfish.adapters.steamworksnet-0.1.0.tgz',
                'com.ambiguous-interactive.signalfish.transport.fishnet-0.1.0.tgz',
                'com.ambiguous-interactive.signalfish.transport.mirror-0.1.0.tgz'
            )) {
            Assert-True ($null -ne ($tarballs | Where-Object { $_.Name -eq $name })) "the $name tarball is present"
        }
        $listing = (tar -tzf (Join-Path $realDist 'com.ambiguous-interactive.signalfish.transport.fishnet-0.1.0.tgz')) -join "`n"
        Assert-True ($listing -match 'Runtime/FishNet/SignalFishFishNetTransport\.cs') 'the FishNet tarball ships the transport bridge'
    }
    finally {
        Remove-Item -LiteralPath $realDist -Recurse -Force -ErrorAction SilentlyContinue
    }
}
finally {
    Remove-TestRepo -Path $repo
}

Get-ScriptExitSummary
