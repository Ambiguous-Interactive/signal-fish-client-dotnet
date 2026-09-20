# Session 011 — Polling core (M3.1, M3.2) + issue debt

Date: 2026-09-20. Branch: `polling-core-m3.1-m3.2`.

## Scope

One coherent surface: the polling client's foundation (PLAN M3.1 + M3.2),
red-green, plus issue-debt reduction (#21, #22, #7). ~1 hour session shape.

## Delivered

- **M3.1** — `ISignalFishClock` (monotonic ms) + `SystemClock`
  (Stopwatch-backed; immune to NTP/DST wall jumps) in `Core/`. `VirtualClock`
  test double in the test project; `ClockTests` pins monotonicity and
  explicit-time behavior. Rationale: `netstandard2.1` has no `TimeProvider`;
  heartbeat/backoff scheduling (M3.4/M4) needs injectable virtual time.
- **M3.2** — `SignalFishStateMachine` + contract types in `Core/`:
  `ConnectionPhase` (derived phase table), `RoomRole`, `RoomMembership`
  (four-field invariant as one struct), `PendingRoomOperation`,
  `AdmissionError`, `ClientCommand`, `SessionEventKind`/`SessionEvent`.
  Semantics ported 1:1 from the Rust client (`client_core.rs`, verified by
  sub-agent research): constructed-live start, sticky transport-ready,
  absorbing terminal, four-field set/cleared atomically, fail-closed fence
  (typed success / matching typed failure / teardown release only; generic
  `Error` never releases), admission error precedence
  `NotConnected → NotAuthenticated → RoomOperationPending → membership/role`.
- **Issue debt**: #21 closed (rule 16 STE-copy rule already exists in
  `.llm/context.md`; parity with the godot repo's resolution of its #38).
  #22 closed as duplicate of #7. #7 progress comment posted (state machine
  hot path now allocation-gated; remaining: M3.4 idle-poll gate, M4.1 buffer
  spike).

## Red-green evidence

- Planted bug 1: generic `ServerError` releasing the fence →
  `Fence_ReleaseTable_TypedResultsOnly` failed. Reverted → green.
- Planted bug 2: `RoomLeft` clearing membership fields but keeping the role
  (violating the four-field atomic clear) → caught by 2 tests. Reverted →
  green.
- 287 tests × net8.0 + net10.0 (22 new), all green.

## Bugbot round (2 findings, both fixed and pinned)

- **Mismatched leave cleared membership**: `RoomLeft`/`SpectatorLeft` wiped
  membership before the type check, so a stray leave while fenced could
  evict a live room — asymmetric with join handling. Fix: while fenced,
  only the leave kind the fence awaits clears membership; mismatched leaves
  are ignored (fail-closed). Unfenced leaves stay tolerant (server-initiated
  removal). Pinned by `Fence_MismatchedLeave_IsIgnoredFailClosed` +
  `Fence_UnfencedLeave_IsAcceptedAsServerRemoval`.
- **`RoomCode` typed non-null but null on default**: annotation now `string?`
  so the absent membership's contract is honest.

## Verification

`dotnet build -warnaserror` 0 warnings both TFMs; CSharpier clean; file-size,
LINQ-ban, and zero-dep lints green; all automation self-tests green.

## Design notes

- Enums are `int`-backed (analyzer-clean). The `Protocol/` byte-backed
  suppression stays scoped to wire types; `Core/` types are not on the wire.
- `Admit` is pure; `Arm` is separate — a failed enqueue never wedges the
  fence (mirrors Rust's `record_admission`-after-validate order and its
  serialization-failure release).
- `PendingOperationFor` returns `PendingRoomOperation?` — nullable-struct
  style, no sentinels (issue #26 direction).
- Authority-gated `StartGame` (Rust `AuthorityRequired`) intentionally
  deferred to M5.2 when authority tracking exists.
- Wire payloads → `SessionEvent` mapping lands with M3.3 (inbound payload
  decode); the machine is wire-agnostic by design.

## CI

No workflow changes. Net-zero CI time; coverage strictly additive (265 → 287
tests per TFM).
