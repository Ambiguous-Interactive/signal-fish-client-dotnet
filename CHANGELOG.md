# Changelog

All notable, user-visible changes to the `SignalFish.Client` package are
documented here. Format: [keep-a-changelog]; versions follow [semver]. Internal
changes (CI, tests, tooling, docs) are not listed.

## [Unreleased]

### Fixed

- A handshake that requests an unsupported `game_data_format` now honors
  the server's pinned downgrade contract exactly: the refused encoding
  can no longer be re-negotiated after the server's one downgrade
  notice, and a repeated notice refuses per the violation policy
  instead of passing silently. Sessions against a conforming server
  behave as before (one notice, JSON fallback, connection stays open).

### Added

- Releases now ship every Unity package as a tarball: a `v*` tag
  attaches all nine UPM `.tgz` files (the core SDK package plus the
  engine adapters) to the GitHub Release next to the NuGet packages,
  so a Unity project can install any package without a UPM registry.
- The UPM packages can be installed from the npm registry: Unity
  users resolve them by name (`com.ambiguous-interactive.signalfish`,
  plus the adapters) from the package manager UI instead of
  hand-downloading tarballs.

### Changed

- The UPM tarballs now follow the npm pack layout (entries root at
  `package/`), the shape Unity's tarball installer requires. Tarballs
  from earlier releases (entries at `./`) keep working everywhere
  they already worked, but new installs from a tarball should use
  the new artifacts.

## [0.1.0] - 2026-10-06

### Added

- Facepunch Steamworks adapter (M8.6): a new UPM package,
  `com.ambiguous-interactive.signalfish.adapters.facepunch`, ships
  `SignalFishFacepunchSteamIdentityBootstrap` — the second half of the
  Steam identity exchange, on the Facepunch.Steamworks binding and
  under the same byte-identical lane keys as the Steamworks.NET half.
  The host opens a Steam relay socket (`CreateRelaySocket`), publishes
  its id (re-published on every join, so late joiners never depend on
  timing), and accepts an incoming Steam connection only after that
  peer's id was advertised on the lane — the room's membership is the
  accept fence (Facepunch's auto-accept default is overridden), and
  Steam's rendezvous-authenticated identity carries the claim. Clients
  dial the host id that arrives on the lane (`ConnectRelay`). Peers
  surface as events the game's own protocol then owns; the live
  Facepunch managers are exposed for the game's message pump (its
  `Interface` receives, the game calls `Receive`). Requires Facepunch's
  manual callback mode (`SteamClient.Init(appId, asyncCallbacks:
  false)` — the bootstrap pumps `SteamClient.RunCallbacks` on its own
  tick) and the Facepunch.Steamworks asset 2.5.2+ (detected through
  the included editor define detector; the asset ships no UPM
  package). The pure
  core (identity envelope) runs in the dotnet test suite; the
  `lint-unity-adapter` CI gate pins the compile-only-with-Facepunch
  guard, the detector contract, and the bootstrap's member
  completeness plus a full type-check against the pinned Facepunch
  surface. Live Unity validation stays the M8.7 runbook item (Unity
  never runs in CI).
- Steamworks.NET adapter (M8.6): a new UPM package,
  `com.ambiguous-interactive.signalfish.adapters.steamworksnet`, ships
  `SignalFishSteamIdentityBootstrap` — a bootstrap that puts Steam P2P
  sockets on a Signal Fish room by exchanging the two SteamId64s each
  side needs over the room's game-data lane. The host opens a Steam
  listen socket, publishes its id (re-published on every join, so late
  joiners never depend on timing), and accepts an incoming Steam
  connection only after that peer's id was advertised on the lane —
  the room's membership is the accept fence, and Steam's
  rendezvous-authenticated identity carries the claim. Clients dial
  the host id that arrives on the lane. Peers surface as events the
  game's own protocol then owns. Requires Steamworks.NET 20.0.0+
  (detected through the UPM package, with an included editor define
  detector for vendored installs). The pure core (identity envelope)
  runs in the dotnet test suite; the `lint-unity-adapter` CI gate pins
  the compile-only-with-Steamworks guard, the detector and package-pin
  contracts, and the bootstrap's member completeness against the
  pinned Steamworks.NET surface. Live Unity validation stays the M8.7
  runbook item (Unity never runs in CI).
- Fusion adapter (M8.5): a new UPM package,
  `com.ambiguous-interactive.signalfish.adapters.fusion`, ships
  `SignalFishFusionBootstrap` — a bootstrap that puts a Photon Fusion
  session on a Signal Fish room by exchanging the session name over the
  room's game-data lane. The host starts the Fusion session
  (`GameMode.Host`) and publishes its name (re-published on every join,
  so late joiners never depend on timing); clients join by room code
  and start as `GameMode.Client` with the name that arrives — a mode
  that joins a named session and never creates one. The pure core
  (session-name envelope) runs in the dotnet test suite; the
  `lint-unity-adapter` CI gate pins the compile-only-with-Fusion guard,
  the detector contract (Fusion ships as an asset, so the detector owns
  the define), and the bootstrap's member completeness against the
  pinned Fusion 2 surface. A session-name exchange sample covers the
  scene wiring. Live Unity validation stays the M8.7 runbook item
  (Unity never runs in CI).
- PUN2 adapter (M8.5): a new UPM package,
  `com.ambiguous-interactive.signalfish.adapters.pun2`, ships
  `SignalFishPun2Bootstrap` — a bootstrap that puts a Photon PUN2 room
  on a Signal Fish room by exchanging the PUN room name over the room's
  game-data lane. The host creates the PUN room and publishes its name
  (re-published on every join, so late joiners never depend on timing);
  clients join by room code and enter the PUN room whose name arrives.
  The pure core (room-name envelope) runs in the dotnet test suite; the
  `lint-unity-adapter` CI gate pins the compile-only-with-PUN2 guard,
  the detector contract (PUN2 ships as an asset, so the detector owns
  the define), and the bootstrap's member completeness against the
  pinned PUN 2 surface. A room-name exchange sample covers the scene
  wiring. Live Unity validation stays the M8.7 runbook item (Unity
  never runs in CI).
- NGO adapter (M8.3): a new UPM package,
  `com.ambiguous-interactive.signalfish.adapters.ngo`, ships
  `SignalFishRoomCoordinator` — a coordinator that runs a Netcode for
  GameObjects session over a Signal Fish room. The room owns matchmaking
  and membership: the host allocates a Unity Relay server through a
  game hook and hands the join code to members over the room's game-data
  lane (late joiners get a fresh broadcast), and every NGO connection is
  approved only when its claimed Signal Fish player id is a live room
  member. The pure core (join-code envelope, room roster, approval
  payload) runs in the dotnet test suite; the `lint-unity-adapter` CI
  gate pins the compile-only-with-NGO guard, the package-pin and
  detector contracts, and the coordinator's member completeness. A
  Relay wiring sample covers the Unity Gaming Services setup. Live Unity
  validation stays the M8.7 runbook item (Unity never runs in CI).
- Mirror adapter (M8.2): a new UPM package,
  `com.ambiguous-interactive.signalfish.transport.mirror`, ships
  `SignalFishMirrorTransport` — a Mirror `Transport` whose wire is a
  Signal Fish room's v3 binary game-data lane. The room authority plays
  the Mirror server, members play clients, and the adapter's receive
  rules turn the broadcast relay into Mirror's star topology;
  host mode needs no loopback (Mirror's local connection never touches
  the transport), and a derived packet size is included. A
  `SignalFishRoomManager` bootstrap and a late-join/reconnect sample
  cover the join flows. The pure core (header, routing, receive rules,
  MTU) runs in the dotnet test suite; the `lint-unity-adapter` CI gate
  pins the compile-only-with-Mirror guard, the define-ownership and
  detector contracts, and the bridge's member completeness. Live Unity
  validation stays the M8.7 runbook item (Unity never runs in CI).
- FishNet adapter: FishNet installed vendored under `Assets/` (the
  classic Asset Store layout) is now detected. An editor define detector
  probes for the compiled `FishNet.Runtime` assembly (or the UPM package
  marker) and toggles the same `SIGNALFISH_FISHNET` define the
  `versionDefines` pin produces — previously the bridge stayed inert on
  vendored installs.
- FishNet adapter (M8.1): a new UPM package,
  `com.ambiguous-interactive.signalfish.transport.fishnet`, ships
  `SignalFishFishNetTransport` — a FishNet `Transport` whose wire is a
  Signal Fish room's v3 binary game-data lane. The room authority plays
  the FishNet server, members play clients, and the adapter's receive
  rules turn the broadcast relay into FishNet's star topology; host-mode
  loopback, per-peer fanout, and a derived MTU are included. The pure
  core (header, routing, receive rules, MTU, loopback) runs in the dotnet
  test suite; the `lint-unity-adapter` CI gate pins the compile-only-
  with-FishNet guard, the `versionDefines` pin, and the bridge's member
  completeness. Live Unity validation stays the M8.7 runbook item (Unity
  never runs in CI).
- WebGL reference transport (M7.3): the Unity package now ships a
  browser-WebSocket `ITransport` for WebGL builds in
  `Plugins/SignalFishWebGL/` (`SignalFishWebGLTransport` +
  `SignalFishWebSocket.jslib`, compiled only on the WebGL platform) —
  same contract as the .NET transport: connect-once, single reader,
  terminal close surfaced once with the observed wire code, 64 KiB
  send cap, polite 1000 dispose. WebGL limits apply (no threads:
  receives poll a JS-side queue; no `client-config` probe: the 8 MiB
  protocol default bounds receives). `WebSocketTransport` remains
  unusable on WebGL; inject the WebGL transport there instead.
- Unity package (M7.1/M7.2): the library now ships as the UPM package
  `com.ambiguous-interactive.signalfish` (source distribution, Unity
  2021.2+, zero dependencies preserved). `Runtime/` mirrors the library
  sources exactly (freshness enforced in CI and the pre-commit hook,
  `scripts/sync-unity-package.ps1`), `link.xml` guards IL2CPP stripping,
  and a `SignalFishPollingDriver` sample MonoBehaviour shows the
  frame-driven loop: poll in `Update()`, drain events on the main
  thread. Install from a UPM tarball (release lane wiring lands with
  M9.2) or from disk; see the new Unity docs page.
- Mesh session fences (M6.6): on a negotiated-v3 connection the client now
  hardens the mesh signaling surface the same way the Rust client does. A
  replayed `SessionPlan` whose generation was already superseded surfaces
  as a protocol violation and never overwrites the current plan (bounded
  to the eight most recent generations), and inbound `Signal` frames that
  race the plan — no authoritative plan yet, a stale generation, or a
  sender the live generation retired (departed, or dropped by a
  same-generation re-plan) — are absorbed silently as benign relay
  ordering instead of surfacing to the game. Everything else surfaces
  exactly as before.
- v3 mesh signaling (M6.5): on a negotiated-v3 connection the client now
  speaks the WebRTC mesh surface. `SessionPlan`, `NewPeer`,
  `PeerTransportStatus`, and `Signal` frames surface as typed events, and
  the new `MeshSession` tracker folds them into an always-consistent view
  (topology, transport, peers with their server-assigned `initiate` glare
  role and liveness, elected host, ICE servers) — obeying the server's
  plan and never recomputing it. `SendSignal(to, generation, signal)`
  relays a verbatim WebRTC signal to a plan peer (refused with typed
  errors while no plan is live, when the generation is superseded, or
  when the target is not a plan peer), `SendTransportStatus` reports your
  data-path state (peers see it as `PeerTransportStatus`), and
  `SendProvideConnectionInfo` publishes engine connection info on the
  v2-compatible floor. Room snapshots now expose the ICE pre-gather list.
  The relay floor is untouched: mesh is additive, and a v2 client
  observes byte-identical behavior.
- Binary game data flows both ways on a negotiated v3 connection (M6.4):
  pass `game_data_format: "message_pack"` on your `Authenticate` (the
  async client's `SignalFishClientOptions.GameDataFormat` carries it
  across automatic reconnects) and the client receives the server's
  MessagePack relay frames as ordinary `GameData` events — sender,
  opaque payload, and the same accountability stamps as JSON — while
  `SendBinaryGameData(payload)` relays one raw payload as a binary
  frame (always reliable; no class metadata). Binary frames outside a
  negotiated non-JSON encoding surface as violations, malformed ones as
  bounded `DecodeFailed` events, and the receive hot path stays
  allocation-free.
- Delivery accountability is now enforced on received relay traffic
  (M6.3): on a negotiated-v3 connection, the client validates every
  `GameData` stamp, `DeliveryReport` gap range, `RelayStats` interval, and
  roster watermark against the delivery contract. Violations surface as a
  `ProtocolViolation` event carrying the diagnostic text. How the session
  reacts is your choice: `SignalFishClientOptions`/`PollingClientOptions`
  accept a `ViolationPolicy` — `Quarantine` (default) suppresses the
  room's game data until the next authoritative rebaseline and exposes it
  as `ClientSnapshot.Quarantined`, `Disconnect` tears the session down,
  `Observe` keeps everything flowing. Received game-data frames also
  expose the sender's `Seq`/`Epoch` stamps, and violations carry their
  diagnostic on `PollEvent.Diagnostic`.
- Incoming JSON `GameData` decodes the optional v3 relay stamps
  (`seq`/`epoch`) the server already sends — previously dropped, which
  left delivery accounting nothing to validate.
- v3 delivery accountability decodes (M6.3 core): room snapshots and
  reconnections now expose the sender epoch/seq baselines and
  `sender_watermarks` the server already sends on v3 connections, and
  `DeliveryReport`, `RelayStats`, and `GoingAway` frames surface as events
  instead of being dropped (RelayStats was previously unroutable).
- v3 classified delivery (M6.2): on a negotiated-v3 connection, relayed
  game data carries a delivery class — `reliable` (the unchanged v2 wire
  form), `latest{key}` (server keeps only the newest value per key), or
  `volatile` (never paces the sender). `new GameDataMessage(payload, class,
  key)` sends classified; received frames surface the sender's class and
  key, so rate-sensitive games can coalesce state updates without losing
  the last word. Classified sends without a negotiated v3 are refused
  locally (`ProtocolUnsupported`); payloads deeper than the protocol's
  128-level JSON bound are refused at construction, before anything is
  sent.

### Changed

- The SDK version reported on every `Authenticate`
  (`SignalFishClientInfo.SdkVersion`) is now stamped from the git tag
  by MinVer instead of being a hardcoded `0.1.0` constant — the tag,
  the NuGet package version, and the wire identity can no longer
  drift apart (the Unity package's own `package.json` version stays
  manual for now). Builds without a tag (local branches, PR CI)
  report a `0.1.0-alpha.0` pre-release (suffixed
  `.<commit-height>` when the checkout has history depth); Unity and
  other engine consumers that compile the mirrored sources keep
  reporting `0.1.0`.
- The Unity adapter packages share one adapter-core package: the new
  `com.ambiguous-interactive.signalfish.adapters.core` owns the 18-byte
  adapter header (`AdapterWire`), the MTU math (`AdapterMtu`), the peer
  router (`SignalFishPeerRouter`), and the star-topology receive rules
  (`SignalFishReceiveRules` with `AdapterFrameRoute`), under
  adapter-neutral names in the `SignalFish.Client.Adapters` namespace.
  The FishNet and Mirror packages now depend on it — install it next to
  them — so a header or routing fix is one edit instead of one per
  adapter. FishNet's `SignalFishPeerRouter.HostClientConnectionId` is
  now `HostConnectionId`.

- A game-data frame carrying a coalescing key on a non-`latest` class is
  now a delivery violation (surfaced and handled per `ViolationPolicy`)
  instead of being silently normalized — the server refuses such sends,
  so a conforming deployment never sees this. The `PollingClientOptions`
  event ring requires a capacity of at least 3 (two slots are reserved:
  the terminal disconnect and a violation-plus-payload frame).

- Deeper game-data payloads decode: the frame depth bound rises from 64 to
  the protocol's 128 nesting levels, matching the server codec — payloads
  between the old and new bounds now relay instead of surfacing a decode
  failure.

- v3 protocol negotiation (M6.1): `SignalFishClientOptions` accepts a
  `ProtocolVersion` plus optional `SupportedTransports`,
  `SupportedTopologies`, and `RequestedCapabilities` — every automatic
  reconnect round advertises them, so a revived connection negotiates the
  same capabilities as the caller's first handshake. The server's cap-down
  echo arrives in the extended `ProtocolInfo` (now decoded: negotiated
  version, min/max, transports, outbound size bound; absent on the v2
  floor) and is tracked per connection: `ClientSnapshot.NegotiatedProtocolVersion`
  reads null before negotiation or on the v2 floor, matching the Rust
  client. The state machine also gained the `ProtocolUnsupported`
  admission refusal that v3-only sends will check — every command
  currently defined is v2, and a sweep pins that (the gate activates with
  the first v3 send in M6.2/M6.5).

- Authentication credentials (M5.3): `SignalFishClientOptions` accepts
  `AppId` and an optional `ConnectToken` (a tenant credential, redacted to
  presence in `ToString`), with `SdkVersion`/`Platform` defaulting from
  `SignalFishClientInfo`. Every automatic reconnect round re-authenticates
  with these credentials — required on allowlisted deployments, where an
  anonymous fresh connection would be refused before a seat reclaim.

- Authority tracking and requests (M5.2): `SendAuthorityRequest(true/false)`
  on both clients (player role; relinquishing without holding the authority
  is refused locally, matching the Rust client), `AuthorityChanged` now
  feeds the session state, and `ClientSnapshot.IsAuthority` reports the
  confirmed holder — seeded by `RoomJoined`/`Reconnected` baselines and
  updated by every broadcast. `AuthorityChanged.authority_player` is
  spec-nullable: an explicit JSON `null` now decodes as the vacated seat
  instead of surfacing a protocol violation.

- Spectator passwords sealed (M5.1): `JoinRoomMessage`/`JoinAsSpectatorMessage`
  redact the join password in `ToString` (presence, never value), and the
  sealed-room failures (missing password, wrong password, password on an
  open room) are pinned to surface as the same `PASSWORD_REQUIRED` code —
  indistinguishable to the sender, as the protocol intends.

- Opt-in automatic reconnection (M4.5): `SignalFishClientOptions` accepts a
  `ReconnectPolicy` — a transport factory plus a deterministic exponential
  backoff (no jitter) — and the driver then recovers by itself after a
  retryable disconnect: it waits the computed delay, opens a fresh
  transport from the factory, re-authenticates, and reclaims a retained
  player seat with the server-issued token. Each round announces itself
  with `Reconnecting` (attempt + delay) on the event stream, the attempt
  budget resets whenever a connection reaches `Authenticated`, close codes
  classified terminal via `WithTerminalCloseCodes` end the session instead
  of retrying, and budget exhaustion emits `ReconnectAbandoned` before the
  stream ends. Between rounds the phase reads `Connecting` (never
  `Terminal`), the dead connection's queued commands are discarded, and a
  voluntarily left room is never reclaimed. Without a policy, recovery
  stays fully manual, unchanged.

- Async client (M4.1/M4.2): `SignalFishClient` — a thread-safe client whose
  background driver loop multiplexes command sends, frame receives, and
  heartbeats over the transport. Commands flow through a bounded queue
  (fail-fast sends report `SendBufferFull` when full;
  `SendGameDataReliableAsync` waits for a slot instead, pacing high-rate
  payloads to actual transport throughput) and events flow through a bounded
  queue that never drops — a full event queue pauses the loop, which is the
  backpressure contract. Admission and queuing are one atomic step per
  send: a refused command never touches the wire and never wedges a fence.
  Events are dequeued with `DequeueEventAsync` (null after the terminal
  `Disconnected`) or `TryDequeueEvent`; the loop's timing runs on the
  injected clock, so virtual-time tests stay deterministic. The new
  `BoundedQueue<T>`/`IBoundedQueue<T>` pair behind it is channel-free and
  zero-dependency (benchmarked at parity with `System.Threading.Channels`).

- Command-send surface on the polling client (M3.6):
  `SignalFishPollingClient` can now drive a full session —
  `SendAuthenticate`, `SendJoinRoom`, `SendJoinAsSpectator`, `SendReconnect`,
  `SendPlayerReady`, `SendStartGame`, `SendLeaveRoom`, `SendLeaveSpectator`,
  and `SendGameData`. Admission is decided on the calling thread and a
  refused `CommandSend` names why without touching the wire; accepted
  commands emit the canonical wire form, and directed room operations arm
  the same fence the join/leave events release. The handshake is admitted
  in any live phase; the server arbitrates whether it may still run.

- Session snapshot (M3.5): `SignalFishPollingClient.Snapshot` returns one
  coherent `ClientSnapshot` — connection phase fields, the confirmed room
  identity (role, player, room, code), and the latest reconnection token —
  mirroring the Rust client's snapshot semantics, so multiple reads always
  describe the same instant. Reconnection tokens arriving on
  `RoomJoined`/`Reconnected` are now captured and rotated instead of being
  silently dropped (the credential manual reconnection needs); the token
  clears on spectator baselines, confirmed exits, and terminal disconnect,
  and is redacted in `ClientSnapshot.ToString()` so it can never leak
  through logs.

- Polling client (M3.4): `SignalFishPollingClient` for Unity
  `Update()`-style loops. Every `Poll()` drains up to 64 transport frames
  (configurable) into typed struct events via a fixed-capacity ring buffer
  with cooperative backpressure — a full ring pauses frame consumption (no
  frame is ever dropped), and one ring slot is always reserved so the
  terminal `Disconnected` event can never be squeezed out. The full v2
  payload surface arrives as typed events: room snapshots
  (players, lobby, relay), lobby state, player lifecycle, game start with
  peer connections, authority answers, spectator rosters, verbatim
  `GameData`, and typed failure reasons. Heartbeat is automatic (~30 s ping,
  2x-ping liveness timeout) on an injected clock; idle polls allocate zero
  bytes (allocation-gated test). Frames the client cannot honor — malformed
  session-critical fields, oversized or binary frames, failed payload
  decodes — surface as typed protocol violations instead of being silently
  ignored.
- Session-event mapping (M3.3): every v2 server session fact —
  authentication, room/spectator join, reconnection, leaves, the typed
  failure family (`RoomJoinFailed`, `SpectatorJoinFailed`,
  `ReconnectionFailed`), and generic errors — routes and decodes into typed
  session events for the state machine. The routing table now covers the
  full v2 server message set (spectator and authority broadcasts included);
  unknown types stay forward-compatible events.
- Connection core: injectable monotonic clock (`ISignalFishClock`) and a
  connection state machine with Rust-client-parity phase tracking, room
  membership (role/player/room/code as one invariant), and fail-closed
  fencing of in-flight join/leave/reconnect operations.
- Transport layer: `ITransport` abstraction with a `ClientWebSocket`
  implementation. Typed close reporting for every server close code
  (4000-4007, 1009), a pre-connect sizing probe of the server's
  `client-config` endpoint, and client-side enforcement of the server's
  64 KiB inbound frame limit. WebGL targets inject their own transport;
  the library never touches sockets directly.
- Protocol envelope decoding. Total: any input yields a typed event, never an
  exception. Unknown message types and unknown fields surface as
  forward-compatible events. Zero allocations on known messages.
- Protocol envelope encoding for every outbound v2/v3 client message.
  Byte-identical to the server's own wire samples.
- Disposing a client (or transport) on a live connection now performs the
  WebSocket close handshake: the server observes a client-initiated close
  (code 1000) instead of an abrupt TCP abort, so connection accounting and
  clean-vs-abnormal disconnect telemetry classify graceful exits correctly.
  The handshake is best-effort with a short bounded wait; an unresponsive
  server or dead wire falls back to the previous abort.

- One depth contract for outbound verbatim payloads: game data, signal,
  and connection info share a single 128-container bound, validated once
  at message construction; encoding no longer re-validates payloads.
  `SignalMessage` and `ProvideConnectionInfoMessage` now refuse malformed
  or too-deep payloads at construction.

### Fixed

- The Mirror and FishNet adapter sources (bridges and samples) now compile:
  every `using Mirror;` / `using FishNet.*;` and the `Transport` base-class
  spellings are `global::`-qualified. The adapter namespaces
  (`SignalFish.Client.Adapters.Mirror` / `.FishNet`) shadowed the engine
  namespaces for using-directives, and the client's
  `SignalFish.Client.Transport` namespace shadowed the `Transport` base
  class — compile blockers that only ever surfaced in a consumer's Unity
  editor. The new shape-stub bridge-compile lane (`lint-unity-adapter`),
  which type-checks all three engine-gated adapters at the netstandard2.1
  floor on every CI run, caught the three Runtime sources on its first
  run; the same-class sweep fixed the two `Samples~` drivers.
- Room-snapshot rosters (`PlayerInfo`/`SpectatorInfo.ConnectedAt`) decode
  when the server omits `connected_at` — which it always does on negotiated
  v3 connections. Previously every v3 room join failed to decode and the
  session stalled; caught by the live-server conformance suite.
- `JoinRoom` frames now reproduce the server's published canonical wire form:
  `room_code` precedes `player_name`, and absent optionals (`room_code`,
  `max_players`, `supports_authority`, `relay_transport`) are sent as explicit
  `null`s exactly like the upstream golden samples. Decoding accepts both
  forms, and `JoinAsSpectator`'s optional `password` decodes explicit `null`
  as absent too. Found by re-vendoring the golden fixtures, which the server
  extended to cover the full mandatory v2 message floor (`GameStarting`,
  `RoomLeft`, and the typed failure family now have wire samples).

[keep-a-changelog]: https://keepachangelog.com/en/1.1.0/
[semver]: https://semver.org/spec/v2.0.0.html
