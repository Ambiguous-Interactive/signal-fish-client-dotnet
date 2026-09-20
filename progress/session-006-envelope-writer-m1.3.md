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
- `dotnet test`: 224/224 on net8.0 + net10.0 (was 123; +101 new).
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
