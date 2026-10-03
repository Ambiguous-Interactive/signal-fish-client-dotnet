# Examples

Complete, compilable programs for the four shapes most games need: the
lobby flow, a host-authority relay loop, reconnection, and the
frame-driven game loop. Every example runs against a development server —
see the [five-minute quick start](https://ambiguous-interactive.github.io/signal-fish-server/quickstart/)
if you need one.

!!! note "Start on the v2 relay floor"
    The first two examples use the default v2 relay configuration. Get that
    path working before opting into protocol v3, binary frames, or mesh
    signaling ([Protocol versioning](protocol-versioning.md)).

## Player lobby: join, ready, start

Two players join the same room, ready up, and the server gates the start.
When everyone is ready the lobby broadcast reports `AllReady`, the
authority sends `StartGame`, and both seats observe `GameStarting`:

```csharp
using System;
using System.Threading.Tasks;
using SignalFish.Client;
using SignalFish.Client.Async;
using SignalFish.Client.Core;
using SignalFish.Client.Polling;
using SignalFish.Client.Protocol;
using SignalFish.Client.Transport;

SignalFishClient alice = await ConnectAsync("alice");
SignalFishClient bob = await ConnectAsync("bob");

// The first join creates the room; the second reuses its code.
alice.SendJoinRoom(new JoinRoomMessage("my-game", "alice"));
string roomCode =
    (await ExpectAsync(alice, PollEventKind.RoomJoined)).Membership.RoomCode!;
Console.WriteLine($"room code {roomCode}");

bob.SendJoinRoom(new JoinRoomMessage("my-game", "bob", roomCode));
await ExpectAsync(bob, PollEventKind.RoomJoined);

// SetReady toggles: send it once per player.
alice.SendPlayerReady();
bob.SendPlayerReady();

// Lobby broadcasts report readiness; wait until everyone is ready.
while (!(await ExpectAsync(alice, PollEventKind.LobbyStateChanged)).Lobby.AllReady)
{
}

// Readiness and authority rules are enforced server-side.
alice.SendStartGame();
await ExpectAsync(alice, PollEventKind.GameStarting);
await ExpectAsync(bob, PollEventKind.GameStarting);
Console.WriteLine("game starting");

await alice.DisposeAsync();
await bob.DisposeAsync();

static async Task<SignalFishClient> ConnectAsync(string name)
{
    SignalFishClient client = new(
        new WebSocketTransport(),
        SystemClock.Instance);
    await client.ConnectAsync(SignalFishClientInfo.DefaultServerUri());
    client.SendAuthenticate(new AuthenticateMessage(appId: "my-app-id"));
    Console.WriteLine($"{name}: connected");
    return client;
}

static async Task<PollEvent> ExpectAsync(
    SignalFishClient client,
    PollEventKind kind)
{
    while (true)
    {
        PollEvent? ev = await client.DequeueEventAsync();
        if (ev is null)
        {
            throw new InvalidOperationException($"session ended before {kind}");
        }

        if (ev.Value.Kind == kind)
        {
            return ev.Value;
        }
    }
}
```

Moving parts:

- `ExpectAsync` skips unrelated events to keep the example short; a real
  game handles every kind — see [Events](events.md).
- `SendPlayerReady` toggles, so it fires once per ready transition.
- `GameStarting` carries the peer-connection payload for the session; the
  start request itself is refused with a typed error code when readiness or
  authority rules forbid it — see [Errors](errors.md).

Spectators follow the same shape: `SendJoinAsSpectator(new
JoinAsSpectatorMessage("my-game", roomCode, "viewer"))` is answered by
`SpectatorJoined`, relayed traffic still arrives as `GameData` events, and
`SendLeaveSpectator()` ends with `SpectatorLeft`. Sealed rooms take the
optional `password` argument.

## Host-authority relay loop

An authority room with one host broadcasting state and relaying only what
it needs. The host claims the authority, then fans state out as reliable
game data; the backpressure-aware `SendGameDataReliableAsync` waits for a
send-queue slot instead of failing fast:

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

var client = new SignalFishClient(
    new WebSocketTransport(),
    SystemClock.Instance);
await client.ConnectAsync(SignalFishClientInfo.DefaultServerUri());
client.SendAuthenticate(new AuthenticateMessage(appId: "my-app-id"));

// A background pump keeps the event queue drained while the main loop sends.
Task pump = Task.Run(async () =>
{
    while (await client.DequeueEventAsync() is { } ev)
    {
        if (ev.Kind == PollEventKind.GameData)
        {
            Console.WriteLine(
                $"{ev.GameData.FromPlayer}: "
                + Encoding.UTF8.GetString(ev.GameData.Payload.Span));
        }
    }
});

// Create an authority-enabled room, then claim the seat.
client.SendJoinRoom(new JoinRoomMessage("my-game", "host", supportsAuthority: true));
PollEvent joined = await ExpectAsync(client, PollEventKind.RoomJoined);
Console.WriteLine($"hosting room {joined.Membership.RoomCode}");

CommandSend claim = client.SendAuthorityRequest(becomeAuthority: true);
if (!claim.Accepted)
{
    Console.WriteLine($"claim refused: {claim.Refusal}");
    return;
}

// The answer arrives twice: the direct response and the room broadcast.
await ExpectAsync(client, PollEventKind.AuthorityResponse);
await ExpectAsync(client, PollEventKind.AuthorityChanged);

// Host tick: authoritative state fans out as reliable game data.
int tick = 0;
while (client.IsConnected)
{
    CommandSend send = await client.SendGameDataReliableAsync(
        new GameDataMessage(Encoding.UTF8.GetBytes($"""{{"tick":{tick}}}""")));
    if (!send.Accepted)
    {
        break; // NotConnected: the session ended while waiting for a slot.
    }

    tick++;
    await Task.Delay(50); // a 20 Hz host broadcast
}

await client.DisposeAsync();
await pump;

static async Task<PollEvent> ExpectAsync(
    SignalFishClient client,
    PollEventKind kind)
{
    while (true)
    {
        PollEvent? ev = await client.DequeueEventAsync();
        if (ev is null)
        {
            throw new InvalidOperationException($"session ended before {kind}");
        }

        if (ev.Value.Kind == kind)
        {
            return ev.Value;
        }
    }
}
```

Moving parts:

- The client is thread-safe: the pump drains events on the thread pool
  while the main thread relays. The event stream order stays deterministic
  because frames are processed strictly in arrival order.
- `AuthorityResponse` answers the claim (`Granted`, with a reason on a
  denial); `AuthorityChanged` is the room-wide move, also mirrored in
  `client.Snapshot`. Non-authority players relay only their own input, and
  the server refuses their `StartGame`.
- `SendGameDataReliableAsync` returns `NotConnected` when the session ends
  while waiting for a slot; the fail-fast `SendGameData` reports
  `SendBufferFull` instead of waiting. See [Delivery](delivery.md).

## Reconnection

Two recovery paths exist. The opt-in `ReconnectPolicy` automates the whole
round: after a retryable disconnect the driver waits the deterministic
backoff, opens a fresh transport from the factory, re-authenticates, and
reclaims the retained player seat. The reconnection token rides
`RoomJoined`/`Reconnected` on v3 connections (the v2 wire omits it), so the
example connects to the `/v3` endpoint and advertises v3 on both the
explicit handshake and the options — the automatic round replays the
options:

```csharp
using System;
using SignalFish.Client.Async;
using SignalFish.Client.Core;
using SignalFish.Client.Polling;
using SignalFish.Client.Protocol;
using SignalFish.Client.Reconnection;
using SignalFish.Client.Transport;

Uri endpoint = new("ws://localhost:3536/v3/ws");

var options = new SignalFishClientOptions(
    appId: "my-app-id",
    protocolVersion: 3,
    reconnectPolicy: new ReconnectPolicy(
        () => new WebSocketTransport(),
        initialBackoffMilliseconds: 500,
        maxAttempts: 5));

var client = new SignalFishClient(
    new WebSocketTransport(),
    SystemClock.Instance,
    options);
await client.ConnectAsync(endpoint);

client.SendAuthenticate(new AuthenticateMessage(
    appId: "my-app-id",
    protocolVersion: 3));
client.SendJoinRoom(new JoinRoomMessage("my-game", "alice"));

while (await client.DequeueEventAsync() is { } ev)
{
    switch (ev.Kind)
    {
        case PollEventKind.Reconnecting:
            Console.WriteLine(
                $"attempt {ev.Reconnect.Attempt} in "
                + $"{ev.Reconnect.BackoffMilliseconds} ms");
            break;

        case PollEventKind.Reconnected:
            Console.WriteLine($"seat reclaimed in {ev.Membership.RoomCode}");
            break;

        case PollEventKind.ReconnectAbandoned:
            Console.WriteLine(
                $"abandoned after {ev.Reconnect.Attempt} attempts: "
                + ev.Reconnect.LastReason);
            break;

        case PollEventKind.Disconnected:
            Console.WriteLine($"terminal ({ev.Close.Kind})");
            break;
    }
}
```

The backoff is deterministic — no jitter, ever — and the attempt budget
resets whenever a connection reaches the authenticated phase. Close codes
listed via `WithTerminalCloseCodes(...)` end the session instead of
retrying.

The manual path keeps recovery fully in your hands. Capture the seat
triple right after every `RoomJoined`/`Reconnected` (the token rotates on
each), persist it, and present it on a fresh client:

```csharp
if (ReconnectContext.TryCapture(client.Snapshot, out ReconnectContext seat))
{
    Persist(seat.PlayerId, seat.RoomId, seat.Token); // a bearer credential — never log it
}
```

```csharp
// Later, on a fresh client over a fresh transport:
await fresh.ConnectAsync(endpoint);
fresh.SendAuthenticate(new AuthenticateMessage(
    appId: "my-app-id",
    protocolVersion: 3));
fresh.SendReconnect(new ReconnectMessage(
    seat.PlayerId.ToString(),
    seat.RoomId.ToString(),
    seat.Token));
```

Classify a `ReconnectionFailed` event's error code with the built-in
recovery decision tree:

```csharp
case PollEventKind.ReconnectionFailed:
    ReconnectAction action = ReconnectRecovery.Classify(ev.Failure.ErrorCode);
    // FreshJoin: the seat is gone — join again as a new player.
    // WaitForSeat: another live connection holds it — wait, then retry.
    // RetryWithBackoff: transient — retry while the room is worth rejoining.
    break;
```

## Polling client drive loop

Frame-driven hosts (Unity's `Update()`, for example) use
`SignalFishPollingClient`: no background thread, commands admitted on the
calling thread, and one `Poll()` per frame followed by a drain. The
package ships this shape as a ready-made `MonoBehaviour` sample — see
[Unity](unity.md):

```csharp
using System;
using System.Text;
using System.Threading.Tasks;
using SignalFish.Client.Core;
using SignalFish.Client.Polling;
using SignalFish.Client.Protocol;
using SignalFish.Client.Transport;

var client = new SignalFishPollingClient(
    new WebSocketTransport(),
    SystemClock.Instance);
await client.ConnectAsync(new Uri("ws://localhost:3536/v2/ws"));

client.SendAuthenticate(new AuthenticateMessage(appId: "my-app-id"));
client.SendJoinRoom(new JoinRoomMessage("my-game", "player"));

// Once per frame (Unity: Update()). All state stays on this thread.
while (client.IsConnected)
{
    client.Poll();
    foreach (PollEvent ev in client.DrainEvents())
    {
        switch (ev.Kind)
        {
            case PollEventKind.RoomJoined:
                Console.WriteLine($"joined {ev.Membership.RoomCode}");
                client.SendGameData(new GameDataMessage(
                    Encoding.UTF8.GetBytes("""{"tick":0}""")));
                break;

            case PollEventKind.GameData:
                Console.WriteLine(
                    $"{ev.GameData.FromPlayer}: "
                    + Encoding.UTF8.GetString(ev.GameData.Payload.Span));
                break;

            case PollEventKind.Disconnected:
                Console.WriteLine($"terminal ({ev.Close.Kind})");
                break;
        }
    }

    await Task.Delay(16); // your frame budget; in Unity, just return
}

await client.DisposeAsync();
```

Moving parts:

- `Poll()` consumes up to the configured frame budget
  (`MaxFramesPerPoll`, 64 by default), applies each frame to the session
  state, and runs heartbeat/liveness timing on the injected clock.
- `DrainEvents()` starts an enumerable walk over pending events; breaking
  early keeps the tail for the next drain.
- A refused command (`CommandSend.Accepted == false`) never reached the
  wire; check the verdict at the send site.

## Where to go next

- The full event catalog: [Events](events.md)
- Typed failures, error codes, and close codes: [Errors](errors.md)
- Queue sizing and the backpressure contract: [Delivery](delivery.md)
- Deterministic tests for your own loop: [Testing](testing.md)
