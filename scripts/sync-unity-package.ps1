<#
.SYNOPSIS
    Mirrors the library sources into the Unity UPM package (source distribution).

.DESCRIPTION
    M7.1 distribution decision: the UPM package ships SOURCE, not a
    precompiled assembly — the simplest IL2CPP path with no per-Unity
    build matrix. The single source of truth stays src/SignalFish.Client;
    this script mirrors every library *.cs (bin/obj excluded) into the
    package Runtime folder and removes stale copies, so the mirror cannot
    drift silently. Each mirrored file is prefixed with a `#nullable
    enable` line: Unity ignores the csproj and asmdefs have no nullable
    switch, so without the pragma every annotation would warn (CS8632)
    in the consumer project. Modes:

      (default)  Sync the mirror to match src/SignalFish.Client exactly.
      -Check     Fail (exit 1) when the mirror is stale; CI and the
                 pre-commit hook use this.
      -Pack      Stage the installable package (skeleton + fresh mirror)
                 and write the UPM tarball into -OutDir, ready to attach
                 to a GitHub Release. The release lane packs the same
                 way for every v* tag and ships the whole fleet
                 (scripts/pack-unity-packages.ps1).

.EXAMPLE
    pwsh -NoProfile -File scripts/sync-unity-package.ps1
    pwsh -NoProfile -File scripts/sync-unity-package.ps1 -Check
    pwsh -NoProfile -File scripts/sync-unity-package.ps1 -Pack -OutDir dist
#>
[CmdletBinding()]
param(
    [switch]$Check,
    [switch]$Pack,
    [string]$OutDir = 'dist',
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

$libraryRoot = Join-Path $RepoRoot 'src/SignalFish.Client'
$packageRoot = Join-Path $RepoRoot 'unity/Packages/com.ambiguous-interactive.signalfish'
$runtimeRoot = Join-Path $packageRoot 'Runtime'

if (-not (Test-Path -LiteralPath (Join-Path $packageRoot 'package.json'))) {
    Write-Host "Unity package skeleton not found: $packageRoot" -ForegroundColor Red
    exit 1
}

function Get-LibrarySourceFiles {
    return @(
        Get-ChildItem -LiteralPath $libraryRoot -Recurse -File -Filter '*.cs'
        | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
        | Sort-Object -Property FullName
    )
}

# Relative ('/'-separated) path -> absolute source path for everything the
# mirror must hold.
function Get-MirrorPlan {
    $plan = @{}
    foreach ($file in (Get-LibrarySourceFiles)) {
        $relative = $file.FullName.Substring($libraryRoot.Length + 1) -replace '\\', '/'
        $plan[$relative] = $file.FullName
    }
    return $plan
}

# The currently mirrored files under a Runtime root, same shape as the plan.
function Get-MirroredFiles {
    param([string]$TargetRuntimeRoot)

    $mirror = @{}
    if (Test-Path -LiteralPath $TargetRuntimeRoot) {
        foreach ($file in @(Get-ChildItem -LiteralPath $TargetRuntimeRoot -Recurse -File -Filter '*.cs')) {
            $relative = $file.FullName.Substring($TargetRuntimeRoot.Length + 1) -replace '\\', '/'
            $mirror[$relative] = $file.FullName
        }
    }
    return $mirror
}

# The exact content a mirrored file must hold: the source text, prefixed
# with the nullable pragma unless the source already enables it.
$nullablePragma = '#nullable enable'
function Get-ExpectedMirrorContent {
    param([string]$SourcePath)
    $text = [System.IO.File]::ReadAllText($SourcePath)
    if ($text.StartsWith("$nullablePragma`n") -or $text.StartsWith("$nullablePragma`r`n")) {
        return $text
    }
    return "$nullablePragma`n$text"
}

# Lays down a complete, exact mirror under the given Runtime root: copies
# every planned file (nullable pragma prefixed), removes orphans, prunes
# emptied directories.
function Copy-Mirror {
    param([string]$TargetRuntimeRoot)

    $plan = Get-MirrorPlan
    foreach ($relative in $plan.Keys) {
        $target = Join-Path $TargetRuntimeRoot ($relative -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        $parent = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        [System.IO.File]::WriteAllText(
            $target,
            (Get-ExpectedMirrorContent -SourcePath $plan[$relative]),
            [System.Text.UTF8Encoding]::new($false)
        )
    }

    $mirror = Get-MirroredFiles -TargetRuntimeRoot $TargetRuntimeRoot
    foreach ($relative in @($mirror.Keys | Where-Object { -not $plan.ContainsKey($_) })) {
        Remove-Item -LiteralPath $mirror[$relative] -Force
    }
    if (Test-Path -LiteralPath $TargetRuntimeRoot) {
        $directories = @(Get-ChildItem -LiteralPath $TargetRuntimeRoot -Recurse -Directory)
        foreach ($dir in ($directories | Sort-Object { $_.FullName.Length } -Descending)) {
            if (@(Get-ChildItem -LiteralPath $dir.FullName -Force).Count -eq 0) {
                Remove-Item -LiteralPath $dir.FullName -Force
            }
        }
    }
}

function Find-Drift {
    $plan = Get-MirrorPlan
    $mirror = Get-MirroredFiles -TargetRuntimeRoot $runtimeRoot
    $drift = New-Object 'System.Collections.Generic.List[string]'
    foreach ($relative in $plan.Keys) {
        if (-not $mirror.ContainsKey($relative)) {
            $drift.Add("missing: $relative") | Out-Null
        }
        else {
            $expected = Get-ExpectedMirrorContent -SourcePath $plan[$relative]
            $actual = [System.IO.File]::ReadAllText($mirror[$relative])
            if ($expected -cne $actual) {
                $drift.Add("stale: $relative") | Out-Null
            }
        }
    }
    foreach ($relative in $mirror.Keys) {
        if (-not $plan.ContainsKey($relative)) {
            $drift.Add("orphan: $relative") | Out-Null
        }
    }
    # Wrap so an empty drift list survives the return-value unroll (an
    # unwrapped empty list arrives as $null, and $null.Count violates
    # StrictMode).
    return ,@($drift)
}

function New-PackageTarball {
    param([string]$Destination)

    $manifest = Get-Content -LiteralPath (Join-Path $packageRoot 'package.json') -Raw
    if ($manifest -notmatch '"version"\s*:\s*"([^"]+)"') {
        Write-Host 'Cannot read the package version from package.json.' -ForegroundColor Red
        exit 1
    }
    $version = $Matches[1]

    $stage = Join-Path ([System.IO.Path]::GetTempPath()) ("upmpack-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    # npm pack layout: entries root at package/ (required by Unity's
    # tarball installer).
    $stagePackage = Join-Path $stage 'package'
    New-Item -ItemType Directory -Path $stagePackage -Force | Out-Null
    try {
        # Stage the package skeleton (everything but the mirror), then lay a
        # fresh mirror over a verbatim Runtime copy: the mirror overlay must
        # never carry stale sources, but hand-written Runtime files (the
        # asmdef) must reach the tarball — without them the shipped source
        # distribution does not compile as a package.
        foreach ($entry in @(Get-ChildItem -LiteralPath $packageRoot -Force)) {
            if ($entry.Name -eq 'Runtime') {
                continue
            }
            Copy-Item -LiteralPath $entry.FullName -Destination (Join-Path $stagePackage $entry.Name) -Recurse -Force
        }
        $stageRuntime = Join-Path $stagePackage 'Runtime'
        New-Item -ItemType Directory -Path $stageRuntime -Force | Out-Null
        if (Test-Path -LiteralPath $runtimeRoot) {
            foreach ($entry in @(Get-ChildItem -LiteralPath $runtimeRoot -Force)) {
                Copy-Item -LiteralPath $entry.FullName -Destination (Join-Path $stageRuntime $entry.Name) -Recurse -Force
            }
        }
        Copy-Mirror -TargetRuntimeRoot $stageRuntime

        # Pin the shipped asmdef graph: Unity resolves references by name,
        # so every referenced asmdef must ship inside the package.
        $asmdefs = @(Get-ChildItem -LiteralPath $stage -Recurse -File -Filter '*.asmdef')
        $available = @{}
        foreach ($definition in $asmdefs) {
            $manifest = Get-Content -LiteralPath $definition.FullName -Raw | ConvertFrom-Json
            $nameProperty = $manifest.PSObject.Properties['name']
            if ($null -ne $nameProperty -and [string]$nameProperty.Value) {
                $available[[string]$nameProperty.Value] = $true
            }
        }
        foreach ($definition in $asmdefs) {
            $manifest = Get-Content -LiteralPath $definition.FullName -Raw | ConvertFrom-Json
            $nameProperty = $manifest.PSObject.Properties['name']
            $displayName = if ($null -ne $nameProperty) { [string]$nameProperty.Value } else { '' }
            $referencesProperty = $manifest.PSObject.Properties['references']
            $referenceNames = @()
            if ($null -ne $referencesProperty) {
                $referenceNames = @($referencesProperty.Value)
            }
            foreach ($reference in $referenceNames) {
                $referenceName = [string]$reference
                if ($referenceName -eq '') {
                    continue
                }
                if (-not $available.ContainsKey($referenceName)) {
                    Write-Host "pack: shipped asmdef '$displayName' references '$referenceName', which does not ship in the package." -ForegroundColor Red
                    exit 1
                }
            }
        }

        if (-not (Test-Path -LiteralPath $Destination)) {
            New-Item -ItemType Directory -Path $Destination -Force | Out-Null
        }
        # UPM's tarball picker only recognizes the .tgz extension.
        $tarball = Join-Path $Destination "com.ambiguous-interactive.signalfish-$version.tgz"
        if (Test-Path -LiteralPath $tarball) {
            Remove-Item -LiteralPath $tarball -Force
        }
        tar -czf $tarball -C $stage package
        if ($LASTEXITCODE -ne 0) {
            Write-Host "tar failed with exit code $LASTEXITCODE." -ForegroundColor Red
            exit 1
        }
        Write-Host "pack: $tarball"
    }
    finally {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($Pack) {
    if ($Check) {
        Write-Host '-Check and -Pack are mutually exclusive.' -ForegroundColor Red
        exit 1
    }
    New-PackageTarball -Destination $OutDir
    exit 0
}

$drift = Find-Drift
if ($drift.Count -eq 0) {
    Write-Host 'sync: Unity package mirror is fresh.'
    exit 0
}

if ($Check) {
    Write-Host 'sync: Unity package mirror is stale:' -ForegroundColor Red
    foreach ($entry in $drift) {
        Write-Host "    $entry" -ForegroundColor Red
    }
    Write-Host 'Run: pwsh -NoProfile -File scripts/sync-unity-package.ps1' -ForegroundColor Red
    exit 1
}

Copy-Mirror -TargetRuntimeRoot $runtimeRoot
$remaining = Find-Drift
if ($remaining.Count -gt 0) {
    Write-Host 'Sync did not converge; refusing to continue.' -ForegroundColor Red
    exit 1
}
Write-Host "sync: mirrored library sources into the Unity package ($($drift.Count) entries updated)."
