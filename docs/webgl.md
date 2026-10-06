# WebGL (Unity)

Unity WebGL builds cannot use `WebSocketTransport`: browsers expose no
raw sockets and no upgrade headers, so `System.Net.WebSockets.ClientWebSocket`
does not exist there. The Unity package ships a reference
browser-WebSocket transport behind the same `ITransport` contract, so
the rest of the SDK — and your code — runs unchanged.

## Package layout

The transport lives in
[unity/Packages/com.ambiguous-interactive.signalfish/Plugins/SignalFishWebGL](https://github.com/Ambiguous-Interactive/signal-fish-client-dotnet/tree/main/unity/Packages/com.ambiguous-interactive.signalfish/Plugins/SignalFishWebGL):

- `SignalFishWebGLTransport.cs` — an `ITransport` over seven
  `DllImport("__Internal")` entry points: `SignalFishWebSocketOpen`,
  `SignalFishWebSocketStatus`, `SignalFishWebSocketCloseCode`,
  `SignalFishWebSocketSend`, `SignalFishWebSocketPoll`,
  `SignalFishWebSocketClose`, and `SignalFishWebSocketDispose`. No
  engine references, no unsafe code.
- `SignalFishWebSocket.jslib` — the Emscripten plugin that owns the
  browser `WebSocket`, queues incoming frames on the JavaScript side,
  and reports observed close codes. It never throws across the interop
  boundary; failures are return codes.
- `SignalFish.Client.WebGL.asmdef` — references `SignalFish.Client`,
  sets `includePlatforms: ["WebGL"]`, and disables engine references,
  so other build targets never compile the pair.

## Usage

Inject the transport, and for opt-in reconnection provide it as the
reconnect transport factory:

```csharp
#if UNITY_WEBGL
using SignalFish.Client.Async;
using SignalFish.Client.Core;
using SignalFish.Client.Reconnection;
using SignalFish.Client.Transport;

var options = new SignalFishClientOptions(
    reconnectPolicy: new ReconnectPolicy(() => new SignalFishWebGLTransport()));
var client = new SignalFishClient(
    new SignalFishWebGLTransport(), SystemClock.Instance, options);
#endif
```

The transport is compiled only when the active build target is WebGL —
guard consuming code with `#if UNITY_WEBGL`.

## Limits

- **No threads.** A browser page has none, so the plugin queues inbound
  messages and the transport drains them by polling
  `SignalFishWebSocketPoll` on a ~1 ms cadence (`Task.Delay`, subject to
  browser timer clamping). That bounds receive throughput at the poll
  cadence — ample for signaling and modest relay loads, not a
  max-throughput story.
- **No `client-config` probe.** The browser cannot probe
  `GET /v2|v3/client-config`, so the receive bound defaults to the
  protocol maximum (8 MiB, `SignalFishWebGLTransport.DefaultMaxReceiveBytes`)
  instead of the server's advertised outbound limit; both constructors
  accept explicit overrides.
- **64 KiB send cap** (`DefaultOutboundCapBytes`, the server inbound
  limit), mirroring `WebSocketTransport`.
- **Poll contract.** One queued message per call: `n > 0` bytes copied,
  `0` empty, `-1` terminal close (the observed wire code), `-2` the next
  message does not fit the buffer (grow and retry; whole messages only —
  a frame is never split). Zero-length messages are dropped. A failed
  send (`-1` socket not open, `-2` the browser threw) surfaces as the
  terminal close.

## Contract guarantees

Everything `WebSocketTransport` guarantees carries over: connect at
most once (a failed connect releases the plugin entry and throws
`TransportClosedException` with the observed code), single reader, and
the terminal close surfaced exactly once with the observed wire code —
a missing code and the browser's no-status sentinel 1005 both normalize
to the abnormal 1006, and an oversized inbound frame closes with 1009.
Dispose is idempotent and race-safe; on a live connection it initiates
the close handshake with code 1000 so the server observes a
client-initiated close.

## What CI pins

`scripts/lint-webgl-plugin.ps1` is the red gate for a pair no compiler
in this repo builds together:

1. **Entry-point contract** — every `DllImport("__Internal")` name in a
   plugin folder's C# must exist as an exported `name: function` in the
   sibling `*.jslib`, and vice versa; a `*.jslib` without a sibling
   interop C# is a dead plugin. Library-object values must also be
   JSON-safe: the plugin builds its codecs at runtime so
   minified/transformed output stays correct (an eagerly constructed
   `new X()` value would reach the player as `{}` behind a green CI).
2. **Compile check** — the plugin C# plus the library sources compile
   as one netstandard2.1 assembly with `UNITY_WEBGL` defined, warnings
   as errors: the same shape Unity compiles on the WebGL player.
3. **Syntax check** — `node --check` on each `*.jslib` when node is on
   PATH.

!!! note "Validation status"

    CI never builds Unity (a locked project decision). The checks above
    pin everything CI can pin; live in-browser validation is the
    [validation runbook](unity-validation.md#core-package-and-webgl-m74)
    drill (pending a licensed Unity seat). See [Unity](unity.md) for
    the package layout and [Transport](transport.md) for the interface
    contract.
