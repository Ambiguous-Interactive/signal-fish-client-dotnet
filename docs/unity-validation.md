# Unity validation runbook

Unity is never built in CI (a locked project decision). CI pins what a
compiler can pin: the `lint-unity-adapter` gate type-checks every
engine-gated bridge against pinned SDK shape stubs, and the dotnet
suite runs each package's pure core. What no CI compiler sees is the
live behavior: a real editor, the real engine SDKs, a real Signal Fish
server. That gap closes locally, on a licensed Unity seat, through the
scripted MCP pipeline — and this page is its runbook.

Every adapter ships with a drill recorded here: pin the environment,
run the scripted loopback, exercise the failure paths, hold the
budgets, log the result. Until a drill has run, the adapter's page says
so — unvalidated behavior stays the honest unknown it is. The drills
are the only missing piece once the seat lands; M7.4 (the core
package's build smokes and allocation pass) is the same story.

## The shared drill

All adapters run the same skeleton. An entry below lists only what is
specific: pins, roles, and what to watch.

1. **Pin the environment.** Record the Unity version, the engine SDK
   version, and the package versions. A drill result is only valid
   against its pins; an SDK upgrade re-runs the drill.
2. **Two-client loopback.** Open the sample scene and drive play mode
   through the scripted MCP pipeline: one instance hosts, a second
   joins. The room runs join → ready → start, and the adapter's wire
   moves real traffic both ways.
3. **Failure paths.** Drop the client mid-session and end the host
   under traffic. The adapter must surface the failure loudly — an
   event or a teardown, never a silent stall — and honor the
   reconnection path its page documents.
4. **Budgets.** Where a page claims an allocation or frame budget, the
   editor profiler pass must hold it over a scripted lobby session.
   Where none is pinned yet, the first pass records the baseline every
   later drill compares against.
5. **Record.** Append a dated entry to the [session log](#session-log).
   Findings fold back: recurring problems become `.llm` skill rules,
   per-adapter behavior notes go to the adapter's page.

## Drills

### Core package and WebGL (M7.4)

- **Pins:** Unity 2021.2+ (the compatibility floor),
  `com.ambiguous-interactive.signalfish`.
- **Roles:** two editor instances on the polling driver sample
  (Samples~/PollingDriver) over one room.
- **Watch:** an IL2CPP Windows build smoke completes with `link.xml`
  in `Assets/` (the assembly survives managed stripping); a WebGL
  build smoke serves a session through the reference browser-WebSocket
  transport; the profiler allocation pass over the scripted lobby
  session holds the budget.

### FishNet (M8.1)

- **Pins:** FishNet 4.x,
  `com.ambiguous-interactive.signalfish.transport.fishnet`.
- **Roles:** the room authority plays the FishNet server; the
  movement-sync sample is the scenario — owning client feeds input,
  the server rebroadcasts the pose, observers apply it.
- **Watch:** host-mode loopback never touches the relay; a full-MTU
  frame still decodes on every recipient; the channel byte survives
  the reliable lane; an authority move without a running server side
  tears the session down loudly.

### Mirror (M8.2)

- **Pins:** Mirror 96.9.x,
  `com.ambiguous-interactive.signalfish.transport.mirror`.
- **Roles:** host through `SignalFishRoomManager` + the transport
  bridge; the late-join + reconnect sample is the scenario.
- **Watch:** both channels deliver on the reliable lane; an authority
  move without a running server side tears down; a fresh Mirror
  install lights the detector up after one editor compile.

### NGO and Unity Relay (M8.3)

- **Pins:** NGO 1.2.0, Unity Gaming Services (Relay),
  `com.ambiguous-interactive.signalfish.adapters.ngo`.
- **Roles:** the host allocates a Relay server and publishes the join
  code over `GameData`; the client joins by join code and passes
  connection approval against the room roster.
- **Watch:** a Relay-less host start is refused loudly; join-code
  exchange rides the JSON lane; an authority move tears the
  coordination down (NGO 1.x has no host migration — start a new
  room).

### BYOS template (M8.4)

- **Pins:** engine-free — any stack that can host a listener and
  speak the room protocol; the docs sample components are the
  scenario.
- **Roles:** the host component publishes its endpoint and gates
  accepts on the join-code echo; the client component echoes and
  connects.
- **Watch:** the gate opens only for roster members that echoed; a
  reconnected member must echo again (the documented gap — confirm the
  gate reopens on the fresh echo); a start refusal while the lobby is
  not all-ready retries as members ready up.

### PUN2 (M8.5)

- **Pins:** PUN 2.31,
  `com.ambiguous-interactive.signalfish.adapters.pun2`.
- **Roles:** the host joins or creates the room by name and publishes
  it; the client joins the same name.
- **Watch:** both sides run the same PhotonServerSettings (a mismatch
  must surface as the client's join failure, never a silent
  split-brain room); a fresh import lights the detector up after one
  editor compile; an authority move tears the coordination down.

### Fusion (M8.5)

- **Pins:** Fusion 2 (the surface verified against the 2.x runtime
  source), `com.ambiguous-interactive.signalfish.adapters.fusion`.
- **Roles:** the host starts the `NetworkRunner` session and publishes
  the session name; the client joins by name.
- **Watch:** both sides run the same FusionAppSettings (same mismatch
  rule as PUN2); an SDK upgrade that grows `INetworkRunnerCallbacks`
  breaks the editor compile — re-pin the lint stub lane in the same
  change and re-run this drill; an authority move or host migration
  tears the coordination down.

### Steamworks.NET (M8.6)

- **Pins:** Steamworks.NET 2025.165.0,
  `com.ambiguous-interactive.signalfish.adapters.steamworksnet`, the
  Steam client signed in.
- **Roles:** the host opens the `SteamNetworkingSockets` P2P listen
  socket and publishes its SteamId64; the client dials the id that
  arrives on the lane.
- **Watch:** the accept fence — an incoming connection is accepted
  only after that peer's id was advertised on the lane; the
  advertised-membership set only grows within a session; a fresh
  import lights the detector up after one editor compile (the
  `versionDefines` path needs none); and the cross-binding drill
  below.

### Facepunch (M8.6)

- **Pins:** the Facepunch.Steamworks asset 2.5.2,
  `com.ambiguous-interactive.signalfish.adapters.facepunch`, the Steam
  client signed in, manual callback mode (`SteamClient.Init(appId,
  asyncCallbacks: false)`).
- **Roles:** the host `CreateRelaySocket`s and publishes its
  SteamId64; the client `ConnectRelay`s to the published id.
- **Watch:** the bootstrap pumps `SteamClient.RunCallbacks` on its
  tick (the manual callback mode is a documented requirement) while
  message receives ride the game's `ISocketManager`/`IConnectionManager`
  interface; the stale-dispatch guard absorbs post-teardown callbacks;
  registry growth on re-hosting in the same process; and the
  cross-binding drill below.

### Cross-binding Steam room

Both Steamworks halves ship byte-identical lane keys, so one
Steamworks.NET peer and one Facepunch peer can share a room in
principle. The drill: host on one binding, join on the other, exchange
ids on the shared lane, and confirm both halves agree on the envelope.
It runs once per binding-pin change, not per session.

## Session log

Dated entries, newest first. One entry per drill run: the date, the
adapter, the pins, the outcome, and where the findings went. No
entries yet — every drill is pending the licensed Unity seat (the
M7.4 block).
