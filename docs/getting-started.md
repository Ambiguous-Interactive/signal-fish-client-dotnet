# Installation & Quick Start

This guide installs the SDK, connects one client to a Signal Fish server,
authenticates, joins (or creates) a room, and relays one game-data payload.
You need the .NET SDK **8.0 or newer**, a Signal Fish server URL, and an app
ID accepted by that server.

No server yet? Follow the Signal Fish Server [five-minute quick
start](https://ambiguous-interactive.github.io/signal-fish-server/quickstart/).
Its development setup accepts a test app ID; the app ID is a public
application label, not a secret.

The library targets `netstandard2.1` and has zero NuGet dependencies, so the
same package runs on .NET 8+, .NET 10, and Unity 2021.2+ (Mono and IL2CPP —
see [Unity](unity.md)).

## Install the SDK

```sh
dotnet add package SignalFish.Client
```

The package id is `SignalFish.Client`. Tagged releases publish the NuGet
package and attach the Unity UPM `.tgz` tarballs — see
[Releasing](releasing.md). Unity projects install a package from its
tarball or from source (see [Unity](unity.md)).

## Connect and join a room

Create a console project, add the package, and replace `Program.cs`:

```sh
dotnet new console -n SignalFishQuickstart
cd SignalFishQuickstart
dotnet add package SignalFish.Client
```

```csharp
using System;
using System.Text;
using System.Threading.Tasks;
using SignalFish.Client;
using SignalFish.Client.Async;
using SignalFish.Client.Core;
using SignalFish.Client.Polling;
using SignalFish.Client.Protocol;
using SignalFish.Client.Transport;

// Default: ws://localhost:3536/v2/ws. Override with SIGNAL_FISH_URL.
Uri endpoint = SignalFishClientInfo.DefaultServerUri();
if (Environment.GetEnvironmentVariable("SIGNAL_FISH_URL") is { } url)
{
    endpoint = new Uri(url);
}

using SignalFishClient client = new(
    new WebSocketTransport(),
    SystemClock.Instance);
await client.ConnectAsync(endpoint);

// Open-mode server: the handshake is optional but must come first.
client.SendAuthenticate(new AuthenticateMessage(appId: "my-app-id"));

// Omitting the room code creates a room and issues a shareable code.
client.SendJoinRoom(new JoinRoomMessage("my-game", "alice"));

while (await client.DequeueEventAsync() is { } ev)
{
    switch (ev.Kind)
    {
        case PollEventKind.RoomJoined:
            Console.WriteLine($"joined room {ev.Membership.RoomCode}");
            CommandSend send = client.SendGameData(
                new GameDataMessage(Encoding.UTF8.GetBytes("""{"hello":"world"}""")));
            Console.WriteLine($"relay admitted: {send.Accepted}");
            break;

        case PollEventKind.GameData:
            Console.WriteLine(
                $"{ev.GameData.FromPlayer}: "
                + Encoding.UTF8.GetString(ev.GameData.Payload.Span));
            break;

        case PollEventKind.Disconnected:
            Console.WriteLine($"session ended ({ev.Close.Kind})");
            break;
    }
}
```

Run it against a local development server:

```sh
dotnet run
```

### How this program works

- The client is constructed over an **unconnected transport** and a
  **clock** (`SystemClock.Instance`). `ConnectAsync` opens the transport,
  anchors heartbeat timing, and starts the driver loop; it may be called
  once per client, and a failure leaves that instance unusable (build a
  fresh client over a fresh transport to retry).
- Commands (`SendAuthenticate`, `SendJoinRoom`, `SendGameData`, and
  siblings) return a `CommandSend` verdict immediately. `Accepted` means
  the command was encoded and queued for the wire; a refusal names why, and
  a refused command never touched the wire.
- The event surface is `DequeueEventAsync`: it awaits the next event and
  returns `null` once the session is terminal and every event was consumed.
  The terminal `Disconnected` is delivered exactly once, last. The event
  queue is bounded (256 events by default) and never drops — a full queue
  pauses the driver loop, which is the backpressure contract.
- `RoomJoined` carries the confirmed `RoomMembership` (role, player id,
  room id, and the shareable room code).

## Add the game lifecycle

Most games continue from the quickstart with these steps:

1. On `RoomJoined`, call `SendPlayerReady()` once when the local player is
   ready — the command toggles readiness, so a second call un-readies you.
2. Observe `LobbyStateChanged` (lobby state, ready players, and the
   `AllReady` flag) on every lobby change.
3. Call `SendStartGame()` once the server's rules allow it. Readiness and
   authority are enforced server-side, and every seat sees `GameStarting`.
4. Relay gameplay as `GameData` payloads. `SendGameDataReliableAsync` is
   the backpressure-aware variant for high-rate payloads; the v3 delivery
   classes and binary frames are opt-in — see
   [Protocol versioning](protocol-versioning.md) and [Delivery](delivery.md).
5. Handle `Disconnected` (and, with a reconnect policy, `Reconnecting` and
   `ReconnectAbandoned` — see [Examples](examples.md)). Call
   `DisposeAsync` on exit; disposing inside a room stages a graceful leave
   first, within a bounded budget.

## Pick the right client

| Environment | Client and transport |
| --- | --- |
| Async host (.NET service, background loop) | `SignalFishClient` + `WebSocketTransport` |
| Unity, frame-driven game loop | `SignalFishPollingClient` + `WebSocketTransport` |
| Unity WebGL build | Either client + the browser-WebSocket transport |

Unity and WebGL inject a platform transport instead of `ClientWebSocket`;
see [Unity](unity.md) and [WebGL](webgl.md).

## Next steps

- [Examples](examples.md) — lobby flow, authority relay, reconnection, and
  the game-loop drive
- [Client API](client-api.md) — commands, options, and session state
- [Polling client](polling-client.md) — `Poll()` / `DrainEvents()` usage
- [Errors](errors.md) — typed failures and close codes
- [Unity](unity.md) — package install and the polling driver sample
