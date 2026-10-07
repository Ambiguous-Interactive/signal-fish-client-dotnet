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
    #    written asmdef, samples — in the npm pack layout (entries root
    #    at package/, no './' entries), which is what npm publish and
    #    UPM's tarball installer both expect.
    $listing = (tar -tzf $coreTarball) -join "`n"
    Assert-True ($listing -match '(?m)^package/package\.json$') 'core tarball roots package.json at package/'
    Assert-True ($listing -notmatch '(?m)^\./') 'core tarball has no ./ entries'
    Assert-True ($listing -match 'package/Runtime/Core/A\.cs') 'core tarball stages the fresh mirror'
    Assert-True ($listing -match 'package/Runtime/SignalFish\.Client\.asmdef') 'core tarball ships the hand-written Runtime asmdef'
    Assert-True ($listing -match 'package/Samples~/Demo/README\.md') 'core tarball contains the samples'

    # 3. An adapter tarball is the package directory verbatim: manifest,
    #    runtime sources, samples (UPM requires package.json at the root).
    $listing = (tar -tzf $coreAdapterTarball) -join "`n"
    Assert-True ($listing -match '(?m)^package/package\.json$') 'adapter tarball roots package.json at package/'
    Assert-True ($listing -notmatch '(?m)^\./') 'adapter tarball has no ./ entries'
    Assert-True ($listing -match 'package/Runtime/Adapter\.cs') 'adapter tarball contains the runtime sources'
    Assert-True ($listing -match 'package/Samples~/Echo/README\.md') 'adapter tarball contains the samples'
    $listing = (tar -tzf $fishnetTarball) -join "`n"
    Assert-True ($listing -match '(?m)^package/package\.json$') 'sample-less adapter tarball roots package.json at package/'
    Assert-True ($listing -match 'package/Runtime/Adapter\.cs') 'sample-less adapter tarball contains the runtime sources'
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
    # 5c. A JSON null manifest is a shape violation, not a crash.
    Write-TestFile -Path (Join-Path $repo 'unity/Adapters/FishNet/package.json') -Content @('null')
    $run = Invoke-Pwsh -ScriptPath $pack -Arguments @('-RepoRoot', $repo, '-OutDir', $dist)
    Assert-True ($run.ExitCode -ne 0) 'a JSON null manifest fails the pack'
    Assert-OutputContains -Run $run -Pattern 'must contain a JSON object' 'the JSON-null remedy names the shape'
    # 5d. Two packages sharing a name: the second tarball would silently
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
    # 5e. A name or version that could carry a separator into the
    #     tarball filename (path traversal) is rejected before writing.
    Write-TestFile -Path (Join-Path $repo 'unity/Adapters/FishNet/package.json') -Content @(
        '{',
        '    "name": "../evil",',
        '    "version": "0.1.0"',
        '}'
    )
    $run = Invoke-Pwsh -ScriptPath $pack -Arguments @('-RepoRoot', $repo, '-OutDir', $dist)
    Assert-True ($run.ExitCode -ne 0) 'a traversal name fails the pack'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $repo 'evil-0.1.0.tgz'))) 'the traversal name writes nothing outside OutDir'
    Write-TestFile -Path (Join-Path $repo 'unity/Adapters/FishNet/package.json') -Content @(
        '{',
        '    "name": "com.ambiguous-interactive.signalfish.transport.fishnet",',
        '    "version": "1.2/x"',
        '}'
    )
    $run = Invoke-Pwsh -ScriptPath $pack -Arguments @('-RepoRoot', $repo, '-OutDir', $dist)
    Assert-True ($run.ExitCode -ne 0) 'a non-semver version fails the pack'
    # Restore the valid fleet: the remaining failure modes below must be
    # attributed to their own cause, not to this manifest.
    Write-TestFile -Path (Join-Path $repo 'unity/Adapters/FishNet/package.json') -Content @(
        '{',
        '    "name": "com.ambiguous-interactive.signalfish.transport.fishnet",',
        '    "version": "0.1.0"',
        '}'
    )
    # 5f. Tree drift: an emptied fleet fails instead of passing vacuously.
    $run = Invoke-Pwsh -ScriptPath $pack -Arguments @('-RepoRoot', (New-TestRepo), '-OutDir', $dist)
    Assert-True ($run.ExitCode -ne 0) 'an empty fleet fails the pack'
    Assert-OutputContains -Run $run -Pattern 'No package manifests found under unity/' 'the empty-fleet remedy names the glob'
    # 5g. A missing core package is not a valid smaller fleet: the
    #     adapters could never resolve their SDK dependency.
    $capturedCore = Join-Path ([System.IO.Path]::GetTempPath()) ("core-" + [System.Guid]::NewGuid().ToString('N'))
    Move-Item -LiteralPath (Join-Path $repo 'unity/Packages') -Destination $capturedCore
    $run = Invoke-Pwsh -ScriptPath $pack -Arguments @('-RepoRoot', $repo, '-OutDir', $dist)
    Assert-True ($run.ExitCode -ne 0) 'a fleet without the core package fails the pack'
    Assert-OutputContains -Run $run -Pattern 'no core package manifest' 'the missing-core remedy names the expected path'
    Move-Item -LiteralPath $capturedCore -Destination (Join-Path $repo 'unity/Packages')
    # 5h. -OutDir that exists as a file.
    $outFile = Join-Path $repo 'out-file'
    Write-TestFile -Path $outFile -Content 'x'
    $run = Invoke-Pwsh -ScriptPath $pack -Arguments @('-RepoRoot', $repo, '-OutDir', $outFile)
    Assert-True ($run.ExitCode -ne 0) 'an OutDir that is a file fails the pack'
    Remove-Item -LiteralPath $outFile -Force
    $run = Invoke-Pwsh -ScriptPath $pack -Arguments @('-RepoRoot', $repo, '-OutDir', $dist)
    Assert-Equal 0 $run.ExitCode 'pack recovers once the fleet is valid again'

    # 6. The summary counts the packed fleet.
    Assert-OutputContains -Run $run -Pattern 'packed 3 UPM tarballs' 'the summary counts the fleet'

    # 7. A drifted core package name fails instead of shipping a tarball
    #    whose filename lies about the package inside (the sync script
    #    names the staged tarball after its own hard-coded id).
    $drifted = Join-Path $coreRoot 'package.json'
    $manifestText = [System.IO.File]::ReadAllText($drifted)
    [System.IO.File]::WriteAllText($drifted, $manifestText.Replace('com.ambiguous-interactive.signalfish"', 'com.ambiguous-interactive.signalfish.core"'))
    $run = Invoke-Pwsh -ScriptPath $pack -Arguments @('-RepoRoot', $repo, '-OutDir', $dist)
    Assert-True ($run.ExitCode -ne 0) 'a drifted core name fails the pack'
    Assert-OutputContains -Run $run -Pattern 'expected com\.ambiguous-interactive\.signalfish\.core-0\.1\.0\.tgz' 'the drifted core is named with the expected tarball'
    [System.IO.File]::WriteAllText($drifted, $manifestText)
    # A stale artifact with the expected name cannot satisfy the check:
    # the expected name is pre-deleted before staging.
    $driftedSeed = Join-Path $dist 'com.ambiguous-interactive.signalfish.core-0.1.0.tgz'
    Write-TestFile -Path $driftedSeed -Content 'junk'
    [System.IO.File]::WriteAllText($drifted, $manifestText.Replace('com.ambiguous-interactive.signalfish"', 'com.ambiguous-interactive.signalfish.core"'))
    $run = Invoke-Pwsh -ScriptPath $pack -Arguments @('-RepoRoot', $repo, '-OutDir', $dist)
    Assert-True ($run.ExitCode -ne 0) 'a seeded stale tarball cannot satisfy the drifted-core check'
    if (Test-Path -LiteralPath $driftedSeed) {
        Remove-Item -LiteralPath $driftedSeed -Force
    }
    [System.IO.File]::WriteAllText($drifted, $manifestText)
    $run = Invoke-Pwsh -ScriptPath $pack -Arguments @('-RepoRoot', $repo, '-OutDir', $dist)
    Assert-Equal 0 $run.ExitCode 'pack recovers once the core name matches again'

    # 8. An explicitly relative -RepoRoot still routes the core through
    #    the sync staging (absolute paths are what the routing compares).
    Push-Location -LiteralPath $repo
    try {
        $run = Invoke-Pwsh -ScriptPath $pack -Arguments @('-RepoRoot', '.', '-OutDir', 'dist-rel')
        Assert-Equal 0 $run.ExitCode 'pack succeeds with a relative RepoRoot'
        $listing = (tar -tzf (Join-Path $repo 'dist-rel/com.ambiguous-interactive.signalfish-0.1.0.tgz')) -join "`n"
        Assert-True ($listing -match 'Runtime/Core/A\.cs') 'a relative RepoRoot still stages the fresh mirror'
    }
    finally {
        Pop-Location
    }

    # 9. The real repository fleet: nine packages in lockstep, one fleet
    #    version read from the manifests (never hard-coded — the routine
    #    version-coupling PR must not break this suite).
    $realVersion = ([System.IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../unity/Packages/com.ambiguous-interactive.signalfish/package.json')) | ConvertFrom-Json).version
    $realDist = Join-Path ([System.IO.Path]::GetTempPath()) ("upmpack-" + [System.Guid]::NewGuid().ToString('N'))
    try {
        $run = Invoke-Pwsh -ScriptPath $pack -Arguments @('-OutDir', $realDist)
        Assert-Equal 0 $run.ExitCode 'pack succeeds against the real fleet'
        $tarballs = @()
        if (Test-Path -LiteralPath $realDist) {
            $tarballs = @(Get-ChildItem -LiteralPath $realDist -File -Filter '*.tgz')
        }
        Assert-Equal 9 $tarballs.Count 'every fleet package is packed'
        Assert-Equal 9 @(Get-ChildItem -LiteralPath $realDist -File -Filter "*-$realVersion.tgz").Count 'every tarball carries the fleet version'
        $fleetNames = @(
            'com.ambiguous-interactive.signalfish',
            'com.ambiguous-interactive.signalfish.adapters.core',
            'com.ambiguous-interactive.signalfish.adapters.facepunch',
            'com.ambiguous-interactive.signalfish.adapters.fusion',
            'com.ambiguous-interactive.signalfish.adapters.ngo',
            'com.ambiguous-interactive.signalfish.adapters.pun2',
            'com.ambiguous-interactive.signalfish.adapters.steamworksnet',
            'com.ambiguous-interactive.signalfish.transport.fishnet',
            'com.ambiguous-interactive.signalfish.transport.mirror'
        )
        foreach ($name in $fleetNames) {
            $tarball = Join-Path $realDist "$name-$realVersion.tgz"
            Assert-True (Test-Path -LiteralPath $tarball) "the $name tarball is present"
        }
        $listing = (tar -tzf (Join-Path $realDist "com.ambiguous-interactive.signalfish.transport.fishnet-$realVersion.tgz")) -join "`n"
        Assert-True ($listing -match '(?m)^package/package\.json$') 'the real FishNet tarball roots package.json at package/'
        Assert-True ($listing -notmatch '(?m)^\./') 'the real FishNet tarball has no ./ entries'
        Assert-True ($listing -match 'package/Runtime/FishNet/SignalFishFishNetTransport\.cs') 'the FishNet tarball ships the transport bridge'
    }
    finally {
        Remove-Item -LiteralPath $realDist -Recurse -Force -ErrorAction SilentlyContinue
    }
}
finally {
    Remove-TestRepo -Path $repo
}

Get-ScriptExitSummary
