# Improvement Log

Episodic staging log for the mandatory
[reflect-improve](./skills/reflect-improve/SKILL.md) loop. Newest first.
Each entry is an H2 header starting with the date (`## YYYY-MM-DD - scope`)
followed by Trigger / Evidence / Findings / Applied / Open bullets.

Prune an entry once its knowledge has graduated into durable artifacts and
its `Open` items are resolved — this file is staging, not storage (target
under ~150 lines; the 300-line lint ceiling is the hard bound).

## 2026-09-21 - session 012: M3.3 v2 session-fact wire mapping + wire-truth audit

- Trigger: PLAN M3.3 (inbound payload decode -> SessionEvent) exposed two
  wire-truth classes the golden corpus could not catch (corpus has no
  Failed/spectator frames and a placeholder-only RoomJoined).
- Findings: (1) routing-table completeness needs an independent source (the
  server AsyncAPI spec) — kind sets derived from samples alone silently
  strand session facts as `UnknownMessage` (the whole `*Failed`/spectator
  family was unroutable). (2) `Guid.Parse` parity for hand-rolled UUID text
  decode has three trap classes: field big-endianity (first three groups
  are MSB-first but serialize little-endian into the binary Guid), group
  boundaries (8-4-4-4-12, hyphens at 8/13/18/23 — the third hex group sits
  at 19, not 18), and the -1 error sentinel aliasing an all-FFFF field
  (validate each pair, never OR combined signed ints). (3) required-field
  enforcement needs per-field seen flags — `default(Guid)` is a valid
  value, absence is not. (4) 0 B allocation gates must scope by path:
  join mapping legitimately allocates the membership's room-code string
  (cold); payload-less session facts (per-frame traffic) stay 0 B.
- Applied: mapper + routing tests data-driven from spec-shaped frames;
  `TryReadGuid` pinned against `Guid.Parse` as oracle; fence semantics
  unchanged; appended MessageKinds keep ordinals stable (fuzz seeds).
- Open: fold (2) into json-serialization skill when next edited (300-line
  cap).

## 2026-09-21 - session 012: PR #30 feedback verification + sentinel-sweep leftovers

- Trigger: request to re-fetch all PR #30/#27 feedback (human + bugbot),
  verify the dispositions landed on main, and sweep for sibling issues.
- Evidence: all 5 PR #30 threads + 2 PR #27 threads verified fixed on main
  (leave fence, `RoomCode` nullability, 13 enum sentinels, `this.` lint
  wiring, fuzz generator, dispose-during-connect, receive-cap clamp);
  build green, 287 tests x 2 TFMs. Sweep then found 3 leftovers: (1) a
  test-internal enum (`JKind`) kept a *valid* member at 0 — the session-011
  sweep counted only `src/` enums; (2) the 5-byte bugbot repro seed was
  committed at the repo root (`crash-*.bin`, tracked in 4c7da9d) — the
  standalone-replay technique writes artifacts wherever the shell CWD is;
  (3) an exhaustive switch over a sentinel-bearing enum (`Render(JNode)`)
  silently rendered nothing for an unset kind.
- Findings: (1) sentinel sweeps must include test projects; the rule is
  project-wide but the inventory habit was src-scoped. (2) repro/crash
  artifacts belong in gitignored persistence dirs; `crash-*.bin` is now
  ignored repo-wide and the seed lives in `.fuzz/crashes/`. (3) once
  `default(T)` is a distinct non-valid value, every switch over that enum
  must be fail-closed (throw on undefined), mirroring `Admit`/`Apply`.
- Applied: `JKind` sentinel + renumber + fail-closed `Render` default;
  `.gitignore` `crash-*.bin`; rules folded into api-design (sweep scope +
  fail-closed switches), create-test (artifact location), and an
  address-pr-feedback sweep-table row.

## 2026-09-20 - session 011: polling core (M3.1/M3.2) + enum-default and this.-ban project sweep

- Trigger: PR #30 human review: (1) force every enum's default (0) to a
  non-valid `None` sentinel with `[Obsolete]`, project-wide; (2) ban `this.`
  qualification. Supersedes the PR #11-era decision to leave
  `MessageKind`/`DecodeError` unmarked.
- Evidence: 13 enums inventoried; only `EnvelopeEventKind` was compliant.
  `ConnectionPhase`/`ClientCommand`/`JsonMemberState`/`GameDataClass`/
  `RoomOperationCommandKind` had valid members at 0 — worst case,
  `default(GameDataMessage)` silently claimed *reliable* delivery. Marking
  sentinels `[Obsolete]` first turned `-warnaserror` into the sweep linter:
  CS0618 enumerated all 57 reference sites, each swept to `default(T)`.
- Findings: (1) `EnforceCodeStyleInBuild` does NOT enforce IDE0003
  (this. qualification) or naming rules (IDE1006) — Roslyn computes them
  IDE-side only; build-time enforcement needs the repo-conventional lint
  script (new `lint-no-this-qualification.ps1` + self-test + hook + CI,
  mirroring `lint-no-linq.ps1`; dotted `this.` is always a violation —
  ctor chaining `: this(` and indexers `this[` carry no dot). (2) Mechanical
  identifier renames contaminate XML-doc prose — grep `///` for the old
  token after any scripted rename. (3) The writer now refuses
  `default(GameDataMessage)` (unset delivery class) — encode misuse throws,
  matching the codec philosophy. (4) Public API flipped `Admit` →
  `TryAdmit(command, out AdmissionError)` so callers never reference the
  sentinel by name.
- Applied: enum sweep (all 13 enums), `this.` ban sweep + `_camelCase`
  field renames, `.editorconfig` qualification/naming rules (IDE-side),
  lint script + hook + CI step, rules 17-18 in context.md, enum-default
  pattern in [api-design](./skills/api-design/SKILL.md). 287 tests x 2 TFMs.
- Bugbot round 2 (own fallout, red-green proven): the sentinel insertion
  silently renumbered `GameDataClass`, so the fuzz writer generator's
  `(GameDataClass)(byte % 3)` sampled `None` a third of the time and never
  `Volatile`; a seeded payload then hit the new writer refusal and the
  lane crashed (standalone seed replay: exit 134 pre-fix, 0 post-fix).
  Findings: (1) numeric enum sampling/iteration is ordinal-coupled — every
  sentinel insertion must re-check generators, ordinal loops, and guards
  added in the same change (sweep-table row added to address-pr-feedback;
  generator rule + standalone seed-replay technique added to create-test).
  (2) the standalone fuzz host (`SIGNALFISH_FUZZ_TARGET=... dotnet <dll>
  seed.bin`) gives a seconds-scale deterministic red-green for generator
  bugs without the instrumented driver.
- Open: none.

## 2026-09-20 - session 010: transport (M2) + loopback WS test server

- Findings: (1) NUnit's `Throws.InvalidOperationException` is an *exact*
  type constraint - derived types fail it; use
  `Throws.Exception.InstanceOf<T>()` for base-type contracts. (2) Loopback
  test servers hand off connections via a cancellation-safe primitive
  (semaphore + queue): waiter-TCS handoffs lose connections and stale
  waiters steal later ones. (3) `ClientWebSocket` cannot read
  upgrade-response headers - size comes from the pre-connect
  `client-config` HTTP probe; the header path is browser-transport-only
  (M7). (4) RFC 6455 close codes are 1000-4999; out-of-range test codes
  surface as 1006. (5) TOCTOU-free transport shape: CAS transitions,
  single reader/writer, exactly-once close, idempotent dispose.
- Applied: M2 landed red-green (262 tests x 2 TFMs); test-scoped CA2007/
  CA2000/CA1031/CA5350 suppressions in .editorconfig. Open: none.

## 2026-09-20 - session 009: SharpFuzz lane (M1.5) + CI trim

- Findings: (1) the libfuzzer-dotnet parent exits on a dead child WITHOUT a
  crash artifact - fuzz hosts must dump crashing inputs themselves. (2) pwsh
  native-arg parsing mangles `-flag=value` tokens; splat them. (3) verbatim
  payloads roundtrip byte-exactly only with clean value tokens. (4) an
  envelope with no `data` decodes to an empty slice. (5) SharpFuzz.Common.dll
  needs wildcard instrumentation exclusions; publish output must be wiped
  between runs. (6) restore scoping is graph-global - `-p:TargetFramework=`
  strips other TFMs (NETSDK1005); "one SDK per cell" is unachievable while
  Tests targets net8.0;net10.0.
- Applied: FuzzTests project, `fuzz-codec.ps1`, weekly `fuzz.yml`, dotnet.yml
  trim (coverage unchanged). Open: corpus persistence + regression baseline
  land with M9.4.

## 2026-09-20 - session 008: property tests + perf baseline + changelog

- Findings: (1) FsCheck can emit lone surrogates; `WriteString` maps them to
  U+FFFD *by design* - generators must exclude them. (2) a ref-struct writer
  passed by value through recursive helpers silently loses nested writes;
  pass `ref`. (3) a string roundtrip that slices inner text misses missing
  quote escaping; assert the scanner consumes rendered bytes exactly. (4)
  `Encoding.ASCII.GetBytes` maps non-ASCII to `?`; wire strings go through
  the UTF-8 writer only.
- Applied: FsCheck suite, BenchmarkDotNet baselines (`docs/benchmarks.md`),
  CHANGELOG.md, STE user-copy rule (context rule 16). Open: none.

## 2026-09-20 - PR #14 feedback round: gate scope mismatch class

- Findings: (1) a gate lives in three scopes (linter enforced set, hook
  staged-file selector, CI trigger); the hook must be a superset of the
  enforced set's accepted inputs. (2) hook self-test cases share the git
  index - each case must `git reset -q` first or earlier violations mask
  later cases.
- Applied: selectors fixed red-green; class captured in
  [add-quality-gate](./skills/add-quality-gate/SKILL.md) and a sweep row in
  [address-pr-feedback](./skills/address-pr-feedback/SKILL.md). Open: none.

## 2026-09-20 - repo quality round: analyzers, LINQ ban, CSharpier, nested-pwsh self-test

- Findings: (1) conditional NoWarn must live in `Directory.Build.targets`
  (props cannot see csproj-body properties). (2) test-scoped analyzer
  suppressions (CA1707/CA1062/CA1515) plus perf-intentional CA1028/CA1815
  suppressions with rationale. (3) the LINQ ban is a PowerShell linter -
  BannedApiAnalyzers would violate the zero-PackageReference rule. (4)
  version smokes cannot catch arch-mismatched nested binaries; self-test
  runs the real nested `& pwsh` and asserts the ELF e_machine. (5) decode
  corpus-wide allocation gate (min delta over 4 passes).

## 2026-09-20 - envelope writer (M1.3): ref-struct copy hazard caught by red-green

- Findings: (1) a ref struct with mutable position state must be
  `ref`-passed into every helper writing through it; by-value compiles clean
  and corrupts silently. (2) `Try*` methods assigning `out` eagerly must not
  compose with `||` when the failure-path value is observable. (3)
  `stackalloc` inside a loop accumulates per iteration (reclaimed only at
  method return) - fatal, uncatchable StackOverflowException; hoist one
  scratch span. (4) struct ctors must normalize ignored fields or `Equals`
  contradicts the wire.
- Open: fold (1)+(2) into json-serialization when next edited (300-line cap).

## 2026-09-19 - session 005: M1.2 envelope codec + devcontainer carry-forward

- Findings: C# 9 relational patterns need explicit `LangVersion` on
  netstandard2.1; netstandard2.1 lacks Range-based `Slice` and parameterless
  `GetOffsetAndLength()`; stateful loop-exit flags beat switch-breaks in
  parsers. Open: none.
