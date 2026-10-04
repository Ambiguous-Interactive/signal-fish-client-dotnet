namespace SignalFish.Client.Adapters.Mirror.Samples.LateJoinReconnect
{
    using Mirror;
    using UnityEngine;

    /// <summary>
    /// Drives the late-join and reconnect sample: host, join by room code,
    /// and automatic rejoin after a drop. Attach beside a
    /// <see cref="SignalFishRoomManager"/> and wire the manager reference;
    /// call <see cref="Host"/> and <see cref="Join"/> from your UI.
    /// Requires the Mirror asset and the Signal Fish transport under the
    /// NetworkManager (see the sample README).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LateJoinReconnectDriver : MonoBehaviour
    {
        /// <summary>Gets or sets the room manager this driver drives.</summary>
        public SignalFishRoomManager RoomManager
        {
            get { return _roomManager; }
            set { _roomManager = value; }
        }

        /// <summary>Gets or sets whether a dropped client rejoins automatically.</summary>
        public bool AutoRejoin
        {
            get { return _autoRejoin; }
            set { _autoRejoin = value; }
        }

        [SerializeField]
        [Tooltip("The Signal Fish room manager to drive.")]
        private SignalFishRoomManager _roomManager = null!;

        [SerializeField]
        [Tooltip("Rejoin with the last room code after a drop.")]
        private bool _autoRejoin = true;

        [SerializeField]
        [Tooltip("Seconds between rejoin attempts.")]
        private float _rejoinDelaySeconds = 2f;

        private string _lastRoomCode = "";
        private bool _clientWasConnected;
        private float _rejoinAt = -1f;

        /// <summary>Starts the host: creates the room and requests the authority.</summary>
        public void Host()
        {
            _roomManager.StartHost();
        }

        /// <summary>Joins (or late-joins) the room with the given code.</summary>
        public void Join(string roomCode)
        {
            _lastRoomCode = roomCode;
            _roomManager.StartClient(roomCode);
        }

        private void Update()
        {
            bool connected = NetworkClient.isConnected;
            if (connected)
            {
                _clientWasConnected = true;
                _rejoinAt = -1f;
                return;
            }

            if (!_clientWasConnected || !_autoRejoin || _lastRoomCode.Length == 0)
            {
                return;
            }

            /*
                Mirror reports the client disconnected after a live session
                dropped: schedule one rejoin attempt per delay window, and
                let the transport's StartupError surface a refused join.
            */
            if (_rejoinAt < 0f)
            {
                _rejoinAt = Time.unscaledTime + _rejoinDelaySeconds;
                return;
            }

            if (Time.unscaledTime >= _rejoinAt)
            {
                _clientWasConnected = false;
                _rejoinAt = -1f;
                Debug.Log($"[LateJoinReconnect] Rejoining {_lastRoomCode}...");
                Join(_lastRoomCode);
            }
        }
    }
}
