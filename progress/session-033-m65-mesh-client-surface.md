# Session 033 — M6.5 mesh: the v3 signaling client surface

Date: 2026-10-03. Scope: the M6.5 milestone item — `SessionPlan`
application (latest-wins, generation fencing, verbatim `initiate`),
`Signal` verbatim relay, `ProvideConnectionInfo`,
`TransportStatus`/`PeerTransportStatus`, and the public mesh view.
Routine drift check first; no open issues, no open/draft PRs, main CI
green.

## Starting state

- origin/main at 35fb1b7 (fuzz target from session 032 landed);
  CI fully green on all four workflows. Working tree clean.
- The mesh message kinds already routed at the envelope layer
  (`MessageKind`/`MessageKindNames`) and the outbound writers
  (`WriteSignal`, `WriteTransportStatus`, `WriteProvideConnectionInfo`)
  existed from earlier milestones; the mesh payloads were absorbed
  silently by the frame pipeline — nothing surfaced, nothing could send
  them, no plan state existed.

## Delivered

- **Protocol decoders** (`Protocol/MessagesMesh.cs`): strict decodes for
  `SessionPlanMessage` (topology/transport enums, host, direct endpoint,
  peers with per-recipient `initiate`, ICE list, relay fallback),
  `NewPeerMessage`, `PeerTransportStatusMessage`, and
  `IncomingSignalMessage` (verbatim signal bytes), plus `IceServerInfo`,
  `DirectEndpointInfo`, `SessionPeerInfo`, and the
  `SessionTopology`/`SessionTransport` enums (0 = obsolete `None`
  sentinels). Unknown fields skipped, repeated keys and wrong-typed
  values rejected. `RoomSnapshot` now decodes the optional `ice_servers`
  pre-gather list (v3-only key; the v2 wire never carries it).
- **Events** (`PollEventKind` 32-35 + `PollEvent` carriers +
  `FramePipeline` routing): plan frames ride the session-fact path (the
  fact applies to the machine AND the typed event surfaces);
  `NewPeer`/`PeerTransportStatus`/`Signal` are payload events; every
  malformed mesh frame surfaces as a `ProtocolViolation` naming the
  kind.
- **Admission state** (`SignalFishStateMachine`): the plan is a session
  fact applied latest-wins and fail-closed (confirmed membership only),
  cleared at every membership boundary (leave, re-baseline, reconnect,
  teardown). New commands `SendSignal`/`SendTransportStatus` (v3-gated)
  and `ProvideConnectionInfo` (v2-compatible per the server doc), all
  player-role gated; new refusals `SessionPlanUnavailable` and
  `StaleSessionGeneration`.
- **Sends** (both clients): `SendSignal` refuses unless a plan is live,
  the generation is current, the selected transport is `webrtc`, and the
  target is a canonical-UUID plan peer — a refused send never touches
  the wire. `SendTransportStatus` and `SendProvideConnectionInfo` ride
  the standard command queue.
- **Public tracker** (`V3/MeshSession.cs` + `MeshPeer`): a faithful port
  of the Rust client's `mesh.rs` — plan full-replace with
  selected-path liveness survival, `NewPeer` gated on the webrtc
  transport (upsert, flag change resets liveness), status gated on the
  selected transport and known peers only, `PlayerLeft` drops the peer
  and clears a departed host/endpoint, ICE pre-gather precedence
  (join/reconnect seed, plan supersedes), reconnect as a hard plan
  boundary, idempotent resets with change-reporting `Apply`.
- **Review hardening** (adversarial pass): shared
  `EnvelopeWriter.IsCanonicalUuid` so the target fence and the encoder
  agree on one strict UUID shape (a non-canonical target refuses, never
  throws out of an admitted send); plan state cleared on every
  membership baseline (Rust `set_room` parity); `generation` decodes
  optional (legacy Server-0.4 v3 plans; the fence stands down — Rust
  parity); `fallback != relay` rejected at decode (noncanonical plan);
  `RoomSnapshot.IceServers` joined equality/hash; the ICE-array walker
  deduplicated into `ProtocolArrays.TryReadObjectArray` (third copy of
  the loop eliminated).
- **Tests**: 792 total (was 699 + 27 filtered skips), all green on
  net8.0 and net10.0. New: `MeshEventTests` (golden wire → typed events,
  malformed corpus → violations, plan-fact routing), `MeshSessionTests`
  (the ported Rust scenario set), state-machine plan lifecycle +
  fence-ordering tables, mapper decode/failure cases, and driver-level
  send tests on both clients pinning the exact golden wire bytes for
  `Signal`/`TransportStatus`/`ProvideConnectionInfo`.

## Verification

- `dotnet build -warnaserror` clean; `dotnet test` 792/792 on both TFMs;
  all six convention lints green; `scripts/tests/run-all.ps1` green.
- No live-server E2E this session (M6.6 owns the scripted
  `v3-mesh-webrtc`/`v3-host-topology`/`v3-host-failover` runs; Docker
  unavailable locally).

## Deferred (tracked)

- Retired-generation plan fence and inbound stale-signal suppression
  (Rust behaviors) — fold into M6.6 with the live scenarios.
- `MeshSession` is the consumer view; the async client does not
  auto-stamp signal generations (`SignalMessage` carries the generation
  explicitly; callers read it from the plan event/tracker).

## Follow-ups

- M6.6 E2E scenarios (next session's focused surface).
- Consider `ClientSnapshot` exposure of the plan view once a consumer
  needs it (the machine reads keep the surface minimal for now).
