# Improvement Log

Episodic staging log for the mandatory
[reflect-improve](./skills/reflect-improve/SKILL.md) loop. Newest first.
Each entry is an H2 header starting with the date (`## YYYY-MM-DD - scope`)
followed by Trigger / Evidence / Findings / Applied / Open bullets.

Prune an entry once its knowledge has graduated into durable artifacts and
its `Open` items are resolved — this file is staging, not storage (target
under ~150 lines; the 300-line lint ceiling is the hard bound).

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

## 2026-09-20 - PR #18 Bugbot round: CWD-dependent tooling class

- Findings: `dotnet tool run` resolves the manifest by walking up from the
  CWD; every tool invocation needs the same `$RepoRoot`/`git -C` anchoring
  as the script itself. Rule 7 in powershell-tooling; pinned by
  test-install-hooks.ps1. Open: none.

## 2026-09-20 - PR #11 feedback round: MCP auth-header class + style mechanization

- Findings: (1) URL-only assertions on remote MCP entries are a coverage
  blind spot - auth headers need value assertions with throwaway creds. (2)
  optional-parameter builders interpolating into output turn a forgotten
  argument into a literal "undefined"; fail at write time. (3) no-var and
  usings-inside-namespace were mechanized via `.editorconfig` +
  EnforceCodeStyleInBuild; `EnvelopeEventKind` gained the first
  `[Obsolete] None = 0` sentinel (project-wide completion in session 011).
- Applied: fix + guard + self-tests; MCP-writer facts in
  [devcontainer-tooling](./references/devcontainer-tooling.md). Open: none.

## 2026-09-19 - devcontainer rounds (5 entries, condensed)

- Full facts in [devcontainer-tooling](./references/devcontainer-tooling.md):
  fix volume ownership at image build time (pre-create mountpoints
  vscode-owned, parents first); `os.homedir()` ignores `HOME` on win32;
  `containerEnv` cannot self-reference `PATH`; the dotnet feature v1 shadows
  the image SDK; nvm hard-fails under `NPM_CONFIG_PREFIX`; apphost shims work
  top-level yet spawn broken `$PSHOME` binaries - arch-validate nested
  invocations; codex MCP env intentionally persists the zai-vision key
  (chmod 600) so blanket no-secrets greps false-positive there; hermetic
  self-tests need env skip-seams plus backup/restore traps; `COPY --from`
  beats curl-installers; global `zai-mcp-server` bin over runtime `npx`;
  `Z_AI_MODE=ZHIPU` switches remote base URLs; configs prune when
  credentials disappear.
- Open: none (Unity MCP relay deferred until Unity work starts).

## 2026-09-19 - session 005: M1.2 envelope codec + devcontainer carry-forward

- Findings: C# 9 relational patterns need explicit `LangVersion` on
  netstandard2.1; netstandard2.1 lacks Range-based `Slice` and parameterless
  `GetOffsetAndLength()`; stateful loop-exit flags beat switch-breaks in
  parsers. Open: none.
