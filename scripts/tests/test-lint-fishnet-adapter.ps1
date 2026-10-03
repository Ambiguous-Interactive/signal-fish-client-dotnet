<#
.SYNOPSIS
    Self-tests for scripts/lint-fishnet-adapter.ps1 (the FishNet adapter
    contract).
#>
. (Join-Path $PSScriptRoot 'test-common.ps1')

$lint = Join-Path (Split-Path -Parent $PSScriptRoot) 'lint-fishnet-adapter.ps1'
$repo = New-TestRepo
try {
    Write-Host 'test-lint-fishnet-adapter'

    $corePath = Join-Path $repo 'unity/Adapters/FishNet/Runtime/Core/Core.cs'
    $bridgePath = Join-Path $repo 'unity/Adapters/FishNet/Runtime/FishNet/Bridge.cs'
    $samplePath = Join-Path $repo 'unity/Adapters/FishNet/Samples~/MovementSync/Sample.cs'
    $asmdefPath = Join-Path $repo 'unity/Adapters/FishNet/Runtime/Adapter.asmdef'
    $packagePath = Join-Path $repo 'unity/Adapters/FishNet/package.json'

    $coreCs = @(
        '#nullable enable',
        'namespace SignalFish.Client.Adapters.FishNet',
        '{',
        '    public static class Core',
        '    {',
        '        public const int HeaderLength = 18;',
        '    }',
        '}'
    )
    $bridgeCs = @(
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
    $sampleCs = @(
        'namespace Samples',
        '{',
        '    public class Sample : global::FishNet.Object.NetworkBehaviour',
        '    {',
        '    }',
        '}'
    )
    $asmdefJson = @(
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
    $packageJson = @(
        '{',
        '    "name": "com.test.fishnet",',
        '    "samples": [',
        '        { "displayName": "Movement Sync", "path": "Samples~/MovementSync" }',
        '    ]',
        '}'
    )

    Write-TestFile -Path $corePath -Content $coreCs
    Write-TestFile -Path $bridgePath -Content $bridgeCs
    Write-TestFile -Path $samplePath -Content $sampleCs
    Write-TestFile -Path $asmdefPath -Content $asmdefJson
    Write-TestFile -Path $packagePath -Content $packageJson

    # 1. A well-formed package passes the static lane (-NoBuild; the compile
    #    lane is CI's job and the real repo exercises it).
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 0 $run.ExitCode 'well-formed package passes'

    # 2. A FishNet reference without the define guard fails.
    Write-TestFile -Path $bridgePath -Content ($bridgeCs | Where-Object { $_ -ne '#if SIGNALFISH_FISHNET' -and $_ -ne '#endif' })
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'unguarded FishNet reference fails'
    Assert-OutputContains $run 'SIGNALFISH_FISHNET' 'guard failure names the define'
    Write-TestFile -Path $bridgePath -Content $bridgeCs

    # 3. Vendoring the SDK (namespace FishNet) always fails.
    $vendorCs = @('namespace FishNet.Transporting', '{', '    public class Stolen { }', '}')
    Write-TestFile -Path $bridgePath -Content $vendorCs
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'vendored FishNet namespace fails'
    Assert-OutputContains $run 'vendoring' 'vendor failure names the rule'
    Write-TestFile -Path $bridgePath -Content $bridgeCs

    # 4. A dropped pinned Transport member fails.
    $missingMemberCs = @($bridgeCs | Where-Object { $_ -notmatch 'GetMTU' })
    Write-TestFile -Path $bridgePath -Content $missingMemberCs
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'missing pinned member fails'
    Assert-OutputContains $run 'GetMTU' 'member failure names the member'
    Write-TestFile -Path $bridgePath -Content $bridgeCs

    # 5. A core file that reaches for the engine fails.
    $engineCoreCs = @($coreCs + '/* UnityEngine.Debug.Log("x"); */')
    Write-TestFile -Path $corePath -Content $engineCoreCs
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'engine reference in core fails'
    Write-TestFile -Path $corePath -Content $coreCs

    # 6. A missing versionDefines pin fails.
    $looseAsmdef = @($asmdefJson | Where-Object { $_ -notmatch 'com.firstgeargames.fishnet' })
    Write-TestFile -Path $asmdefPath -Content $looseAsmdef
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'missing versionDefines pin fails'

    # 7. An ungated assembly (dangling FishNet reference) fails.
    $looseAsmdef = @($looseAsmdef | Where-Object { $_ -notmatch 'defineConstraints' })
    Write-TestFile -Path $asmdefPath -Content $looseAsmdef
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'ungated asmdef fails'
    Assert-OutputContains $run 'defineConstraints' 'ungate failure names the constraint'
    Write-TestFile -Path $asmdefPath -Content $asmdefJson

    # 8. A package.json sample path that does not exist fails.
    $danglingPackage = @(
        $packageJson -replace 'Samples~/MovementSync', 'Samples~/Missing'
    )
    Write-TestFile -Path $packagePath -Content $danglingPackage
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'dangling sample path fails'
    Write-TestFile -Path $packagePath -Content $packageJson

    # 9. The real repository passes the full lane (compile included).
    $realRoot = (Resolve-Path (Join-Path (Split-Path -Parent $PSScriptRoot) '..')).Path
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $realRoot)
    Assert-Equal 0 $run.ExitCode 'real repository passes the full lane'

    Get-ScriptExitSummary
}
finally {
    Remove-TestRepo -Path $repo
}
