---
name: reconnection
description: Implement the Signal Fish reconnection flow (reconnection tokens, Reconnect message, replay semantics, watermarks, backoff policy). Use when touching disconnect handling, session restore, missed-event replay, or rejoin behavior after a drop.
metadata:
  category: protocol
---

# Reconnection

## Token lifecycle

1. After `JoinRoom` the server issues a **reconnection token**, delivered in
   `RoomJoined.reconnection_token` (v3) and rotated on every successful
   reconnect (`Reconnected.reconnection_token`). Store only the latest.
2. On disconnect, the client sends
   `{ "type": "Reconnect", "data": { "player_id": "...", "room_id": "...", "auth_token": "<token>" } }`
   on a fresh connection.
3. Success → `Reconnected` snapshot: `missed_events` (bounded control-event
   replay), `replay: complete | truncated | unavailable`, and (v3)
   `sender_watermarks` for game-data resync.
4. Failure → `ReconnectionFailed` / error codes such as
   `RECONNECTION_EXPIRED` or `RECONNECTION_TOKEN_INVALID`. The token is
   dead; fall back to a normal `JoinRoom`.

## Hard rules

1. **GameData is never replayed.** The replay covers control events only;
   game-state resync is the application's job (v3 watermarks tell it what
   it missed per sender).
2. **Window**: the server accepts reconnects for a bounded window (default
   300 s from disconnect, monotonic). One winner per token; late attempts
   fail.
3. **Single in-flight attempt**: never send two `Reconnect` messages on one
   connection; never race a `Reconnect` against a `JoinRoom` for the same
   session.
4. **Re-supply parameters**: when rejoining a room by code (full rejoin
   path), the client must re-supply `max_players` if the room may need to
   be re-created.
5. `SERVER_DRAINING` teardown races do not consume the token — retry within
   the window.

## Backoff policy

- Deterministic exponential backoff (no jitter) on consecutive failures; reset on any
  successful server response (`Authenticated` / `Reconnected`).
- Treat close codes 4002 (slow consumer) and 1009 (message too big) as
  congestion: back off **before** reconnecting — see
  [websocket-transport](../websocket-transport/SKILL.md).
- Do not auto-reconnect after 4007 (kicked). Surface it.
- The policy is a config record (`ReconnectPolicy`: initial delay, max
  delay, multiplier, max attempts) — never hardcode magic numbers in the
  driver loop (see [api-design](../api-design/SKILL.md)).

## State machine sketch

```
Connected --drop--> Disconnected --within window--> Reconnecting
    ^                                                    |
    |                                          Reconnect ok
    |                                                    v
    +---------------- rejoin -------------- Reconnected(snapshot)
Disconnected --window expired--> SessionLost (require full JoinRoom)
```

Emit client events for every transition; consumers decide UX, the client
decides mechanics.

## Testing notes

- Fake transport + scripted disconnects — see
  [create-test](../create-test/SKILL.md).
- Always assert the **event sequence** (Disconnected → Reconnected with
  expected replay), not just final state.
- Use deterministic virtual time for backoff; never real `Task.Delay`-based
  sleeps in tests.

## Related Skills

- [protocol-messages](../protocol-messages/SKILL.md) - Reconnect message shape
- [websocket-transport](../websocket-transport/SKILL.md) - close codes that gate retry
- [async-threading](../async-threading/SKILL.md) - timers without threads
