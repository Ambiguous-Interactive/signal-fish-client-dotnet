<#
.SYNOPSIS
    Keeps the Unity adapter packages in contract with the repo's rules.

.DESCRIPTION
    The shared adapter core (unity/Adapters/Core) and each Unity adapter
    (unity/Adapters/<Name>) ship sources an engine SDK no compiler in
    this repo can see (the SDK is never vendored and Unity never runs in
    CI), plus pure cores that must stay engine- and SDK-free. This lint
    is the red gate, run for every package (or one, with -Adapter):

    Shared core lane (-Adapter Core):

      1. Compile check (skipped with -NoBuild): the core's Runtime
         sources compile as one netstandard2.1 assembly, C# 9, nullable,
         warnings as errors, with no UNITY_* or SIGNALFISH_* defines
         - the exact independence the core promises.
      2. Package honesty: the core's Runtime asmdef must stay
         unconditional (no defineConstraints, no versionDefines) and
         engine-free (noEngineReferences), because the core compiles
         everywhere, define or not.
      3. Duplication pin: no adapter may declare the shared types
         (AdapterWire, AdapterMtu, AdapterFrameRoute,
         SignalFishPeerRouter, SignalFishReceiveRules) - they live in
         exactly one place, so a routing-rule or header fix is one edit.

    Per-adapter lane:

      4. Guard contract: any Runtime source referencing SDK types must be
         wrapped in `#if SIGNALFISH_<NAME>` / `#endif`; without the SDK
         the package compiles to nothing. Editor sources are exempt (they
         own the define and reference no SDK types - string literals
         only), and Samples~ is exempt (consumer-compiled; its README
         documents the SDK requirement). Declaring a `namespace <SDK>` is
         vendoring the SDK and always fails.
      5. Define plumbing: the Runtime asmdef must gate its own
         compilation on the adapter's define (so an SDK assembly
         reference can never dangle when the SDK is absent), reference
         the shared core assembly, follow the adapter's define-ownership
         pin (FishNet: versionDefines keyed on its UPM package; Mirror:
         no versionDefines - Mirror ships as an asset, so the editor
         define detector owns the define), and package.json must parse
         with every declared sample path present on disk.
      6. Detector contract: the adapter's Editor define detector must pin
         the same define string and the same SDK probes (assembly and,
         where one exists, package name) the asmdef pins - the two
         activation mechanisms can never drift apart.
      7. Bridge completeness: the bridge file must carry every member of
         the pinned engine surface, route frames through the package's
         pinned shared-wire reference (AdapterWire for a transport
         bridge, the shared approval payload for the NGO coordinator),
         and, where the engine has channel ids, restate the pinned
         channel bytes - a dropped override, a local header or payload
         copy, or a renumbered engine channel is a runtime miss in the
         editor, invisible to every compiler here.

.PARAMETER Adapter
    One package name (Core, FishNet, Mirror), or omit to lint all.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of the scripts directory.

.PARAMETER NoBuild
    Skip the compile checks (used by the self-tests; CI always runs the
    full check).

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-unity-adapter.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Core', 'FishNet', 'Mirror', 'Ngo')]
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

# The per-package pins. Engine member surfaces are name lists in the
# engine's own spelling; the compile checks keep the cores honest. The
# wire pin names the shared-core type the bridge must route frames (or
# approval payloads) through. The channel pin restates the engine's
# delivery ids at the pinned engine version - the shared header's
# channel bytes double as engine channel ids, so a renumber must be
# re-verified against the engine source; a coordinator with no engine
# channel mapping (Ngo) carries none.
$sharedCore = @{
    Root = 'unity/Adapters/Core'
    SharedTypes = @(
        'AdapterWire',
        'AdapterMtu',
        'AdapterFrameRoute',
        'SignalFishPeerRouter',
        'SignalFishReceiveRules'
    )
}
$adapters = @{
    FishNet = @{
        Root = 'unity/Adapters/FishNet'
        Define = 'SIGNALFISH_FISHNET'
        SdkNamespace = 'FishNet'
        SdkReferencePattern = '(^|[^\w.])FishNet\.'
        SdkReference = 'FishNet.Runtime'
        CoreReference = 'SignalFish.Adapters.Core'
        CorePackage = 'com.ambiguous-interactive.signalfish.adapters.core'
        WirePin = 'AdapterWire.'
        ChannelPin = 'Channel.Reliable = 0, Channel.Unreliable = 1'
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
        SdkReferencePattern = '(^|[^\w.])Mirror(\.[A-Za-z_]|;)'
        SdkReference = 'Mirror'
        CoreReference = 'SignalFish.Adapters.Core'
        CorePackage = 'com.ambiguous-interactive.signalfish.adapters.core'
        WirePin = 'AdapterWire.'
        ChannelPin = 'Channels.Reliable = 0, Channels.Unreliable = 1'
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
            'OnClientTransportException',
            'OnServerConnectedWithAddress',
            'OnServerDataReceived',
            'OnServerDataSent',
            'OnServerError',
            'OnServerDisconnected'
        )
    }
    Ngo = @{
        Root = 'unity/Adapters/Ngo'
        Define = 'SIGNALFISH_NGO'
        SdkNamespace = 'Unity.Netcode'
        SdkReferencePattern = '(^|[^\w.])Unity\.Netcode(\.[A-Za-z_]|;)'
        SdkReference = 'Unity.Netcode.Runtime'
        CoreReference = 'SignalFish.Adapters.Core'
        CorePackage = 'com.ambiguous-interactive.signalfish.adapters.core'

        # The coordinator is not a transport bridge: it never frames
        # engine payloads, so there is no engine channel pin. Its wire
        # surface is the shared-core approval payload (the relay's
        # RFC-4122 UUID spelling), which the bridge must go through
        # rather than hand-rolling.
        WirePin = 'ConnectionApprovalPayload.'
        ChannelPin = $null
        PackagePin = 'com.unity.netcode.gameobjects'
        PackageExpression = '1.2.0'
        BridgeFile = 'SignalFishRoomCoordinator.cs'
        DetectorFile = 'SignalFishNgoDefineDetector.cs'
        DetectorProbes = @('Unity.Netcode.Runtime', 'com.unity.netcode.gameobjects')
        PinnedMembers = @(
            'Singleton',
            'ConnectionApprovalCallback',
            'ConnectionApprovalRequest',
            'ConnectionApprovalResponse',
            'StartHost',
            'StartClient',
            'Shutdown',
            'IsServer',
            'IsClient',
            'NetworkConfig',
            'ConnectionData',
            'Payload',
            'Approved',
            'Reason',
            'CreatePlayerObject'
        )
    }
}

function Get-RelativePath([string]$Base, [string]$Path) {
    $relative = [System.IO.Path]::GetFullPath($Path).Substring(
        [System.IO.Path]::GetFullPath($Base).TrimEnd('\', '/').Length + 1)
    return ($relative -replace '\\', '/')
}

function Get-JsonArray {
    param([object]$Json, [string]$Property)

    # A malformed asmdef (missing key) must fail the checks, not crash
    # the lint with a strict-mode property error. Enumerate (the
    # Properties indexer resolves to different overloads for a variable
    # key and returns the raw value, not the member). The local must not
    # re-case the parameter: PowerShell variables are case-insensitive,
    # and the collision corrupts the pipeline below.
    $member = @($Json.PSObject.Properties | Where-Object { $_.Name -eq $Property })
    if ($member.Count -eq 0) {
        return @()
    }

    return @($member[0].Value | Where-Object { $null -ne $_ })
}

function Get-JsonValue {
    param([object]$Json, [string]$Property)

    $member = @($Json.PSObject.Properties | Where-Object { $_.Name -eq $Property })
    if ($member.Count -eq 0) {
        return $null
    }

    return $member[0].Value
}

function Get-UnguardedLines {
    param([string]$Path, [string]$Define, [string]$ReferencePattern)

    # Lines outside every `#if <define>` region that reference the SDK.
    # #else flips the innermost region; #elif flips and re-evaluates. A
    # NEGATED condition (`#if !<define>`) compiles exactly when the SDK
    # is absent, so it is an unguarded region, not a guarded one.
    $unguarded = New-Object 'System.Collections.Generic.List[object]'
    $regions = New-Object 'System.Collections.Generic.List[bool]'
    $negationPattern = "!\s*[()]*\s*$( [regex]::Escape($Define) )\b"
    $lines = [System.IO.File]::ReadAllLines($Path)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i].TrimStart()
        if ($line -match '^#\s*if\s+(.+)$') {
            $condition = $Matches[1]
            $regions.Add(
                (($condition -match [regex]::Escape($Define)) -and
                ($condition -notmatch $negationPattern)))
            continue
        }

        if ($line -match '^#\s*if\b') {
            $regions.Add($false)
            continue
        }

        if ($line -match '^#\s*elif\s+(.+)$') {
            if ($regions.Count -gt 0) {
                $condition = $Matches[1]
                $regions[$regions.Count - 1] =
                    (($condition -match [regex]::Escape($Define)) -and
                    ($condition -notmatch $negationPattern))
            }

            continue
        }

        if ($line -match '^#\s*else\b') {
            if ($regions.Count -gt 0) {
                $regions[$regions.Count - 1] = -not $regions[$regions.Count - 1]
            }

            continue
        }

        if ($line -match '^#\s*endif\b') {
            if ($regions.Count -gt 0) {
                $regions.RemoveAt($regions.Count - 1)
            }

            continue
        }

        if ($line -match '^#') {
            continue
        }

        if (($regions -contains $true) -or $line -notmatch $ReferencePattern) {
            continue
        }

        $unguarded.Add([pscustomobject]@{ Line = ($i + 1); Text = $line })
    }

    return $unguarded
}

function Invoke-CoreCompile {
    param(
        [string[]]$SourcePaths,
        [string]$Base,
        [string]$Label
    )

    # Compiles pure-core sources as one assembly at Unity's floor: no
    # engine, no SDK, no defines. Returns the failure message, if any.
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) {
        return 'compile check requested but dotnet was not found on PATH.'
    }

    $stage = Join-Path ([System.IO.Path]::GetTempPath()) (
        "unityadapter-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    try {
        $compileItems = @(
            foreach ($source in $SourcePaths) {
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
            return "$Label failed to compile standalone (netstandard2.1, C# 9, nullable, warnings as errors, no engine or adapter defines)."
        }

        return $null
    }
    finally {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-CorePackageLint {
    param(
        [hashtable]$Pin,
        [string]$Base,
        [hashtable[]]$AdapterPins,
        [switch]$SkipBuild
    )

    $violations = New-Object 'System.Collections.Generic.List[string]'
    $coreRoot = Join-Path $Base $Pin.Root

    if (-not (Test-Path -LiteralPath $coreRoot)) {
        $violations.Add("no $($Pin.Root) package - the shared core has no home.")
        return $violations
    }

    $coreFiles = [string[]]@(Get-ChildItem -LiteralPath $coreRoot -Recurse -File -Filter '*.cs' |
        ForEach-Object { $_.FullName })
    [System.Array]::Sort($coreFiles, [System.StringComparer]::Ordinal)

    if ($coreFiles.Count -eq 0) {
        $violations.Add('the shared core has no Runtime sources - the package would ship nothing.')
    }

    # The core compiles everywhere, define or not, engine or not: the
    # sources stay clean and the asmdef stays unconditional.
    foreach ($source in $coreFiles) {
        $relative = Get-RelativePath -Base $Base -Path $source
        $text = [System.IO.File]::ReadAllText($source)
        if ($text -match 'UnityEngine|SIGNALFISH_') {
            $violations.Add(
                "$relative : core sources must stay engine- and SDK-free (no UnityEngine, no SIGNALFISH_* references).")
        }
    }

    $asmdefPaths = [string[]]@(Get-ChildItem -LiteralPath $coreRoot -Recurse -File -Filter '*.asmdef' |
        ForEach-Object { $_.FullName })
    if ($asmdefPaths.Count -eq 0) {
        $violations.Add('the shared core has no asmdef - the adapters have no assembly to reference.')
    }

    foreach ($asmdef in $asmdefPaths) {
        $json = Get-Content -LiteralPath $asmdef -Raw | ConvertFrom-Json
        $name = [System.IO.Path]::GetFileName($asmdef)
        if ((Get-JsonValue -Json $json -Property 'noEngineReferences') -ne $true) {
            $violations.Add(
                "$name : noEngineReferences is not true - the core must stay engine-free at the Unity level, not just by review.")
        }

        if (@(Get-JsonArray -Json $json -Property 'defineConstraints').Count -gt 0) {
            $violations.Add(
                "$name : carries defineConstraints - the core compiles everywhere, define or not.")
        }

        if (@(Get-JsonArray -Json $json -Property 'versionDefines').Count -gt 0) {
            $violations.Add(
                "$name : carries versionDefines - the core has no SDK to version against.")
        }
    }

    # Duplication pin: the shared types live in exactly one place. An
    # adapter declaring one is a fork of the wire, router, or rules.
    $declarationPattern = '(class|struct|record|interface|enum)\s+(' + ($Pin.SharedTypes -join '|') + ')\b'
    foreach ($adapterPin in $AdapterPins) {
        $adapterRoot = Join-Path $Base $adapterPin.Root
        if (-not (Test-Path -LiteralPath $adapterRoot)) {
            continue
        }

        $adapterFiles = @(Get-ChildItem -LiteralPath $adapterRoot -Recurse -File -Filter '*.cs')
        foreach ($file in $adapterFiles) {
            $text = [System.IO.File]::ReadAllText($file.FullName)
            if ($text -match $declarationPattern) {
                $violations.Add(
                    "$(Get-RelativePath -Base $Base -Path $file.FullName) : declares a shared core type ($($Matches[2])) - the shared types live only in $($Pin.Root); fork one and a header or routing fix needs N synchronized edits again.")
            }
        }
    }

    $packageJsonPath = Join-Path $coreRoot 'package.json'
    if (-not (Test-Path -LiteralPath $packageJsonPath)) {
        $violations.Add('package.json is missing.')
    }
    else {
        $package = Get-Content -LiteralPath $packageJsonPath -Raw | ConvertFrom-Json
        foreach ($sample in @(Get-JsonArray -Json $package -Property 'samples')) {
            $samplePath = Join-Path $coreRoot $sample.path
            if (-not (Test-Path -LiteralPath $samplePath)) {
                $violations.Add("package.json declares sample path '$($sample.path)' which does not exist.")
            }
        }
    }

    if (-not $SkipBuild -and $coreFiles.Count -gt 0) {
        $failure = Invoke-CoreCompile -SourcePaths $coreFiles -Base $Base -Label 'the shared adapter core'
        if ($null -ne $failure) {
            $violations.Add($failure)
        }
    }

    return $violations
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

    # 1. Guard contract and vendoring. The SDK reference spellings differ
    #    by SDK surface (FishNet is referenced qualified; Mirror mostly
    #    through `using Mirror;`), so each pin carries its own pattern.
    #    The guard check is per-line and region-aware: only text outside
    #    `#if <define>` regions can violate, so a file guarded somewhere
    #    but not everywhere still fails.
    $referencePattern = $Pin.SdkReferencePattern
    $namespacePattern = "namespace\s+$([regex]::Escape($Pin.SdkNamespace))\b"

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

        if ($source -match '[\\/]Runtime[\\/]') {
            $unguarded = @(Get-UnguardedLines -Path $source -Define $Pin.Define `
                -ReferencePattern $referencePattern)
            foreach ($entry in $unguarded) {
                $violations.Add(
                    "$relative`:$($entry.Line) : references $($Pin.SdkNamespace) types outside ``#if $($Pin.Define)`` - without the SDK the package must compile to nothing.")
            }

            if ($coreFiles -contains $source) {
                if ($text -match 'UnityEngine|SIGNALFISH_') {
                    $violations.Add(
                        "$relative : core sources must stay engine- and SDK-free (no UnityEngine, no SIGNALFISH_* references).")
                }
            }
        }
    }

    # 2. Bridge completeness against the pinned Transport surface, the
    #    shared header, and the engine channel pin.
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

        if ($bridgeText -notmatch [regex]::Escape($Pin.WirePin)) {
            $violations.Add(
                "$($Pin.BridgeFile) : does not route frames through the shared wire pin '$($Pin.WirePin)' - a local header or payload copy forks the adapter wire format.")
        }

        if ($null -ne $Pin.ChannelPin -and $bridgeText -notmatch [regex]::Escape($Pin.ChannelPin)) {
            $violations.Add(
                "$($Pin.BridgeFile) : does not restate the engine channel pin '$($Pin.ChannelPin)' - the shared header's channel bytes double as engine channel ids, so a renumber must be re-verified against the pinned engine source.")
        }
    }

    # 3. Define plumbing: the Runtime asmdef gates on the define and
    #    follows the adapter's define-ownership pin.
    $asmdefPaths = [string[]]@(Get-ChildItem -LiteralPath $adapterRoot -Recurse -File -Filter '*.asmdef' |
        ForEach-Object { $_.FullName })
    $pinFound = $false
    foreach ($asmdef in $asmdefPaths) {
        $json = Get-Content -LiteralPath $asmdef -Raw | ConvertFrom-Json
        foreach ($define in @(Get-JsonArray -Json $json -Property 'versionDefines')) {
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
            $constrained = @(Get-JsonArray -Json $json -Property 'defineConstraints') -contains $Pin.Define
            if (-not $constrained) {
                $violations.Add(
                    "$([System.IO.Path]::GetFileName($asmdef)) : defineConstraints does not gate on $($Pin.Define) - without the gate the $($Pin.SdkReference) reference dangles whenever the SDK is absent.")
            }

            if (@(Get-JsonArray -Json $json -Property 'references') -notcontains $Pin.SdkReference) {
                $violations.Add(
                    "$([System.IO.Path]::GetFileName($asmdef)) : references $($Pin.SdkNamespace) types but does not reference the $($Pin.SdkReference) assembly.")
            }

            if (@(Get-JsonArray -Json $json -Property 'references') -notcontains $Pin.CoreReference) {
                $violations.Add(
                    "$([System.IO.Path]::GetFileName($asmdef)) : does not reference $($Pin.CoreReference) - the shared core is the adapter's wire, router, and receive rules.")
            }

            if ($null -eq $Pin.PackagePin -and @(Get-JsonArray -Json $json -Property 'versionDefines').Count -gt 0) {
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
        foreach ($sample in @(Get-JsonArray -Json $package -Property 'samples')) {
            $samplePath = Join-Path $adapterRoot $sample.path
            if (-not (Test-Path -LiteralPath $samplePath)) {
                $violations.Add("package.json declares sample path '$($sample.path)' which does not exist.")
            }
        }

        # The asmdef may reference the core assembly, but consumers get
        # packages through package.json dependencies — both must pin it.
        $dependencies = Get-JsonValue -Json $package -Property 'dependencies'
        $dependencyNames = @()
        if ($null -ne $dependencies) {
            $dependencyNames = @($dependencies.PSObject.Properties | ForEach-Object { $_.Name })
        }

        if ($dependencyNames -notcontains $Pin.CorePackage) {
            $violations.Add(
                "package.json : does not depend on $($Pin.CorePackage) - the asmdef's core reference dangles for consumers without the package.")
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
        if (@(Get-JsonArray -Json $json -Property 'includePlatforms') -contains 'Editor') {
            $editorGated = $true
        }
    }

    if (-not $editorGated) {
        $violations.Add('no Editor asmdef is gated to the Editor platform - the detector would break player builds.')
    }

    # 5. Compile check: any adapter-local core compiles with the shared
    #    core, as Unity's floor compiles it. An adapter whose whole
    #    Runtime is SDK-gated (Mirror) has no local core to compile; the
    #    shared core has its own lane.
    if (-not $SkipBuild -and $coreFiles.Count -gt 0) {
        $sharedRoot = Join-Path $RepoRoot $sharedCore.Root
        $sharedFiles = [string[]]@(Get-ChildItem -LiteralPath $sharedRoot -Recurse -File -Filter '*.cs' |
            ForEach-Object { $_.FullName })
        $failure = Invoke-CoreCompile `
            -SourcePaths (@($sharedFiles) + @($coreFiles)) `
            -Base $RepoRoot `
            -Label "the $($Pin.Root) core"
        if ($null -ne $failure) {
            $violations.Add($failure)
        }
    }

    return $violations
}

$names = if ($Adapter) { @($Adapter) } else { @('Core') + @($adapters.Keys | Sort-Object) }
$allViolations = New-Object 'System.Collections.Generic.List[string]'
$checked = 0
$coreCount = 0
$bridgeCount = 0

foreach ($name in $names) {
    if ($name -eq 'Core') {
        $violations = Invoke-CorePackageLint -Pin $sharedCore -Base $RepoRoot `
            -AdapterPins @($adapters.Values) -SkipBuild:$NoBuild
        $packageRoot = Join-Path $RepoRoot $sharedCore.Root
        if (Test-Path -LiteralPath $packageRoot) {
            $coreFileCount = [int](Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter '*.cs' |
                Measure-Object).Count
            $checked += $coreFileCount
            $coreCount += $coreFileCount
        }
    }
    else {
        $pin = $adapters[$name]
        $violations = Invoke-AdapterLint -Pin $pin -Base $RepoRoot -SkipBuild:$NoBuild
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

    foreach ($violation in $violations) {
        $allViolations.Add("$name : $violation")
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
