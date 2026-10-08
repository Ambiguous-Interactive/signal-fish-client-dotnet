# Session 061 — ProtocolInfo 0.10.0 parity (implementation_version + game_data_limits)

Date: 2026-10-08
Branch: `protocol-info-010-parity`
PR: (this session)

## Drift check

- origin/main already merged (working tree clean at `1ad0305`, #118);
  no stash; no open or draft PRs; stale local branches from merged
  PRs (`join-only-admission`, `server-downgrade-notice`) deleted.
- Main CI green on `30b824a`; `1ad0305` runs finishing (e2e + Docs
  already success).
- Open issues: **#117** (this session's target), #115
  (operator-blocked: npm org + `NPM_TOKEN`), #80 (dormant by its own
  text).
- Upstream drift: server main moved past 0.10.0 (2026-10-06). The
  published wire-sample corpus (`.llm/code-samples/protocol/`) was
  re-issued for 0.10.0.

## The surface: close issue #117 — mirror the last two 0.10.0 fields

`ProtocolInfoMessage` (decode-only S→C) silently skipped two additive
server 0.10.0 fields; unknown fields decode as absent, so nothing
broke — the client just could not see them:

- `implementation_version` (#631): the exact server release disclosed
  behind authentication on v3 — pin the deployment a session was
  tested against.
- `game_data_limits` (#634): one `{encoding, max_bytes}` entry per
  capped encoding the connection can negotiate, canonical encoding
  order; present only when the deployment configures
  `security.max_game_data_bytes`; advisory (the server refuses
  over-cap payloads `MESSAGE_TOO_LARGE` at admission).

Contract verified against the server's `src/protocol/types.rs`
(`ProtocolInfoPayload`, `GameDataLimitPayload`) and
`docs/protocol.md` — matched upstream, never guessed.

## Golden corpus bump (red-green source)

`sync-protocol-fixtures.ps1` re-vendored the corpus at
`44c90905` (server main, post-0.10.0; old pin `07a6fd08` was 0.9.2).
Reviewed upstream diff: the v3 `ProtocolInfo` sample gained
`implementation_version: "0.10.0"` plus a second all-formats sample;
new client samples cover `JoinRoom.join_only: true` and
`Authenticate.game_data_format: rkyv|protobuf`. No upstream sample
exists for `game_data_limits` (deployment-gated, absent by default) —
its decode policy is pinned by inline wire strings, noted in
`PROVENANCE.md` (template updated in the script).

The new client samples exercise the encode path byte-identically, so
`EnvelopeWriterTests.BuildFromFixture` learned two fixture fields:
`game_data_format` (Authenticate) and `join_only` (JoinRoom →
`AsJoinOnly()`). Both writer paths already existed and reproduce the
lines exactly.

## What shipped

- `GameDataLimit` public struct (`Encoding`, `MaxBytes`) with strict
  decode: required `encoding` (string) + `max_bytes` (uint), repeated
  key / wrong type rejected, unknown fields skipped — the
  `IceServerInfo` policy.
- `ProtocolInfoMessage.ImplementationVersion` +
  `GameDataLimits`: additive optional properties, ctor defaults,
  `Equals`/`GetHashCode` extended, decode branches with seen flags and
  null-as-absent (same policy as the other v3 fields). Decode-only, so
  no fluent guard; additive-only under the api-compat gate.
- Tests: golden fixture asserts (v3 sample: version `"0.10.0"`, limits
  absent; v2 sample: both absent); present-decode across all four
  encodings + payload equality; present-empty array stays an empty
  list (not invented absence); 11 new malformed-wire table cases.
- Docs: `protocol-quick-reference.md` (new "ProtocolInfo payload"
  section — the SSOT mirror of the server docs),
  `docs/protocol-versioning.md` step 2, XML wire-order doc extended,
  `CHANGELOG.md` (Added). Unity mirror synced.

## Verification

- RED first: new assertions failed to compile (no properties).
- GREEN: 980/980 Release net8.0 (full suite, loopback included);
  fast-check 953 passed / 27 loopback-skipped; csharpier clean; six
  convention lints clean; lint-file-sizes + PLAN budget; fixture
  verify-mode byte-identical at the new pin; E2E/Perf/Fuzz build
  `-warnaserror`; all 18 script self-tests pass; Unity mirror fresh.

## Adversarial loop

One sub-agent round found: a CA1861 analyzer error (constant array as
ctor argument in the equality assert — hoisted to locals/statics) and
a `lint-comment-form` violation (2-line `//` → `/* */` block); both
fixed and re-verified. Second round confirmed zero remaining issues.

## Leftover / notes

- #117 closes with this PR. Remaining 0.10.0 parity: none — the
  release's additive contracts are fully mirrored (join_only in 060,
  downgrade notice in 059, these two fields here).
- #115 (npm activation) and #80 (mkdocs extensions) stay open,
  operator-gated / dormant.
- `lint-conventions` takes >3 min locally (fine on CI); not a
  regression from this session.
