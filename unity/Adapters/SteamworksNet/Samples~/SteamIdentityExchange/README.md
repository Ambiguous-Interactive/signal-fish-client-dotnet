# Steam Identity Exchange sample

A host/client wiring of `SignalFishSteamIdentityBootstrap`:

- The **host** joins (or creates) the Signal Fish room, takes the
  authority, opens the Steam P2P listen socket, and publishes its
  SteamId64 over the room's game-data lane.
- A **client** joins by room code, marks ready, dials the host
  SteamId64 that arrives on the lane, and publishes its own id so the
  host's accept fence recognizes it.

Initialize the Steamworks API first (your `SteamManager` calls
`SteamAPI.Init()`); the bootstrap performs every Steam call itself.

## What to expect

- Host console: the room code, the local SteamId64, and one line per
  fenced peer as its Steam connection comes and goes.
- Client console: the published host id, then the established
  connection.
- A peer whose SteamId64 never arrives on the lane is refused after
  the grace window (`AcceptGraceSeconds`).
