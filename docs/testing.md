# Testing

Integration tests against a multiplayer SDK usually fail in two ways that
have nothing to do with your code: a deadline that was *probably* long
enough, and a sleep that was *probably* long enough for the event you were
awaiting. The SDK removes both at the source — every client reads time only
from an injected `ISignalFishClock` and talks only to an injected
`ITransport` — so app tests script frames and step time explicitly, and the
SDK's own suite never waits on a real timer.

This page covers both audiences: testing an app built on the SDK, and how
the SDK validates itself.

## Injection points

Both clients are constructed over injected interfaces:

```csharp
var asyncClient = new SignalFishClient(transport, clock, options);
var pollingClient = new SignalFishPollingClient(transport, clock, options);
```

- `ITransport` (see [Transport](transport.md)) — script the frames a server
  would send, record what the client sends, and inject closes.
- `ISignalFishClock` — two members (`ElapsedMilliseconds` and
  `DelayAsync(milliseconds, ct)`); it drives the heartbeat cadence, the
  liveness timeout, reconnect backoff, and the shutdown budget. Production
  uses `SystemClock.Instance`.
- The options objects carry the buffer sizes: the async client's
  `EventCapacity` (256 events) and `CommandCapacity` (1024 commands), the
  polling client's `EventCapacity` (256) and `MaxFramesPerPoll` (64), plus
  the heartbeat defaults (~30 s ping, 2x-ping liveness timeout).

!!! note "Queues are configured, not injected"
    Both clients construct their own queues from the configured capacities.
    `IBoundedQueue<T>` is the public queue abstraction behind them (it
    exists so alternatives stay swap-in, e.g. benchmarks against
    `System.Threading.Channels`), but it is not a constructor parameter.
    Inject behavior through the transport, the clock, and the options.

## Deterministic app tests

A fake transport is small — the whole `ITransport` contract is four
methods plus disposal:

```csharp
public sealed class FakeTransport : ITransport
{
    private readonly Queue<TransportFrame> _incoming = new();
    private readonly List<byte[]> _sent = new();
    private TransportClose? _close;
    private bool _closeDelivered;

    public IReadOnlyList<byte[]> Sent => _sent;

    public void EnqueueText(string text) =>
        _incoming.Enqueue(new TransportFrame(Encoding.UTF8.GetBytes(text), isText: true));

    public void EnqueueClose(int code) => _close = new TransportClose(code);

    public Task ConnectAsync(Uri uri, CancellationToken ct = default) =>
        Task.CompletedTask;

    public ValueTask<int> SendAsync(
        ReadOnlyMemory<byte> frame,
        CancellationToken ct = default)
    {
        _sent.Add(frame.ToArray());
        return ValueTask.FromResult(frame.Length);
    }

    public ValueTask<int> SendBinaryAsync(
        ReadOnlyMemory<byte> frame,
        CancellationToken ct = default)
    {
        _sent.Add(frame.ToArray());
        return ValueTask.FromResult(frame.Length);
    }

    public ValueTask<TransportFrame> ReceiveAsync(CancellationToken ct = default)
    {
        if (_incoming.Count > 0)
        {
            return ValueTask.FromResult(_incoming.Dequeue());
        }

        if (_closeDelivered)
        {
            throw new TransportClosedException(_close ?? new TransportClose(1000));
        }

        _closeDelivered = true;
        return ValueTask.FromResult(
            TransportFrame.FromClose(_close ?? new TransportClose(1000)));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

Inbound wire frames are the JSON envelope form `{"type": "...", "data":
{...}}` — hand-write one, or replay a line from the golden fixtures
described below.

The polling client is the easiest client to drive deterministically: it is
synchronous, samples the clock only inside `ConnectAsync` and `Poll()`, and
its event ring is drained on demand. This test steps virtual time past the
liveness timeout and observes the teardown — microseconds of real time, no
sleeps:

```csharp
[Test]
public async Task SilencePastTheLivenessTimeoutEndsTheSession()
{
    var transport = new FakeTransport();
    var clock = new VirtualClock();
    var client = new SignalFishPollingClient(transport, clock);

    await client.ConnectAsync(new Uri("ws://localhost:3536/v2/ws"));

    client.SendAuthenticate(new AuthenticateMessage(appId: "my-app-id"));
    Assert.That(transport.Sent, Is.Not.Empty, "the handshake reached the wire");

    clock.Advance(60_000); // the default liveness timeout, in total silence
    client.Poll();

    bool disconnected = false;
    foreach (PollEvent ev in client.DrainEvents())
    {
        disconnected |= ev.Kind == PollEventKind.Disconnected;
    }

    Assert.That(disconnected, Is.True, "liveness declared death on virtual time");
}
```

A minimal virtual clock for the polling client is three members:

```csharp
public sealed class VirtualClock : ISignalFishClock
{
    private long _nowMilliseconds;

    public long ElapsedMilliseconds => Volatile.Read(ref _nowMilliseconds);

    public void Advance(long milliseconds) =>
        Interlocked.Add(ref _nowMilliseconds, milliseconds);

    public Task DelayAsync(int milliseconds, CancellationToken ct = default) =>
        Task.CompletedTask;
}
```

!!! note "The async client needs a deadline-honoring clock"
    `SignalFishClient` awaits `DelayAsync` for idle parking, reconnect
    backoff, and the shutdown budget, so a clock whose delays complete
    immediately would spin those loops. A virtual clock for it must park
    each wait and release it on `Advance`. The repository's own test
    helper (`tests/SignalFish.Client.Tests/Core/VirtualClock.cs`) shows the
    pattern: an advance releases exactly the delays whose deadline the new
    time reaches.

The same three rules keep these tests honest:

- **Waiting is `Advance`, never `sleep`.** If a test must wait, move the
  clock instead of sleeping; a real sleep reintroduces the flake.
- **Mocks never read the wall clock.** Script responses through the fake
  transport's queue; a mock that deadlines on `DateTime` drags real time
  back in.
- **Real I/O stays on the real clock.** `WebSocketTransport` performs
  actual socket work; keep end-to-end tests against a live server on real
  time (see the conformance suite below) and use fakes when you want
  virtual time.

## How the SDK tests itself

For contributors — the suites a change must pass before merge.

### Unit and property tests

`tests/SignalFish.Client.Tests` is the NUnit suite, multi-targeted over
net8.0 and net10.0 (the library itself stays `netstandard2.1`):

```sh
dotnet test tests/SignalFish.Client.Tests
```

Every client test runs on the suite's own `VirtualClock` and
`FakeTransport`. FsCheck drives the codec property tests
(`CodecPropertyTests`), and the `WebSocketTransport` tests exercise the
real transport against a local loopback WebSocket server
(`TestWsServer`). CI (`dotnet.yml`) runs the suite on three cells —
ubuntu/net8.0 (which also adds the convention lints, CSharpier, and
coverage), ubuntu/net10.0, and windows/net8.0 — with warnings as errors.

### Golden wire fixtures

`tests/Golden/` holds the protocol's wire corpus as JSONL, one frame per
line, split by version and direction: `v2-client-messages.jsonl`,
`v2-server-messages.jsonl`, `v3-client-messages.jsonl`, and
`v3-server-messages.jsonl`. The files are vendored verbatim from the
server repository by `scripts/sync-protocol-fixtures.ps1`; the source
repository, upstream path, and pinned commit are recorded in
`tests/Golden/PROVENANCE.md`. Never hand-edit them — resync or fix
upstream. The corpus tests pin those files and validate every line's
envelope shape, and the benchmark's `DecodeFullCorpus` operation decodes
the whole corpus (see [Benchmarks](benchmarks.md)).

### Fuzz lane

`tests/SignalFish.Client.FuzzTests` (SharpFuzz) targets the JSON reader,
the envelope writer, and the binary msgpack-frame path. Run it locally
with `pwsh -NoProfile -File scripts/fuzz-codec.ps1`. CI runs the lane on a
weekly schedule (`fuzz.yml`) — never on pull requests, to keep CI time
flat — persists the corpus between runs, and uploads any crashing input as
an artifact for triage.

### Performance tests

`tests/SignalFish.Client.PerfTests` (BenchmarkDotNet) holds the codec
baselines; zero-allocation gate tests on both codec directions run inside
the unit suite, so an allocation regression fails the ordinary build.
Recorded baselines and budgets: [Benchmarks](benchmarks.md).

### Live-server conformance

`tests/SignalFish.Client.E2E` drives a real server through the
client-author checklist (`ServerConformanceTests`, `MeshConformanceTests`)
with `SignalFishPollingClient` over `WebSocketTransport`. Locally:

```sh
pwsh -NoProfile -File scripts/run-e2e.ps1
```

With Docker available, the script boots a throwaway open-mode server
(`ghcr.io/ambiguous-interactive/signal-fish-server:latest`, port 3536,
short liveness timers), waits for it, runs the suite, and removes the
container. Pass `-ServerUrl ws://localhost:3536` to test a server that is
already running. CI runs the same suite against a service container
(`e2e.yml`). The checklist items and their status: [Server
Conformance](conformance.md).
