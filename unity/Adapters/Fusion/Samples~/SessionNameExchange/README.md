# Fusion Session Name Exchange

A host/client pair that wires `SignalFishFusionBootstrap` to a minimal
UI: the host starts the Fusion session (`GameMode.Host`), and every
client joins the Signal Fish room by code and starts its runner
(`GameMode.Client`) with the session name the host publishes over the
room's game-data lane.

## Scene wiring

1. Add `SignalFishFusionBootstrap` to a scene object. Point `Endpoint`
   at your Signal Fish server (the default targets
   `ws://127.0.0.1:3536/v2/ws`).
2. Add `FusionSessionNameExchangeSample` to the same object and assign
   the bootstrap reference.
3. Set `SessionName` on the bootstrap to choose the Fusion session
   name, or leave it empty to derive the name from the Signal Fish
   room code.
4. Call `StartHost()` (or wire it to a button) on the host, and
   `StartClient()` with the room code on every client.

Photon's own settings (the `FusionAppSettings` asset imported with the
Fusion SDK) supply the AppId and region — this sample configures
nothing about Photon itself.

## What the bootstrap does

- Host: Signal Fish join → authority → `NetworkRunner.StartGame`
  (`GameMode.Host`) → publish the session name → ready → start game.
  Every later join re-publishes, so late joiners never depend on
  timing.
- Client: Signal Fish join → ready → wait for the published name →
  `NetworkRunner.StartGame` (`GameMode.Client`, session-creation
  refused) with that name.

The runner lives on its own `SignalFishFusionRunner` GameObject that
the bootstrap creates and tears down; scene and simulation ownership
stay with the game.
