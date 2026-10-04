# Relay Room Coordinator sample

A two-flow bootstrap for `SignalFishRoomCoordinator`: a host that
allocates a Unity Relay server through Unity Gaming Services and a client
that binds the join code the coordinator receives over the Signal Fish
room. Drop the component on the same GameObject as a `NetworkManager`,
wire the hooks, and call `StartHost` / `StartClient` from your UI.

Requires: `com.unity.netcode.gameobjects`, `com.unity.services.authentication`,
`com.unity.services.relay`, `com.unity.transport`, the Signal Fish client
package, and a Signal Fish server endpoint.

## Scene steps

1. Add a `NetworkManager` with a `UnityTransport`.
2. Add `SignalFishRoomCoordinator` and fill `Endpoint`, `GameName`, and
   `PlayerName`.
3. Add `RelayRoomCoordinatorSample` and call `StartHost` /
   `StartClient` from your UI. Keep the component enabled: the hooks
   hand their Unity Transport binding to the component's main-thread
   queue, which only drains while it ticks.

## What the hooks do

- `RelayAllocationRequest` (host): signs in to Unity Gaming Services,
  allocates a Relay server, binds the host's Unity Transport to it, and
  returns the join code. It runs off the main thread; the sample
  marshals the Unity Transport binding back through a main-thread
  queue.
- `RelayJoinBinder` (client): binds the join code received over the room
  to the client's Unity Transport. It runs off the main thread right
  before the coordinator stages the NGO client start; the sample
  marshals the binding the same way.

Connection approval is automatic: an NGO connection is accepted only when
its payload carries the id of a player currently in the Signal Fish room.
