# Session 006 — Envelope writer (M1.3)

Date: 2026-09-20
Branch: `envelope-writer-m1.3`
Goal: PLAN.md M1.3 — `EnvelopeWriter` byte-identical outbound frames plus the
per-message payload structs and their decode half.

## What landed

- **Payload structs** (`Protocol/Messages*.cs`, one file per family, all
  `readonly struct` with `IEquatable<T>`): `AuthenticateMessage`,
  `JoinRoomMessage`, `JoinAsSpectatorMessage`, `ReconnectMessage`,
  `AuthorityRequestMessage`, `ProvideConnectionInfoMessage`,
  `GameDataMessage` (+ `GameDataClass`),
  `RoomOperationMessage` (+ `RoomOperationCommand`, `RoomOperationCommandKind`),
  `SignalMessage`, `TransportStatusMessage`. Illegal wire shapes are
  unrepresentable: the GameData class/key pair moves together, and
  `RoomOperationCommand` is a closed factory-built union (kind and payload
  cannot disagree).
- **`JsonWriter`** (in `JsonPrimitives.cs`): allocation-free escape-aware
  UTF-8 writer over `IBufferWriter<byte>`; splits across arbitrarily small
  buffer segments; RFC 8259 short escapes plus `\u00XX`; non-ASCII encodes
  in place (no per-field `byte[]`).
- **`EnvelopeWriter`**: one static writer per outbound message kind (16
  total, including `Pong`). Wire order and `", "` / `": "` canonical
  spacing reproduce the golden fixtures byte-for-byte. Encode misuse throws
  `ArgumentException` (missing required fields, non-JSON verbatim payloads,
  non-canonical UUIDs for `operation_id` / `Signal.to` / `generation`).
  Payloadless commands omit `data` entirely; `SetRoomAccess(null)` presents
  an explicit `"password": null` (reopen semantics).
- **Payload decode** (internal, `[InternalsVisibleTo]` for tests until the
  M3 state machine freezes the public decode surface): member-walk helpers
  on `JsonScanner` (`BeginObject` / `ScanMember` / `EndMember`, `KeyIs`,
  typed readers) + `TryDecode` per outbound struct, mirroring the reader's
  policies (escape-decoded keys, unknown fields skipped, bounded errors,
  no throws). `EnvelopeReader`'s duplicated escape/key helpers were folded
  into `JsonScanner` (behavior preserved; all 123 prior tests pass).

## Verification

- `dotnet build -warnaserror`: 0 warnings / 0 errors (solution-wide).
- `dotnet test`: 228/228 on net8.0 + net10.0 (was 123; +105 new).
- New tests: byte-identical vs all 21 client fixture lines, envelope-level
  roundtrip for the corpus, write→decode→struct-equality roundtrips for
  non-fixture combinations (creation-form join, passwords, SetRoomAccess
  both polarities, correlated lifecycle ops), escaping table (control
  chars, quotes, DEL passthrough), UTF-8 passthrough, chunked-buffer
  writes (1/3/17-byte segments) matching single-segment output, encode
  misuse throws, payload-decode negative classes, and a steady-state
  allocation gate: writing the whole outbound corpus allocates 0 B.
- Mutation checks (temporary, reverted): wire-format drift failed 30
  tests; removed required-field validation failed the misuse test;
  dropped decode advance failed 5 roundtrip tests. The suite detects all
  three corruption classes.
- `scripts/tests/run-all.ps1` 6/6; `lint-zero-dependencies` clean.

## Bugs found by the red-green loop

1. Ref-struct copy bug: helpers took `JsonWriter` by value, so their
   buffer-position mutations were lost on return and later writes clobbered
   already-flushed regions. Fixed by `ref`-passing every mutating helper.
2. `TryReadString` assigns its `out` param before failing, so a
   `TryReadString || TryReadNull` compound overwrote a JSON `null` with
   `""`. Fixed with explicit branching in `TryDecodePassword`.
3. `Memory.Slice(Range)` is absent on netstandard2.1 — decode uses explicit
   `GetOffsetAndLength` slicing.

## CI failure RCA: allocation gate vs JIT/OSR jitter

The PR's first CI run failed all four build legs on exactly one test:
`Write_SteadyStateFullCorpus_AllocatesNothing` measured a 24 B delta —
only in CI and only under `--collect:"XPlat Code Coverage"` (coverlet
instrumentation changes JIT tiering/OSR decisions; the on-stack-replacement
of the measured loop attributed one-time bookkeeping to the thread).
Bisecting per fixture showed 0 B per write; adding a counter call inside
the loop made the failure vanish — confirming measurement jitter, not a
library allocation. Fix: the gate now takes the **minimum delta across 4
passes** — real regressions allocate every pass and stay red; one-time
JIT bookkeeping inflates only the first pass. Verified red-free under
coverage and plain runs, Release and Debug.

Lesson: exact-zero allocation gates must be steady-state (min-of-N), never
single-shot — JIT tier transitions allocate a few bytes nondeterministically.

## Adversarial review round (findings → fixes)

A dedicated adversarial reviewer audited the change against the upstream
protocol doc and reproduced two real defects; all findings were fixed in
this branch:

1. **MAJOR — stackalloc in a loop** in `JsonWriter.WriteString(string)`
   overflowed the (uncatchable) stack at ~800k non-ASCII chars; the
   "stack slots are reused" assumption was wrong. Fixed by hoisting one
   scratch span above the loop, with a 125k-char regression test.
2. **MINOR — `GameDataMessage` ctor kept a key for non-latest classes**,
   so `Equals` contradicted the wire (`decode(encode(m)) != m`). Fixed by
   normalizing in the ctor; decode-side duplication removed.
3. **MINOR — verbatim payloads were checked first-byte-only** while docs
   promised valid JSON. Fixed: `RequireJsonValue` now rescans the full
   value (allocation-free) and requires exactly one value; `{oops` and
   `{} trailing` now throw at the call site.
4. **MINOR — wire-order blind spots**: added byte-exact asserts for the
   nested `RoomOperation` moderation/lifecycle shapes (no upstream
   fixtures exist for them), `relay_transport`, and the spectator
   password.
5. **NITs**: null array elements now throw `ArgumentException` instead of
   NRE; `WritePong` removed (protocol defines `Pong` as the reply to a
   client `Ping`, so the client never sends one); the canonical-UUID
   assumption on `Signal.to`/`generation` is documented.

## Notes / follow-ups

- `Authenticate.game_data_format` / `connect_token` and the JoinRoom
  moderation extras (`max_players`, `supports_authority`, `relay_transport`,
  `password`) have no fixture coverage upstream (issue #9); they are pinned
  by roundtrip tests here and the canonical field order follows the server
  protocol docs.
- Inbound (server→client) payload decode stays with the M3/M6 state
  machine tasks per PLAN.
- Steady-state zero-alloc covers the encode path (issue #7); decode-side
  allocation budgets land with the benchmark work (M1.6).
