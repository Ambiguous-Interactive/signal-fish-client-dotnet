# Session 013 — Test-stack migration (NUnit 4 + FsCheck 3) + fixture pin bump

Date: 2026-09-21. Branch: `test-stack-nunit4-fscheck3`.

## Scope

Issue-debt round: #31/#29 (drive the dependabot nuget bump green via the
NUnit 4 + FsCheck 3 migration), #9 (upstream published the missing v2 wire
samples — bump the pin), #26/#20 conventions in touched code. CI time watch.
~1 hour session shape.

## Delivered

- **Golden corpus covers the full v2 floor (#9 closed)**: pin `eaae1ca` →
  `07a6fd08` (server 0.9.2). v2 server samples 10 → 24 lines — `GameStarting`
  (with `peer_connections`), `RoomLeft`, the typed failure family
  (`RoomJoinFailed`/`SpectatorJoinFailed`/`ReconnectionFailed`), spectator
  lifecycle, `AuthenticationError`, `AuthorityChanged`, `Reconnected`, and
  richer `RoomJoined`/`Authenticated` payload shapes. v2 client samples
  12 → 13 — a second `JoinRoom` form carrying the full optional field set
  with explicit nulls. Corpus-driven tests grew 301 → 319 per TFM (all
  data-driven; no hand-written cases).
- **Wire-truth fix (correctness)**: the new canonical `JoinRoom` sample
  exposed that our writer guessed the wire form at M1.3. Canonical order is
  `game_name, room_code, player_name, max_players, supports_authority,
  relay_transport` with absent optionals as explicit `null`s; `password` is
  emitted only when set, last. Fixed in one place: the envelope writer now
  delegates to the shared `WriteJoinRoomFields`, eliminating two divergent
  field-writer copies (envelope vs nested `RoomOperation` payload). Payload
  decode treats explicit JSON `null` as absent (`TryReadNull`), matching the
  spec (`password: [string, 'null']`) and samples.
- **Test-stack migration (#31, #29 superseded)**: NUnit 3.14 → 4.6.1,
  FsCheck.NUnit 2.16.6 → 3.4.0, Test SDK 17.8 → 18.10.1, coverlet 6.0 →
  10.0.1, NUnit3TestAdapter 4.5 → 6.3.0, NUnit.Analyzers 3.9 → 4.15.0,
  reportgenerator 5.4.1 → 5.5.11. Code changes: one classic assert →
  `Assert.That(..., Throws.Nothing)`; lambda delegates disambiguated with
  explicit `Action` (sync) / `Func<Task>` (async) casts; async delegate
  assertions moved to `await Assert.ThatAsync(...)` (CA1849-enforced);
  FsCheck 3's C# API lives in `FsCheck.Fluent` (`using` added; the
  PascalCase `Prop.ForAll`/`Arb.From`/`Gen.*` bodies are unchanged).
- **Fragile-check elimination**: `TryMap_NonSessionFacts_AreNotMapped` pinned
  corpus LINE NUMBERS and broke when the corpus reordered (line 10 became
  `Error`, not `Pong`). Added `GoldenFixtures.ReadFirstLineOfType` and
  rewrote every line-number pin (mapper tests x4, V2LifecycleTests x1) to
  select samples by wire type; the synthetic
  `Replace("RoomJoined","GameStarting")` hack became the real golden line.

## Red-green evidence

- RED 1: `Write_FixtureLine_ReproducesByteIdenticalFrame(JoinRoom)` failed
  against the new corpus (expected 174 bytes, wrote 103 — the parser dropped
  `max_players`/`supports_authority`/`relay_transport`; after the parse fix,
  field order still diverged at byte 55: writer emitted `player_name` where
  canonical emits `room_code`).
- RED 2: `TryMap_NonSessionFacts_AreNotMapped` failed — line-number pin hit
  `Error` (maps) instead of `Pong`. Root-caused as fragility, not protocol.
- RED 3 (migration): compile-red — CS0305/CS0103 (FsCheck 3 namespaces),
  CS0121 (`Assert.That`/`ThrowsAsync` delegate ambiguity), CS0618
  (`TestDelegate`/`AsyncTestDelegate` deprecated in favor of `Action`/
  `Func<Task>`), CA1849 (sync block on `Func<Task>` asserts).
- GREEN: 319 tests x net8.0 + net10.0, `-warnaserror`, coverage collector
  green on coverlet 10.

## Design notes

- The golden corpus is executable wire truth: "byte-identical to the
  server's own wire samples" (M1.3 claim) is now actually enforced against
  the full v2 floor, not just the subset upstream published at M1.1.
- FsCheck 3 split its API into `FsCheck.FSharp` / `FsCheck.Fluent`; our
  suite already used the C#-friendly PascalCase surface, so the migration
  cost concentrated in NUnit 4's delegate-typing rules.
- Canonical `JoinRoom` emission keeps the M3.4 payload-decode policy noted
  in PLAN.md: explicit `null` = absent.

## CI

No workflow changes; same matrix, same gates. The corpus growth is
data-driven (+18 `TestCaseSource` cases, ~1 s per suite run locally). The
NuGet cache key already hashes `**/*.csproj` + `.config/dotnet-tools.json`,
so the bump invalidates once; first CI run pays a one-time repopulation.

## Verification

`dotnet build -warnaserror` 0 warnings both TFMs; 319 x 2 TFMs green;
coverage lane green; CSharpier clean; zero-dep, LINQ-ban, this.-ban,
file-size, and llm-instruction lints green; fixtures byte-identical to
`07a6fd08` (sync-script verify mode); tooling projects (fuzz, perf) build
green.
