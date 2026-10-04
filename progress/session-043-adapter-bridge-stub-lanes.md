# Session 043 — Bridge shape-stub compile lanes for FishNet and Mirror

Date: 2026-10-04
Branch: `adapter-bridge-stub-lanes-m8`
Closes: #91

## What shipped

**Issue #91** — the shape-stub bridge-compile lane that PR #90 added for
the NGO coordinator now covers the FishNet and Mirror transport bridges
too, and its first run earned its keep immediately:

- `scripts/lint-unity-adapter.ps1`: `BridgeCompile` + `BridgeStubs`
  pins for both adapters. The stubs are checked-in shape contracts of
  the pinned engine surfaces (no SDK is vendored or referenced): the
  bridge + package Runtime sources type-check at the netstandard2.1
  floor (C# 9, nullable, warnings as errors) with the adapter define
  set, on every CI run.
- **The lanes caught three real Unity compile blockers on their first
  run** — the exact failure class the issue predicted (errors surface
  only in a consumer's editor):
  - C# name resolution gives enclosing-namespace members precedence
    over using directives, so `using FishNet.Transporting;` inside
    `SignalFish.Client.Adapters.FishNet` resolved to this package, and
    `using Mirror;` inside `SignalFish.Client.Adapters.Mirror`
    likewise. Same bug in both `Samples~` drivers (consumer-compiled)
    and the Mirror `SignalFishRoomManager`. Fixed with
    `using global::<Engine>;` everywhere (with explanatory comments).
  - The Mirror transport's base class `Transport` is shadowed by the
    client's `SignalFish.Client.Transport` *namespace* — unqualified
    `: Transport` is CS0118 in Unity too. Now
    `global::Mirror.Transport`.
- Stubs pinned against the engines' actual sources (not memory), which
  corrected two would-be-lies: Mirror's callbacks are plain public
  `Action` fields (not `event`s — a derived transport raises them
  directly; an `event` stub would have flagged the bridge's correct
  invocation as CS0070), and FishNet's `LocalConnectionState` is
  `[Flags]` (1/2/4/8) with `RemoteConnectionState.Started = 2` while
  `NetworkManager.LogWarning` is a static extension method.
- `author-engine-adapter` skill: the namespace-shadowing rule
  (`global::` is load-bearing — the next adapters, Fusion/Steamworks,
  will hit the same trap) and the verify-stubs-against-engine-source
  rule with the concrete catches.
- CHANGELOG Fixed entry (the compile blockers ship in published
  source packages).

## Validation

- `lint-unity-adapter` full lane: green (3 bridge files, 8 core files).
- `scripts/tests/run-all.ps1`: 15/15 self-test files (test 17 runs the
  real repo's full lane, now covering all three compile lanes).
- `dotnet test`: 876 passed per TFM (net8.0 + net10.0); lint-conventions
  and CSharpier clean.

## Session roll-up

- PR #90 (previous session's M8.3 NGO + Relay coordinator): all three
  Bugbot threads already fixed, CI green — squash-merged.
- PR #89 (dependabot coverlet.collector 10.0.1 → 10.1.0): branch
  updated over the merge; merged when green.
- origin/main merged before work started; no conflicts (fast-forward).
