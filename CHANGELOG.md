# Changelog

All notable, user-visible changes to the `SignalFish.Client` package are
documented here. Format: [keep-a-changelog]; versions follow [semver]. Internal
changes (CI, tests, tooling, docs) are not listed.

## [Unreleased]

### Fixed

- `JoinRoom` frames now reproduce the server's published canonical wire form:
  `room_code` precedes `player_name`, and absent optionals (`room_code`,
  `max_players`, `supports_authority`, `relay_transport`) are sent as explicit
  `null`s exactly like the upstream golden samples. Decoding accepts both
  forms, and `JoinAsSpectator`'s optional `password` decodes explicit `null`
  as absent too. Found by re-vendoring the golden fixtures, which the server
  extended to cover the full mandatory v2 message floor (`GameStarting`,
  `RoomLeft`, and the typed failure family now have wire samples).

### Added

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

[keep-a-changelog]: https://keepachangelog.com/en/1.1.0/
[semver]: https://semver.org/spec/v2.0.0.html
