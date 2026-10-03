#nullable enable

namespace SignalFish.Samples
{
    using System;
    using System.Text;
    using System.Threading.Tasks;
    using SignalFish.Client.Core;
    using SignalFish.Client.Polling;
    using SignalFish.Client.Protocol;
    using SignalFish.Client.Transport;
    using UnityEngine;

    /// <summary>
    /// A minimal frame-driven Signal Fish session for a Unity scene: the
    /// component connects, authenticates, and joins (or creates) a room on
    /// start, then runs the client once per frame —
    /// <see cref="SignalFishPollingClient.Poll"/> in <c>Update()</c>, and a
    /// <c>foreach</c> over <see cref="SignalFishPollingClient.DrainEvents"/>
    /// to dispatch each polled event. The polling client is deliberately
    /// single-threaded and Unity calls <c>Update</c> on the main thread, so
    /// every handler below is already main-thread — no marshaling; just
    /// keep the handlers cheap and pick leftover work up on the next
    /// frame. WebGL note: <see cref="WebSocketTransport"/> wraps
    /// <c>ClientWebSocket</c>, which does not exist on WebGL; a WebGL
    /// build injects a browser-WebSocket <c>ITransport</c> instead (see
    /// the package documentation).
    /// </summary>
    public sealed class SignalFishPollingDriver : MonoBehaviour
    {
        [SerializeField]
        private string endpoint = "ws://localhost:8080";

        [SerializeField]
        private string appId = "unity-sample";

        [SerializeField]
        private string gameName = "sample-game";

        [SerializeField]
        private string playerName = "player";

        /// <summary>Empty joins-or-creates with a server-generated code.</summary>
        [SerializeField]
        private string roomCode = string.Empty;

        private SignalFishPollingClient? client;

        /*
            Anchored only after ConnectAsync resolves: the client's
            heartbeat clock starts at connect, so polling before that
            would time a not-yet-open transport out.
        */
        private bool connected;

        /// <summary>
        /// Sends one reliable relay payload (UTF-8 JSON) to the room. Call
        /// from gameplay code any time after the RoomJoined event.
        /// </summary>
        public void SendHello()
        {
            if (client is null)
            {
                return;
            }

            byte[] payload = Encoding.UTF8.GetBytes("{\"sample\":\"hello\"}");
            Admit(client.SendGameData(new GameDataMessage(payload)));
        }

        private async void Start()
        {
            client = new SignalFishPollingClient(new WebSocketTransport(), SystemClock.Instance);
            try
            {
                await client.ConnectAsync(new Uri(endpoint));
            }
            catch (Exception e)
            {
                /*
                    A failed connect is terminal for this client instance
                    (destroying the component mid-connect also lands here —
                    the aborted socket surfaces as a connect failure).
                */
                Debug.LogException(e);
                return;
            }

            connected = true;

            /*
                Commands are admitted on the calling thread and encoded
                fire-and-forget; a refusal means the command never reached
                the wire (wrong phase), so surface it and keep polling.
            */
            Admit(client.SendAuthenticate(new AuthenticateMessage(appId: appId)));
            Admit(client.SendJoinRoom(new JoinRoomMessage(gameName, playerName, RoomCodeOrNull())));
        }

        private void Update()
        {
            if (client is null || !connected)
            {
                return;
            }

            client.Poll();
            foreach (PollEvent ev in client.DrainEvents())
            {
                Handle(ev);
            }
        }

        private void OnDestroy()
        {
            if (client is not null)
            {
                _ = client.DisposeAsync();
                client = null;
            }
        }

        private void Handle(PollEvent ev)
        {
            switch (ev.Kind)
            {
                case PollEventKind.RoomJoined:
                    Debug.Log(
                        $"[SignalFish] joined room {ev.Membership.RoomCode} as {ev.Membership.Role}"
                    );
                    break;
                case PollEventKind.GameData:
                    string payload = Encoding.UTF8.GetString(ev.GameData.Payload.Span);
                    Debug.Log($"[SignalFish] game data from {ev.GameData.FromPlayer}: {payload}");
                    break;
                case PollEventKind.Disconnected:
                    Debug.Log(
                        $"[SignalFish] session terminal ({ev.Close.Kind}); re-add the component to reconnect."
                    );
                    break;
                case PollEventKind.ServerError:
                case PollEventKind.RoomJoinFailed:
                    Debug.LogWarning(
                        $"[SignalFish] {ev.Kind}: {ev.Failure.Reason} ({ev.Failure.ErrorCode})"
                    );
                    break;
                default:
                    Debug.Log($"[SignalFish] {ev.Kind}");
                    break;
            }
        }

        private void Admit(CommandSend send)
        {
            if (!send.Accepted)
            {
                Debug.LogWarning($"[SignalFish] command refused: {send.Refusal}");
            }
        }

        private string? RoomCodeOrNull()
        {
            return string.IsNullOrWhiteSpace(roomCode) ? null : roomCode;
        }
    }
}
