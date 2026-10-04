# Late Join and Reconnect sample

The smallest end-to-end loop for the Mirror adapter: one machine hosts
(creating the Signal Fish room and taking the authority), everyone else
joins by room code — at any time — and a dropped client rejoins with the
same code.

## Scene setup

1. Install `com.ambiguous-interactive.signalfish`, the Mirror asset, and
   this adapter. Let the editor compile twice: the define detector lights
   the adapter up after Mirror's first compile.
2. Create an empty scene with a `NetworkManager` (Mirror) and put
   `SignalFishMirrorTransport` on the same GameObject, under the
   NetworkManager's transport list.
3. Fill the transport's session fields: `Endpoint` (the v2 relay floor;
   v3 is negotiated on top), `GameName`, `PlayerName`.
4. Add a `SignalFishRoomManager` and assign the NetworkManager and the
   transport.
5. Add `LateJoinReconnectDriver` and assign the room manager.

## Flow

- Call `Host()` on the host machine. The transport creates the room;
  read `JoinedRoomCode` from the manager and share the code.
- Guests call `Join(code)` — before the first round or any time later;
  Mirror spawns the player objects, the relay carries the traffic.
- With `AutoRejoin` on, a guest whose session drops rejoins the same room
  automatically after the configured delay.

The player object networked over this session is whatever you spawn
yourself — the bridge carries bytes, not game code.
