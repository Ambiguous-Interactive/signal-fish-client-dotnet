# Session 010 — Transport layer (M2) + issue debt round

Date: 2026-09-20. Scope: land PLAN M2 (transport) end to end and pay down
issue debt without touching PR CI time. Issue debt: #25 and #19 closed by
this session's work; #24 (TOCTOU) closed by the transport's race-free
design + tests; #26 and #20 triaged with status comments.

## What shipped

- **M2.1 — `ITransport` contract** (`src/SignalFish.Client/Transport/`):
  `ITransport` (connect once / send verbatim / receive in order),
  `TransportFrame` (payload + text/binary flag, or the terminal close),
  `TransportClose` (wire code + server-defined `TransportCloseKind` for
  4000-4007, 1009, 1000, 1006), `TransportClosedException` (carries the
  close — error codes are data). Contract tests (`TransportContractTests`)
  pin the full table: connect-once, send-before-connect misuse, verbatim
  sends, ordered receives, close surfaced exactly once, abort during a
  pending receive, post-close send carrying the close, and concurrent
  dispose idempotence — via the scripted `FakeTransport`.
- **M2.2 — `WebSocketTransport`** against a hand-rolled loopback WebSocket
  server (`TestWsServer`: TCP + RFC 6455 handshake + masked/unmasked
  framing; no extra packages, no OS URL ACLs, runs on both CI OSes):
  - client-config probe before upgrade sizes receive buffering
    (`max_outbound_message_size`, fallback to the 8 MiB protocol default on
    any probe failure); the probe is enforced for real (server advertising
    16 bytes → 17-byte message → 1009 close).
  - text and binary receives; sends capped at the server inbound limit
    (default 64 KiB, ctor-tunable) and rejected client-side before the wire.
  - close-code mapping pinned end-to-end by a data-driven loopback suite
    (all 8 server codes + 1009 + an unmapped code).
  - single-reader, single-writer, atomic state transitions (CAS, no
    check-then-act), close delivered exactly once, idempotent race-safe
    dispose — the TOCTOU-avoidance shape #24 asks for.
- **M2.3 — `docs/transport.md`**: the WebGL reality (`ClientWebSocket`
  unusable → injected `ITransport`; reference browser transport lands in
  M7), plus the transport contract summary. Wired into mkdocs nav.
- **Issue #25 — Dependabot**: `.github/dependabot.yml`, weekly, NuGet +
  GitHub Actions, each group in one PR. Zero PR-CI impact (own schedule).
- **Issue #19 — fuzz corpus persistence**: `fuzz.yml` caches `.fuzz/`
  between scheduled runs (per-run key + prefix restore; driver stays
  SHA256-pinned on every run). Crash-graduation procedure documented in
  `scripts/fuzz-codec.ps1`. Scheduled lane only — PR CI time flat.
- **CI time trim**: `dotnet.yml` ran `dotnet tool restore` twice per PR
  (CSharpier check + coverage report); now once.

## Red-green evidence

- Contract RED: `Send_BeforeConnect_IsMisuse` initially asserted the exact
  NUnit constraint `Throws.InvalidOperationException` and failed against the
  derived `TransportClosedException` — weakened to `InstanceOf` (the
  contract is "misuse throws InvalidOperationException-family", and the
  derived type is the better signal).
- Probe scanner RED: the loopback probe-enforcement test failed with the
  first scanner (it treated the key's closing quote as a parse error);
  fixed and re-verified.
- Mapping RED: planted `case 4007 → Unknown` in `TransportClose.MapCode` →
  data-driven loopback suite failed; reverted → green.
- GREEN: 262 tests × net8.0 + net10.0 (32 new), CSharpier repo-wide,
  zero-deps + LINQ-ban lints, `-warnaserror` builds on both TFMs.

## Notes for the next session

- `ClientWebSocket` cannot read upgrade-response headers, so the
  `x-signal-fish-max-outbound-message-size` sniff is implemented as the
  equivalent pre-connect HTTP probe (same value per the protocol
  reference); the header path matters for the M7 browser transport.
- Received frames materialize as exact-size copies (caller-owned);
  M3's ring-buffer drain can re-examine whether slices beat copies.
- M3 (polling client) is the next milestone; `IBoundedQueue`/ring buffer
  per PLAN.
