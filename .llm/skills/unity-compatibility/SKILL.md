---
name: unity-compatibility
description: Keep the SignalFish.Client library Unity compatible on netstandard2.1 (Mono, IL2CPP, WebGL). Use when choosing .NET APIs, adding NuGet dependencies, touching threading, WebAssembly builds, IL2CPP stripping, or anything platform-specific.
metadata:
  category: core
---

# Unity Compatibility

The library targets `netstandard2.1` and must work inside Unity 2021.2+ on
Mono, IL2CPP, and WebGL. Unity is a first-class consumer, not an afterthought.

## API surface rules

1. Only `netstandard2.1` APIs. If an API is missing from
   `System.Net.WebSockets`, `System.IO`, or `System.Threading` on
   netstandard2.1, it does not exist here.
2. **No `System.Threading.Channels` hard dependency without evaluation** —
   it ships as a NuGet package consumable from netstandard2.1, but verify
   IL2CPP behavior before relying on it. If in doubt, wrap it behind a
   small internal abstraction so it can be swapped.
3. No `System.Text.Json` source-generation (`JsonSerializerContext`)
   assumptions — see [json-serialization](../json-serialization/SKILL.md)
   for the Unity-safe strategy.
4. No DI containers, no reflection-heavy magic, no `dynamic`, no
   `System.Linq.Expressions` on hot paths (IL2CPP interpreter and stripping
   make them slow or fragile).

## Threading

- WebGL has **no threads and no `Thread.Sleep`**; blocking waits deadlock
  the player. Everything must complete via continuations/polling.
- IL2CPP: background threads work but are expensive — prefer the async
  pipeline and the polling client shape.
- Never call into Unity's API from library code; the library is
  engine-agnostic. Consumers marshal to the main thread themselves (or use
  the polling client from [async-threading](../async-threading/SKILL.md)).

## WebGL transport reality

`System.Net.WebSockets.ClientWebSocket` does not work on WebGL. This is the
main reason the transport is an interface — see
[websocket-transport](../websocket-transport/SKILL.md). WebGL builds inject
a browser-WebSocket-based `ITransport` implementation (JS interop) provided
by the consuming project or a companion package.

## IL2CPP / stripping

- Reflection-based serializers survive only if types are preserved. Document
  `link.xml` usage in the README when the default serializer relies on
  reflection over protocol types.
- Avoid `Type.GetType(string)`, `Activator.CreateInstance` on hot paths, and
  assembly scanning entirely.

## Dependencies policy

- Zero mandatory dependencies is the goal. Every candidate dependency must
  (a) target netstandard2.1, (b) work under IL2CPP, and (c) have a
  documented escape hatch.
- When adding one, note the Unity verification status in the PR description.

## Verification checklist for platform-sensitive changes

- `dotnet build` + `dotnet test` green (CI gates this).
- No new API outside netstandard2.1 (compiler enforces).
- Manual review question answered: "does this still compile into a WebGL
  player without threads, and into IL2CPP without stripping surprises?"

## When NOT to Use

- Pure protocol/wire questions with no platform angle — use
  [protocol-messages](../protocol-messages/SKILL.md).

## Related Skills

- [websocket-transport](../websocket-transport/SKILL.md) - the WebGL/ClientWebSocket split
- [json-serialization](../json-serialization/SKILL.md) - Unity-safe JSON strategy
- [async-threading](../async-threading/SKILL.md) - frame-loop-friendly consumption
