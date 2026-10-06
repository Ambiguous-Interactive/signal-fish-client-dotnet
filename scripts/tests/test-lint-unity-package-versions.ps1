<#
.SYNOPSIS
    Self-tests for scripts/lint-unity-package-versions.ps1 (M9.5 release
    coupling): lockstep versions across the UPM fleet, exact internal pins.
#>
. (Join-Path $PSScriptRoot 'test-common.ps1')

$scriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'lint-unity-package-versions.ps1'
$repo = New-TestRepo
try {
    Write-Host 'test-lint-unity-package-versions'

    # A realistic fleet under real names: the core SDK package, the
    # adapter core, and one adapter that depends on both, plus one
    # external engine dependency the lint must ignore.
    function New-FleetPackage {
        param(
            [string]$Name,
            [string]$Version,
            [hashtable]$Dependencies
        )
        $dir = Join-Path $repo "unity/Adapters/$Name"
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $manifest = [ordered]@{
            name        = $Name
            version     = $Version
            displayName = "Signal Fish $Name"
        }
        if ($Dependencies) {
            $manifest.dependencies = [ordered]@{}
            foreach ($key in $Dependencies.Keys) {
                $manifest.dependencies[$key] = $Dependencies[$key]
            }
        }
        Write-TestFile -Path (Join-Path $dir 'package.json') -Content ($manifest | ConvertTo-Json -Depth 4)
    }

    $signalfish = 'com.ambiguous-interactive.signalfish'
    $adaptersCore = 'com.ambiguous-interactive.signalfish.adapters.core'

    function New-LockstepFleet {
        New-FleetPackage -Name $signalfish -Version '0.1.0'
        New-FleetPackage -Name $adaptersCore -Version '0.1.0' -Dependencies @{
            $signalfish = '0.1.0'
        }
        New-FleetPackage -Name 'com.ambiguous-interactive.signalfish.transport.fishnet' -Version '0.1.0' -Dependencies @{
            $signalfish   = '0.1.0'
            $adaptersCore = '0.1.0'
            'com.example.engine-sdk' = '2021.2.11'
        }
    }

    # 1. The real repository fleet is consistent (default RepoRoot).
    # Expected red on a release-prep branch mid-bump: that state fails
    # the lint by design; the CI lint step covers the checkout too.
    $run = Invoke-Pwsh -ScriptPath $scriptPath
    Assert-Equal 0 $run.ExitCode 'real repository fleet is in lockstep'

    # 2. A clean lockstep fleet passes.
    New-LockstepFleet
    $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo)
    Assert-Equal 0 $run.ExitCode 'lockstep fleet passes'

    # 3. A version outlier fails and names the package.
    New-FleetPackage -Name 'mirror' -Version '0.2.0'
    $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo)
    Assert-True ($run.ExitCode -ne 0) 'version outlier fails'
    Assert-OutputContains -Run $run -Pattern 'mirror' 'version outlier names the package'
    Remove-Item -LiteralPath (Join-Path $repo 'unity/Adapters/mirror/package.json') -Force
    $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo)
    Assert-Equal 0 $run.ExitCode 'fleet passes again after the outlier is removed'

    # 4. A stale internal pin fails and names both versions.
    $repo2 = New-TestRepo
    try {
        $manifest = [ordered]@{ name = 'com.ambiguous-interactive.signalfish'; version = '0.2.0' }
        $dir = Join-Path $repo2 'unity/Packages/core'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Write-TestFile -Path (Join-Path $dir 'package.json') -Content ($manifest | ConvertTo-Json -Depth 4)
        $manifest = [ordered]@{
            name         = 'com.ambiguous-interactive.signalfish.adapters.x'
            version      = '0.2.0'
            dependencies = [ordered]@{ 'com.ambiguous-interactive.signalfish' = '0.1.0' }
        }
        $dir = Join-Path $repo2 'unity/Adapters/x'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Write-TestFile -Path (Join-Path $dir 'package.json') -Content ($manifest | ConvertTo-Json -Depth 4)

        $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo2)
        Assert-True ($run.ExitCode -ne 0) 'stale internal pin fails'
        Assert-OutputContains -Run $run -Pattern '0\.1\.0.*0\.2\.0|0\.2\.0.*0\.1\.0' 'stale pin names both versions'
    }
    finally {
        Remove-TestRepo -Path $repo2
    }

    # 5. Non-exact pins fail (the fleet resolves on exact versions only).
    $repo3 = New-TestRepo
    try {
        $manifest = [ordered]@{ name = 'com.ambiguous-interactive.signalfish'; version = '0.1.0' }
        $dir = Join-Path $repo3 'unity/Packages/core'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Write-TestFile -Path (Join-Path $dir 'package.json') -Content ($manifest | ConvertTo-Json -Depth 4)
        $manifest = [ordered]@{
            name         = 'com.ambiguous-interactive.signalfish.adapters.y'
            version      = '0.1.0'
            dependencies = [ordered]@{ 'com.ambiguous-interactive.signalfish' = '^0.1.0' }
        }
        $dir = Join-Path $repo3 'unity/Adapters/y'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Write-TestFile -Path (Join-Path $dir 'package.json') -Content ($manifest | ConvertTo-Json -Depth 4)

        $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo3)
        Assert-True ($run.ExitCode -ne 0) 'range pin fails'
        Assert-OutputContains -Run $run -Pattern 'exact' 'range pin message says exact'
    }
    finally {
        Remove-TestRepo -Path $repo3
    }

    # 6. A pin to a package outside the fleet fails (typo protection).
    New-FleetPackage -Name 'com.ambiguous-interactive.signalfish.adapters.pun2' -Version '0.1.0' -Dependencies @{
        $adaptersCore = '0.1.0'
    }
    Remove-Item -LiteralPath (Join-Path $repo "unity/Adapters/$adaptersCore/package.json") -Force
    $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo)
    Assert-True ($run.ExitCode -ne 0) 'pin to a missing package fails'
    Assert-OutputContains -Run $run -Pattern "pins '$adaptersCore', which is not a fleet package" 'missing-pin message names the package'

    # 7. A malformed version fails (UPM requires semver).
    $repo4 = New-TestRepo
    try {
        $manifest = [ordered]@{ name = 'com.ambiguous-interactive.signalfish'; version = '0.1' }
        $dir = Join-Path $repo4 'unity/Packages/core'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Write-TestFile -Path (Join-Path $dir 'package.json') -Content ($manifest | ConvertTo-Json -Depth 4)
        $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo4)
        Assert-True ($run.ExitCode -ne 0) 'malformed version fails'
        Assert-OutputContains -Run $run -Pattern 'semver' 'malformed version message'
    }
    finally {
        Remove-TestRepo -Path $repo4
    }

    # 8. No packages at all fails with the remedy (cannot bless a typo'd glob).
    $repo5 = New-TestRepo
    try {
        $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo5)
        Assert-True ($run.ExitCode -ne 0) 'empty fleet fails'
        Assert-OutputContains -Run $run -Pattern 'No package' 'empty fleet remedy'
    }
    finally {
        Remove-TestRepo -Path $repo5
    }

    # 9-13. Malformed manifest shapes must be violations, never crashes
    # or silent passes. Raw JSON gives exact control over the shapes
    # (ConvertTo-Json does not round-trip single-element arrays).
    function New-ShapedAdapterRepo {
        param([string]$AdapterJson)
        $r = New-TestRepo
        $coreDir = Join-Path $r 'unity/Packages/core'
        New-Item -ItemType Directory -Force -Path $coreDir | Out-Null
        Write-TestFile -Path (Join-Path $coreDir 'package.json') -Content '{"name": "com.ambiguous-interactive.signalfish", "version": "0.1.0"}'
        $adapterDir = Join-Path $r 'unity/Adapters/x'
        New-Item -ItemType Directory -Force -Path $adapterDir | Out-Null
        Write-TestFile -Path (Join-Path $adapterDir 'package.json') -Content $AdapterJson
        return $r
    }

    $cases = @(
        @{
            Name = 'null dependencies'
            Json = '{"name": "com.ambiguous-interactive.signalfish.adapters.x", "version": "0.1.0", "dependencies": null}'
            Pattern = 'dependencies must be an object'
        },
        @{
            Name = 'array version'
            Json = '{"name": "com.ambiguous-interactive.signalfish.adapters.x", "version": ["0.1.0"]}'
            Pattern = 'must carry a version string'
        },
        @{
            Name = 'array dependencies'
            Json = '{"name": "com.ambiguous-interactive.signalfish.adapters.x", "version": "0.1.0", "dependencies": ["com.ambiguous-interactive.signalfish"]}'
            Pattern = 'dependencies must be an object'
        },
        @{
            Name = 'self dependency'
            Json = '{"name": "com.ambiguous-interactive.signalfish.adapters.x", "version": "0.1.0", "dependencies": {"com.ambiguous-interactive.signalfish.adapters.x": "0.1.0"}}'
            Pattern = 'depends on itself'
        },
        @{
            Name = 'non-string pin'
            Json = '{"name": "com.ambiguous-interactive.signalfish.adapters.x", "version": "0.1.0", "dependencies": {"com.ambiguous-interactive.signalfish": ["0.1.0"]}}'
            Pattern = 'non-string value'
        }
    )
    foreach ($case in $cases) {
        $shapedRepo = New-ShapedAdapterRepo -AdapterJson $case.Json
        try {
            $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $shapedRepo)
            Assert-True ($run.ExitCode -ne 0) "$($case.Name) fails"
            Assert-OutputContains -Run $run -Pattern $case.Pattern "$($case.Name) message"
        }
        finally {
            Remove-TestRepo -Path $shapedRepo
        }
    }
    # 14. A prerelease fleet version is valid lockstep (the tag lane,
    # not this lint, ties it to the tag).
    $repo6 = New-TestRepo
    try {
        $coreDir = Join-Path $repo6 'unity/Packages/core'
        New-Item -ItemType Directory -Force -Path $coreDir | Out-Null
        Write-TestFile -Path (Join-Path $coreDir 'package.json') -Content '{"name": "com.ambiguous-interactive.signalfish", "version": "0.2.0-rc.1"}'
        $adapterDir = Join-Path $repo6 'unity/Adapters/x'
        New-Item -ItemType Directory -Force -Path $adapterDir | Out-Null
        Write-TestFile -Path (Join-Path $adapterDir 'package.json') -Content '{"name": "com.ambiguous-interactive.signalfish.adapters.x", "version": "0.2.0-rc.1", "dependencies": {"com.ambiguous-interactive.signalfish": "0.2.0-rc.1"}}'
        $run = Invoke-Pwsh -ScriptPath $scriptPath -Arguments @('-RepoRoot', $repo6)
        Assert-Equal 0 $run.ExitCode 'prerelease lockstep fleet passes'
    }
    finally {
        Remove-TestRepo -Path $repo6
    }

    Get-ScriptExitSummary
}
finally {
    Remove-TestRepo -Path $repo
}
