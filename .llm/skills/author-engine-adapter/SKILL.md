---
name: author-engine-adapter
description: Author and extend the Unity engine-adapter packages (unity/Adapters/<Name> - FishNet, Mirror, NGO, ...) against the shared adapter core - package plumbing, define ownership, the bridge-compile stub lane, event-to-fatal classification, and engine-gated code hygiene. Use when adding an adapter, touching adapter Runtime sources, extending lint-unity-adapter, or wiring a sample that calls an engine or Unity Gaming Services SDK.
metadata:
  category: core
---

# Author Engine Adapters

The adapter packages put an engine's netcode on a Signal Fish room. The
compilers in this repo never see an engine SDK, so the discipline that
keeps them correct is: pin the engine surface, compile against a shape
stub, and classify room events against the server's documented
broadcast behavior - never against memory of an API.

## The pattern (a new adapter = replicate these)

1. **Package** `unity/Adapters/<Name>/{package.json, Runtime/*.asmdef,
   Runtime/Core/*.cs, Runtime/<Engine>/*.cs, Editor/<Name>DefineDetector.cs,
   Editor/*.Editor.asmdef, Samples~/...}`.
2. **Define ownership**: a UPM-shipped SDK gets a `versionDefines` pin
   (package name + expression -> `SIGNALFISH_<NAME>`, FishNet/NGO
   shape); an asset-shipped SDK gets the editor define detector as the
   define's sole owner (Mirror shape). Both mechanisms always pin the
   same string.
3. **Pure core** in `Runtime/Core/`: engine- and SDK-free C# that the
   dotnet test suite compiles directly (add the `<Compile Include>` to
   `tests/SignalFish.Client.Tests.csproj`). Everything testable lives
   here; the engine-gated bridge stays thin glue over it.
4. **Lint pins**: add the adapter to `scripts/lint-unity-adapter.ps1` -
   `Define`, `SdkReferencePattern` (per-SDK regex, never an if/else on
   the SDK name), `SdkReference`, `WirePin` (the shared-core type the
   bridge routes through), `ChannelPin` (engine channel bytes; `$null`
   when the surface has none - do not pin a lie), `PackagePin`,
   `BridgeFile`, `DetectorProbes`, `PinnedMembers`, and - for the
   bridge-compile lane - `BridgeCompile` + `BridgeStubs`.
5. **Self-test**: extend the fixture pins in
   `scripts/tests/test-lint-unity-adapter.ps1` (a missing fixture key
   the lint reads is a StrictMode crash, and every new pin field needs
   the negative loops to include the new fixture).

## Engine-gated code hygiene

- **No compiler in CI sees the real SDK.** The lint's shape-stub
  compile lane (`BridgeCompile` + `BridgeStubs`) type-checks the bridge
  at Unity's floor with `DefineConstants` set. Keep it green; a stub
  drift is a lint failure by design. Its first real catches: an `await`
  of a void `Task` assigned to a string, and
  `Environment.TickCount64` - which does not exist on the
  netstandard2.1 API floor Unity compiles against even though the
  dotnet build passes.
- **`global::` is load-bearing on engine usings.** The adapter
  namespace `SignalFish.Client.Adapters.<Name>` is a member of the
  enclosing `SignalFish.Client.Adapters` namespace, and
  enclosing-namespace members win over using directives: plain
  `using Fusion;` inside `...Adapters.Fusion` resolves to this
  package, not the SDK - a compile failure in Unity that no dotnet
  compiler sees. Spell every colliding using `using global::<Engine>;`
  and global::-qualify base types shadowed by client namespaces
  (`SignalFish.Client.Transport` shadows `Mirror.Transport`). The
  shape-stub lane caught three of these in the Runtime sources on its
  first run; the same-class sweep fixed the two `Samples~` drivers
  (which the lane intentionally does not compile - samples are
  consumer-compiled).
- **Pin stubs against the engine's actual source, not memory.** The
  Mirror/FishNet stubs in `lint-unity-adapter.ps1` were checked against
  the engines' repos: Mirror's callbacks are plain public `Action`
  fields (not `event`s - transports raise them directly, so a derived
  bridge may invoke them), FishNet's `LocalConnectionState` is
  `[Flags]` (Stopped=1..Started=8), `RemoteConnectionState.Started =
  2`, and `NetworkManager.LogWarning` is a static extension method.
  A stub that invents the engine's shape compiles the bridge against a
  lie.
- **Verify every external-SDK member against the engine's actual source
  or docs, never memory.** Real case: a Unity Relay `Allocation` has no
  join code - the host must call `GetJoinCodeAsync(allocationId)`. The
  sample compiles in a consumer project, so an invented member ships
  broken.
- **Pin semantics, not just names.** Member shapes are half the pin;
  the binding's *behavior* needs the same source-verified discipline.
  Facepunch.Steamworks real cases (all caught in one adversarial
  round against the 2.5.2 sources, none visible in the member
  surface): an SDK init can default to a background-thread callback
  pump (`SteamClient.Init(asyncCallbacks: true)` starts one) - a
  tick-dispatch bootstrap must verify the pump mode and require the
  manual one in its docs; a refusal can surface as a throw from a
  registry setter instead of a returned default handle (the returned-
  default check you planned is dead code); static dispatch registries
  can never remove entries (stale post-teardown dispatches and
  recycled handles are real - scope every callback to the session's
  instance and clear what is clearable); base-class state machines can
  absorb states the sibling binding surfaced (Facepunch's
  `ConnectionManager.Connecting` starts `true`, absorbing the own-dial
  Connecting that blocked the Steamworks.NET half). Diff the two
  bindings' *flows*, not just their signatures.
- **Await-shape discipline**: hook contracts that the bootstrap awaits
  must be `Func<..., Task<T>>` when the bootstrap needs the value; a
  fire-and-forget or void-task await loses it silently in Unity-only
  code.
- **Clock**: use `SystemClock.Instance.ElapsedMilliseconds` (Stopwatch,
  monotonic, portable). Never `Environment.TickCount64` - see above.

## Room-event classification

- The server **broadcasts room-wide events for your own successful
  requests too**: an `AuthorityResponse` (granted) is followed by an
  `AuthorityChanged` with `you_are_authority: true` for the grantee, in
  either order. A drain loop that fails on every `AuthorityChanged`
  kills every healthy start. Classify payload variants before choosing
  fatality; the conformance suite (`tests/SignalFish.Client.E2E`) is
  the executable spec of what the server sends and in what order.
- Bootstrap waits and the live drain must agree on what is fatal:
  events consumed during bootstrap are gone, so anything the live drain
  treats as fatal must not be silently absorbed by a wait (and
  vice versa - membership events absorbed by a wait must be applied,
  or the approval roster misses those players).

## Sample-first for hook-shaped APIs

Author the sample's call sites before freezing the runtime API: the
sample is the cheapest reviewer. It has caught a sync-hook-vs-async-SDK
race and a missing main-thread marshal (Unity object APIs throw off the
main thread - marshal through a queue the component drains in
`Update()`) that API-only review missed.

## Validation honesty

Unity never runs in CI. The packages are authored, lint-pinned, and
stub-compiled; live two-client validation is the M8.7 runbook item
(licensed seat). Document what is validated and what is contract-checked
in `docs/adapters/<name>.md`.

## Related Skills

- [add-quality-gate](../add-quality-gate/SKILL.md) - the three-scope
  contract when extending the adapter lint
- [address-pr-feedback](../address-pr-feedback/SKILL.md) - the sweep
  table this skill's failure classes came from
- [unity-compatibility](../unity-compatibility/SKILL.md) - the
  netstandard2.1 floor and platform traps
- [async-threading](../async-threading/SKILL.md) - main-thread/frame-loop
  consumption rules
