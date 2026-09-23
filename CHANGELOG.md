# Changelog

All notable, user-visible changes to the `SignalFish.Client` package are
documented here. Format: [keep-a-changelog]; versions follow [semver]. Internal
changes (CI, tests, tooling, docs) are not listed.

## [Unreleased]

### Added

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

### Fixed

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
