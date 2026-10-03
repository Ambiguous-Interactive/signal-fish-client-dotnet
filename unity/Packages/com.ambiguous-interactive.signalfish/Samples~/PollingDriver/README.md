# Polling Driver sample

Import this sample, add the `SignalFishPollingDriver` component to any
GameObject, set the endpoint, app id, game name, and player name in the
inspector, and press Play. The component connects, authenticates, and
joins (or creates) the room, then polls and dispatches client events in
`Update()` — all on the main thread, with no marshaling.

What to look at:

- `Update()` is the whole loop: `Poll()` once per frame, then a
  `foreach` over `DrainEvents()` to handle each polled event (polling
  starts once `ConnectAsync` resolves — the heartbeat clock begins at
  connect).
- `SendHello()` shows the outbound shape: commands are admitted on the
  calling thread, and a refusal (`CommandSend.Accepted == false`) means
  the message never reached the wire.
- `OnDestroy()` disposes the client; the session is terminal after a
  disconnect, so re-add the component (or reload the scene) to retry.

WebGL builds cannot use the default `WebSocketTransport`
(`ClientWebSocket` does not exist there); inject a browser-WebSocket
`ITransport` instead — see the
[package docs](https://ambiguous-interactive.github.io/signal-fish-client-dotnet/unity/)
and the [transport guide](https://ambiguous-interactive.github.io/signal-fish-client-dotnet/transport/).
