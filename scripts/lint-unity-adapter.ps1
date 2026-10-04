<#
.SYNOPSIS
    Keeps the Unity adapter packages in contract with the repo's rules.

.DESCRIPTION
    Each Unity adapter (unity/Adapters/<Name>) ships an engine Transport
    bridge that references an SDK no compiler in this repo can see (the
    SDK is never vendored and Unity never runs in CI), plus a pure core
    that must stay engine- and SDK-free. This lint is the red gate, run
    for every adapter (or one, with -Adapter):

      1. Compile check (skipped with -NoBuild): the adapter's Runtime/Core
         sources compile as one netstandard2.1 assembly, C# 9, nullable,
         warnings as errors, with no UNITY_* or SIGNALFISH_* defines
         - the exact independence the core promises.
      2. Guard contract: any Runtime source referencing SDK types must be
         wrapped in `#if SIGNALFISH_<NAME>` / `#endif`; without the SDK
         the package compiles to nothing. Editor sources are exempt (they
         own the define and reference no SDK types - string literals
         only), and Samples~ is exempt (consumer-compiled; its README
         documents the SDK requirement). Declaring a `namespace <SDK>` is
         vendoring the SDK and always fails.
      3. Define plumbing: the Runtime asmdef must gate its own
         compilation on the adapter's define (so an SDK assembly
         reference can never dangle when the SDK is absent), follow the
         adapter's define-ownership pin (FishNet: versionDefines keyed on
         its UPM package; Mirror: no versionDefines - Mirror ships as an
         asset, so the editor define detector owns the define), and
         package.json must parse with every declared sample path present
         on disk.
      4. Detector contract: the adapter's Editor define detector must pin
         the same define string and the same SDK probes (assembly and,
         where one exists, package name) the asmdef pins - the two
         activation mechanisms can never drift apart.
      5. Bridge completeness: the bridge file must carry every member of
         the pinned engine Transport surface (a dropped override or
         unraised callback is a runtime miss in the editor, invisible to
         every compiler here).

.PARAMETER Adapter
    One adapter name, or omit to lint every adapter.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of the scripts directory.

.PARAMETER NoBuild
    Skip the compile check (used by the self-tests; CI always runs the
    full check).

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-unity-adapter.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('FishNet', 'Mirror')]
    [string]$Adapter,
    [string]$RepoRoot,
    [switch]$NoBuild,
    [switch]$VerboseOutput
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

# The per-adapter pins. Transport member surfaces are name lists in the
# engine's own spelling; the compile check keeps the cores honest.
$adapters = @{
    FishNet = @{
        Root = 'unity/Adapters/FishNet'
        Define = 'SIGNALFISH_FISHNET'
        SdkNamespace = 'FishNet'
        SdkReference = 'FishNet.Runtime'
        PackagePin = 'com.firstgeargames.fishnet'
        PackageExpression = '4.0.0'
        BridgeFile = 'SignalFishFishNetTransport.cs'
        DetectorFile = 'SignalFishFishNetDefineDetector.cs'
        DetectorProbes = @('FishNet.Runtime', 'com.firstgeargames.fishnet')
        PinnedMembers = @(
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
    }
    Mirror = @{
        Root = 'unity/Adapters/Mirror'
        Define = 'SIGNALFISH_MIRROR'
        SdkNamespace = 'Mirror'
        SdkReference = 'Mirror'
        PackagePin = $null
        PackageExpression = $null
        BridgeFile = 'SignalFishMirrorTransport.cs'
        DetectorFile = 'SignalFishMirrorDefineDetector.cs'
        DetectorProbes = @('"Mirror"')
        PinnedMembers = @(
            'Available',
            'ClientConnected',
            'ClientConnect',
            'ClientSend',
            'ClientDisconnect',
            'ServerUri',
            'ServerActive',
            'ServerStart',
            'ServerSend',
            'ServerDisconnect',
            'ServerGetClientAddress',
            'ServerStop',
            'GetMaxPacketSize',
            'Shutdown',
            'ClientEarlyUpdate',
            'ServerEarlyUpdate',
            'ClientLateUpdate',
            'ServerLateUpdate',
            'OnClientConnected',
            'OnClientDataReceived',
            'OnClientDataSent',
            'OnClientError',
            'OnClientDisconnected',
            'OnServerConnectedWithAddress',
            'OnServerDataReceived',
            'OnServerDataSent',
            'OnServerError',
            'OnServerDisconnected'
        )
    }
}

function Get-RelativePath([string]$Base, [string]$Path) {
    $relative = [System.IO.Path]::GetFullPath($Path).Substring(
        [System.IO.Path]::GetFullPath($Base).TrimEnd('\', '/').Length + 1)
    return ($relative -replace '\\', '/')
}

function Invoke-AdapterLint {
    param(
        [hashtable]$Pin,
        [string]$Base,
        [switch]$SkipBuild
    )

    $violations = New-Object 'System.Collections.Generic.List[string]'
    $adapterRoot = Join-Path $Base $Pin.Root

    if (-not (Test-Path -LiteralPath $adapterRoot)) {
        $violations.Add("no $($Pin.Root) package - nothing to enforce.")
        return $violations
    }

    # Sources by lane. Only Runtime/ compiles behind the asmdef-scoped
    # define; Editor owns the define; Samples~ compiles in consumers.
    $sourceFiles = [string[]]@(Get-ChildItem -LiteralPath $adapterRoot -Recurse -File -Filter '*.cs' |
        ForEach-Object { $_.FullName })
    [System.Array]::Sort($sourceFiles, [System.StringComparer]::Ordinal)

    $coreFiles = [string[]]@($sourceFiles | Where-Object { $_ -match '[\\/]Runtime[\\/]Core[\\/]' })
    $runtimeSources = [string[]]@($sourceFiles | Where-Object { $_ -match '[\\/]Runtime[\\/]' })
    $bridgeFiles = [string[]]@($runtimeSources | Where-Object {
        [System.IO.Path]::GetFileName($_) -eq $Pin.BridgeFile
    })

    if ($coreFiles.Count -eq 0) {
        $violations.Add('the adapter has no Runtime/Core sources - the CI-compiled core is the package.')
    }

    # 1. Guard contract and vendoring. The SDK reference spellings differ
    #    by SDK surface (FishNet is referenced qualified; Mirror mostly
    #    through `using Mirror;`), so each pin carries its own pattern.
    $referencePattern =
        if ($Pin.SdkNamespace -eq 'FishNet') { '(^|[^\w.])FishNet\.' }
        else { '(^|[^\w.])Mirror(\.[A-Za-z_]|;)' }
    $namespacePattern = "namespace\s+$($Pin.SdkNamespace)\b"

    foreach ($source in $sourceFiles) {
        $relative = Get-RelativePath -Base $Base -Path $source
        $text = [System.IO.File]::ReadAllText($source)

        if ($text -match $namespacePattern) {
            $violations.Add(
                "$relative : declares ``namespace $($Pin.SdkNamespace)`` - vendoring the SDK is forbidden; reference it behind $($Pin.Define) instead.")
        }

        if ($source -notmatch '[\\/](Runtime|Editor|Samples~)[\\/]') {
            continue
        }

        $inRuntime = $source -match '[\\/]Runtime[\\/]'
        if ($inRuntime -and $text -match $referencePattern) {
            if ($text -notmatch "#if\s+$($Pin.Define)" -or $text -notmatch '#endif') {
                $violations.Add(
                    "$relative : references $($Pin.SdkNamespace) types but is not wrapped in ``#if $($Pin.Define)`` / ``#endif`` - without the SDK the package must compile to nothing.")
            }
        }

        if ($inRuntime -and ($coreFiles -contains $source)) {
            if ($text -match 'UnityEngine|SIGNALFISH_') {
                $violations.Add(
                    "$relative : core sources must stay engine- and SDK-free (no UnityEngine, no SIGNALFISH_* references).")
            }
        }
    }

    # 2. Bridge completeness against the pinned Transport surface.
    if ($bridgeFiles.Count -eq 0) {
        $violations.Add("the bridge file $($Pin.BridgeFile) is missing - the pinned Transport surface has no carrier.")
    }
    else {
        $bridgeText = [System.IO.File]::ReadAllText($bridgeFiles[0])
        foreach ($member in $Pin.PinnedMembers) {
            if ($bridgeText -notmatch [regex]::Escape($member)) {
                $violations.Add(
                    "$($Pin.BridgeFile) : the pinned $($Pin.SdkNamespace) Transport member '$member' is missing - a dropped override or unraised callback surfaces as a runtime miss in the editor only.")
            }
        }
    }

    # 3. Define plumbing: the Runtime asmdef gates on the define and
    #    follows the adapter's define-ownership pin.
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
                $define.name -eq $Pin.PackagePin `
                -and $define.expression -eq $Pin.PackageExpression `
                -and $define.define -eq $Pin.Define
            ) {
                $pinFound = $true
            }
        }

        if ($asmdef -match '[\\/]Runtime[\\/]') {
            $constrained = @($json.defineConstraints) -contains $Pin.Define
            if (-not $constrained) {
                $violations.Add(
                    "$([System.IO.Path]::GetFileName($asmdef)) : defineConstraints does not gate on $($Pin.Define) - without the gate the $($Pin.SdkReference) reference dangles whenever the SDK is absent.")
            }

            if (@($json.references) -notcontains $Pin.SdkReference) {
                $violations.Add(
                    "$([System.IO.Path]::GetFileName($asmdef)) : references $($Pin.SdkNamespace) types but does not reference the $($Pin.SdkReference) assembly.")
            }

            if ($null -eq $Pin.PackagePin -and @($json.versionDefines).Count -gt 0) {
                $violations.Add(
                    "$([System.IO.Path]::GetFileName($asmdef)) : carries versionDefines, but $($Pin.SdkNamespace) ships as an asset with no UPM package - the editor define detector owns $($Pin.Define), and a package pin would be a lie.")
            }
        }
    }

    if ($null -ne $Pin.PackagePin -and -not $pinFound) {
        $violations.Add(
            "no asmdef maps $($Pin.PackagePin) to $($Pin.Define) through versionDefines - the guard would never activate.")
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

    # 4. Detector contract: the editor detector pins the same define and
    #    the same SDK probes the asmdef pins, and stays editor-only.
    $detectorPath = Join-Path (Join-Path $adapterRoot 'Editor') $Pin.DetectorFile
    if (-not (Test-Path -LiteralPath $detectorPath)) {
        $violations.Add("Editor/$($Pin.DetectorFile) is missing - without a detector the define has no owner.")
    }
    else {
        $detectorText = [System.IO.File]::ReadAllText($detectorPath)
        if ($detectorText -notmatch [regex]::Escape($Pin.Define)) {
            $violations.Add("Editor/$($Pin.DetectorFile) does not pin $($Pin.Define) - the two activation mechanisms must share one define string.")
        }

        foreach ($probe in $Pin.DetectorProbes) {
            if ($detectorText -notmatch [regex]::Escape($probe)) {
                $violations.Add("Editor/$($Pin.DetectorFile) does not probe '$probe' - the detector and the asmdef must probe the same SDK markers.")
            }
        }
    }

    $editorAsmdefs = [string[]]@($asmdefPaths | Where-Object { $_ -match '[\\/]Editor[\\/]' })
    $editorGated = $false
    foreach ($asmdef in $editorAsmdefs) {
        $json = Get-Content -LiteralPath $asmdef -Raw | ConvertFrom-Json
        if (@($json.includePlatforms) -contains 'Editor') {
            $editorGated = $true
        }
    }

    if (-not $editorGated) {
        $violations.Add('no Editor asmdef is gated to the Editor platform - the detector would break player builds.')
    }

    # 5. Compile check: the core alone, as Unity's floor compiles it.
    if (-not $SkipBuild) {
        $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($null -eq $dotnet) {
            $violations.Add('compile check requested but dotnet was not found on PATH.')
        }
        else {
            $stage = Join-Path ([System.IO.Path]::GetTempPath()) (
                "unityadapter-" + [System.Guid]::NewGuid().ToString('N'))
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
                    (Join-Path $stage 'AdapterCore.csproj'),
                    (@($projectLines) -join "`n"),
                    [System.Text.UTF8Encoding]::new($false))

                $buildOutput = & dotnet build (Join-Path $stage 'AdapterCore.csproj') -c Release --nologo -v q 2>&1
                if ($LASTEXITCODE -ne 0) {
                    foreach ($line in @($buildOutput | Select-Object -Last 20)) {
                        Write-Host "    $line"
                    }
                    $violations.Add(
                        'the adapter core failed to compile standalone (netstandard2.1, C# 9, nullable, warnings as errors, no engine or adapter defines).')
                }
            }
            finally {
                Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    return $violations
}

$names = if ($Adapter) { @($Adapter) } else { @($adapters.Keys | Sort-Object) }
$allViolations = New-Object 'System.Collections.Generic.List[string]'
$checked = 0
$coreCount = 0
$bridgeCount = 0

foreach ($name in $names) {
    $pin = $adapters[$name]
    $violations = Invoke-AdapterLint -Pin $pin -Base $RepoRoot -SkipBuild:$NoBuild
    foreach ($violation in $violations) {
        $allViolations.Add("$name : $violation")
    }

    $adapterRoot = Join-Path $RepoRoot $pin.Root
    if (Test-Path -LiteralPath $adapterRoot) {
        $checked += [int](Get-ChildItem -LiteralPath $adapterRoot -Recurse -File -Filter '*.cs' |
            Measure-Object).Count
        $coreCount += [int](Get-ChildItem -LiteralPath (Join-Path $adapterRoot 'Runtime/Core') `
            -Recurse -File -Filter '*.cs' -ErrorAction SilentlyContinue |
            Measure-Object).Count
        $bridgeCount += [int](@(
            Get-ChildItem -LiteralPath (Join-Path $adapterRoot 'Runtime') -Recurse -File -Filter $pin.BridgeFile `
                -ErrorAction SilentlyContinue) | Measure-Object).Count
    }
}

if ($allViolations.Count -gt 0) {
    foreach ($violation in $allViolations) {
        Write-Host "lint-unity-adapter: $violation"
    }
    Write-Host "lint-unity-adapter FAILED: checked $checked source file(s), $($allViolations.Count) violation(s)."
    exit 1
}
if ($VerboseOutput) {
    Write-Host "lint-unity-adapter passed: $coreCount core file(s), $bridgeCount bridge file(s), all pinned members present."
}
exit 0
