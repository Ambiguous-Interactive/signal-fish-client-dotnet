# Session 025 — M6.1 v3 negotiation + CI-time trim

Date: 2026-09-23. Scope: carry the session-024 in-progress state forward
(already merged upstream as #55), advance PLAN to the next surface (M6.1
v3 negotiation), and trim CI wall time without touching coverage. One
deliverable PR.

## Starting state

- Local main carried an in-progress merge of origin/main with conflict
  markers in two files. Root cause: the local session-024 commits had been
  merged upstream as PR #55 (squash), whose final tree also includes two
  later review commits (release-then-claim E2E flow, combined waits) that
  supersede the local tips. Nothing local was unique — verified by diffing
  local HEAD against the merge head (only those two files differ, and the
  upstream side is the reviewed superset). Resolved by aborting the merge
  and resetting local main onto origin/main.
- Open issues: zero (the session-024 sweep closed #53/#49/#54 and the
  back-catalog). The issue-debt goal is already met; nothing to re-open.

## Delivered

- **M6.1 — v3 negotiation (red-green)**
  - Decode: `ProtocolInfoMessage` gains the extended v3 fields
    (`protocol_version`, `min_protocol_version`, `max_protocol_version`,
    `transports`, `max_outbound_message_size`). Optional with strict
    typing — explicit JSON null decodes as absent, wrong-typed values and
    repeated keys reject fail-closed; the required v2 lists are unchanged,
    unknown fields still skipped (pinned).
  - Session fact: `ProtocolInfo` is now a session fact
    (`SessionEventKind.ProtocolInfo`, carrying the negotiated version) —
    the machine tracks it per connection (a re-echo replaces; teardown
    clears) and `ClientSnapshot.NegotiatedProtocolVersion` exposes it
    (null = pre-negotiation or v2 negotiation, Rust `Option<u16>` parity).
    Malformed `ProtocolInfo` still surfaces as a violation (mapper
    fail-closed), and the surfaced `PollEventKind.ProtocolInfo` event is
    unchanged for consumers.
  - Advertisement: `SignalFishClientOptions.ProtocolVersion` /
    `SupportedTransports` / `SupportedTopologies` / `RequestedCapabilities`
    ride every automatic reconnect handshake (a revived connection must
    re-negotiate, or the server would silently drop the round to the v2
    floor). Null defaults omit the fields — the v2 handshake bytes stay
    byte-identical, pinned against the vendored v3 `Authenticate` fixture.
  - Gate: `AdmissionError.ProtocolUnsupported` + the state-machine
    consultation, checked last (membership/role verdicts win, Rust
    parity). Inert until the first v3-only command exists; an enum sweep
    pins that no current command requires v3, so M6.2/M6.5 flips a row and
    inherits the gate. The wire truth is pinned end-to-end: decode →
    mapper → machine → snapshot, plus the golden reconnect-handshake
    bytes.
  - E2E: `/v2` floor negotiates null; a `/v3` relay-only advertisement
    echoes the capped-down v3 result (asserting the server never raises
    the client above its advertised maximum), mirrored on the snapshot.
- **CI wall time** — the conformance suite now declares
  `[Parallelizable(ParallelScope.All)]`: all 12 scenarios are fully
  isolated (unique game names/rooms, own connections), so the e2e cell's
  wall clock drops from the sum of scenario durations (~85 s) to
  roughly the slowest scenario. The heartbeat scenario's poll cadence
  went from 1 s to 250 ms so its ping schedule stays resilient under the
  parallel suite. Coverage unchanged (same tests, same server checklist).
  Unit cells untouched: they carry allocation gates and concurrency-stress
  pins whose determinism is worth more than seconds.

## Verification

- `dotnet build` clean; 563 unit tests green on net8.0 and net10.0 (+18).
- Six convention lints + csharpier clean; E2E/tooling projects compile.
- Red-check: the new tests fail to compile against the pre-change library
  (all asserted surfaces absent) — build-level red, not just assertion red.
- CI-time: e2e cell expected to decrease (parallel scenarios), dotnet cells
  net-flat (+18 tests ≈ +0.5 s each). First PR run validates both the
  parallel E2E and the live v3 negotiation (Docker is unavailable locally;
  first-live-verification-in-CI is the established pattern).
- Open issue found during this session: none.

## Findings / decisions

- `ProtocolInfo` moving from payload-event to session-fact keeps the
  consumer event surface identical while giving the machine the
  negotiation result — the same pattern `AuthorityChanged` followed in
  M5.2.
- Repeated optional keys reject outright (seen flags), matching the
  `AuthorityResponse.reason` precedent — a repeated key after an explicit
  null included.
- The options constructor validates the advertisement lists (non-empty,
  no null/empty tokens, `relay` present in the transports) and stores
  defensive copies: a malformed list would otherwise throw inside the
  reconnect round's encoder on the driver loop — an unsurfaced session
  death — and a mutated list would change the wire after construction.
- The `ProtocolUnsupported` refusal is one enum value (no Rust-style
  `mode`): the snapshot distinguishes pre-negotiation from negotiated-v2
  only by whether a `ProtocolInfo` event arrived; add diagnostics with
  M6.2 if callers need more.
- Adversarial review round (11 findings; all applied or recorded): the
  decode seen-flag policy, the full 1..255 command sweep (an appended
  command can no longer skip classification), the options validation,
  exact test counts in this brief, the heartbeat cadence hardening, and
  record cleanups. Recorded for later: the C→S `AuthenticateMessage.TryDecode`
  (inbound echo) still tolerates repeated keys — a deliberate M6.2 sweep
  candidate; protocol-version decode is `uint` where Rust uses `u16`
  (the server contract stays the decider).

## Leftover (next session)

- M6.2 classified delivery (`reliable`/`latest{key}`/`volatile`,
  `INVALID_DELIVERY_CLASS`/`INVALID_INPUT`, 128-container depth bound) —
  the first v3-only send; flips the negotiation gate's sweep row red.
