<#
.SYNOPSIS
    Self-tests for scripts/lint-unity-adapter.ps1 (the Unity adapter
    contracts).
#>
. (Join-Path $PSScriptRoot 'test-common.ps1')

$lint = Join-Path (Split-Path -Parent $PSScriptRoot) 'lint-unity-adapter.ps1'
$repo = New-TestRepo
try {
    Write-Host 'test-lint-unity-adapter'

    # --- Fixture content ------------------------------------------------
    $core = @{
        Root = 'unity/Adapters/Core'
        PackageJson = @(
            '{',
            '    "name": "com.test.adapters.core",',
            '    "samples": []',
            '}'
        )
        AsmdefJson = @(
            '{',
            '    "name": "SignalFish.Adapters.Core",',
            '    "references": [],',
            '    "defineConstraints": [],',
            '    "versionDefines": [],',
            '    "noEngineReferences": true',
            '}'
        )
        CoreCs = @(
            '#nullable enable',
            'namespace SignalFish.Client.Adapters',
            '{',
            '    public static class AdapterWire',
            '    {',
            '        public const int HeaderLength = 18;',
            '    }',
            '}'
        )
    }

    $fishnet = @{
        Root = 'unity/Adapters/FishNet'
        Define = 'SIGNALFISH_FISHNET'
        LocalCoreCs = @(
            '#nullable enable',
            'namespace SignalFish.Client.Adapters.FishNet',
            '{',
            '    public readonly struct HostLoopbackFrame',
            '    {',
            '        public byte Channel { get; }',
            '        public HostLoopbackFrame(byte channel) => Channel = channel;',
            '    }',
            '}'
        )
        ChannelPin = 'Channel.Reliable = 0, Channel.Unreliable = 1'
        BridgeCs = @(
            '#if SIGNALFISH_FISHNET',
            'namespace SignalFish.Client.Adapters.FishNet',
            '{',
            '    public sealed class Bridge : global::FishNet.Transporting.Transport',
            '    {',
            '        // Channel.Reliable = 0, Channel.Unreliable = 1 (FishNet 4.0.0; pinned by lint-unity-adapter).',
            '        public int HeaderBytes => global::SignalFish.Client.Adapters.AdapterWire.HeaderLength;',
            '        public override int GetMTU(byte channel) => HeaderBytes;',
            '        public override void SendToServer(byte channel, ArraySegment<byte> segment) { }',
            '        public override void SendToClient(byte channel, ArraySegment<byte> segment, int connectionId) { }',
            '        public override void IterateIncoming(bool asServer) { }',
            '        public override void IterateOutgoing(bool asServer) { }',
            '        public override bool StartConnection(bool server) => true;',
            '        public override bool StopConnection(bool server) => true;',
            '        public override bool StopConnection(int connectionId, bool immediately) => true;',
            '        public override void Shutdown() { }',
            '        public override string GetConnectionAddress(int connectionId) => string.Empty;',
            '        public override LocalConnectionState GetConnectionState(bool server) => default;',
            '        public override RemoteConnectionState GetConnectionState(int connectionId) => default;',
            '        public override bool IsLocalTransport(int connectionId) => true;',
            '        public override void HandleClientConnectionState(ClientConnectionStateArgs args) { }',
            '        public override void HandleServerConnectionState(ServerConnectionStateArgs args) { }',
            '        public override void HandleRemoteConnectionState(RemoteConnectionStateArgs args) { }',
            '        public override void HandleClientReceivedDataArgs(ClientReceivedDataArgs args) { }',
            '        public override void HandleServerReceivedDataArgs(ServerReceivedDataArgs args) { }',
            '        public override event System.Action<ClientConnectionStateArgs> OnClientConnectionState;',
            '        public override event System.Action<ServerConnectionStateArgs> OnServerConnectionState;',
            '        public override event System.Action<RemoteConnectionStateArgs> OnRemoteConnectionState;',
            '        public override event System.Action<ClientReceivedDataArgs> OnClientReceivedData;',
            '        public override event System.Action<ServerReceivedDataArgs> OnServerReceivedData;',
            '    }',
            '}',
            '#endif'
        )
        AsmdefJson = @(
            '{',
            '    "name": "SignalFish.Transport.FishNet",',
            '    "references": ["SignalFish.Client", "SignalFish.Adapters.Core", "FishNet.Runtime"],',
            '    "defineConstraints": ["SIGNALFISH_FISHNET"],',
            '    "versionDefines": [',
            '        { "name": "com.firstgeargames.fishnet", "expression": "4.0.0", "define": "SIGNALFISH_FISHNET" }',
            '    ],',
            '    "noEngineReferences": false',
            '}'
        )
        DetectorCs = @(
            'namespace SignalFish.Client.Adapters.FishNet.Editor',
            '{',
            '    internal static class SignalFishFishNetDefineDetector',
            '    {',
            '        private const string Define = "SIGNALFISH_FISHNET";',
            '        private const string Assembly = "FishNet.Runtime";',
            '        private const string Package = "com.firstgeargames.fishnet";',
            '    }',
            '}'
        )
        DetectorFile = 'SignalFishFishNetDefineDetector.cs'
        BridgeFile = 'SignalFishFishNetTransport.cs'
    }

    $mirror = @{
        Root = 'unity/Adapters/Mirror'
        Define = 'SIGNALFISH_MIRROR'
        LocalCoreCs = $null
        ChannelPin = 'Channels.Reliable = 0, Channels.Unreliable = 1'
        BridgeCs = @(
            '#if SIGNALFISH_MIRROR',
            'namespace SignalFish.Client.Adapters.Mirror',
            '{',
            '    public sealed class Bridge : global::Mirror.Transport',
            '    {',
            '        // Channels.Reliable = 0, Channels.Unreliable = 1 (Mirror v96.9.23; pinned by lint-unity-adapter).',
            '        public int HeaderBytes => global::SignalFish.Client.Adapters.AdapterWire.HeaderLength;',
            '        public override bool Available() => true;',
            '        public override bool ClientConnected() => true;',
            '        public override void ClientConnect(string address) { }',
            '        public override void ClientSend(ArraySegment<byte> segment, int channelId) { }',
            '        public override void ClientDisconnect() { }',
            '        public override System.Uri ServerUri() => default!;',
            '        public override bool ServerActive() => true;',
            '        public override void ServerStart() { }',
            '        public override void ServerSend(int connectionId, ArraySegment<byte> segment, int channelId) { }',
            '        public override void ServerDisconnect(int connectionId) { }',
            '        public override string ServerGetClientAddress(int connectionId) => string.Empty;',
            '        public override void ServerStop() { }',
            '        public override int GetMaxPacketSize(int channelId) => 0;',
            '        public override void Shutdown() { }',
            '        public override void ClientEarlyUpdate() { }',
            '        public override void ServerEarlyUpdate() { }',
            '        public override void ClientLateUpdate() { }',
            '        public override void ServerLateUpdate() { }',
            '        public System.Action OnClientConnected = null!;',
            '        public System.Action<ArraySegment<byte>, int> OnClientDataReceived = null!;',
            '        public System.Action<ArraySegment<byte>, int> OnClientDataSent = null!;',
            '        public System.Action<int, string> OnClientError = null!;',
            '        public System.Action OnClientDisconnected = null!;',
            '        public System.Action<Exception> OnClientTransportException = null!;',
            '        public System.Action<int, string> OnServerConnectedWithAddress = null!;',
            '        public System.Action<int, ArraySegment<byte>, int> OnServerDataReceived = null!;',
            '        public System.Action<int, ArraySegment<byte>, int> OnServerDataSent = null!;',
            '        public System.Action<int, string> OnServerError = null!;',
            '        public System.Action<int> OnServerDisconnected = null!;',
            '    }',
            '}',
            '#endif'
        )
        AsmdefJson = @(
            '{',
            '    "name": "SignalFish.Transport.Mirror",',
            '    "references": ["SignalFish.Client", "SignalFish.Adapters.Core", "Mirror"],',
            '    "defineConstraints": ["SIGNALFISH_MIRROR"],',
            '    "versionDefines": [],',
            '    "noEngineReferences": false',
            '}'
        )
        DetectorCs = @(
            'namespace SignalFish.Client.Adapters.Mirror.Editor',
            '{',
            '    internal static class SignalFishMirrorDefineDetector',
            '    {',
            '        private const string Define = "SIGNALFISH_MIRROR";',
            '        private const string AssemblyName = "Mirror";',
            '    }',
            '}'
        )
        DetectorFile = 'SignalFishMirrorDefineDetector.cs'
        BridgeFile = 'SignalFishMirrorTransport.cs'
    }

    $editorAsmdefJson = @(
        '{',
        '    "name": "Adapter.Editor",',
        '    "includePlatforms": ["Editor"],',
        '    "defineConstraints": [],',
        '    "versionDefines": [],',
        '    "noEngineReferences": true',
        '}'
    )
    $sampleCs = @(
        'namespace Samples',
        '{',
        '    public class Sample { }',
        '}'
    )
    $packageJson = @(
        '{',
        '    "name": "com.test.adapter",',
        '    "dependencies": {',
        '        "com.ambiguous-interactive.signalfish": "0.1.0",',
        '        "com.ambiguous-interactive.signalfish.adapters.core": "0.1.0"',
        '    },',
        '    "samples": [',
        '        { "displayName": "Sample", "path": "Samples~/Sample" }',
        '    ]',
        '}'
    )

    function Write-CoreFixture {
        $root = Join-Path $repo $core.Root
        Write-TestFile -Path (Join-Path $root 'Runtime/Core.cs') -Content $core.CoreCs
        Write-TestFile -Path (Join-Path $root 'Runtime/Core.asmdef') -Content $core.AsmdefJson
        Write-TestFile -Path (Join-Path $root 'package.json') -Content $core.PackageJson
    }

    function Write-AdapterFixture {
        param([hashtable]$Pin)

        $root = Join-Path $repo $Pin.Root
        if ($null -ne $Pin.LocalCoreCs) {
            Write-TestFile -Path (Join-Path $root 'Runtime/Core/HostLoopback.cs') -Content $Pin.LocalCoreCs
        }

        Write-TestFile -Path (Join-Path $root "Runtime/Engine/$($Pin.BridgeFile)") -Content $Pin.BridgeCs
        Write-TestFile -Path (Join-Path $root 'Runtime/Adapter.asmdef') -Content $Pin.AsmdefJson
        Write-TestFile -Path (Join-Path $root "Editor/$($Pin.DetectorFile)") -Content $Pin.DetectorCs
        Write-TestFile -Path (Join-Path $root 'Editor/Editor.asmdef') -Content $editorAsmdefJson
        Write-TestFile -Path (Join-Path $root 'Samples~/Sample/Sample.cs') -Content $sampleCs
        Write-TestFile -Path (Join-Path $root 'package.json') -Content $packageJson
    }

    Write-CoreFixture
    Write-AdapterFixture -Pin $fishnet
    Write-AdapterFixture -Pin $mirror

    # 1. Well-formed packages pass the static lane (-NoBuild; the compile
    #    lane is CI's job and the real repo exercises it).
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 0 $run.ExitCode 'well-formed packages pass'

    # 2. -Adapter scopes the run to one package.
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild', '-Adapter', 'Mirror')
    Assert-Equal 0 $run.ExitCode 'single-package run passes'
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild', '-Adapter', 'Core')
    Assert-Equal 0 $run.ExitCode 'core-only run passes'

    # 3. An SDK reference without the define guard fails (both shapes).
    foreach ($pin in @($fishnet, $mirror)) {
        $bridgePath = Join-Path $repo "$($pin.Root)/Runtime/Engine/$($pin.BridgeFile)"
        $unguarded = @($pin.BridgeCs | Where-Object { $_ -notmatch '^#if SIGNALFISH_' -and $_ -ne '#endif' })
        Write-TestFile -Path $bridgePath -Content $unguarded
        $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
        Assert-Equal 1 $run.ExitCode "$($pin.Root): unguarded SDK reference fails"
        Assert-OutputContains $run $pin.Define 'guard failure names the define'
        Write-TestFile -Path $bridgePath -Content $pin.BridgeCs
    }

    # 4. Vendoring the SDK (namespace <SDK>) always fails.
    foreach ($pin in @($fishnet, $mirror)) {
        $bridgePath = Join-Path $repo "$($pin.Root)/Runtime/Engine/$($pin.BridgeFile)"
        $vendorCs = @(
            "namespace $($pin.Root.Split('/')[-1]).Transporting",
            '{',
            '    public class Stolen { }',
            '}'
        )
        Write-TestFile -Path $bridgePath -Content $vendorCs
        $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
        Assert-Equal 1 $run.ExitCode "$($pin.Root): vendored SDK namespace fails"
        Assert-OutputContains $run 'vendoring' 'vendor failure names the rule'
        Write-TestFile -Path $bridgePath -Content $pin.BridgeCs
    }

    # 5. A dropped pinned Transport member fails.
    foreach ($pin in @($fishnet, $mirror)) {
        $bridgePath = Join-Path $repo "$($pin.Root)/Runtime/Engine/$($pin.BridgeFile)"
        $probe =
            if ($pin.Define -eq 'SIGNALFISH_FISHNET') { 'GetMTU' }
            else { 'GetMaxPacketSize' }
        $missingMemberCs = @($pin.BridgeCs | Where-Object { $_ -notmatch $probe })
        Write-TestFile -Path $bridgePath -Content $missingMemberCs
        $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
        Assert-Equal 1 $run.ExitCode "$($pin.Root): missing pinned member fails"
        Assert-OutputContains $run $probe 'member failure names the member'
        Write-TestFile -Path $bridgePath -Content $pin.BridgeCs
    }

    # 6. A bridge that forks the wire (no shared AdapterWire) or drops the
    #    engine channel pin fails.
    foreach ($pin in @($fishnet, $mirror)) {
        $bridgePath = Join-Path $repo "$($pin.Root)/Runtime/Engine/$($pin.BridgeFile)"
        $forkedCs = @($pin.BridgeCs | Where-Object { $_ -notmatch 'AdapterWire\.HeaderLength' })
        Write-TestFile -Path $bridgePath -Content $forkedCs
        $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
        Assert-Equal 1 $run.ExitCode "$($pin.Root): bridge without the shared header fails"
        Assert-OutputContains $run 'AdapterWire' 'wire-fork failure names the shared type'
        Write-TestFile -Path $bridgePath -Content $pin.BridgeCs

        $pinlessCs = @($pin.BridgeCs | Where-Object { $_ -notmatch [regex]::Escape($pin.ChannelPin) })
        Write-TestFile -Path $bridgePath -Content $pinlessCs
        $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
        Assert-Equal 1 $run.ExitCode "$($pin.Root): bridge without the channel pin fails"
        Assert-OutputContains $run 'channel pin' 'channel-pin failure names the rule'
        Write-TestFile -Path $bridgePath -Content $pin.BridgeCs
    }

    # 7. An adapter re-declaring a shared core type fails (duplication pin).
    $stolenCoreCs = @(
        '#nullable enable',
        'namespace SignalFish.Client.Adapters.Mirror',
        '{',
        '    public static class AdapterMtu',
        '    {',
        '        public const int WireReserve = 256;',
        '    }',
        '}'
    )
    Write-TestFile -Path (Join-Path $repo "$($mirror.Root)/Runtime/Core/AdapterMtu.cs") -Content $stolenCoreCs
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'adapter-local shared-type declaration fails'
    Assert-OutputContains $run 'live only in' 'duplication failure names the rule'
    Remove-Item -LiteralPath (Join-Path $repo "$($mirror.Root)/Runtime/Core/AdapterMtu.cs") -Force

    # 8. A shared core file that reaches for the engine fails.
    $engineCoreCs = @($core.CoreCs + '/* UnityEngine.Debug.Log("x"); */')
    Write-TestFile -Path (Join-Path $repo "$($core.Root)/Runtime/Core.cs") -Content $engineCoreCs
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'engine reference in the shared core fails'
    Write-TestFile -Path (Join-Path $repo "$($core.Root)/Runtime/Core.cs") -Content $core.CoreCs

    # 9. Shared-core asmdef honesty: define gates and engine references fail.
    $gatedCoreAsmdef = @($core.AsmdefJson -replace '"defineConstraints": \[\],', '"defineConstraints": ["SIGNALFISH_FISHNET"],')
    Write-TestFile -Path (Join-Path $repo "$($core.Root)/Runtime/Core.asmdef") -Content $gatedCoreAsmdef
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'gated shared-core asmdef fails'
    Assert-OutputContains $run 'defineConstraints' 'core gate failure names the rule'

    $engineCoreAsmdef = @($core.AsmdefJson -replace '"noEngineReferences": true', '"noEngineReferences": false')
    Write-TestFile -Path (Join-Path $repo "$($core.Root)/Runtime/Core.asmdef") -Content $engineCoreAsmdef
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'engine-referencing shared-core asmdef fails'
    Assert-OutputContains $run 'noEngineReferences' 'core engine-reference failure names the rule'
    Write-TestFile -Path (Join-Path $repo "$($core.Root)/Runtime/Core.asmdef") -Content $core.AsmdefJson

    # 10. FishNet define plumbing: a missing versionDefines pin, an
    #     ungated assembly, and a missing shared-core reference fail.
    $asmdefPath = Join-Path $repo "$($fishnet.Root)/Runtime/Adapter.asmdef"
    $looseAsmdef = @($fishnet.AsmdefJson | Where-Object { $_ -notmatch 'com.firstgeargames.fishnet' })
    Write-TestFile -Path $asmdefPath -Content $looseAsmdef
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'missing versionDefines pin fails'
    $looseAsmdef = @($looseAsmdef | Where-Object { $_ -notmatch 'defineConstraints' })
    Write-TestFile -Path $asmdefPath -Content $looseAsmdef
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'ungated asmdef fails'
    Assert-OutputContains $run 'defineConstraints' 'ungate failure names the constraint'
    Write-TestFile -Path $asmdefPath -Content $fishnet.AsmdefJson

    $coreLessAsmdef = @($fishnet.AsmdefJson | Where-Object { $_ -notmatch 'SignalFish.Adapters.Core' })
    Write-TestFile -Path $asmdefPath -Content $coreLessAsmdef
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'missing shared-core reference fails'
    Assert-OutputContains $run 'SignalFish.Adapters.Core' 'core-reference failure names the assembly'
    Write-TestFile -Path $asmdefPath -Content $fishnet.AsmdefJson

    # 11. Mirror define ownership: a versionDefines pin is a lie (Mirror
    #     ships as an asset, no UPM package).
    $asmdefPath = Join-Path $repo "$($mirror.Root)/Runtime/Adapter.asmdef"
    $pinnedMirror = @(
        $mirror.AsmdefJson -replace '"versionDefines": \[\]',
        '"versionDefines": [ { "name": "com.mirrorng.mirror", "expression": "96.0.0", "define": "SIGNALFISH_MIRROR" } ]'
    )
    Write-TestFile -Path $asmdefPath -Content $pinnedMirror
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'Mirror versionDefines pin fails'
    Assert-OutputContains $run 'versionDefines' 'mirror pin failure names the rule'
    Write-TestFile -Path $asmdefPath -Content $mirror.AsmdefJson

    # 12. The detector contract: missing file, missing define string,
    #     missing probe, and an ungated editor assembly all fail.
    foreach ($pin in @($fishnet, $mirror)) {
        $detectorPath = Join-Path $repo "Editor/$($pin.DetectorFile)".Replace('Editor/', "$($pin.Root)/Editor/")
        $expectedProbe =
            if ($pin.Define -eq 'SIGNALFISH_FISHNET') { 'com.firstgeargames.fishnet' }
            else { '"Mirror"' }

        $missingProbe = @($pin.DetectorCs | Where-Object { $_ -notmatch [regex]::Escape($expectedProbe) })
        Write-TestFile -Path $detectorPath -Content $missingProbe
        $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
        Assert-Equal 1 $run.ExitCode "$($pin.Root): detector missing probe fails"
        Assert-OutputContains $run 'probe' 'probe failure names the rule'
        Write-TestFile -Path $detectorPath -Content $pin.DetectorCs

        $missingDefine = @($pin.DetectorCs | Where-Object { $_ -notmatch $pin.Define })
        Write-TestFile -Path $detectorPath -Content $missingDefine
        $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
        Assert-Equal 1 $run.ExitCode "$($pin.Root): detector missing define fails"
        Assert-OutputContains $run 'define string' 'define failure names the rule'
        Write-TestFile -Path $detectorPath -Content $pin.DetectorCs

        Remove-Item -LiteralPath $detectorPath -Force
        $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
        Assert-Equal 1 $run.ExitCode "$($pin.Root): missing detector fails"
        Write-TestFile -Path $detectorPath -Content $pin.DetectorCs

        $editorAsmdefPath = Join-Path $repo "$($pin.Root)/Editor/Editor.asmdef"
        $ungatedEditor = @($editorAsmdefJson -replace '"includePlatforms": \["Editor"\],', '')
        Write-TestFile -Path $editorAsmdefPath -Content $ungatedEditor
        $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
        Assert-Equal 1 $run.ExitCode "$($pin.Root): ungated editor asmdef fails"
        Write-TestFile -Path $editorAsmdefPath -Content $editorAsmdefJson
    }

    # 13. A package.json sample path that does not exist fails; a
    #     package.json without a samples key passes cleanly (the old
    #     direct-property read crashed under StrictMode).
    $packagePath = Join-Path $repo "$($fishnet.Root)/package.json"
    $danglingPackage = @($packageJson -replace 'Samples~/Sample', 'Samples~/Missing')
    Write-TestFile -Path $packagePath -Content $danglingPackage
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'dangling sample path fails'
    Write-TestFile -Path $packagePath -Content $packageJson

    $sampleLessPackage = @(
        '{',
        '    "name": "com.test.adapter",',
        '    "dependencies": {',
        '        "com.ambiguous-interactive.signalfish": "0.1.0",',
        '        "com.ambiguous-interactive.signalfish.adapters.core": "0.1.0"',
        '    }',
        '}'
    )
    Write-TestFile -Path $packagePath -Content $sampleLessPackage
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 0 $run.ExitCode 'samples-less package.json passes cleanly'
    Write-TestFile -Path $packagePath -Content $packageJson

    # 14. An adapter package.json without the core dependency fails (the
    #     asmdef reference alone dangles for consumers).
    $dependencyLess = @(
        '{',
        '    "name": "com.test.adapter",',
        '    "dependencies": {',
        '        "com.ambiguous-interactive.signalfish": "0.1.0"',
        '    },',
        '    "samples": [',
        '        { "displayName": "Sample", "path": "Samples~/Sample" }',
        '    ]',
        '}'
    )
    Write-TestFile -Path $packagePath -Content $dependencyLess
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'missing core dependency fails'
    Assert-OutputContains $run 'signalfish.adapters.core' 'dependency failure names the package'
    Write-TestFile -Path $packagePath -Content $packageJson

    # 15. A record or interface fork of a shared type fails the
    #     duplication pin.
    $recordFork = @(
        '#nullable enable',
        'namespace SignalFish.Client.Adapters.FishNet',
        '{',
        '    public record AdapterWire;',
        '    public interface SignalFishPeerRouter { }',
        '}'
    )
    Write-TestFile -Path (Join-Path $repo "$($fishnet.Root)/Runtime/Core/Fork.cs") -Content $recordFork
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'record or interface shared-type fork fails'
    Assert-OutputContains $run 'live only in' 'fork failure names the rule'
    Remove-Item -LiteralPath (Join-Path $repo "$($fishnet.Root)/Runtime/Core/Fork.cs") -Force

    # 16. A negated define condition (`#if !<define>`) is not a guard.
    $negatedBridge = @(
        $fishnet.BridgeCs | Where-Object { $_ -notmatch '^#if SIGNALFISH_' -and $_ -ne '#endif' }
    )
    $negatedBridge = @('#if !SIGNALFISH_FISHNET') + $negatedBridge + @('#endif')
    $bridgePath = Join-Path $repo "$($fishnet.Root)/Runtime/Engine/$($fishnet.BridgeFile)"
    Write-TestFile -Path $bridgePath -Content $negatedBridge
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'negated define condition is not a guard'
    Assert-OutputContains $run 'SIGNALFISH_FISHNET' 'negated-guard failure names the define'
    Write-TestFile -Path $bridgePath -Content $fishnet.BridgeCs

    # 17. The real repository passes the full lane (compiles included).
    $realRoot = (Resolve-Path (Join-Path (Split-Path -Parent $PSScriptRoot) '..')).Path
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $realRoot)
    Assert-Equal 0 $run.ExitCode 'real repository passes the full lane'

    Get-ScriptExitSummary
}
finally {
    Remove-TestRepo -Path $repo
}
