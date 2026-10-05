#if SIGNALFISH_PUN2
using System;
using SignalFish.Client.Adapters.Pun2;
using UnityEngine;

namespace SignalFish.Client.Adapters.Pun2.Samples
{
    /// <summary>
    /// Wires the PUN2 room bootstrap to a UI: the host creates the PUN
    /// room and the bootstrap publishes its name over the Signal Fish
    /// room; a client joins by Signal Fish room code and the bootstrap
    /// joins the PUN room whose name arrives. Start host or client from
    /// your UI; the bootstrap performs every PUN call itself, so no
    /// extra wiring is needed — Photon's own settings (the
    /// PhotonServerSettings asset) supply the AppId and region.
    /// </summary>
    public sealed class Pun2RoomNameExchangeSample : MonoBehaviour
    {
        [SerializeField]
        private SignalFishPun2Bootstrap bootstrap = null!;

        [SerializeField]
        private string roomCode = string.Empty;

        public async void StartHost()
        {
            try
            {
                await bootstrap.StartHostAsync();
                Debug.Log(
                    $"Hosting room {bootstrap.JoinedRoomCode} on PUN room {bootstrap.PhotonRoomName}."
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
                    $"Joined room {bootstrap.JoinedRoomCode} on PUN room {bootstrap.PhotonRoomName}."
                );
            }
            catch (Exception failure)
            {
                Debug.LogError($"The client start failed: {failure.Message}");
            }
        }

        private void OnEnable()
        {
            bootstrap.PunRoomNameReceived += LogRoomName;
            bootstrap.CoordinationFailed += LogFailure;
        }

        private void OnDisable()
        {
            bootstrap.PunRoomNameReceived -= LogRoomName;
            bootstrap.CoordinationFailed -= LogFailure;
        }

        private void OnDestroy()
        {
            bootstrap.Shutdown();
        }

        private static void LogRoomName(string roomName)
        {
            Debug.Log($"The host published PUN room '{roomName}'; joining it.");
        }

        private static void LogFailure(string reason)
        {
            Debug.LogWarning($"The coordinated session failed: {reason}");
        }
    }
}
#endif
