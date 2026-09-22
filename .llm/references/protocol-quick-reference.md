# Signal Fish Protocol — Quick Reference

Canonical facts for the .NET client. When this file and the upstream server
docs disagree, **the server wins** — verify and update this file.

- Server repo: <https://github.com/Ambiguous-Interactive/signal-fish-server>
- Protocol docs: <https://github.com/Ambiguous-Interactive/signal-fish-server/blob/main/docs/protocol.md>
- Client-building guide: <https://github.com/Ambiguous-Interactive/signal-fish-server/blob/main/docs/guides/building-a-client.md>
- Machine-readable spec (AsyncAPI 3.0): `spec/signal-fish-protocol.asyncapi.yaml` in the server repo

## Transport & endpoints

| Item | Value |
| --- | --- |
| Default port | `3536` |
| v2 endpoint | `/v2/ws` (mandatory relay floor) |
| v3 endpoint | `/v3/ws` (optional negotiated capabilities) |
| Health | `GET /v2/health` |
| Client config | `GET /v2/client-config`, `GET /v3/client-config` → `max_outbound_message_size` |
| Size header | `x-signal-fish-max-outbound-message-size` on the upgrade response |
| Encoding | JSON text frames; MessagePack optional for game data (`game_data_formats`) |
| Server inbound limit | `max_message_size` default **64 KiB** — client outbound must stay under it |
| Server outbound limit | `max_outbound_message_size` default **8 MiB** (8388608) |

## Envelope

```json
{ "type": "JoinRoom", "data": { "game_name": "my_game", "player_name": "Alice" } }
{ "type": "PlayerReady" }
```

- `type`: PascalCase discriminator. Payload fields: `snake_case`.
- No-payload messages omit `data` entirely.

## Mandatory v2 lifecycle

```text
Client                              Server
  |--- Authenticate? ---------------->|
  |<-- Authenticated -----------------|
  |<-- ProtocolInfo ------------------|
  |--- JoinRoom --------------------->|
  |<-- RoomJoined --------------------|
  |<-- LobbyStateChanged -------------|
  |--- PlayerReady ------------------>|
  |<-- LobbyStateChanged (all_ready) -|
  |--- StartGame -------------------->|
  |<-- GameStarting ------------------|
  |--- GameData --------------------->|   (relay loop)
  |<-- GameData ----------------------|
  |--- LeaveRoom --------------------->|
  |<-- RoomLeft ----------------------|
```

- `JoinRoom` without `room_code` creates a room (6-char code); with a code
  it joins (or creates with that code). `Authenticate` is required first in
  allowlist mode, optional in open mode.
- Readiness never auto-starts; `StartGame` is authority-gated.
- Mandatory handling: `Error`/`*Failed` with `error_code`, `Ping`/`Pong`
  heartbeat, close-code reactions.

## Optional features

| Feature | Version | Notes |
| --- | --- | --- |
| `Reconnect` + `Reconnected` replay | v2+ | control events only; window default 300 s |
| Spectators (`JoinAsSpectator`) | v2+ | |
| Authority (`AuthorityRequest`) | v2+ | |
| v3 negotiation (`protocol_version: 3`) | v3 | + `supported_transports`, `supported_topologies` |
| Delivery classes `reliable`/`latest`/`volatile`, `seq`/`epoch`, `DeliveryReport` | v3 | mandatory once negotiated |
| WebRTC mesh (`Signal`, `SessionPlan`, `NewPeer`) | v3 | obey server plan; never recompute glare |
| `RoomOperation` (admin ops with `operation_id`) | v3 | |

## Server timeouts / limits (defaults)

| Key | Default |
| --- | --- |
| `websocket.auth_timeout_secs` | 10 |
| `idle_timeout_secs` | 300 |
| `server_ping_interval_secs` | 10 |
| `pong_timeout_secs` | 5 |
| `slow_consumer_timeout_ms` | 5000 |
| `server.reconnection_window` | 300 s |
| `server.event_buffer_size` | 100 (control-event replay ring) |
| `default_max_players` | 8 (limit 100) |
| `room_code_length` | 6 |

## Close codes

| Code | Meaning |
| --- | --- |
| 4000 | server_shutdown |
| 4001 | auth_timeout |
| 4002 | slow_consumer (congestion) |
| 4003 | activity_timeout |
| 4004 | idle_timeout |
| 4005 | room_inactive |
| 4006 | inbound_rate_limited |
| 4007 | kicked |
| 1009 | outbound_message_too_large |

## Common error codes

`ROOM_FULL`, `ROOM_NOT_FOUND`, `INVALID_GAME_NAME`, `INVALID_INPUT`,
`RATE_LIMIT_EXCEEDED`, `MISSING_APP_ID`, `INVALID_APP_ID`,
`CONNECT_TOKEN_INVALID`, `CONNECT_TOKEN_REQUIRED`,
`INVALID_DELIVERY_CLASS`, `GAME_START_NOT_READY`, `GAME_START_FORBIDDEN`,
`INVALID_ROOM_STATE`, `RECONNECTION_FAILED`, `RECONNECTION_EXPIRED`,
`RECONNECTION_TOKEN_INVALID`, `PLAYER_ALREADY_CONNECTED`, `NOT_IN_ROOM`,
`SERVER_DRAINING`.
Full list: `docs/reference/error-codes.md` in the server repo.

## Rate limit shapes

`Authenticated.data.rate_limits` → `{ "per_minute": 60, "per_hour": 3600,
"per_day": 86400 }` — budgets apply to requests; relay has separate byte
budgets (`max_relay_bytes` 256 MiB/sender/window,
`max_room_relay_bytes` 1 GiB/room/window).

## Environment

- Server env nesting uses double underscores:
  `SIGNAL_FISH__SECURITY__ENFORCE_APP_ID_ALLOWLIST=false`.
- Reference client env: `SIGNAL_FISH_URL` (e.g. `ws://localhost:3536/v2/ws`).
