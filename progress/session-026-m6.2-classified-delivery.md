# Session 026 — M6.2 classified delivery + iteration speed

Date: 2026-09-23. Scope: sync local main with origin (PR #56 merged
upstream; local carried duplicate squash commits plus the unpushed
session-025b `.llm` work, now bundled here), advance PLAN to the next
surface (M6.2 classified delivery), cut local iteration time ~3x, and
trim CI wall time. One deliverable PR.

## Starting state

- origin/main already contained PR #56 (M6.1, squash); local main was
  content-identical but 4 commits ahead (the pre-squash duplicates plus a
  merge commit). Reset onto origin/main; the three modified `.llm` files
  (session-025b cross-PR feedback audit) were stashed and carried forward.
- Open issues: **zero** — the issue-debt goal is already met; nothing to
  address. Follow-up findings below were fixed in-session, not filed.
- CI on main: green (dotnet 2m33s, e2e 58s, docs 25s).

## Delivered

- **M6.2 — classified delivery (red-green)**
  - Inbound: `IncomingGameData` decodes the sender's `class`/`key`
    (omitted = reliable; unknown/escaped tokens, repeated metadata, or
    malformed keys fail the frame as a typed `ProtocolViolation`) and
    rides the shared `PollEvent`, so both clients surface the class.
    Token matching is zero-alloc (`KeyIs` span compare, no string
    materialization on the classified-frame path).
  - v3 send gate live on all three send paths (polling, async fail-fast,
    async waiting): `latest`/`volatile` without a negotiated v3 refuse
    with `ProtocolUnsupported`; membership/role refusals keep precedence;
    reliable relay is never gated (v2 floor byte-identical). A
    command×delivery-class sweep pins exactly the classified pairs.
  - Depth bounds unified at the server codec's 128: the decode bound
    rises 64→128 (`EnvelopeReader`/`JsonScanner`), so payloads the
    server legally relays decode instead of failing; outbound game data
    validates at construction (alloc-free, budget-bounded walk) and the
    writer's duplicate re-validation is gone — the encode hot path got
    strictly cheaper. Boundary tests pin the scanner semantics
    (root = level 1) and the send→decode roundtrip (embedded payload
    sits two levels deeper than its standalone form).
  - Live-server E2E: `/v3` room, all three classes roundtrip with
    class/key surfaced; one combined `WaitForEventsAsync` drain so
    delivery-timing decoupling cannot eat a later match. Conformance
    checklist row flipped.
- **Local iteration speed (~2.8x)**
  - Fixture-level NUnit parallelization (`[assembly: Parallelizable]`):
    17 s → ~7 s per TFM, suite deterministic and green on both TFMs; the
    library has no mutable statics and the test WS server binds
    ephemeral ports (parallel-safe, verified).
  - `scripts/fast-check.ps1`: single-TFM Debug build (no restore, with
    one-shot fallback restore) + test, ~17 s vs ~47 s for the full
    loop. Added to the `.llm` command table.
- **CI time (flat-or-down)**
  - The Windows cell spent 79 s of its 146 s installing both SDKs and
    set the workflow wall clock. SDKs now install into a cacheable
    `runner.temp/dotnet` (`DOTNET_INSTALL_DIR`/`DOTNET_ROOT`, the
    documented setup-dotnet override) with a version-hash-keyed cache
    (`.github/dotnet-versions.txt` keeps the key honest). Expected wall
    ≈ 1m40s after the first (miss) run. No coverage changes.

## Adversarial review loop (two reviewers)

Reviewer 1 (M6.2) found a BLOCKER the unit tests missed: the async
waiting send (`SendGameDataReliableAsync`) admitted classified messages
on pre-v3 connections (gate defaulted to reliable) — a frame the server
would answer `INVALID_DELIVERY_CLASS` while the client reported success.
Fixed (gate now consults `message.Class`) with a regression test. Also
fixed from review: duplicate class/key now fail decode (docs claimed it,
code did last-wins); the role-precedence test actually tests precedence
now; the inbound class-token read is allocation-free; EnvelopeWriter /
bound docs reworded to match reality; E2E sequential waits replaced with
a combined drain. The 64→128 decode raise came out of the reviewer's
own-frame roundtrip proof (a client could send a payload it could not
receive) — fixed at the root by matching the server codec's limit, per
the Fix Philosophy. Reviewer 2 (CI/tooling) APPROVED with a cache-key
staleness MINOR (fixed via the versions-file hash) and formatting NITs
(applied).

## Verification

- `dotnet test`: 571 passed / 0 failed on net8.0 and net10.0.
- `lint-conventions` (all six lints), `csharpier --check`,
  `lint-file-sizes`, `lint-llm-instructions`, `scripts/tests/run-all.ps1`:
  clean.
- E2E project builds `-warnaserror`; the new scenario runs in the PR's
  `e2e.yml` job (Docker unavailable locally).

## Open / next

- M6.3 DeliveryReport accounting (hardest task; port the Rust client's
  test scenarios as the executable spec) — the wire decoding of
  `DeliveryReport` and the `seq`/`epoch` baselines land there.
- Confirm the CI SDK cache hit on the second run after merge.
