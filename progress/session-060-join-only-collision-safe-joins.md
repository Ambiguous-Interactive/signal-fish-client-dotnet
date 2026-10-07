# Session 060 — JoinRoom.join_only (collision-safe admission)

Date: 2026-10-07
Branch: `join-only-admission`
PR: (this session)

## Drift check

- origin/main already merged (working tree clean at `30b824a`, #116);
  no stash; no open or draft PRs from earlier sessions.
- Main CI green on #116 (dotnet, Docs, e2e success; LLM Context
  finishing).
- Open issues at start: #115 (operator-blocked: npm account +
  `NPM_TOKEN`), #80 (dormant by its own text). Neither is
  agent-actionable.
- Upstream drift: the server's `Unreleased` is metrics/ops only, but
  the **0.10.0** release (2026-10-06) carried client-facing additive
  contracts the client had not mirrored: `JoinRoom.join_only` (#625,
  #630), `ProtocolInfo.implementation_version` (#631), and
  `ProtocolInfo.game_data_limits` (#634). Session 059 had covered the
  downgrade notice from the same release train.

## The surface: join_only — the most gameplay-impacting item

Without the flag, a directory-driven join that names an explicit code
which no longer resolves **silently creates a duplicate room** on the
wrong home. The server's contract (docs/protocol.md rule 4, serde in
`src/protocol/messages.rs`): `join_only: true` refuses an unknown code
with `ROOM_NOT_FOUND` and never creates; `join_only` without
`room_code` is refused `INVALID_INPUT`; absent/`false` keeps the
legacy create-on-join; an older server ignores the flag (treat an
unexpected `RoomJoined` as version skew and re-resolve).

## The API shape (the session's hard part)

The public surface is frozen at `v0.1.0` behind the api-compat gate,
so the field could not ride the existing ctor:

- an in-place 8th defaulted parameter removes the baseline ctor (gate
  red);
- a second fully-defaulted public ctor makes every existing call
  ambiguous (CS0121 — no fewer-optional-parameters tie-break);
- a second public ctor without defaults breaks skip-the-middle named
  calls (CS7036).

Shipped shape: get-only `bool? JoinOnly` property + public fluent
`AsJoinOnly()` copy (throws `ArgumentException` naming `RoomCode` on a
codeless join — the server's INVALID_INPUT pairing is unrepresentable
at the call site) + internal full ctor for decode. Additive only;
api-compat clean against the released `v0.1.0` package.

## Red-green

- RED: the new tests failed to compile (no `JoinOnly`), then the tail
  test failed on the `false` case (`AsJoinOnly` always sets true —
  `false` only arrives off the wire, so decode→re-encode fidelity got
  its own test).
- GREEN: 955/955 on net8.0 and net10.0; csharpier clean; all six
  convention lints clean; lint-file-sizes (improvement log pruned back
  to the threshold); lint-llm-instructions; api-compat gate clean;
  Unity mirror fresh (`sync-unity-package` + `-Check`); E2E, fuzz, and
  perf projects build `-warnaserror`.

## What shipped

- `JoinRoomMessage.JoinOnly` + `AsJoinOnly()`; decode branch (bool or
  null, wrong type rejected); `Equals`/`GetHashCode` include the flag.
- `EnvelopeWriter`: `join_only` written last, after `password`,
  omitted when null — every pre-existing canonical frame stays
  byte-identical (untouched full-string tests pin it).
- Tests: 11 unit cases (byte-exact tails, round trips, v3
  `RoomOperation` nested tail, refusal, decode wire forms, wrong-typed
  rejection, false-fidelity) + a live conformance case in
  `RefusedJoinsCarryTypedErrorCodes` (join-only refusal
  `ROOM_NOT_FOUND`, same code flagless creates — the contrast proves
  the refusal came from the flag).
- Docs: `protocol-quick-reference.md` (contract + version-skew note),
  `docs/client-api.md`, XML docs, `CHANGELOG.md` (Added), Unity
  mirror.

## Adversarial loop

Two sub-agent rounds. Round 1 found two red gates (csharpier format on
three files, comment-form on the new E2E comment), the CHANGELOG
Unreleased order (Added → Changed → Fixed), a pre-existing wrong wire
order in the struct's XML doc that my diff was cementing, and the
missing `paramName` on the new throw. Round 2 verified all fixes,
caught a post-format-edit csharpier regression (missing blank line),
and confirmed the rest. Both findings folded into the improvement log.

## Leftover / notes

- Filed: #117 tracks `ProtocolInfo.implementation_version` and
  `game_data_limits` (0.10.0 additive, diagnostics / usability tier)
  for a next round.
- The E2E conformance addition is CI-verified (no Docker in this
  environment; the e2e workflow runs the suite against the real server
  on every PR).
- #115 and #80 stay open, operator-gated; see PLAN.md.
