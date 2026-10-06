#if SIGNALFISH_FACEPUNCH
using System;
using SignalFish.Client.Adapters.Facepunch;
using UnityEngine;

namespace SignalFish.Client.Adapters.Facepunch.Samples
{
    /// <summary>
    /// Wires the Steam identity bootstrap to a UI: the host joins the
    /// Signal Fish room, opens the Steam P2P relay socket, and
    /// publishes its SteamId64; a client joins by room code and dials
    /// the host id that arrives. Start host or client from your UI
    /// after the Facepunch Steamworks API is initialized (your
    /// SteamManager); the bootstrap performs every Steam call itself
    /// and hands you each fenced peer's SteamId64 as it connects.
    /// </summary>
    public sealed class SteamIdentityExchangeSample : MonoBehaviour
    {
        [SerializeField]
        private SignalFishFacepunchSteamIdentityBootstrap bootstrap = null!;

        [SerializeField]
        private string roomCode = string.Empty;

        public async void StartHost()
        {
            try
            {
                await bootstrap.StartHostAsync();
                Debug.Log(
                    $"Hosting room {bootstrap.JoinedRoomCode} as Steam user {bootstrap.LocalSteamId}."
                );
            }
            catch (Exception failure)
            {
                Debug.LogError($"The host start failed: {failure.Message}");
            }
        }

        public async void StartClient()
        {
            try
            {
                await bootstrap.StartClientAsync(roomCode);
                Debug.Log(
                    $"Joined room {bootstrap.JoinedRoomCode}; dialed the host at {bootstrap.HostSteamId}."
                );
            }
            catch (Exception failure)
            {
                Debug.LogError($"The client start failed: {failure.Message}");
            }
        }

        private void OnEnable()
        {
            bootstrap.SteamPeerConnected += LogPeerConnected;
            bootstrap.SteamPeerDisconnected += LogPeerDisconnected;
            bootstrap.SteamHostIdReceived += LogHostId;
            bootstrap.CoordinationFailed += LogFailure;
        }

        private void OnDisable()
        {
            bootstrap.SteamPeerConnected -= LogPeerConnected;
            bootstrap.SteamPeerDisconnected -= LogPeerDisconnected;
            bootstrap.SteamHostIdReceived -= LogHostId;
            bootstrap.CoordinationFailed -= LogFailure;
        }

        private void OnDestroy()
        {
            bootstrap.Shutdown();
        }

        private static void LogPeerConnected(ulong steamId)
        {
            Debug.Log($"Peer {steamId} connected over Steam; the room fenced them in.");
        }

        private static void LogPeerDisconnected(ulong steamId)
        {
            Debug.Log($"Peer {steamId}'s Steam connection closed.");
        }

        private static void LogHostId(ulong steamId)
        {
            Debug.Log($"The host published SteamId64 {steamId}; dialing it.");
        }

        private static void LogFailure(string reason)
        {
            Debug.LogWarning($"The coordinated session failed: {reason}");
        }
    }
}
#endif
