---
name: protocol-messages
description: Implement or extend Signal Fish protocol message handling (the type-discriminated JSON envelope, v2 mandatory relay floor, optional v3 negotiation). Use when adding message types, wiring send/receive paths, or checking what the server requires vs. what is optional.
metadata:
  category: protocol
---

# Protocol Messages

The protocol is JSON over WebSocket. Every frame is an envelope:

```json
{ "type": "JoinRoom", "data": { "game_name": "my_game", "player_name": "Alice" } }
```

- `type` is a PascalCase discriminator string.
- `data` fields are `snake_case`. Payload-less messages omit `data`
  entirely: `{ "type": "PlayerReady" }`.
- The protocol is **additive**: unknown `type` values and unknown fields
  must be tolerated, never treated as fatal.

## v2 relay floor (mandatory for every client)

| Message | Direction | Notes |
| --- | --- | --- |
| `Authenticate` | C→S | Optional in open mode; first message if sent; exactly once |
| `Authenticated` | S→C | Contains rate limit budgets |
| `ProtocolInfo` | S→C | Capabilities + game data formats |
| `JoinRoom` | C→S | With `room_code` joins; without creates a room (6-char code) |
| `RoomJoined` | S→C | Includes `reconnection_token` (v3) |
| `PlayerReady` / `LobbyStateChanged` | C→S / S→C | Readiness does not auto-start the game |
| `StartGame` / `GameStarting` | C→S / S→C | Authority-gated |
| `GameData` | both | The relay payload; relayed reliably over the socket |
| `LeaveRoom` / `RoomLeft` | both | |
| `Ping` / `Pong` | C→S / S→C | Client heartbeat; server replies; avoids idle timeout |
| `Error` | S→C | Always handle; see [error-handling](../error-handling/SKILL.md) |

Optional v2: `AuthorityRequest`/`AuthorityResponse`, `JoinAsSpectator`,
`ProvideConnectionInfo`, `Reconnect` (see
[reconnection](../reconnection/SKILL.md)).

## v3 additions (opt-in negotiation only)

- Advertise via `Authenticate` with `protocol_version: 3`,
  `supported_transports`, `supported_topologies`.
- `GameData` gains `class` (`reliable` | `latest` + `key` | `volatile`),
  `seq`/`epoch` stamps, and `DeliveryReport` gap accounting (mandatory once
  v3 is negotiated).
- WebRTC signaling (`Signal`, `SessionPlan`, `NewPeer`,
  `TransportStatus`) — obey the server's plan; never recompute glare.
- `RoomOperation` wrapper with `operation_id` for room admin operations.

## Implementation rules

1. Route **only** on the `type` discriminator; never on payload contents or
   prose.
2. One `readonly struct` per message shape under `Protocol/`; decode/encode
   through the hand-rolled codec with static `snake_case` field tokens (see
   [json-serialization](../json-serialization/SKILL.md)).
3. Unknown inbound `type` → emit an `UnknownMessage` event (carrying
   the raw JSON) and continue. Never throw.
4. Never construct wire JSON by string concatenation; always through the
   codec's span-based writers.
5. Client outbound message size must respect the server's inbound limit
   (`max_message_size`, default 64 KiB) — check
   [websocket-transport](../websocket-transport/SKILL.md).

## Authoritative references

Do not guess shapes. Confirm against
[protocol-quick-reference](../../references/protocol-quick-reference.md)
which links the server's protocol docs and AsyncAPI spec.

## Related Skills

- [json-serialization](../json-serialization/SKILL.md) - envelope decoding and casing
- [websocket-transport](../websocket-transport/SKILL.md) - endpoints, limits, close codes
- [error-handling](../error-handling/SKILL.md) - Error message and error_code handling
- [reconnection](../reconnection/SKILL.md) - Reconnect flow and replay semantics
