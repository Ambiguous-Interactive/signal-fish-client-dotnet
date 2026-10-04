/*
    SIGNALFISH_MIRROR is produced by this package's editor define
    detector; the assembly is skipped without it (see the transport).
*/
#nullable enable
#if SIGNALFISH_MIRROR
namespace SignalFish.Client.Adapters.Mirror
{
    using Mirror;
    using UnityEngine;

    /// <summary>
    /// The M8.2 bootstrap: the thinnest possible glue between a
    /// <see cref="NetworkManager"/> and the Signal Fish transport, so a
    /// game only ever talks to Mirror's own API. Start the host through
    /// <see cref="StartHost"/> (the transport creates the room and
    /// requests the authority); join through <see cref="StartClient"/>
    /// with a shareable room code — late joiners call it at any time, and
    /// a dropped client calls it again with the same code to rejoin.
    /// Read <see cref="JoinedRoomCode"/> on the host to share the code.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SignalFishRoomManager : MonoBehaviour
    {
        /// <summary>Gets or sets the NetworkManager this manager drives.</summary>
        public NetworkManager NetworkManager
        {
            get { return _networkManager; }
            set { _networkManager = value; }
        }

        /// <summary>Gets or sets the Signal Fish transport under the NetworkManager.</summary>
        public SignalFishMirrorTransport Transport
        {
            get { return _transport; }
            set { _transport = value; }
        }

        /// <summary>Gets the joined room's shareable code, when one is joined.</summary>
        public string? JoinedRoomCode
        {
            get { return _transport == null ? null : _transport.JoinedRoomCode; }
        }

        [SerializeField]
        [Tooltip("The NetworkManager whose transport list carries the Signal Fish transport.")]
        private NetworkManager _networkManager = null!;

        [SerializeField]
        [Tooltip("The Signal Fish Mirror transport under the NetworkManager.")]
        private SignalFishMirrorTransport _transport = null!;

        /// <summary>
        /// Starts the host path: Mirror starts its server and local
        /// client, and the transport creates the room (or joins the
        /// transport's configured <c>RoomCode</c>) and requests the
        /// authority. The room code surfaces on <see cref="JoinedRoomCode"/>
        /// once the room is live.
        /// </summary>
        public void StartHost()
        {
            RequireWired();
            _networkManager.StartHost();
        }

        /// <summary>
        /// Starts the client path: joins the Signal Fish room with
        /// <paramref name="roomCode"/> and lets Mirror connect as a
        /// client. Late joiners call this at any time; a dropped client
        /// calls it again with the same code to rejoin.
        /// </summary>
        public void StartClient(string roomCode)
        {
            RequireWired();
            _networkManager.networkAddress = roomCode;
            _networkManager.StartClient();
        }

        private void RequireWired()
        {
            if (_networkManager == null || _transport == null)
            {
                throw new InvalidOperationException(
                    "SignalFishRoomManager needs both a NetworkManager and the Signal Fish transport assigned."
                );
            }
        }
    }
}
#endif
