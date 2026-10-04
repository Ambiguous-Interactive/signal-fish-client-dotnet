# Session 040 — M8.2 Mirror adapter + vendored-SDK detection

Date: 2026-10-04
Branch: `mirror-adapter-m8.2`
Closes: #81

## What shipped

**M8.2 Wave 1 Mirror** (`unity/Adapters/Mirror`, package
`com.ambiguous-interactive.signalfish.transport.mirror`), same shape as
M8.1, swapping the bridge's target to `Mirror.Transport` (pinned against
the Mirror v96.9.23 release source):

- `SignalFishMirrorTransport`: authority-as-server over the v3 binary
  relay lane, per-peer fanout behind the 18-byte adapter header, derived
  packet size, staged-event drain so **every** Mirror callback (connect,
  data, errors, disconnects) is raised on the main thread from the
  early/late update iterates — the async bootstrap thread only stages
  facts. Host mode needs no loopback: Mirror's local connection bypasses
  the transport entirely (the FishNet bridge needed one; this one
  deliberately does not).
- `SignalFishRoomManager`: the PLAN's bootstrap component — StartHost
  (creates the room, requests the authority) / StartClient(roomCode) via
  the NetworkManager address; a late-join + reconnect driver sample
  (`Samples~/LateJoinReconnect`) covers the join flows.
- Pure core (wire, peer router, receive rules, MTU) compiles in the
  dotnet suite — +35 tests (wire 14, MTU 6, router 6, receive rules 9).

**Issue #81 (FishNet vendored installs)**: `Editor/DefineDetector`s for
both adapters. FishNet: `versionDefines` stays primary (UPM), the
detector fills the vendored `Assets/` gap by probing the compiled
`FishNet.Runtime` assembly and toggling the same `SIGNALFISH_FISHNET`
define. Mirror: ships as an asset with **no UPM package**, so the
detector is the define's only owner (`SIGNALFISH_MIRROR`; asmdef carries
an empty `versionDefines`, and the lint fails any package pin as a lie).

**Gate generalization**: `lint-fishnet-adapter.ps1` →
`lint-unity-adapter.ps1` with a per-adapter pin table (define ownership,
SDK reference pattern, pinned bridge file, pinned Transport member
surface, detector probes). One CI step lints both adapters; the compile
lane compiles each core standalone (netstandard2.1, C# 9, nullable,
warnaserror). Self-test rewritten (`test-lint-unity-adapter.ps1`, 34
assertions) covering both fixtures, both define-ownership rules, and the
detector contract.

## Decisions

- **Detector over versionDefines for Mirror**: Mirror's documented
  install paths are Asset Store / zip (no root `package.json`; git-URL
  UPM installs don't exist for it). Inventing a package name for the pin
  would be a lie the wire can't keep — the lint now enforces the honest
  shape instead.
- **Per-adapter core, shared hoist deferred**: the Mirror core is a
  mechanical twin of the FishNet core (same wire bytes, same routing
  rules, `HostClientConnectionId`→`HostConnectionId`). Hoisting a shared
  `adapters.core` package is the SSOT-correct refactor but touches the
  shipped M8.1 package; with M8.3-M8.6 planned, the duplication cost
  compounds — filed as a follow-up issue for a dedicated session.
- **Mirror surface pinned to v96.9.23**: the abstract Transport members,
  delegate-field callbacks, `Channels` constants, and `TransportError`
  byte enum were taken from the release tag, not main (they matched, but
  the pin is what the lint enforces).
- **`OnServerError` before disconnect**: Mirror's contract says raise the
  error first, per connection — the lint caught the bridge missing it on
  the first run; session-fatal failures now stage a per-peer error before
  each staged disconnect.

## Verification

- `fast-check.ps1`: 864 passed (net8.0), loopback E2E skipped by design.
- `lint-unity-adapter.ps1`: both adapters pass (cores compile standalone;
  bridges member-complete; detector contracts hold).
- `lint-conventions.ps1`: all six lints clean.
- `sync-unity-package.ps1 -Check`: fresh.
- Mirror adapter tests: 35 passed (`--filter Adapters.Mirror`).
- `mkdocs` not installed locally; docs page added to nav — docs.yml
  validates the strict build in CI.

## Follow-ups

- Shared adapter-core package hoist (filed as an issue).
- M8.7 live validation runbook item covers both adapters (licensed seat).
