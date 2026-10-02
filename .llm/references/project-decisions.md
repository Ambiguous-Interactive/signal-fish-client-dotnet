# Project Decisions & References

Locked decisions, product scope, upstream references, and shipping
checkpoints for the .NET client. PLAN.md links here instead of carrying
this context itself; when a decision changes, change it here (and note the
change in the session file and improvement log).

## Locked decisions

| Decision | Choice |
| --- | --- |
| v1 (0.1.0) protocol scope | **Full 0.14 parity**: v2 floor + v3 delivery classes/DeliveryReport accounting + mesh signaling |
| JSON codec | **Hand-rolled UTF-8 codec**, zero dependencies, zero reflection (supersedes the STJ guidance in the json-serialization skill) |
| CI E2E | **Docker E2E in CI** against `ghcr.io/ambiguous-interactive/signal-fish-server` (license-free) |
| NuGet distribution | **GitHub Packages** (+ `.nupkg` attached to GitHub Releases as no-auth fallback) |
| Unity distribution | **UPM tarballs** on GitHub Releases (git-URL installable) |
| Unity in CI | **Never.** All Unity validation is local, MCP-driven (avoids blocking a licensed seat) |
| Engine adapters | Wave 1: FishNet, Mirror, NGO + Unity Relay, generic BYOS sample. Wave 2: Fusion/PUN2, Steamworks (Facepunch + Steamworks.NET) |
| Adapter validation depth | **Two-client loopback E2E** per adapter in local Unity via the MCP pipeline |
| Language floor | C# 9 / `netstandard2.1` (Unity 2021.2+ compatible; `IsExternalInit` polyfill) |
| Tooling defaults | NUnit, SharpFuzz, FsCheck, BenchmarkDotNet, MinVer, coverlet + ReportGenerator, CSharpier, mkdocs. All swappable |
| Local working docs | `PLAN.md` and `GOAL.md` are gitignored, local-only docs (never published or linked from the site) |

## Product scope

**Is:** a C# client SDK for the Signal Fish signaling protocol — rooms,
lobbies, readiness/game-start, relay (`GameData`), heartbeats,
reconnection, spectators, authority, v3 negotiation, classified delivery,
WebRTC mesh signaling, binary game data. Two consumption shapes: async
(`SignalFishClient`) and frame-driven (`SignalFishPollingClient`) for
Unity `Update()` loops.

**Is not:** a game engine, rollback netcode, object replication, or a
WebRTC data-channel implementation. The game owns simulation and peer
networking.

**Non-goals for 0.1.0:** MessagePack game-data payload codec
(wire-negotiation support only, payload codec later), NuGet.org
publishing, Unity-in-CI, source-generated serializers.

## Upstream references

- Server repo: <https://github.com/Ambiguous-Interactive/signal-fish-server>
  - Client-author contract: `docs/guides/building-a-client.md`
    (conformance checklist + pitfalls — authoritative; the E2E suite maps
    1:1 to it, and each implemented item flips a row in
    `docs/conformance.md`)
  - Protocol reference: `docs/protocol.md`; versions:
    `docs/concepts/protocol-versions.md`; reconnection:
    `docs/concepts/reconnection.md`
  - Error codes: `docs/reference/error-codes.md`; AsyncAPI spec:
    `spec/signal-fish-protocol.asyncapi.yaml`; golden wire samples:
    `.llm/code-samples/protocol/`
  - Worked scenarios: `docs/scenarios/v2-two-player-relay.md`,
    `v3-mesh-webrtc.md`, `v3-host-topology.md`, `v3-host-failover.md`
- Rust client (API parity source):
  <https://github.com/Ambiguous-Interactive/signal-fish-client-rust>
  (`docs/client.md` is the API mirror target; events/errors/testing/
  transport pages)
- Local canonical facts:
  [protocol-quick-reference](./protocol-quick-reference.md) — when it
  disagrees with the server, the server wins (update it).

## CI shape

Workflows live in `.github/workflows/` and are the operational truth:
`dotnet.yml` (3-cell matrix, `-warnaserror`, coverage, allocation-gate
tests), `e2e.yml` (server service container, open-dev env, conformance
suite), `docs.yml` (mkdocs + markdownlint/typos/lychee, Pages deploy on
main), `fuzz.yml` (weekly codec fuzz gate), `release.yml` (tag `v*`).
The E2E server must run exactly as documented for local dev.

## Shipping checkpoints

| Milestone | Ships |
| --- | --- |
| M0-M5 (done) | 0.1.0-rc capability: protocol core, transport, polling, async, reconnection, spectators/authority/credentials |
| M6 | 0.1.0 — full 0.14 parity (negotiation, classified delivery, accountability, binary data, mesh) |
| M7 | Unity 1.0-ready (UPM package, WebGL transport, docs site) |
| M8 Wave 1 / Wave 2 | Adapter releases (FishNet, Mirror, NGO, BYOS / Fusion, PUN2, Steamworks) |
| M9 | Finalized release pipelines (runs incrementally from M0) |

Each checkpoint: `dotnet build`, `dotnet test`, E2E green, budgets
recorded, changelog + improvement log updated, skills index regenerated
if `.llm` changed.
