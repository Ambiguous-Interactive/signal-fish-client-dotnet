#if SIGNALFISH_FUSION
using System;
using SignalFish.Client.Adapters.Fusion;
using UnityEngine;

namespace SignalFish.Client.Adapters.Fusion.Samples
{
    /// <summary>
    /// Wires the Fusion bootstrap to a UI: the host starts the Fusion
    /// session and the bootstrap publishes its name over the Signal
    /// Fish room; a client joins by Signal Fish room code and the
    /// bootstrap starts its runner with the name that arrives. Start
    /// host or client from your UI; the bootstrap performs every Fusion
    /// call itself, so no extra wiring is needed — Photon's own
    /// settings (the FusionAppSettings asset) supply the AppId and
    /// region.
    /// </summary>
    public sealed class FusionSessionNameExchangeSample : MonoBehaviour
    {
        [SerializeField]
        private SignalFishFusionBootstrap bootstrap = null!;

        [SerializeField]
        private string roomCode = string.Empty;

        public async void StartHost()
        {
            try
            {
                await bootstrap.StartHostAsync();
                Debug.Log(
                    $"Hosting room {bootstrap.JoinedRoomCode} on Fusion session {bootstrap.FusionSessionName}."
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
                    $"Joined room {bootstrap.JoinedRoomCode} on Fusion session {bootstrap.FusionSessionName}."
                );
            }
            catch (Exception failure)
            {
                Debug.LogError($"The client start failed: {failure.Message}");
            }
        }

        private void OnEnable()
        {
            bootstrap.SessionNameReceived += LogSessionName;
            bootstrap.CoordinationFailed += LogFailure;
        }

        private void OnDisable()
        {
            bootstrap.SessionNameReceived -= LogSessionName;
            bootstrap.CoordinationFailed -= LogFailure;
        }

        private void OnDestroy()
        {
            bootstrap.Shutdown();
        }

        private static void LogSessionName(string sessionName)
        {
            Debug.Log($"The host published Fusion session '{sessionName}'; starting it.");
        }

        private static void LogFailure(string reason)
        {
            Debug.LogWarning($"The coordinated session failed: {reason}");
        }
    }
}
#endif
