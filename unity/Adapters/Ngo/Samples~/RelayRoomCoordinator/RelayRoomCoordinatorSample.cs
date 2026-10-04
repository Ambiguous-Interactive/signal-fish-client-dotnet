#if SIGNALFISH_NGO
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using SignalFish.Client.Adapters.Ngo;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

namespace SignalFish.Client.Adapters.Ngo.Samples
{
    /// <summary>
    /// Wires the Signal Fish room coordinator to Unity Gaming Services:
    /// the host allocation hook signs in, allocates a Relay server, binds
    /// the host's Unity Transport, and returns the join code; the client
    /// bind hook does the join-code half. Both hooks run off the main
    /// thread, so the Unity Transport binding is marshaled back through a
    /// main-thread queue. Start host or client from your UI; the
    /// coordinator handles the room, the join code exchange, and
    /// connection approval.
    /// </summary>
    public sealed class RelayRoomCoordinatorSample : MonoBehaviour
    {
        [SerializeField]
        private SignalFishRoomCoordinator coordinator = null!;

        [SerializeField]
        private string roomCode = string.Empty;

        private readonly ConcurrentQueue<Action> _mainThreadWork = new ConcurrentQueue<Action>();

        public async void StartHost()
        {
            try
            {
                await coordinator.StartHostAsync();
                Debug.Log(
                    $"Hosting room {coordinator.JoinedRoomCode} on relay {coordinator.RelayJoinCode}."
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
                await coordinator.StartClientAsync(roomCode);
                Debug.Log($"Joined over room {coordinator.JoinedRoomCode}.");
            }
            catch (Exception failure)
            {
                Debug.LogError($"The client start failed: {failure.Message}");
            }
        }

        private void Update()
        {
            while (_mainThreadWork.TryDequeue(out Action action))
            {
                action();
            }
        }

        private void Start()
        {
            coordinator.RelayAllocationRequest = AllocateRelayServerAsync;
            coordinator.RelayJoinBinder = BindRelayServerAsync;
            coordinator.CoordinationFailed += reason => Debug.LogError(reason);
            coordinator.RelayJoinCodeReceived += _ => Debug.Log("Relay join code received.");
        }

        private Task RunOnMainThreadAsync(Action action)
        {
            TaskCompletionSource<bool> done = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _mainThreadWork.Enqueue(() =>
            {
                try
                {
                    action();
                    done.SetResult(true);
                }
                catch (Exception failure)
                {
                    done.SetException(failure);
                }
            });
            return done.Task;
        }

        private async Task<string> AllocateRelayServerAsync()
        {
            await UnityServicesEnsureSignedInAsync();
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(
                maxConnections: 31,
                region: null
            );
            await RunOnMainThreadAsync(() =>
                NetworkManager
                    .Singleton.GetComponent<UnityTransport>()
                    .SetRelayServerData(new RelayServerData(allocation, "dtls"))
            );
            return allocation.JoinCode;
        }

        private async Task BindRelayServerAsync(string joinCode)
        {
            await UnityServicesEnsureSignedInAsync();
            JoinAllocation join = await RelayService.Instance.JoinAllocationAsync(joinCode);
            await RunOnMainThreadAsync(() =>
                NetworkManager
                    .Singleton.GetComponent<UnityTransport>()
                    .SetRelayServerData(new RelayServerData(join, "dtls"))
            );
        }

        private static async Task UnityServicesEnsureSignedInAsync()
        {
            if (
                Unity.Services.Core.UnityServices.State
                != Unity.Services.Core.ServicesInitializationState.Initialized
            )
            {
                await Unity.Services.Core.UnityServices.InitializeAsync();
            }

            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            }
        }
    }
}
#endif
