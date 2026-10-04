# Movement Sync sample

A two-player movement loop over the Signal Fish bridge: the host starts a
Signal Fish room, the guest joins by room code, and FishNet replicates a
capsule pose through the bridge's v3 binary relay lane.

## Scene setup

1. Install the packages: `com.ambiguous-interactive.signalfish`, this
   adapter, and FishNet (its git URL), then let Unity compile.
2. Add a `NetworkManager` (FishNet) to the scene.
3. Under **TransportManager > Transports**, add `SignalFishFishNetTransport`
   and remove any other transport.
4. Fill the session fields: `Endpoint` (your Signal Fish server, e.g.
   `ws://127.0.0.1:8080/v2/ws`), `GameName`, and `PlayerName`.
5. Create a capsule with a `NetworkObject` and the `MovementSyncPlayer`
   component; make it a player prefab in the NetworkManager's spawnable
   prefabs.

## Run a session

1. **Host**: leave `Room Code` empty and call `NetworkManager.ServerManager.StartConnection()` +
   `ClientManager.StartConnection()` (or the host menu). The bridge creates the
   room, requests the authority, and reports Started; read
   `JoinedRoomCode` for the code to share.
2. **Guest**: set `Room Code` to the host's code and start a client. The
   bridge joins the room and the FishNet client connects; movement syncs
   once the pose stream starts.

## Notes

- The sample reads the legacy Input Manager (`Input.GetAxis`): set
  **Active Input Handling** to include the old Input Manager, or swap
  the reads for your input package.
- Both channels ride the relay's reliable binary lane in this version;
  the unreliable channel is preserved end-to-end and documented in
  [the adapter page](https://ambiguous-interactive.github.io/signal-fish-client-dotnet/adapters/fishnet/).
- On WebGL, set `TransportFactory` to return the package's browser
  WebSocket transport before starting the connection.
- Validate live behavior with the two-client loopback runbook (M8.7)
  before shipping.
