---
name: websocket-transport
description: Implement or modify the transport layer (ITransport abstraction, ClientWebSocket on .NET, endpoints /v2/ws and /v3/ws, message size limits, close codes). Use when touching connection code, TLS, close handling, or adding platform transports such as WebGL browser sockets.
metadata:
  category: protocol
---

# WebSocket Transport

## Abstraction first

The protocol layer never sees sockets. Everything goes through:

```csharp
public interface ITransport : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken ct = default);
    ValueTask<int> SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default);
    ValueTask<TransportFrame> ReceiveAsync(CancellationToken ct = default);
}
```

Rationale: WebGL cannot use `ClientWebSocket`, tests need fake transports,
and future platforms (console, native plugins) bring their own sockets. See
[unity-compatibility](../unity-compatibility/SKILL.md).

## Endpoints and sizing facts

- Default server port: **3536**. Paths: `/v2/ws` (relay floor), `/v3/ws`
  (negotiated v3). Health: `GET /v2/health`.
- Pre-connect receive sizing: `GET /v2/client-config` or `/v3/client-config`
  returns `max_outbound_message_size` (server default 8 MiB = 8388608); the
  same value is mirrored in the `x-signal-fish-max-outbound-message-size`
  upgrade response header.
- Server **inbound** limit (`max_message_size`) defaults to 64 KiB — the
  client must never send outbound frames larger than the server accepts.
- `wss://` works wherever the server has TLS configured; on Unity, TLS
  certificate handling differs per platform — keep it in the transport.

## Behavior rules

1. `ConnectAsync` must not retry internally; retry policy belongs to the
   client/reconnect layer ([reconnection](../reconnection/SKILL.md)).
2. Close handling: map server close codes to typed disconnect events —
   never swallow a close frame.

| Close code | Meaning | Client reaction |
| --- | --- | --- |
| 4000 | server_shutdown | Surface event; retry later |
| 4001 | auth_timeout | Reconnect and authenticate promptly |
| 4002 | slow_consumer | Congestion — back off, shrink send rate |
| 4003 | activity_timeout | Heartbeat missed — check [async-threading](../async-threading/SKILL.md) |
| 4004 | idle_timeout | Same as 4003 |
| 4005 | room_inactive | Room is gone; rejoin from scratch |
| 4006 | inbound_rate_limited | Honor budgets from `Authenticated.rate_limits` |
| 4007 | kicked | Do not auto-rejoin; surface to user |
| 1009 | message too big | Reduce outbound frame size; never retry as-is |

3. Receive loop owns a single read at a time; frames are dispatched to an
   inbound channel. Send path must be safe for concurrent callers (lock or
   single-writer channel).
4. A read failure or close is surfaced as a terminal event exactly once;
   after that the transport is dead and `DisposeAsync` is idempotent.

## Testing

Transport tests use an in-memory fake implementing `ITransport` —
[create-test](../create-test/SKILL.md). No real sockets in unit tests.

## When NOT to Use

- Reconnect/backoff policy — [reconnection](../reconnection/SKILL.md).
- Message contents — [protocol-messages](../protocol-messages/SKILL.md).

## Related Skills

- [protocol-messages](../protocol-messages/SKILL.md) - what travels over the wire
- [unity-compatibility](../unity-compatibility/SKILL.md) - WebGL transport injection
- [async-threading](../async-threading/SKILL.md) - heartbeat cadence requirements
