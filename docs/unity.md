# Unity

The `SignalFish.Client` library targets `netstandard2.1` and ships as a
Unity package: `com.ambiguous-interactive.signalfish`. The package lives
in this repository under
[unity/Packages/com.ambiguous-interactive.signalfish](https://github.com/Ambiguous-Interactive/signal-fish-client-dotnet/tree/main/unity/Packages/com.ambiguous-interactive.signalfish).

## Install

- **GitHub Release (UPM tarball):** download
  `com.ambiguous-interactive.signalfish-<version>.tgz` from the release,
  then in Unity open **Window &gt; Package Manager &gt; Add package from
  tarball**.
- **From disk (development):** clone this repository and use
  **Add package from disk** on the package folder — the `Runtime`
  folder mirrors `src/SignalFish.Client` exactly (enforced by CI), so a
  checkout is always a working package.

Requirement: Unity 2021.2 or newer (Mono and IL2CPP).

## What ships in the package

- `Runtime/` — the library sources plus a
  `SignalFish.Client.asmdef` (no engine references: the library is
  engine-agnostic and compiles clean in every player; each file carries
  a `#nullable enable` prefix because Unity ignores the csproj).
- `link.xml` — a ready-made stripping snippet. Unity ignores `link.xml`
  inside packages, so copy it into your project's `Assets/` folder (or
  merge the `SignalFish.Client` assembly rule into an existing one) to
  keep the assembly intact under IL2CPP managed stripping. The library
  uses zero reflection, so nothing inside depends on stripping
  behavior.
- `Samples~/PollingDriver` — a minimal `MonoBehaviour` that drives the
  polling client (see below).

The library has **zero NuGet dependencies**: JSON is a hand-rolled UTF-8
codec and queuing is a channel-free bounded ring, so nothing needs to be
carried into the player.

## The polling driver sample

Import the sample from **Package Manager &gt; Samples**, add
`SignalFishPollingDriver` to a GameObject, set the endpoint, app id, game
name, and player name, and press Play:

- `Start()` connects, authenticates, and joins (or creates) the room.
- `Update()` is the whole loop: `Poll()` once per frame, then a
  `foreach` over `DrainEvents()` to handle each polled event. Unity
  calls `Update` on the main thread and the polling client is
  deliberately single-threaded, so every handler is already
  main-thread — no marshaling.
- `SendHello()` shows the outbound shape: commands are admitted on the
  calling thread, and `CommandSend.Accepted == false` means the message
  never reached the wire.

## WebGL

`WebSocketTransport` wraps `ClientWebSocket`, which **does not exist on
WebGL**. WebGL builds inject a browser-WebSocket `ITransport` instead —
see [Transport](transport.md) for the interface contract. A reference
`*.jslib` transport is planned for the next milestone.

## Validation status

Unity is never built in CI (a locked project decision): all Unity
validation runs locally through the scripted MCP pipeline
(`docs/unity-validation.md`, landing with the validation milestone).
The package layout, asmdef, and sample are authored and
mirror-checked here; live IL2CPP and WebGL smokes are tracked in the
plan before the 0.1.0 Unity checkpoint.
