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
- GREEN: 265 tests × net8.0 + net10.0 (35 new), CSharpier repo-wide,
  zero-deps + LINQ-ban lints, `-warnaserror` builds on both TFMs.

## Bugbot round (2 findings, both verified red before the fix)

- **High — dispose during connect leaked the socket**: `DisposeAsync`
  racing the client-config probe let a socket be created *after* disposal
  and upgraded (leaking a live connection). Fix: `ThrowIfDisposed()` guards
  the upgrade; any socket reaching the catch is disposed. The regression
  test parks the connect in the probe via a server-side gate, disposes,
  then verifies no socket reached the wire — or that the server-side
  connection closes promptly. Red check mattered: the first version of the
  test passed against the bug (the post-connect CAS branch also throws ODE),
  so the leak assertion was strengthened until it caught it.
- **Medium — probe cap vs pool buckets**: the receive cap was enforced
  against the rented array length, but `ArrayPool.Rent(5000)` returns an
  8192-byte bucket, so over-cap messages slipped through after one growth.
  Fix: usable capacity is clamped to `_maxReceiveBytes` at rent and growth.
  Pinned by a boundary pair: exactly 5000 bytes delivers; 5001 closes 1009
  (fails without the clamp, verified).

## Final state

- PR #27: all 11 CI checks green (dotnet matrix x2 OS x2 TFM, LLM-context
  lint, markdownlint, spell, link, mkdocs, Bugbot re-review clean); both
  Bugbot findings replied to with fix evidence; #25/#19/#24 closed;
  #26/#20 triaged in comments. 265 tests × net8.0 + net10.0 green.
- CI time: PR cells unchanged-to-faster (tool-restore dedupe); Dependabot
  and the fuzz cache live outside PR CI; coverage unchanged (265 vs 230
  tests, strictly additive).

## Notes for the next session

- `ClientWebSocket` cannot read upgrade-response headers, so the
  `x-signal-fish-max-outbound-message-size` sniff is implemented as the
  equivalent pre-connect HTTP probe (same value per the protocol
  reference); the header path matters for the M7 browser transport.
- Received frames materialize as exact-size copies (caller-owned);
  M3's ring-buffer drain can re-examine whether slices beat copies.
- M3 (polling client) is the next milestone; `IBoundedQueue`/ring buffer
  per PLAN.
