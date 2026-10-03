<#
.SYNOPSIS
    Keeps the FishNet adapter package in contract with the repo's rules.

.DESCRIPTION
    The FishNet adapter (unity/Adapters/FishNet) ships a Transport bridge
    that references FishNet types no compiler in this repo can see (the
    SDK is never vendored and Unity never runs in CI), plus a pure core
    that must stay engine- and FishNet-free. This lint is the red gate:

      1. Compile check (skipped with -NoBuild): the adapter's Runtime/Core
         sources compile as one netstandard2.1 assembly, C# 9, nullable,
         warnings as errors, with no UNITY_* or SIGNALFISH_FISHNET defines
         - the exact independence the core promises.
      2. Guard contract: any adapter source referencing FishNet types
         (`using FishNet.` / `namespace FishNet`) must be wrapped in
         `#if SIGNALFISH_FISHNET` / `#endif`; without FishNet the package
         compiles to nothing. Declaring a `namespace FishNet` is vendoring
         the SDK and always fails.
      3. Define plumbing: the asmdef must map FishNet.Runtime to
         SIGNALFISH_FISHNET through versionDefines, and package.json must
         parse with every declared sample path present on disk.
      4. Bridge completeness: the bridge file must carry every member of
         the FishNet 4.x abstract Transport surface this adapter pins
         (a dropped override is a runtime NotImplementedException in the
         editor, invisible to every compiler here).

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of the scripts directory.

.PARAMETER NoBuild
    Skip the compile check (used by the self-tests; CI always runs the
    full check).

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-fishnet-adapter.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [switch]$NoBuild,
    [switch]$VerboseOutput
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

$adapterRoot = Join-Path $RepoRoot 'unity/Adapters/FishNet'
$violations = New-Object 'System.Collections.Generic.List[string]'

if (-not (Test-Path -LiteralPath $adapterRoot)) {
    Write-Host 'lint-fishnet-adapter FAILED: no unity/Adapters/FishNet package - nothing to enforce.'
    exit 1
}

# The pinned FishNet 4.x abstract Transport surface (member names only;
# signatures are FishNet's). The bridge must carry all of them.
$pinnedMembers = @(
    'OnClientConnectionState',
    'OnServerConnectionState',
    'OnRemoteConnectionState',
    'OnClientReceivedData',
    'OnServerReceivedData',
    'HandleClientConnectionState',
    'HandleServerConnectionState',
    'HandleRemoteConnectionState',
    'HandleClientReceivedDataArgs',
    'HandleServerReceivedDataArgs',
    'GetConnectionAddress',
    'GetConnectionState',
    'SendToServer',
    'SendToClient',
    'IterateIncoming',
    'IterateOutgoing',
    'StartConnection',
    'StopConnection',
    'Shutdown',
    'GetMTU'
)

$sourceFiles = [string[]]@(Get-ChildItem -LiteralPath $adapterRoot -Recurse -File -Filter '*.cs' |
    ForEach-Object { $_.FullName })
[System.Array]::Sort($sourceFiles, [System.StringComparer]::Ordinal)

$coreFiles = [string[]]@($sourceFiles | Where-Object { $_ -match '[\\/]Runtime[\\/]Core[\\/]' })
$bridgeFiles = [string[]]@($sourceFiles | Where-Object { $_ -notmatch '[\\/]Runtime[\\/]Core[\\/]' })

if ($coreFiles.Count -eq 0) {
    $violations.Add('the adapter has no Runtime/Core sources - the CI-compiled core is the package.')
}

# 1. Guard contract for Runtime/ sources - the only files that compile
#    inside the adapter assembly, where the asmdef-scoped define exists.
#    Samples~ code compiles in the consumer's assemblies (where the
#    define never activates), so it is exempt: it references FishNet
#    freely and simply requires FishNet to be installed, which its
#    README documents.
foreach ($source in $sourceFiles) {
    $relative = [System.IO.Path]::GetFullPath($source).Substring(
        [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/').Length + 1)
    $relative = $relative -replace '\\', '/'
    $text = [System.IO.File]::ReadAllText($source)

    if ($text -match 'namespace\s+FishNet\b') {
        $violations.Add("$relative : declares `namespace FishNet` - vendoring the SDK is forbidden; reference it behind SIGNALFISH_FISHNET instead.")
    }

    if ($source -notmatch '[\\/]Runtime[\\/]') {
        continue
    }

    if ($text -match '(^|[^\w.])FishNet\.') {
        if ($text -notmatch '#if\s+SIGNALFISH_FISHNET' -or $text -notmatch '#endif') {
            $violations.Add("$relative : references FishNet types but is not wrapped in `#if SIGNALFISH_FISHNET` / `#endif` - without FishNet the package must compile to nothing.")
        }
    }
    elseif ($coreFiles -contains $source) {
        if ($text -match 'UnityEngine|SIGNALFISH_FISHNET') {
            $violations.Add("$relative : core sources must stay engine- and FishNet-free (no UnityEngine, no SIGNALFISH_FISHNET references).")
        }
    }
}

# 2. Bridge completeness against the pinned abstract surface (Runtime
#    sources only; samples are not bridges).
foreach ($bridge in @($bridgeFiles | Where-Object { $_ -match '[\\/]Runtime[\\/]' })) {
    $relative = [System.IO.Path]::GetFullPath($bridge).Substring(
        [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/').Length + 1)
    $relative = $relative -replace '\\', '/'
    $text = [System.IO.File]::ReadAllText($bridge)
    foreach ($member in $pinnedMembers) {
        if ($text -notmatch [regex]::Escape($member)) {
            $violations.Add("$relative : the pinned FishNet Transport member '$member' is missing - a dropped override surfaces as a runtime miss in the editor only.")
        }
    }
}

# 3. Define plumbing: versionDefines pin and package.json sanity.
#    The pin must name FishNet's UPM package (com.firstgeargames.fishnet,
#    verified against its package.json) - an asmdef name is not a valid
#    version define resource - with a concrete expression, and the asmdef
#    must gate its own compilation on the define so the FishNet.Runtime
#    reference can never dangle when FishNet is absent.
$asmdefPaths = [string[]]@(Get-ChildItem -LiteralPath $adapterRoot -Recurse -File -Filter '*.asmdef' |
    ForEach-Object { $_.FullName })
$pinFound = $false
foreach ($asmdef in $asmdefPaths) {
    $json = Get-Content -LiteralPath $asmdef -Raw | ConvertFrom-Json
    foreach ($define in @($json.versionDefines)) {
        if ($null -eq $define) {
            continue
        }

        if (
            $define.name -eq 'com.firstgeargames.fishnet' `
            -and $define.expression -eq '4.0.0' `
            -and $define.define -eq 'SIGNALFISH_FISHNET'
        ) {
            $pinFound = $true
        }
    }

    $constrained = @($json.defineConstraints) -contains 'SIGNALFISH_FISHNET'
    if ($asmdef -match '[\\/]Runtime[\\/]' -and -not $constrained) {
        $violations.Add("$([System.IO.Path]::GetFileName($asmdef)) : defineConstraints does not gate on SIGNALFISH_FISHNET - without the gate the FishNet.Runtime reference dangles whenever FishNet is absent.")
    }

    if (
        $asmdef -match '[\\/]Runtime[\\/]' `
        -and @($json.references) -notcontains 'FishNet.Runtime'
    ) {
        $violations.Add("$([System.IO.Path]::GetFileName($asmdef)) : references FishNet types but does not reference the FishNet.Runtime assembly.")
    }
}

if (-not $pinFound) {
    $violations.Add('no asmdef maps FishNet.Runtime to SIGNALFISH_FISHNET through versionDefines - the guard would never activate.')
}

$packageJsonPath = Join-Path $adapterRoot 'package.json'
if (-not (Test-Path -LiteralPath $packageJsonPath)) {
    $violations.Add('package.json is missing.')
}
else {
    $package = Get-Content -LiteralPath $packageJsonPath -Raw | ConvertFrom-Json
    foreach ($sample in @($package.samples)) {
        $samplePath = Join-Path $adapterRoot $sample.path
        if (-not (Test-Path -LiteralPath $samplePath)) {
            $violations.Add("package.json declares sample path '$($sample.path)' which does not exist.")
        }
    }
}

# 4. Compile check: the core alone, as Unity's floor compiles it.
if (-not $NoBuild) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) {
        $violations.Add('compile check requested but dotnet was not found on PATH.')
    }
    else {
        $stage = Join-Path ([System.IO.Path]::GetTempPath()) ("fishnetad-" + [System.Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $stage -Force | Out-Null
        try {
            $compileItems = @(
                foreach ($source in $coreFiles) {
                    $path = $source -replace '\\', '/'
                    "        <Compile Include=`"$path`" />"
                }
            )
            $projectLines = @(
                '<Project Sdk="Microsoft.NET.Sdk">',
                '    <PropertyGroup>',
                '        <TargetFramework>netstandard2.1</TargetFramework>',
                '        <LangVersion>9.0</LangVersion>',
                '        <Nullable>enable</Nullable>',
                '        <EnableDefaultCompileItems>false</EnableDefaultCompileItems>',
                '        <ImplicitUsings>disable</ImplicitUsings>',
                '        <TreatWarningsAsErrors>true</TreatWarningsAsErrors>',
                '    </PropertyGroup>',
                '    <ItemGroup>'
            ) + $compileItems + @('    </ItemGroup>', '</Project>', '')
            [System.IO.File]::WriteAllText(
                (Join-Path $stage 'FishNetAdapterCore.csproj'),
                (@($projectLines) -join "`n"),
                [System.Text.UTF8Encoding]::new($false))

            $buildOutput = & dotnet build (Join-Path $stage 'FishNetAdapterCore.csproj') -c Release --nologo -v q 2>&1
            if ($LASTEXITCODE -ne 0) {
                foreach ($line in @($buildOutput | Select-Object -Last 20)) {
                    Write-Host "    $line"
                }
                $violations.Add('the adapter core failed to compile standalone (netstandard2.1, C# 9, nullable, warnings as errors, no engine or FishNet defines).')
            }
        }
        finally {
            Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

if ($violations.Count -gt 0) {
    foreach ($violation in $violations) {
        Write-Host "lint-fishnet-adapter: $violation"
    }
    Write-Host "lint-fishnet-adapter FAILED: checked $($sourceFiles.Count) source file(s), $($violations.Count) violation(s)."
    exit 1
}
if ($VerboseOutput) {
    Write-Host "lint-fishnet-adapter passed: $($coreFiles.Count) core file(s), $($bridgeFiles.Count) bridged file(s), all pinned members present."
}
exit 0
