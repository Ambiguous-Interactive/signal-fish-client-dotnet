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
        VendorNamespace = 'FishNet.Transporting'
        MemberProbe = 'GetMTU'
        WireFilter = 'AdapterWire\.HeaderLength'
        WireName = 'AdapterWire'
        DetectorProbe = 'com.firstgeargames.fishnet'
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
        VendorNamespace = 'Mirror.Transporting'
        MemberProbe = 'GetMaxPacketSize'
        WireFilter = 'AdapterWire\.HeaderLength'
        WireName = 'AdapterWire'
        DetectorProbe = '"Mirror"'
    }

    $ngo = @{
        Root = 'unity/Adapters/Ngo'
        Define = 'SIGNALFISH_NGO'
        LocalCoreCs = @(
            '#nullable enable',
            'namespace SignalFish.Client.Adapters.Ngo',
            '{',
            '    public static class ConnectionApprovalPayload',
            '    {',
            '        public const int Length = 16;',
            '        public static bool TryRead(ReadOnlySpan<byte> payload, out Guid playerId) => throw null!;',
            '    }',
            '}'
        )
        ChannelPin = $null
        BridgeCs = @(
            '#if SIGNALFISH_NGO',
            'namespace SignalFish.Client.Adapters.Ngo',
            '{',
            '    public sealed class Bridge',
            '    {',
            '        private readonly SignalFishRoomRoster roster = new SignalFishRoomRoster();',
            '        public void Approve(global::Unity.Netcode.NetworkManager.ConnectionApprovalRequest request,',
            '            global::Unity.Netcode.NetworkManager.ConnectionApprovalResponse response)',
            '        {',
            '            response.Approved = ConnectionApprovalPayload.TryRead(request.Payload, out Guid playerId)',
            '                && roster.IsMember(playerId);',
            '            response.Reason = response.Approved ? null : "not a member";',
            '            response.CreatePlayerObject = false;',
            '        }',
            '        public void Start(Unity.Netcode.NetworkManager manager, byte[] connectionData)',
            '        {',
            '            manager = global::Unity.Netcode.NetworkManager.Singleton;',
            '            manager.NetworkConfig.ConnectionData = connectionData;',
            '            manager.ConnectionApprovalCallback += Approve;',
            '            if (manager.IsServer && manager.IsClient) { manager.StartHost(); } else { manager.StartClient(); }',
            '            manager.Shutdown();',
            '        }',
            '    }',
            '}',
            '#endif'
        )
        AsmdefJson = @(
            '{',
            '    "name": "SignalFish.Adapters.Ngo",',
            '    "references": ["SignalFish.Client", "SignalFish.Adapters.Core", "Unity.Netcode.Runtime"],',
            '    "defineConstraints": ["SIGNALFISH_NGO"],',
            '    "versionDefines": [',
            '        { "name": "com.unity.netcode.gameobjects", "expression": "1.2.0", "define": "SIGNALFISH_NGO" }',
            '    ],',
            '    "noEngineReferences": false',
            '}'
        )
        DetectorCs = @(
            'namespace SignalFish.Client.Adapters.Ngo.Editor',
            '{',
            '    internal static class SignalFishNgoDefineDetector',
            '    {',
            '        private const string Define = "SIGNALFISH_NGO";',
            '        private const string AssemblyName = "Unity.Netcode.Runtime";',
            '        private const string PackageName = "com.unity.netcode.gameobjects";',
            '    }',
            '}'
        )
        DetectorFile = 'SignalFishNgoDefineDetector.cs'
        BridgeFile = 'SignalFishRoomCoordinator.cs'
        VendorNamespace = 'Unity.Netcode.Transporting'
        MemberProbe = 'ConnectionApprovalCallback'
        WireFilter = 'ConnectionApprovalPayload\.'
        WireName = 'ConnectionApprovalPayload'
        DetectorProbe = 'com.unity.netcode.gameobjects'
    }

    $pun2 = @{
        Root = 'unity/Adapters/Pun2'
        Define = 'SIGNALFISH_PUN2'
        LocalCoreCs = @(
            '#nullable enable',
            'namespace SignalFish.Client.Adapters.Pun2',
            '{',
            '    public static class Pun2RoomEnvelope',
            '    {',
            '        public const int MaxRoomNameLength = 128;',
            '        public static bool IsValidRoomName(string? roomName) => throw null!;',
            '    }',
            '}'
        )
        ChannelPin = $null
        BridgeCs = @(
            '#if SIGNALFISH_PUN2',
            'namespace SignalFish.Client.Adapters.Pun2',
            '{',
            '    public sealed class Bridge : global::Photon.Pun.MonoBehaviourPunCallbacks',
            '    {',
            '        private readonly System.Collections.Generic.List<string> names = new();',
            '        public void Exchange()',
            '        {',
            '            bool connected = global::Photon.Pun.PhotonNetwork.ConnectUsingSettings();',
            '            bool queued = global::Photon.Pun.PhotonNetwork.JoinOrCreateRoom(',
            '                "room", new global::Photon.Realtime.RoomOptions { MaxPlayers = 4 },',
            '                global::Photon.Realtime.TypedLobby.Default);',
            '            string? name = global::Photon.Pun.PhotonNetwork.CurrentRoom?.Name;',
            '            if (name is not null && Pun2RoomEnvelope.IsValidRoomName(name))',
            '            {',
            '                names.Add(name);',
            '            }',
            '            if (!connected || !queued) { global::Photon.Pun.PhotonNetwork.Disconnect(); }',
            '        }',
            '        public override void OnConnectedToMaster() { }',
            '        public override void OnJoinedRoom() { }',
            '        public override void OnLeftRoom() { }',
            '        public override void OnCreateRoomFailed(short returnCode, string message) { }',
            '        public override void OnJoinRoomFailed(short returnCode, string message) { }',
            '        public override void OnDisconnected(global::Photon.Realtime.DisconnectCause cause) { }',
            '    }',
            '}',
            '#endif'
        )
        AsmdefJson = @(
            '{',
            '    "name": "SignalFish.Adapters.Pun2",',
            '    "references": ["SignalFish.Client", "SignalFish.Adapters.Core", "PhotonUnityNetworking", "PhotonRealtime"],',
            '    "defineConstraints": ["SIGNALFISH_PUN2"],',
            '    "versionDefines": [],',
            '    "noEngineReferences": false',
            '}'
        )
        DetectorCs = @(
            'namespace SignalFish.Client.Adapters.Pun2.Editor',
            '{',
            '    internal static class SignalFishPun2DefineDetector',
            '    {',
            '        private const string Define = "SIGNALFISH_PUN2";',
            '        private const string AssemblyName = "PhotonUnityNetworking";',
            '    }',
            '}'
        )
        DetectorFile = 'SignalFishPun2DefineDetector.cs'
        BridgeFile = 'SignalFishPun2Bootstrap.cs'
        VendorNamespace = 'Photon.Transporting'
        MemberProbe = 'ConnectUsingSettings'
        WireFilter = 'Pun2RoomEnvelope\.'
        WireName = 'Pun2RoomEnvelope'
        DetectorProbe = 'PhotonUnityNetworking'
    }

    $fusion = @{
        Root = 'unity/Adapters/Fusion'
        Define = 'SIGNALFISH_FUSION'
        LocalCoreCs = @(
            '#nullable enable',
            'namespace SignalFish.Client.Adapters.Fusion',
            '{',
            '    public static class FusionSessionEnvelope',
            '    {',
            '        public const int MaxSessionNameLength = 128;',
            '        public static bool IsValidSessionName(string? sessionName) => throw null!;',
            '    }',
            '}'
        )
        ChannelPin = $null
        BridgeCs = @(
            '#if SIGNALFISH_FUSION',
            'namespace SignalFish.Client.Adapters.Fusion',
            '{',
            '    public sealed class Bridge : global::Fusion.INetworkRunnerCallbacks',
            '    {',
            '        private readonly System.Collections.Generic.List<string> names = new();',
            '        public void Exchange()',
            '        {',
            '            global::Fusion.NetworkRunner runner = new UnityEngine.GameObject("runner").AddComponent<global::Fusion.NetworkRunner>();',
            '            runner.AddCallbacks(this);',
            '            global::Fusion.StartGameResult host = runner.StartGame(new global::Fusion.StartGameArgs',
            '            {',
            '                GameMode = global::Fusion.GameMode.Host,',
            '                SessionName = "room",',
            '                PlayerCount = 4,',
            '            }).Result;',
            '            global::Fusion.StartGameResult client = runner.StartGame(new global::Fusion.StartGameArgs',
            '            {',
            '                GameMode = global::Fusion.GameMode.Client,',
            '                SessionName = "room",',
            '                EnableClientSessionCreation = false,',
            '            }).Result;',
            '            if (host.Ok && FusionSessionEnvelope.IsValidSessionName(host.ErrorMessage))',
            '            {',
            '                names.Add(host.ErrorMessage);',
            '            }',
            '            runner.Shutdown();',
            '        }',
            '        public void OnConnectFailed(global::Fusion.NetworkRunner runner, global::Fusion.Sockets.NetAddress address,',
            '            global::Fusion.Sockets.NetConnectFailedReason reason) { }',
            '        public void OnDisconnectedFromServer(global::Fusion.NetworkRunner runner,',
            '            global::Fusion.Sockets.NetDisconnectReason reason) { }',
            '        public void OnShutdown(global::Fusion.NetworkRunner runner, global::Fusion.ShutdownReason reason) { }',
            '        public void OnHostMigration(global::Fusion.NetworkRunner runner,',
            '            global::Fusion.HostMigrationToken token) { }',
            '        public void OnObjectExitAOI(global::Fusion.NetworkRunner runner, global::Fusion.NetworkObject obj,',
            '            global::Fusion.PlayerRef player) { }',
            '        public void OnObjectEnterAOI(global::Fusion.NetworkRunner runner, global::Fusion.NetworkObject obj,',
            '            global::Fusion.PlayerRef player) { }',
            '        public void OnPlayerJoined(global::Fusion.NetworkRunner runner, global::Fusion.PlayerRef player) { }',
            '        public void OnPlayerLeft(global::Fusion.NetworkRunner runner, global::Fusion.PlayerRef player) { }',
            '        public void OnConnectRequest(global::Fusion.NetworkRunner runner,',
            '            global::Fusion.NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }',
            '        public void OnUserSimulationMessage(global::Fusion.NetworkRunner runner,',
            '            global::Fusion.SimulationMessagePtr message) { }',
            '        public void OnReliableDataReceived(global::Fusion.NetworkRunner runner, global::Fusion.PlayerRef player,',
            '            global::Fusion.ReliableKey key, System.ArraySegment<byte> data) { }',
            '        public void OnReliableDataProgress(global::Fusion.NetworkRunner runner, global::Fusion.PlayerRef player,',
            '            global::Fusion.ReliableKey key, float progress) { }',
            '        public void OnInput(global::Fusion.NetworkRunner runner, global::Fusion.NetworkInput input) { }',
            '        public void OnInputMissing(global::Fusion.NetworkRunner runner, global::Fusion.PlayerRef player,',
            '            global::Fusion.NetworkInput input) { }',
            '        public void OnConnectedToServer(global::Fusion.NetworkRunner runner) { }',
            '        public void OnSessionListUpdated(global::Fusion.NetworkRunner runner,',
            '            System.Collections.Generic.List<global::Fusion.SessionInfo> sessionList) { }',
            '        public void OnCustomAuthenticationResponse(global::Fusion.NetworkRunner runner,',
            '            System.Collections.Generic.Dictionary<string, object> data) { }',
            '        public void OnSceneLoadDone(global::Fusion.NetworkRunner runner) { }',
            '        public void OnSceneLoadStart(global::Fusion.NetworkRunner runner) { }',
            '    }',
            '}',
            '#endif'
        )
        AsmdefJson = @(
            '{',
            '    "name": "SignalFish.Adapters.Fusion",',
            '    "references": ["SignalFish.Client", "SignalFish.Adapters.Core", "Fusion.Runtime", "Fusion.Sockets"],',
            '    "defineConstraints": ["SIGNALFISH_FUSION"],',
            '    "versionDefines": [],',
            '    "noEngineReferences": false',
            '}'
        )
        DetectorCs = @(
            'namespace SignalFish.Client.Adapters.Fusion.Editor',
            '{',
            '    internal static class SignalFishFusionDefineDetector',
            '    {',
            '        private const string Define = "SIGNALFISH_FUSION";',
            '        private const string AssemblyName = "Fusion.Runtime";',
            '    }',
            '}'
        )
        DetectorFile = 'SignalFishFusionDefineDetector.cs'
        BridgeFile = 'SignalFishFusionBootstrap.cs'
        VendorNamespace = 'Fusion.Transporting'
        MemberProbe = 'EnableClientSessionCreation'
        WireFilter = 'FusionSessionEnvelope\.'
        WireName = 'FusionSessionEnvelope'
        DetectorProbe = 'Fusion.Runtime'
    }

    $steamworksNet = @{
        Root = 'unity/Adapters/SteamworksNet'
        Define = 'SIGNALFISH_STEAMWORKSNET'
        LocalCoreCs = @(
            '#nullable enable',
            'namespace SignalFish.Client.Adapters.SteamworksNet',
            '{',
            '    public static class SteamIdentityEnvelope',
            '    {',
            '        public const int MaxSteamIdLength = 20;',
            '        public static bool IsValidSteamId(string? steamId) => throw null!;',
            '    }',
            '}'
        )
        ChannelPin = $null
        BridgeCs = @(
            '#if SIGNALFISH_STEAMWORKSNET',
            'namespace SignalFish.Client.Adapters.SteamworksNet',
            '{',
            '    public sealed class Bridge : UnityEngine.MonoBehaviour',
            '    {',
            '        private readonly System.Collections.Generic.List<string> ids = new();',
            '        private global::Steamworks.HSteamListenSocket socket =',
            '            global::Steamworks.HSteamListenSocket.Invalid;',
            '        public void Host()',
            '        {',
            '            socket = global::Steamworks.SteamNetworkingSockets.CreateListenSocketP2P(0, 0, null!);',
            '        }',
            '        public void Exchange()',
            '        {',
            '            ulong local = global::Steamworks.SteamUser.GetSteamID().m_SteamID;',
            '            var identity = new global::Steamworks.SteamNetworkingIdentity();',
            '            identity.SetSteamID64(local);',
            '            var handle = global::Steamworks.SteamNetworkingSockets.ConnectP2P(',
            '                ref identity, 0, 0, null!);',
            '            if (handle != global::Steamworks.HSteamNetConnection.Invalid',
            '                && SteamIdentityEnvelope.IsValidSteamId(local.ToString()))',
            '            {',
            '                ids.Add(local.ToString());',
            '            }',
            '            global::Steamworks.SteamAPI.RunCallbacks();',
            '            if (global::Steamworks.SteamNetworkingSockets.AcceptConnection(handle)',
            '                == global::Steamworks.EResult.k_EResultOK)',
            '            {',
            '                global::Steamworks.SteamNetworkingSockets.CloseConnection(handle, 1000, null!, false);',
            '            }',
            '            global::Steamworks.SteamNetworkingSockets.CloseListenSocket(socket);',
            '        }',
            '        private void OnConnectionStatusChanged(',
            '            global::Steamworks.SteamNetConnectionStatusChangedCallback_t callback)',
            '        {',
            '            global::Steamworks.HSteamNetConnection connection = callback.m_hConn;',
            '            if (callback.m_info.m_hListenSocket != global::Steamworks.HSteamListenSocket.Invalid',
            '                && callback.m_info.m_eState',
            '                    == global::Steamworks.ESteamNetworkingConnectionState',
            '                        .k_ESteamNetworkingConnectionState_Connecting)',
            '            {',
            '                ids.Add(callback.m_info.m_identityRemote.GetSteamID64().ToString());',
            '            }',
            '            else if (callback.m_info.m_eState',
            '                == global::Steamworks.ESteamNetworkingConnectionState',
            '                    .k_ESteamNetworkingConnectionState_Connected',
            '                || callback.m_info.m_eState',
            '                    == global::Steamworks.ESteamNetworkingConnectionState',
            '                        .k_ESteamNetworkingConnectionState_ClosedByPeer',
            '                || callback.m_info.m_eState',
            '                    == global::Steamworks.ESteamNetworkingConnectionState',
            '                        .k_ESteamNetworkingConnectionState_ProblemDetectedLocally)',
            '            {',
            '                ids.Add(connection.m_HSteamNetConnection.GetHashCode().ToString());',
            '                ids.Add(callback.m_info.m_szEndDebug);',
            '            }',
            '        }',
            '    }',
            '}',
            '#endif'
        )
        AsmdefJson = @(
            '{',
            '    "name": "SignalFish.Adapters.SteamworksNet",',
            '    "references": ["SignalFish.Client", "SignalFish.Adapters.Core", "com.rlabrecque.steamworks.net"],',
            '    "defineConstraints": ["SIGNALFISH_STEAMWORKSNET"],',
            '    "versionDefines": [',
            '        { "name": "com.rlabrecque.steamworks.net", "expression": "20.0.0", "define": "SIGNALFISH_STEAMWORKSNET" }',
            '    ],',
            '    "noEngineReferences": false',
            '}'
        )
        DetectorCs = @(
            'namespace SignalFish.Client.Adapters.SteamworksNet.Editor',
            '{',
            '    internal static class SignalFishSteamworksNetDefineDetector',
            '    {',
            '        private const string Define = "SIGNALFISH_STEAMWORKSNET";',
            '        private const string Package = "com.rlabrecque.steamworks.net";',
            '    }',
            '}'
        )
        DetectorFile = 'SignalFishSteamworksNetDefineDetector.cs'
        BridgeFile = 'SignalFishSteamIdentityBootstrap.cs'
        VendorNamespace = 'Steamworks.Transporting'
        MemberProbe = 'CreateListenSocketP2P'
        WireFilter = 'SteamIdentityEnvelope\.'
        WireName = 'SteamIdentityEnvelope'
        DetectorProbe = 'com.rlabrecque.steamworks.net'
    }

    $facepunch = @{
        Root = 'unity/Adapters/Facepunch'
        Define = 'SIGNALFISH_FACEPUNCH'
        LocalCoreCs = @(
            '#nullable enable',
            'namespace SignalFish.Client.Adapters.Facepunch',
            '{',
            '    public static class SteamIdentityEnvelope',
            '    {',
            '        public const int MaxSteamIdLength = 20;',
            '        public static bool IsValidSteamId(string? steamId) => throw null!;',
            '    }',
            '}'
        )
        ChannelPin = $null
        BridgeCs = @(
            '#if SIGNALFISH_FACEPUNCH',
            'namespace SignalFish.Client.Adapters.Facepunch',
            '{',
            '    public sealed class Bridge : UnityEngine.MonoBehaviour',
            '    {',
            '        private readonly System.Collections.Generic.List<string> ids = new();',
            '        private global::Steamworks.SocketManager? socketManager;',
            '        private global::Steamworks.ConnectionManager? connectionManager;',
            '        public void Host()',
            '        {',
            '            var manager = global::Steamworks.SteamNetworkingSockets.CreateRelaySocket<Manager>(0);',
            '            manager.Owner = this;',
            '            socketManager = manager;',
            '        }',
            '        public void Exchange()',
            '        {',
            '            ulong local = global::Steamworks.SteamClient.SteamId.Value;',
            '            if (!global::Steamworks.SteamClient.IsValid)',
            '            {',
            '                return;',
            '            }',
            '            connectionManager =',
            '                global::Steamworks.SteamNetworkingSockets.ConnectRelay<Client>(local, 0);',
            '            if (SteamIdentityEnvelope.IsValidSteamId(local.ToString()))',
            '            {',
            '                ids.Add(local.ToString());',
            '            }',
            '            global::Steamworks.SteamClient.RunCallbacks();',
            '        }',
            '        private sealed class Manager : global::Steamworks.SocketManager',
            '        {',
            '            public Bridge Owner = null!;',
            '            public override void OnConnecting(',
            '                global::Steamworks.Data.Connection connection,',
            '                global::Steamworks.Data.ConnectionInfo info)',
            '            {',
            '                connection.Accept();',
            '            }',
            '            public override void OnConnected(',
            '                global::Steamworks.Data.Connection connection,',
            '                global::Steamworks.Data.ConnectionInfo info)',
            '            {',
            '                base.OnConnected(connection, info);',
            '            }',
            '            public override void OnDisconnected(',
            '                global::Steamworks.Data.Connection connection,',
            '                global::Steamworks.Data.ConnectionInfo info)',
            '            {',
            '                connection.Close(false, (int)global::Steamworks.NetConnectionEnd.App_Min, "refused");',
            '            }',
            '        }',
            '        private sealed class Client : global::Steamworks.ConnectionManager',
            '        {',
            '            public Bridge Owner = null!;',
            '            public override void OnConnected(global::Steamworks.Data.ConnectionInfo info)',
            '            {',
            '                Owner.ids.Add(info.Identity.SteamId.Value.ToString());',
            '                Owner.ids.Add(((int)info.EndReason).ToString());',
            '            }',
            '            public override void OnDisconnected(global::Steamworks.Data.ConnectionInfo info)',
            '            {',
            '                Close(false, (int)global::Steamworks.NetConnectionEnd.App_Min, "refused");',
            '            }',
            '        }',
            '    }',
            '}',
            '#endif'
        )
        AsmdefJson = @(
            '{',
            '    "name": "SignalFish.Adapters.Facepunch",',
            '    "references": ["SignalFish.Client", "SignalFish.Adapters.Core", "Facepunch.Steamworks.Win32", "Facepunch.Steamworks.Win64", "Facepunch.Steamworks.Posix"],',
            '    "defineConstraints": ["SIGNALFISH_FACEPUNCH"],',
            '    "versionDefines": [],',
            '    "noEngineReferences": false',
            '}'
        )
        DetectorCs = @(
            'namespace SignalFish.Client.Adapters.Facepunch.Editor',
            '{',
            '    internal static class SignalFishFacepunchDefineDetector',
            '    {',
            '        private const string Define = "SIGNALFISH_FACEPUNCH";',
            '        private static readonly string[] AssemblyNames =',
            '        {',
            '            "Facepunch.Steamworks.Win32",',
            '            "Facepunch.Steamworks.Win64",',
            '            "Facepunch.Steamworks.Posix",',
            '        };',
            '    }',
            '}'
        )
        DetectorFile = 'SignalFishFacepunchDefineDetector.cs'
        BridgeFile = 'SignalFishFacepunchSteamIdentityBootstrap.cs'
        VendorNamespace = 'Steamworks.Data.Transporting'
        MemberProbe = 'CreateRelaySocket'
        WireFilter = 'SteamIdentityEnvelope\.'
        WireName = 'SteamIdentityEnvelope'
        DetectorProbe = 'Facepunch.Steamworks.Win64'
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
    Write-AdapterFixture -Pin $ngo
    Write-AdapterFixture -Pin $pun2
    Write-AdapterFixture -Pin $fusion
    Write-AdapterFixture -Pin $steamworksNet
    Write-AdapterFixture -Pin $facepunch

    # 1. Well-formed packages pass the static lane (-NoBuild; the compile
    #    lane is CI's job and the real repo exercises it).
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 0 $run.ExitCode 'well-formed packages pass'

    # 2. -Adapter scopes the run to one package.
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild', '-Adapter', 'Mirror')
    Assert-Equal 0 $run.ExitCode 'single-package run passes'
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild', '-Adapter', 'Core')
    Assert-Equal 0 $run.ExitCode 'core-only run passes'

    # 3. An SDK reference without the define guard fails (all shapes).
    foreach ($pin in @($fishnet, $mirror, $ngo, $pun2, $fusion, $steamworksNet, $facepunch)) {
        $bridgePath = Join-Path $repo "$($pin.Root)/Runtime/Engine/$($pin.BridgeFile)"
        $unguarded = @($pin.BridgeCs | Where-Object { $_ -notmatch '^#if SIGNALFISH_' -and $_ -ne '#endif' })
        Write-TestFile -Path $bridgePath -Content $unguarded
        $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
        Assert-Equal 1 $run.ExitCode "$($pin.Root): unguarded SDK reference fails"
        Assert-OutputContains $run $pin.Define 'guard failure names the define'
        Write-TestFile -Path $bridgePath -Content $pin.BridgeCs
    }

    # 4. Vendoring the SDK (namespace <SDK>) always fails.
    foreach ($pin in @($fishnet, $mirror, $ngo, $pun2, $fusion, $steamworksNet, $facepunch)) {
        $bridgePath = Join-Path $repo "$($pin.Root)/Runtime/Engine/$($pin.BridgeFile)"
        $vendorCs = @(
            "namespace $($pin.VendorNamespace)",
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

    # 5. A dropped pinned engine member fails.
    foreach ($pin in @($fishnet, $mirror, $ngo, $pun2, $fusion, $steamworksNet, $facepunch)) {
        $bridgePath = Join-Path $repo "$($pin.Root)/Runtime/Engine/$($pin.BridgeFile)"
        $probe = $pin.MemberProbe
        $missingMemberCs = @($pin.BridgeCs | Where-Object { $_ -notmatch $probe })
        Write-TestFile -Path $bridgePath -Content $missingMemberCs
        $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
        Assert-Equal 1 $run.ExitCode "$($pin.Root): missing pinned member fails"
        Assert-OutputContains $run $probe 'member failure names the member'
        Write-TestFile -Path $bridgePath -Content $pin.BridgeCs
    }

    # 6. A bridge that forks the shared wire fails; a transport bridge
    #    that drops the engine channel pin fails.
    foreach ($pin in @($fishnet, $mirror, $ngo, $pun2, $fusion, $steamworksNet, $facepunch)) {
        $bridgePath = Join-Path $repo "$($pin.Root)/Runtime/Engine/$($pin.BridgeFile)"
        $forkedCs = @($pin.BridgeCs | Where-Object { $_ -notmatch $pin.WireFilter })
        Write-TestFile -Path $bridgePath -Content $forkedCs
        $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
        Assert-Equal 1 $run.ExitCode "$($pin.Root): bridge without the shared wire fails"
        Assert-OutputContains $run $pin.WireName 'wire-fork failure names the shared type'
        Write-TestFile -Path $bridgePath -Content $pin.BridgeCs

        if ($null -eq $pin.ChannelPin) {
            continue
        }

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
    foreach ($pin in @($fishnet, $mirror, $ngo, $pun2, $fusion, $steamworksNet, $facepunch)) {
        $detectorPath = Join-Path $repo "Editor/$($pin.DetectorFile)".Replace('Editor/', "$($pin.Root)/Editor/")
        $expectedProbe = $pin.DetectorProbe

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
