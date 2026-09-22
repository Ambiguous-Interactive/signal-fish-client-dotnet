# Transport

A `SignalFish.Client` transport moves opaque frames over the wire. The
protocol layer never sees sockets — everything goes through
`SignalFish.Client.Transport.ITransport`:

```csharp
public interface ITransport : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken ct = default);
    ValueTask<int> SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default);
    ValueTask<TransportFrame> ReceiveAsync(CancellationToken ct = default);
}
```

Contract highlights:

- `ConnectAsync` never retries; retry policy belongs to the reconnect layer.
- Sends are capped (default 64 KiB, the server inbound limit) and
  serialized; receives are single-reader.
- The close frame is surfaced exactly once, with the wire close code mapped
  to a `TransportCloseKind` (4000-4007, 1009). After that, receives throw
  `TransportClosedException`. Dispose is idempotent and race-safe, and on a
  live connection it initiates the WebSocket close handshake (close-out
  code 1000) with a short bounded wait, so the server observes a
  client-initiated close instead of a TCP abort; if the handshake cannot
  complete in time the socket is aborted as before.

## WebSocketTransport

`WebSocketTransport` wraps `ClientWebSocket`. Before upgrading it probes
`GET /v2|v3/client-config` for `max_outbound_message_size` (the server
outbound limit, default 8 MiB) and bounds receive buffering with it; probe
failure falls back to the protocol default without blocking the connect.

## WebGL (Unity)

`ClientWebSocket` is unusable in WebGL builds: browsers do not expose raw
sockets or upgrade headers. WebGL targets inject their own `ITransport`
instead — the library is transport-agnostic by design. A reference browser
WebSocket transport ships with the Unity package (planned milestone M7).
