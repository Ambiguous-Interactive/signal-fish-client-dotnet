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
    $fishnet = @{
        Root = 'unity/Adapters/FishNet'
        Define = 'SIGNALFISH_FISHNET'
        CoreCs = @(
            '#nullable enable',
            'namespace SignalFish.Client.Adapters.FishNet',
            '{',
            '    public static class Core',
            '    {',
            '        public const int HeaderLength = 18;',
            '    }',
            '}'
        )
        BridgeCs = @(
            '#if SIGNALFISH_FISHNET',
            'namespace SignalFish.Client.Adapters.FishNet',
            '{',
            '    public sealed class Bridge : global::FishNet.Transporting.Transport',
            '    {',
            '        public override int GetMTU(byte channel) => HeaderLength;',
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
            '    "references": ["SignalFish.Client", "FishNet.Runtime"],',
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
        CoreCs = @(
            '#nullable enable',
            'namespace SignalFish.Client.Adapters.Mirror',
            '{',
            '    public static class Core',
            '    {',
            '        public const int HeaderLength = 18;',
            '    }',
            '}'
        )
        BridgeCs = @(
            '#if SIGNALFISH_MIRROR',
            'namespace SignalFish.Client.Adapters.Mirror',
            '{',
            '    public sealed class Bridge : global::Mirror.Transport',
            '    {',
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
            '    "references": ["SignalFish.Client", "Mirror"],',
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
        '    "samples": [',
        '        { "displayName": "Sample", "path": "Samples~/Sample" }',
        '    ]',
        '}'
    )

    function Write-AdapterFixture {
        param([hashtable]$Pin)

        $root = Join-Path $repo $Pin.Root
        Write-TestFile -Path (Join-Path $root 'Runtime/Core/Core.cs') -Content $Pin.CoreCs
        Write-TestFile -Path (Join-Path $root "Runtime/Engine/$($Pin.BridgeFile)") -Content $Pin.BridgeCs
        Write-TestFile -Path (Join-Path $root 'Runtime/Adapter.asmdef') -Content $Pin.AsmdefJson
        Write-TestFile -Path (Join-Path $root "Editor/$($Pin.DetectorFile)") -Content $Pin.DetectorCs
        Write-TestFile -Path (Join-Path $root 'Editor/Editor.asmdef') -Content $editorAsmdefJson
        Write-TestFile -Path (Join-Path $root 'Samples~/Sample/Sample.cs') -Content $sampleCs
        Write-TestFile -Path (Join-Path $root 'package.json') -Content $packageJson
    }

    Write-AdapterFixture -Pin $fishnet
    Write-AdapterFixture -Pin $mirror

    # 1. Well-formed packages pass the static lane (-NoBuild; the compile
    #    lane is CI's job and the real repo exercises it).
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 0 $run.ExitCode 'well-formed packages pass'

    # 2. -Adapter scopes the run to one adapter.
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild', '-Adapter', 'Mirror')
    Assert-Equal 0 $run.ExitCode 'single-adapter run passes'

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

    # 6. A core file that reaches for the engine fails.
    $engineCoreCs = @($fishnet.CoreCs + '/* UnityEngine.Debug.Log("x"); */')
    Write-TestFile -Path (Join-Path $repo "$($fishnet.Root)/Runtime/Core/Core.cs") -Content $engineCoreCs
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'engine reference in core fails'
    Write-TestFile -Path (Join-Path $repo "$($fishnet.Root)/Runtime/Core/Core.cs") -Content $fishnet.CoreCs

    # 7. FishNet define plumbing: a missing versionDefines pin fails; an
    #    ungated assembly fails.
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

    # 8. Mirror define ownership: a versionDefines pin is a lie (Mirror
    #    ships as an asset, no UPM package).
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

    # 9. The detector contract: missing file, missing define string,
    #    missing probe, and an ungated editor assembly all fail.
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

    # 10. A package.json sample path that does not exist fails.
    $packagePath = Join-Path $repo "$($fishnet.Root)/package.json"
    $danglingPackage = @($packageJson -replace 'Samples~/Sample', 'Samples~/Missing')
    Write-TestFile -Path $packagePath -Content $danglingPackage
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'dangling sample path fails'
    Write-TestFile -Path $packagePath -Content $packageJson

    # 11. The real repository passes the full lane (compiles included).
    $realRoot = (Resolve-Path (Join-Path (Split-Path -Parent $PSScriptRoot) '..')).Path
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $realRoot)
    Assert-Equal 0 $run.ExitCode 'real repository passes the full lane'

    Get-ScriptExitSummary
}
finally {
    Remove-TestRepo -Path $repo
}
