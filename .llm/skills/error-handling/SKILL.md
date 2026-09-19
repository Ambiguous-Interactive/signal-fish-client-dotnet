---
name: error-handling
description: Handle Signal Fish Error and *Failed messages, error_code tokens, close codes, and rate limits; design the SignalFishException hierarchy. Use when adding error paths, mapping server failures to exceptions or events, or handling rate limiting and congestion.
metadata:
  category: protocol
---

# Error Handling

Two distinct channels carry failures: **in-band messages** (`Error`,
`*Failed` with `error_code` tokens) and **out-of-band closes** (WebSocket
close codes). Both must be handled explicitly.

## In-band: Error / *Failed

Shape: `{ "type": "Error", "data": { "message": "Room is full", "error_code": "ROOM_FULL" } }`.

Rules:

1. **Switch on `error_code`, never on `message` text.** Prose is for humans.
2. Unknown codes → generic failure path + `UnknownErrorCode` diagnostic.
   The server may add codes at any time (additive protocol).
3. Command-originated failures (`RoomJoinFailed`, `SpectatorJoinFailed`,
   ...) fail the pending operation as a typed exception carrying the code;
   unsolicited errors become events.

Common codes and client behavior:

| Code | Meaning | Typical reaction |
| --- | --- | --- |
| `ROOM_FULL` | capacity reached | surface; retry later is safe |
| `ROOM_NOT_FOUND` | bad/expired code | surface; do not loop |
| `INVALID_GAME_NAME` / `INVALID_INPUT` | bug in our request | log loudly; this is a client bug |
| `RATE_LIMIT_EXCEEDED` | budget spent | back off using `Authenticated.rate_limits` |
| `MISSING_APP_ID` / `INVALID_APP_ID` | auth config broken | surface; fail fast |
| `NOT_IN_ROOM` | state desync | resync state machine |
| `SERVER_DRAINING` | server shutting down | reconnect later |
| `RECONNECTION_EXPIRED` / `RECONNECTION_TOKEN_INVALID` | dead token | full rejoin path — [reconnection](../reconnection/SKILL.md) |

## Out-of-band: close codes

Full table lives in [websocket-transport](../websocket-transport/SKILL.md).
Rule of thumb: 4002/1009 = congestion (back off), 4003/4004 = heartbeat
failure (check [async-threading](../async-threading/SKILL.md)), 4007 =
kicked (never auto-retry).

## Exception hierarchy

```text
SignalFishException                        (abstract base; carries ErrorCode? when in-band)
├── SignalFishProtocolException            (server rejected a command; has ErrorCode)
├── SignalFishConnectionException          (transport-level failure; has CloseCode?)
└── SignalFishProtocolViolationException   (we sent something invalid - client bug)
```

- Every public async API surfaces failures as these types or standard
  .NET exceptions (`OperationCanceledException`, `ObjectDisposedException`,
  `ArgumentException`) — nothing else.
- `error_code` strings map to a `SignalFishErrorCode` enum with an
  `Unknown` fallback keeping the raw string.

## Rate limits

`Authenticated.rate_limits` publishes per_minute/per_hour/per_day budgets.
Track spend client-side, self-throttle before the server has to, and treat
`RATE_LIMIT_EXCEEDED` as a signal to reset the budget tracker.

## When NOT to Use

- Serialization failures — [json-serialization](../json-serialization/SKILL.md).
- Retry/backoff policy — [reconnection](../reconnection/SKILL.md).

## Related Skills

- [protocol-messages](../protocol-messages/SKILL.md) - message catalog
- [websocket-transport](../websocket-transport/SKILL.md) - close code table
- [api-design](../api-design/SKILL.md) - exception design rules
