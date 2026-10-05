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

      8. Bridge compile (packages with the BridgeCompile pin; skipped
         with -NoBuild): the engine-gated bridge is type-checked
         against a checked-in shape stub of the engine surface it
         pins - no compiler in this repo sees the real SDK, and blind
         edits have shipped type errors (a void-task await, a missing
         using) that only the editor would catch. The stub is the
         pinned member surface, not the engine; a stub drift is a lint
         failure by design.

.PARAMETER Adapter
    One package name (Core, FishNet, Mirror, Ngo, Pun2), or omit to lint all.

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
    [ValidateSet('Core', 'FishNet', 'Mirror', 'Ngo', 'Pun2', 'Fusion')]
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
# Shape stubs for the Ngo bridge compile lane: the pinned member
# surface the coordinator's bridge touches, spelled the way the bridge
# uses it. This is NOT the engine - it is the compile contract the lint
# enforces, and a stub drift is a lint failure by design (the PinnedMembers
# table is what guards the real engine's shape).
$ngoBridgeStubs = @'
// Shape stubs (UnityEngine / Unity.Netcode surface the coordinator
// touches). The engine SDK is never vendored or referenced in CI.
#nullable enable
namespace UnityEngine
{
    public class MonoBehaviour { }

    public sealed class SerializeFieldAttribute : System.Attribute { }
}

namespace Unity.Netcode
{
    public delegate void ConnectionApprovalCallbackDeclaration(
        NetworkManager.ConnectionApprovalRequest request,
        NetworkManager.ConnectionApprovalResponse response
    );

    public sealed class NetworkConfig
    {
        public bool ConnectionApproval;

        public byte[] ConnectionData = System.Array.Empty<byte>();
    }

    public sealed class NetworkManager
    {
        public static NetworkManager Singleton => null!;

        public bool IsServer => false;

        public bool IsClient => false;

        public NetworkConfig NetworkConfig => null!;

        public ConnectionApprovalCallbackDeclaration? ConnectionApprovalCallback { get; set; }

        public bool StartHost() => true;

        public bool StartClient() => true;

        public void Shutdown() { }

        public sealed class ConnectionApprovalRequest
        {
            public byte[] Payload = System.Array.Empty<byte>();

            public ulong ClientNetworkId;
        }

        public sealed class ConnectionApprovalResponse
        {
            public bool Approved;

            public string? Reason;

            public bool CreatePlayerObject;
        }
    }
}
'@

# Shape stubs for the FishNet bridge compile lane: the pinned member
# surface the transport bridge touches, spelled the way the bridge uses
# it. This is NOT the engine - it is the compile contract the lint
# enforces, and a stub drift is a lint failure by design (the
# PinnedMembers table is what guards the real engine's shape).
$fishNetBridgeStubs = @'
// Shape stubs (UnityEngine / FishNet.Managing / FishNet.Transporting
// surface the bridge touches). The engine SDK is never vendored or
// referenced in CI.
#nullable enable
namespace UnityEngine
{
    public class MonoBehaviour { }

    public sealed class SerializeFieldAttribute : System.Attribute { }

    public sealed class HeaderAttribute : System.Attribute
    {
        public HeaderAttribute(string header) { }
    }

    public sealed class TooltipAttribute : System.Attribute
    {
        public TooltipAttribute(string tooltip) { }
    }

    public sealed class DisallowMultipleComponentAttribute : System.Attribute { }

    public static class Debug
    {
        public static void LogError(object message) { }

        public static void LogWarning(object message) { }

        public static void LogException(System.Exception exception) { }
    }
}

namespace FishNet.Managing
{
    public partial class NetworkManager : UnityEngine.MonoBehaviour { }

    // The bridge logs through FishNet's extension spelling.
    public static class NetworkManagerExtensions
    {
        public static void LogWarning(this NetworkManager networkManager, string message) { }
    }
}

namespace FishNet.Transporting
{
    // Real shapes, pinned against the FishNet 4.x source (the channel
    // bytes double as the shared header's channel ids: Channel.Reliable
    // = 0, Channel.Unreliable = 1 - the channel pin restates these
    // against the pinned engine version).
    [System.Flags]
    public enum LocalConnectionState : int
    {
        Stopped = 1 << 0,
        Stopping = 1 << 1,
        Starting = 1 << 2,
        Started = 1 << 3,
    }

    public enum RemoteConnectionState : byte
    {
        Stopped = 0,
        Started = 2,
    }

    public enum Channel : byte
    {
        Reliable = 0,
        Unreliable = 1,
    }

    public struct ClientConnectionStateArgs
    {
        public ClientConnectionStateArgs(
            LocalConnectionState connectionState,
            int transportIndex
        ) { }
    }

    public struct ServerConnectionStateArgs
    {
        public ServerConnectionStateArgs(
            LocalConnectionState connectionState,
            int transportIndex
        ) { }
    }

    public struct RemoteConnectionStateArgs
    {
        public RemoteConnectionStateArgs(
            RemoteConnectionState connectionState,
            int connectionId,
            int transportIndex
        ) { }
    }

    public struct ClientReceivedDataArgs
    {
        public ClientReceivedDataArgs(
            System.ArraySegment<byte> data,
            Channel channel,
            int transportIndex
        ) { }
    }

    public struct ServerReceivedDataArgs
    {
        public ServerReceivedDataArgs(
            System.ArraySegment<byte> data,
            Channel channel,
            int connectionId,
            int transportIndex
        ) { }
    }

    // The real engine declares the events and methods abstract (oblivious
    // nullability); the nullable annotations match the bridge's overrides
    // so the lane stays strict. An abstract member the bridge drops is a
    // compile failure here - the same failure the editor would show.
    public abstract class Transport : UnityEngine.MonoBehaviour
    {
        public global::FishNet.Managing.NetworkManager NetworkManager { get; private set; } =
            null!;

        public int Index { get; private set; }

        public virtual void Initialize(
            global::FishNet.Managing.NetworkManager networkManager,
            int transportIndex
        )
        {
            NetworkManager = networkManager;
            Index = transportIndex;
        }

        public abstract event System.Action<ClientConnectionStateArgs>? OnClientConnectionState;

        public abstract event System.Action<ServerConnectionStateArgs>? OnServerConnectionState;

        public abstract event System.Action<RemoteConnectionStateArgs>? OnRemoteConnectionState;

        public abstract event System.Action<ClientReceivedDataArgs>? OnClientReceivedData;

        public abstract event System.Action<ServerReceivedDataArgs>? OnServerReceivedData;

        public abstract void HandleClientConnectionState(
            ClientConnectionStateArgs connectionStateArgs
        );

        public abstract void HandleServerConnectionState(
            ServerConnectionStateArgs connectionStateArgs
        );

        public abstract void HandleRemoteConnectionState(
            RemoteConnectionStateArgs connectionStateArgs
        );

        public abstract void HandleClientReceivedDataArgs(
            ClientReceivedDataArgs receivedDataArgs
        );

        public abstract void HandleServerReceivedDataArgs(
            ServerReceivedDataArgs receivedDataArgs
        );

        public abstract LocalConnectionState GetConnectionState(bool server);

        public abstract RemoteConnectionState GetConnectionState(int connectionId);

        public abstract string GetConnectionAddress(int connectionId);

        public abstract int GetMTU(byte channel);

        public virtual bool IsLocalTransport(int connectionId) => false;

        public abstract bool StartConnection(bool server);

        public abstract bool StopConnection(bool server);

        public abstract bool StopConnection(int connectionId, bool immediately);

        public abstract void Shutdown();

        public abstract void SendToServer(byte channelId, System.ArraySegment<byte> segment);

        public abstract void SendToClient(
            byte channelId,
            System.ArraySegment<byte> segment,
            int connectionId
        );

        public abstract void IterateIncoming(bool asServer);

        public abstract void IterateOutgoing(bool asServer);
    }
}
'@

# Shape stubs for the Mirror bridge compile lane: the pinned member
# surface the transport bridge and the room-manager bootstrap touch.
# Same contract as the FishNet stubs above.
$mirrorBridgeStubs = @'
// Shape stubs (UnityEngine / Mirror surface the bridge and the room
// manager touch). The engine SDK is never vendored or referenced in CI.
#nullable enable
namespace UnityEngine
{
    public class MonoBehaviour { }

    public sealed class SerializeFieldAttribute : System.Attribute { }

    public sealed class HeaderAttribute : System.Attribute
    {
        public HeaderAttribute(string header) { }
    }

    public sealed class TooltipAttribute : System.Attribute
    {
        public TooltipAttribute(string tooltip) { }
    }

    public sealed class DisallowMultipleComponentAttribute : System.Attribute { }

    public static class Debug
    {
        public static void LogError(object message) { }

        public static void LogWarning(object message) { }

        public static void LogException(System.Exception exception) { }
    }
}

namespace Mirror
{
    // Real shapes, pinned against the Mirror v96.9.x source (the channel
    // ids double as the shared header's channel bytes: Channels.Reliable
    // = 0, Channels.Unreliable = 1 - the channel pin restates these
    // against the pinned engine version).
    public static class Channels
    {
        public const int Reliable = 0;

        public const int Unreliable = 1;
    }

    public enum TransportError : byte
    {
        DnsResolve,
        Refused,
        Timeout,
        Congestion,
        InvalidReceive,
        InvalidSend,
        ConnectionClosed,
        Unexpected,
    }

    public class NetworkManager
    {
        public string networkAddress = "localhost";

        public void StartHost() { }

        public void StartClient() { }
    }

    // The real engine's callbacks are plain public Action fields (not
    // `event`s - transports raise them directly), and its abstract
    // methods are oblivious under Unity's default nullable settings; the
    // nullable annotations here match the bridge's overrides so the lane
    // stays strict. A member the bridge touches wrongly is a compile
    // failure here - the same failure the editor would show.
    public abstract class Transport : UnityEngine.MonoBehaviour
    {
        public System.Action? OnClientConnected;

        public System.Action<System.ArraySegment<byte>, int>? OnClientDataReceived;

        public System.Action<System.ArraySegment<byte>, int>? OnClientDataSent;

        public System.Action<TransportError, string>? OnClientError;

        public System.Action? OnClientDisconnected;

        public System.Action<System.Exception>? OnClientTransportException;

        public System.Action<int, string>? OnServerConnectedWithAddress;

        public System.Action<int, System.ArraySegment<byte>, int>? OnServerDataReceived;

        public System.Action<int, System.ArraySegment<byte>, int>? OnServerDataSent;

        public System.Action<int, TransportError, string>? OnServerError;

        public System.Action<int>? OnServerDisconnected;

        public abstract bool Available();

        public abstract System.Uri ServerUri();

        public abstract bool ServerActive();

        public abstract bool ClientConnected();

        public abstract void ClientConnect(string address);

        public abstract void ClientSend(
            System.ArraySegment<byte> segment,
            int channelId = Channels.Reliable
        );

        public abstract void ClientDisconnect();

        public abstract void ServerStart();

        public abstract void ServerSend(
            int connectionId,
            System.ArraySegment<byte> segment,
            int channelId = Channels.Reliable
        );

        public abstract void ServerDisconnect(int connectionId);

        public abstract string ServerGetClientAddress(int connectionId);

        public abstract void ServerStop();

        public abstract int GetMaxPacketSize(int channelId = Channels.Reliable);

        public abstract void Shutdown();

        public virtual void ClientEarlyUpdate() { }

        public virtual void ServerEarlyUpdate() { }

        public virtual void ClientLateUpdate() { }

        public virtual void ServerLateUpdate() { }
    }
}
'@

# Shape stubs for the PUN2 bridge compile lane: the pinned member
# surface the bootstrap touches, spelled the way the bootstrap uses it.
# Same contract as the stubs above; members pinned against the PUN 2.31
# source (PhotonNetwork.JoinOrCreateRoom returns bool, RoomOptions.
# MaxPlayers is a byte field, TypedLobby.Default is a static readonly
# reference, the PUN callbacks are virtual on MonoBehaviourPunCallbacks).
$pun2BridgeStubs = @'
// Shape stubs (UnityEngine / Photon.Pun / Photon.Realtime surface the
// bootstrap touches). The engine SDK is never vendored or referenced in CI.
#nullable enable
namespace UnityEngine
{
    public class MonoBehaviour { }

    public static class Debug
    {
        public static void LogWarning(object message) { }
    }
}

namespace Photon.Realtime
{
    // Real member names, pinned against the PUN 2.31 source. The bridge
    // only formats this enum into messages (it never switches on a
    // value), so the member list is the pin, not the numeric values.
    public enum DisconnectCause
    {
        None,
        ExceptionOnConnect,
        DnsExceptionOnConnect,
        ServerAddressInvalid,
        Exception,
        ServerTimeout,
        ClientTimeout,
        DisconnectByServerLogic,
        DisconnectByServerReasonUnknown,
        InvalidAuthentication,
        CustomAuthenticationFailed,
        AuthenticationTicketExpired,
        MaxCcuReached,
        InvalidRegion,
        OperationNotAllowedInCurrentState,
        DisconnectByClientLogic,
        DisconnectByOperationLimit,
        DisconnectByDisconnectMessage,
    }

    public class Room
    {
        public string Name => string.Empty;
    }

    public class RoomOptions
    {
        public byte MaxPlayers;
    }

    public class TypedLobby
    {
        public static readonly TypedLobby Default = new TypedLobby();
    }
}

namespace Photon.Pun
{
    public class MonoBehaviourPun : UnityEngine.MonoBehaviour { }

    // The real callbacks are public virtual on MonoBehaviourPunCallbacks;
    // an override the bootstrap drops is a silent runtime miss in the
    // editor, so the pinned surface is what the lane type-checks.
    public class MonoBehaviourPunCallbacks : MonoBehaviourPun
    {
        public virtual void OnConnectedToMaster() { }

        public virtual void OnDisconnected(global::Photon.Realtime.DisconnectCause cause) { }

        public virtual void OnJoinedRoom() { }

        public virtual void OnLeftRoom() { }

        public virtual void OnCreateRoomFailed(short returnCode, string message) { }

        public virtual void OnJoinRoomFailed(short returnCode, string message) { }
    }

    public static class PhotonNetwork
    {
        public static global::Photon.Realtime.Room? CurrentRoom => null;

        public static bool ConnectUsingSettings() => false;

        public static bool JoinOrCreateRoom(
            string roomName,
            global::Photon.Realtime.RoomOptions roomOptions,
            global::Photon.Realtime.TypedLobby typedLobby
        ) => false;

        public static bool JoinRoom(string roomName) => false;

        public static void Disconnect() { }
    }
}
'@

# Shape stubs for the Fusion bridge compile lane: the pinned member
# surface the bootstrap touches, spelled the way the bootstrap uses it.
# Same contract as the stubs above; members pinned against the Fusion 2
# runtime source (NetworkRunner.StartGame returns Task<StartGameResult>,
# StartGameArgs is a struct with GameMode/SessionName/PlayerCount/
# EnableClientSessionCreation fields, INetworkRunnerCallbacks carries
# the 19 members implemented here, Fusion's runtime ships as precompiled
# DLLs so the lane models the plugin surface the adapter references).
$fusionBridgeStubs = @'
// Shape stubs (UnityEngine / Fusion / Fusion.Sockets surface the
// bootstrap touches). The engine SDK is never vendored or referenced in CI.
#nullable enable
namespace UnityEngine
{
    public class MonoBehaviour { }

    public static class Debug
    {
        public static void LogWarning(object message) { }
    }

    public sealed class GameObject
    {
        public GameObject(string name) { }

        public T AddComponent<T>()
            where T : MonoBehaviour => null!;
    }
}

namespace Fusion
{
    // Real member names, pinned against the Fusion 2 runtime source. The
    // bootstrap only formats this enum into messages (it never switches on
    // a value), so the member list is the pin, not the numeric values.
    public enum ShutdownReason
    {
        Ok,
        Error,
        IncompatibleConfiguration,
        ServerInRoom,
        DisconnectedByPluginLogic,
        GameClosed,
        GameNotFound,
        MaxCcuReached,
        InvalidRegion,
        GameIdAlreadyExists,
        GameIsFull,
        InvalidAuthentication,
        CustomAuthenticationFailed,
        AuthenticationTicketExpired,
        PhotonCloudTimeout,
        AlreadyRunning,
        InvalidArguments,
        HostMigration,
        ConnectionTimeout,
        ConnectionRefused,
        OperationTimeout,
        OperationCanceled,
    }

    // Real members, pinned against the Fusion 2 runtime source
    // (Single = 1, Shared, Server, Host, Client, AutoHostOrClient).
    public enum GameMode
    {
        Single = 1,
        Shared,
        Server,
        Host,
        Client,
        AutoHostOrClient,
    }

    public struct PlayerRef { }

    public struct NetworkInput { }

    public struct SimulationMessagePtr { }

    public struct ReliableKey { }

    public struct HostMigrationToken { }

    public class NetworkObject : UnityEngine.MonoBehaviour { }

    public class SessionInfo
    {
        public string Name => string.Empty;
    }

    public class NetworkRunnerCallbackArgs
    {
        public sealed class ConnectRequest { }
    }

    public class StartGameResult
    {
        public bool Ok => false;

        public ShutdownReason ShutdownReason => ShutdownReason.Error;

        public string ErrorMessage => string.Empty;
    }

    public struct StartGameArgs
    {
        public GameMode GameMode;

        public string SessionName;

        public int? PlayerCount;

        public bool? EnableClientSessionCreation;
    }

    // The real callbacks are the registration surface a bootstrap must
    // implement in full (AddCallbacks takes this interface); a member the
    // bootstrap drops is a compile miss in the editor, so the pinned
    // surface is what the lane type-checks.
    public interface INetworkRunnerCallbacks
    {
        void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player);

        void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player);

        void OnPlayerJoined(NetworkRunner runner, PlayerRef player);

        void OnPlayerLeft(NetworkRunner runner, PlayerRef player);

        void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason);

        void OnDisconnectedFromServer(NetworkRunner runner, global::Fusion.Sockets.NetDisconnectReason reason);

        void OnConnectRequest(
            NetworkRunner runner,
            NetworkRunnerCallbackArgs.ConnectRequest request,
            byte[] token
        );

        void OnConnectFailed(
            NetworkRunner runner,
            global::Fusion.Sockets.NetAddress remoteAddress,
            global::Fusion.Sockets.NetConnectFailedReason reason
        );

        void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message);

        void OnReliableDataReceived(
            NetworkRunner runner,
            PlayerRef player,
            ReliableKey key,
            System.ArraySegment<byte> data
        );

        void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress);

        void OnInput(NetworkRunner runner, NetworkInput input);

        void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input);

        void OnConnectedToServer(NetworkRunner runner);

        void OnSessionListUpdated(NetworkRunner runner, System.Collections.Generic.List<SessionInfo> sessionList);

        void OnCustomAuthenticationResponse(
            NetworkRunner runner,
            System.Collections.Generic.Dictionary<string, object> data
        );

        void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken);

        void OnSceneLoadDone(NetworkRunner runner);

        void OnSceneLoadStart(NetworkRunner runner);
    }

    // The real runner derives from Behaviour; the lane models the
    // MonoBehaviour surface the bootstrap's GameObject.AddComponent needs.
    public class NetworkRunner : UnityEngine.MonoBehaviour
    {
        public void AddCallbacks(params INetworkRunnerCallbacks[] callbacks) { }

        public System.Threading.Tasks.Task<StartGameResult> StartGame(StartGameArgs args) => null!;

        public System.Threading.Tasks.Task Shutdown(
            bool destroyGameObject = true,
            ShutdownReason shutdownReason = ShutdownReason.Ok,
            bool forceShutdownProcedure = false
        ) => null!;
    }
}

namespace Fusion.Sockets
{
    // Real member names, pinned against the Fusion 2 runtime source.
    public enum NetDisconnectReason : byte
    {
        Unknown = 1,
        Timeout,
        Requested,
        SequenceOutOfBounds,
        SendWindowFull,
        ByRemote,
    }

    public enum NetConnectFailedReason : byte
    {
        Timeout = 1,
        ServerFull,
        ServerRefused,
    }

    public struct NetAddress { }
}
'@

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
        BridgeCompile = $true
        BridgeStubs = $fishNetBridgeStubs
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
        BridgeCompile = $true
        BridgeStubs = $mirrorBridgeStubs
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
        BridgeCompile = $true
        BridgeStubs = $ngoBridgeStubs
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
    Pun2 = @{
        Root = 'unity/Adapters/Pun2'
        Define = 'SIGNALFISH_PUN2'
        SdkNamespace = 'Photon'
        SdkReferencePattern = '(^|[^\w.])Photon\.'
        SdkReference = @('PhotonUnityNetworking', 'PhotonRealtime')
        CoreReference = 'SignalFish.Adapters.Core'
        CorePackage = 'com.ambiguous-interactive.signalfish.adapters.core'

        # The bootstrap is not a transport bridge: it never frames engine
        # payloads, so there is no engine channel pin. Its wire surface
        # is the package's own room-name envelope, which the bridge must
        # go through rather than hand-rolling. PUN2 ships as an asset
        # (no UPM package), so the editor define detector owns the
        # define and a package pin would be a lie.
        WirePin = 'Pun2RoomEnvelope.'
        ChannelPin = $null
        PackagePin = $null
        PackageExpression = $null
        BridgeFile = 'SignalFishPun2Bootstrap.cs'
        BridgeCompile = $true
        BridgeStubs = $pun2BridgeStubs
        DetectorFile = 'SignalFishPun2DefineDetector.cs'
        DetectorProbes = @('PhotonUnityNetworking')
        PinnedMembers = @(
            'ConnectUsingSettings',
            'JoinOrCreateRoom',
            'JoinRoom',
            'CurrentRoom',
            'Disconnect',
            'RoomOptions',
            'MaxPlayers',
            'TypedLobby',
            'OnConnectedToMaster',
            'OnJoinedRoom',
            'OnLeftRoom',
            'OnCreateRoomFailed',
            'OnJoinRoomFailed',
            'OnDisconnected'
        )
    }
    Fusion = @{
        Root = 'unity/Adapters/Fusion'
        Define = 'SIGNALFISH_FUSION'
        SdkNamespace = 'Fusion'
        SdkReferencePattern = '(^|[^\w.])Fusion(\.[A-Za-z_]|;)'
        SdkReference = @('Fusion.Runtime', 'Fusion.Sockets')
        CoreReference = 'SignalFish.Adapters.Core'
        CorePackage = 'com.ambiguous-interactive.signalfish.adapters.core'

        # The bootstrap is not a transport bridge: it never frames engine
        # payloads, so there is no engine channel pin. Its wire surface
        # is the package's own session-name envelope, which the bridge
        # must go through rather than hand-rolling. Fusion ships as an
        # asset (precompiled DLLs under Assets/Photon/Fusion, not a UPM
        # package the Package Manager knows), so the editor define
        # detector owns the define and a package pin would be a lie.
        WirePin = 'FusionSessionEnvelope.'
        ChannelPin = $null
        PackagePin = $null
        PackageExpression = $null
        BridgeFile = 'SignalFishFusionBootstrap.cs'
        BridgeCompile = $true
        BridgeStubs = $fusionBridgeStubs
        DetectorFile = 'SignalFishFusionDefineDetector.cs'
        DetectorProbes = @('Fusion.Runtime')
        PinnedMembers = @(
            'StartGame',
            'StartGameArgs',
            'GameMode.Host',
            'GameMode.Client',
            'SessionName',
            'PlayerCount',
            'EnableClientSessionCreation',
            'AddCallbacks',
            'Shutdown',
            'ErrorMessage',
            'OnConnectFailed',
            'OnDisconnectedFromServer',
            'OnShutdown',
            'OnHostMigration'
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

function Invoke-BridgeCompile {
    param(
        [hashtable]$Pin,
        [string]$Base,
        [string[]]$SourcePaths,
        [string]$Stubs
    )

    # Type-checks an engine-gated bridge against a shape stub of the
    # pinned engine surface. No compiler in this repo sees the real
    # SDK, so a blind edit's type error would otherwise surface only
    # in the editor. Returns the failure message, if any.
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) {
        return 'compile check requested but dotnet was not found on PATH.'
    }

    $stage = Join-Path ([System.IO.Path]::GetTempPath()) (
        "bridgecompile-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    try {
        $compileItems = @(
            foreach ($source in $SourcePaths) {
                $path = $source -replace '\\', '/'
                "        <Compile Include=`"$path`" />"
            }
        )
        $clientProject = (Join-Path $Base 'src/SignalFish.Client/SignalFish.Client.csproj') -replace '\\', '/'
        $projectLines = @(
            '<Project Sdk="Microsoft.NET.Sdk">',
            '    <PropertyGroup>',
            '        <TargetFramework>netstandard2.1</TargetFramework>',
            '        <LangVersion>9.0</LangVersion>',
            '        <Nullable>enable</Nullable>',
            '        <EnableDefaultCompileItems>false</EnableDefaultCompileItems>',
            '        <ImplicitUsings>disable</ImplicitUsings>',
            '        <TreatWarningsAsErrors>true</TreatWarningsAsErrors>',
            "        <DefineConstants>$($Pin.Define)</DefineConstants>",
            '    </PropertyGroup>',
            '    <ItemGroup>'
        ) + $compileItems + @(
            '        <Compile Include="EngineStubs.cs" />',
            '    </ItemGroup>',
            '    <ItemGroup>',
            "        <ProjectReference Include=`"$clientProject`" />",
            '    </ItemGroup>',
            '</Project>',
            ''
        )
        [System.IO.File]::WriteAllText(
            (Join-Path $stage 'AdapterBridge.csproj'),
            (@($projectLines) -join "`n"),
            [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText(
            (Join-Path $stage 'EngineStubs.cs'),
            $Stubs,
            [System.Text.UTF8Encoding]::new($false))

        $buildOutput = & dotnet build (Join-Path $stage 'AdapterBridge.csproj') -c Release --nologo -v q 2>&1
        if ($LASTEXITCODE -ne 0) {
            foreach ($line in @($buildOutput | Select-Object -Last 20)) {
                Write-Host "    $line"
            }
            return "$($Pin.BridgeFile) failed to compile against the $($Pin.SdkNamespace) shape stub (netstandard2.1, C# 9, nullable, warnings as errors, $($Pin.Define) defined) - a type error in engine-gated code is invisible to every other compiler in this repo."
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

            # SdkReference may be one assembly or a list: a bootstrap that
            # touches two SDK assemblies (Pun2: PhotonUnityNetworking +
            # PhotonRealtime) pins both, so dropping either passes only
            # the lint's absence, never the editor's.
            foreach ($sdkReference in @($Pin.SdkReference)) {
                if (@(Get-JsonArray -Json $json -Property 'references') -notcontains $sdkReference) {
                    $violations.Add(
                        "$([System.IO.Path]::GetFileName($asmdef)) : references $($Pin.SdkNamespace) types but does not reference the $sdkReference assembly.")
                }
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

    # 6. Bridge compile: type-check the engine-gated bridge against a
    #    shape stub of the pinned engine surface. No compiler in this
    #    repo sees the real SDK - a blind edit's type error (a void-task
    #    await, a missing using) would otherwise surface only in the
    #    editor. Skipped with -NoBuild; CI always runs it.
    if (-not $SkipBuild -and $null -ne $Pin['BridgeCompile']) {
        $sharedRoot = Join-Path $RepoRoot $sharedCore.Root
        $sharedFiles = [string[]]@(Get-ChildItem -LiteralPath $sharedRoot -Recurse -File -Filter '*.cs' |
            ForEach-Object { $_.FullName })
        $runtimeSources = [string[]]@($sourceFiles | Where-Object { $_ -match '[\\/]Runtime[\\/]' })
        $failure = Invoke-BridgeCompile `
            -Pin $Pin `
            -Base $RepoRoot `
            -SourcePaths (@($sharedFiles) + @($runtimeSources)) `
            -Stubs $Pin['BridgeStubs']
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
