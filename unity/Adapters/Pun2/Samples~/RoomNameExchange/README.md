# PUN2 Room Name Exchange sample

A two-flow bootstrap for `SignalFishPun2Bootstrap`: a host that creates
the PUN room and publishes its name over the Signal Fish room, and a
client that joins by Signal Fish room code and enters the PUN room whose
name arrives. Drop both components on one GameObject, wire Photon's own
settings, and call `StartHost` / `StartClient` from your UI.

Requires: Photon PUN 2 (imported into the project), the Signal Fish
client package, and a Signal Fish server endpoint.

## Scene steps

1. Import PUN 2 and run its setup wizard (the `PhotonServerSettings`
   asset supplies the AppId and region for `ConnectUsingSettings`).
2. Add `SignalFishPun2Bootstrap` and fill `Endpoint` (the Signal Fish
   room endpoint), `GameName`, and `PlayerName`. Optionally set
   `RoomName` (empty derives the PUN room name from the Signal Fish
   room code) and `Max Players`.
3. Add `Pun2RoomNameExchangeSample` and call `StartHost` /
   `StartClient` from your UI. The room code field feeds the client
   flow; the host may pass its own code to claim a specific room.

## What the bootstrap does

- Host: Signal Fish join → authority → PUN `ConnectUsingSettings` →
  `JoinOrCreateRoom` → publish `{"signal_fish_pun2_room": "<name>"}`
  over the room's game-data lane → ready + start the game. Every later
  join re-publishes the name, so late joiners never depend on timing.
- Client: Signal Fish join → ready → wait for the published name →
  PUN connect → `JoinOrCreateRoom(name)`.

The PUN room is Photon-hosted: anyone with the name can join it, so
membership enforcement (if the game needs it) belongs to the game's own
room logic — the Signal Fish room's part here is matchmaking plus the
name exchange.
