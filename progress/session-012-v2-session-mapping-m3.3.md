# Session 012 — M3.3 v2 session-fact wire mapping + issue debt

Date: 2026-09-21. Branch: `v2-lifecycle-m3.3`.

## Scope

One coherent surface: PLAN M3.3 (v2 lifecycle wire→session mapping),
red-green, plus issue-debt reduction (#9, #7, #26, #20) and dependency
rounds (#28 merged green; #29 RCA'd). ~1 hour session shape.

## Delivered

- **Routing completeness (correctness fix)**: the v2 server wire types
  `RoomJoinFailed`, `SpectatorJoinFailed`, `ReconnectionFailed`,
  `SpectatorJoined`, `SpectatorLeft`, `NewSpectatorJoined`,
  `SpectatorDisconnected`, `PlayerReconnected`, `AuthorityChanged`,
  `AuthenticationError` were absent from `MessageKind`/`MessageKindNames` —
  every one of them would surface as `UnknownMessage`, stranding session
  facts (a join failure could never release its fence). All 10 appended
  (append-only: existing ordinals unchanged, fuzz seeds stay valid) and
  verified against the server AsyncAPI spec (`43a3d8e`) + Rust client.
- **Membership decode**: `RoomJoinedMessage`/`SpectatorJoinedMessage`
  session-critical subsets (player/spectator/room ids + room code) with
  unknown-field tolerance; required fields enforced via seen flags.
- **`SessionEventMapper`**: the single Protocol→Core bridge;
  `TryMap(EnvelopeEvent, out SessionEvent)` covers all 13 session-fact
  kinds; non-session messages (gameplay, lobby, liveness) are refused so
  they stay with the M3.4 event surface; unknown types never map.
- **Wire-truth fixes (red-green)**: `SessionEvent.Authenticated()` is
  payload-less (the v2 `Authenticated` message carries no player id — the
  identity is confirmed with the membership at join/reconnect); the machine
  lost `_authenticatedPlayerId`/`AuthenticatedPlayerId` (the four-field
  membership owns the identity). Failure kinds renamed to wire names
  (`JoinRoomFailed`→`RoomJoinFailed`, `JoinSpectatorFailed`→
  `SpectatorJoinFailed`, `ReconnectFailed`→`ReconnectionFailed`).
- **Allocation-free `TryReadGuid`**: `JsonScanner` gains hyphenated-UUID
  parsing (no string materialization), byte-order pinned data-driven
  against `Guid.Parse`.
- **Issue debt**: #9 checked upstream (HEAD `43a3d8e` still lacks the
  missing fixtures; M3.3 closed the decode gap with spec-shaped canonical
  frames pinned in tests), #7 (mapper hot path allocation-gated),
  #26/#20 (conventions applied in all new code; contradiction between the
  issue's underscore ban and the repo's `Method_Scenario_Expectation` rule
  flagged to the owner), #29 RCA'd (NUnit 4/FsCheck 3 major migration —
  follow-up issue opened).
- **Dependencies**: #28 (actions bumps) merged — all CI lanes green. #29
  (nuget bumps) is red by design of its own bumps; migration scoped in the
  follow-up issue.

## Red-green evidence

- RED: routing test (`RoomJoinFailed` et al. → `TryRoute` false);
  mapper/lifecycle suites compile-red on the new API.
- Planted-bug equivalents found by the new suites during development (each
  caught by a failing test before the fix):
  1. `TryReadGuid` skipped the third hex group (hyphen at 18) and over-read
     the array → `IndexOutOfRange` + wrong Guids. Caught by the lifecycle
     spectator test.
  2. Field endianness wrong (pairs used as whole fields) →
     `0f8fad5b-…` decoded as `0000000f-00d9-0046-…`. Caught by the
     `Guid.Parse` oracle test.
  3. All-FFFF fields alias the `-1` error sentinel → `FFFFFFFF-…` rejected.
     Caught by the same oracle.
  4. Missing `player_id` accepted (no seen flags). Caught by the malformed
     payload test.
- 301 tests × net8.0 + net10.0 green (287 → 301; 14 new).

## Design notes

- Session-critical subset decode is deliberate: the remaining v2 payload
  fields (players, lobby state, rate limits) land with the M3.4 event
  surface that will actually surface them; decoding them now would be dead
  surface. Unknown-field tolerance keeps the subset spec-safe.
- The 0 B allocation gate scopes to payload-less/failure session facts
  (per-frame traffic); join/reconnect mapping allocates exactly one string
  (the membership's room code) on a cold path — same policy as the codec's
  encode-only 0 B gate.
- `SessionEventMapper` is `internal` + `InternalsVisibleTo` (issue #26
  direction); the public event-surface API freezes at M3.4.
- Authenticated idempotence is retained (re-apply keeps the flag) — the
  removed "conflicting id" guard has no wire counterpart anymore.

## CI

No workflow changes; dotnet lane time flat (same matrix, same gates);
coverage strictly additive (287 → 301 tests per TFM). #28's action bumps
land on main first and ride in from the rebase.

## Verification

`dotnet build -warnaserror` 0 warnings both TFMs; 301 × 2 TFMs green;
CSharpier clean; zero-dep, LINQ-ban, this.-ban, file-size lints green.
