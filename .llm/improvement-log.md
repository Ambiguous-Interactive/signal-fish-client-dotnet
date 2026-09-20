# Improvement Log

Episodic staging log for the mandatory
[reflect-improve](./skills/reflect-improve/SKILL.md) loop. Newest first.
Each entry is an H2 header starting with the date (`## YYYY-MM-DD - scope`)
followed by Trigger / Evidence / Findings / Applied / Open bullets.

Prune an entry once its knowledge has graduated into durable artifacts and
its `Open` items are resolved — this file is staging, not storage (target
under ~150 lines; the 300-line lint ceiling is the hard bound).

## 2026-09-20 - PR #11 feedback round: MCP auth-header class + style rules

- Trigger: Bugbot (1 High, 1 dup-of-fixed) + 3 human repo-wide style asks on
  PR #11.
- Evidence: Bugbot High verified — `write-mcp-configs.mjs` called
  `builder.remote(url)` while the claude/copilot/gemini/cursor builders take
  `(url, key)` and interpolate `Bearer ${key}` → those harnesses persisted
  `Bearer undefined` for all three Z.AI remotes (opencode/nanocoder
  unaffected: env refs). RED: added 5 auth-header self-test assertions, ran
  self-test → exactly those 5 failed (118 pass). Class sweep: codex TOML
  writer already correct (`bearer_token_env_var`), `.vscode/mcp.json` uses
  `${input:}`, `ai-backends.sh` clean — one bad site total. GREEN after the
  one-line fix + an inline guard that throws when an Authorization carries
  neither the key nor an env-ref marker: 123/123.
- Findings: (1) URL-only assertions on remote MCP entries are a coverage
  blind spot — auth headers need value assertions with injected throwaway
  creds. (2) Optional-parameter builder functions that interpolate into
  output are an arity footgun: a forgotten argument becomes a literal
  "undefined" in output; fail at write time, not at auth time. (3) The human
  asks (no-var, usings-inside-namespace, enum-0-sentinel) were mechanized:
  `.editorconfig` IDE0008/IDE0065 severity=error + EnforceCodeStyleInBuild,
  `dotnet format` applied the bulk, ~59 non-inferable sites hand-fixed;
  enforcement red-checked with a planted violation.
- Applied: fix + guard + 8 self-test assertions; `EnvelopeEventKind` gained
  an `[Obsolete] None = 0` sentinel (real values shifted; `MessageKind`/
  `DecodeError` already had sanctioned `None` sentinels — no Obsolete there,
  comparisons are legitimate); all repo `.cs` files moved usings inside
  namespaces and dropped `var`; rules captured in
  [api-design](./skills/api-design/SKILL.md) and MCP-writer facts in
  [devcontainer-tooling](./references/devcontainer-tooling.md).
- Open: none for this round; toolchain-smoke follow-up filed as an issue.

## 2026-09-19 - session 005: M1.2 envelope codec + devcontainer carry-forward

- Trigger: PLAN M1.2 red-green (EnvelopeReader) + committing session 004's
  untracked devcontainer work.
- Findings (condensed): an apphost shim can work top-level yet spawn a broken
  `$PSHOME` binary - arch-validate nested invocations, not just
  `pwsh --version`; C# 9 relational patterns need explicit `LangVersion` on
  netstandard2.1; netstandard2.1 lacks Range-based `Slice` and parameterless
  `GetOffsetAndLength()`; stateful loop-exit flags beat switch-breaks in
  parsers.
- Applied: `Protocol/` package (scanner, reader, events, kinds); 123 tests
  both TFMs. Knowledge graduated into skills/references.
- Open: none.

## 2026-09-19 - devcontainer EACCES: root-owned volume mountpaths

- Findings (condensed; full facts in
  [devcontainer-tooling](./references/devcontainer-tooling.md)): fix volume
  ownership at image build time - pre-create mountpoints vscode-owned, parents
  listed before children (`install -d -o` chowns only the final component);
  `--version` smokes do not cover state-writing paths, assert dir writability.
- Open: none (verified in-container).

## 2026-09-19 - devcontainer upgrade (MCP survey, AI backends, build speed)

- Findings (condensed; full facts in
  [devcontainer-tooling](./references/devcontainer-tooling.md)):
  `os.homedir()` ignores `HOME` on win32 (host-side tests are a live
  pollution hazard); verification spanning two ephemeral containers tests
  nothing; `COPY --from` stages beat curl-installers on repeat builds; codex
  MCP env intentionally persists the zai-vision key (chmod 600), so blanket
  no-secrets greps false-positive there.
- Open: Unity MCP relay consideration deferred until Unity work starts.

## 2026-09-19 - devcontainer with prewired agentic harnesses and MCP

- Findings (condensed; full facts in
  [devcontainer-tooling](./references/devcontainer-tooling.md)):
  `containerEnv` cannot self-reference `PATH` (use Dockerfile `ENV`); the
  dotnet feature v1 shadows the image SDK (install SDKs into
  `/usr/share/dotnet`); the node feature's nvm hard-fails under
  `NPM_CONFIG_PREFIX`; opencode's arm64 postinstall mis-selects libc;
  named volumes and parents of volume targets can be root-owned.
- Open: none.

## 2026-09-19 - devcontainer MCP round 2: convergence with sibling repos

- Findings (condensed; full facts in
  [devcontainer-tooling](./references/devcontainer-tooling.md)): global
  `zai-mcp-server` bin over runtime `npx` for in-container harnesses;
  `Z_AI_MODE=ZHIPU` switches remote base URLs; configs pruned when
  credentials disappear; control-character env validation; hermetic
  self-tests need env skip-seams plus backup/restore traps; key-gated MCP
  startup is a distinct passing probe outcome; npm registry flakes need
  bounded retries.
- Open: confirm VS Code resolves the three `shellCommand` inputs on a first
  real session (devcontainer-build CI green on its first real run,
  2026-09-20, which does not verify this).

## 2026-09-20 - envelope writer (M1.3): ref-struct copy hazard caught by red-green

- Trigger: M1.3 milestone work (EnvelopeWriter + payload structs + decode).
- Evidence: with `JsonWriter` (ref struct) passed by value, helpers mutated
  their copy; the caller's stale `_span`/`_pos` then clobbered
  already-flushed buffer regions — the byte-identical fixture test caught it
  only for messages whose helper wrote AFTER a flush boundary (RoomOperation
  moderation payloads), while 20 of 21 fixture cases stayed green.
  `TryReadString` assigns its `out` param before failing, so
  `TryReadString(..) || TryReadNull(..)` turned JSON `null` into `""` and
  broke SetRoomAccess reopen roundtripping.
- Findings: (1) repo rule — a `ref struct` with mutable position state must
  be `ref`-passed into every helper that writes through it; by-value
  passing compiles clean and corrupts silently. Caught here by tests; worth
  a json-serialization skill note. (2) `Try*` methods that assign `out`
  params eagerly must not be composed with `||` when the failure-path value
  is observable. (3) Mutation-checking paid off: all three planted bugs
  (wire drift, missing validation, dropped decode advance) were detected by
  the suite. (4) Adversarial review round: `stackalloc` inside a loop
  accumulates per iteration (frame memory is reclaimed only at method
  return) — reproduced a fatal, uncatchable StackOverflowException at
  ~800k non-ASCII chars; "the slots are reused" is a myth. Hoist one
  scratch span above the loop. Also fixed: struct ctor must normalize
  ignored fields or `Equals` contradicts the wire; verbatim-payload
  "valid JSON" docs must match enforcement (full allocation-free rescan
  added).
- Actions: applied in this change set (ref-passing helpers, explicit
  branching in `TryDecodePassword`); follow-up: fold rules (1)+(2) into the
  json-serialization skill when it is next edited (300-line cap applies).

## 2026-09-20 - repo quality round: analyzers, LINQ ban, CSharpier, nested-pwsh self-test

- Trigger: issue debt round (#5 CSharpier, #6 max warnings + analyzers,
  #7 zero-alloc enforcement, #12 devcontainer nested-invocation self-test).
- Findings: (1) `Directory.Build.props` conditions cannot see properties set
  in the csproj body (`IsTestProject`) - conditional NoWarn must live in
  `Directory.Build.targets`. (2) `latest-all` analyzer set + NUnit requires
  three test-scoped suppressions (underscore names CA1707, framework-guaranteed
  args CA1062, public classes CA1515); byte-backed wire enums (CA1028) and the
  non-compared hot-path event struct (CA1815) are perf-intentional and are
  suppressed in `.editorconfig` with rationale. (3) The reader had no
  allocation gate; the corpus-wide steady-state gate (min delta over 4 passes,
  0 B) now covers decode; red-checked with a planted allocation. (4) LINQ ban
  is enforced as a repo-conventional PowerShell linter (BannedApiAnalyzers
  would violate the zero-PackageReference rule for src/). (5) Version smokes
  cannot catch arch-mismatched nested binaries; the self-test now runs the
  real nested `& pwsh` invocation and asserts the ELF e_machine (od, offset
  18) matches `uname -m`.
- Actions: applied in this change set; CI nets a time *decrease* (coverage
  collection trimmed to the Linux cells that consume it).

## 2026-09-20 - PR #14 feedback round: gate scope mismatch class (hook vs linter vs CI)

- Trigger: Cursor Bugbot on PR #14 - the pre-commit hook forwarded only
  staged `src/**/*.cs` to the LINQ linter, so a staged src `.csproj`
  injecting `<Using Include="System.Linq" />` (an input the linter had
  learned to reject) was blessed locally and failed in CI.
- Findings: (1) the class generalizes - a gate is enforced in three scopes
  (linter default-mode enforced set, hook staged-file selector, CI
  trigger); the hook must be a superset of the enforced set's accepted
  inputs. (2) Sibling sweep found a second instance: lint-file-sizes
  enforces every `.cursor/rules/*.mdc` but the hook knew only one pointer
  file. (3) Hook self-test cases share the git index - staged files
  accumulate, so an earlier violating file makes later cases pass for the
  wrong reason; each case must `git reset -q` first (this masked the
  .csproj case on the first red run).
- Actions: both selectors fixed and red-green tested (4 new assertions in
  test-pre-commit.ps1); knowledge captured as a new skill
  `.llm/skills/add-quality-gate/` plus a sweep-table row in
  address-pr-feedback.

## 2026-09-20 - Session 009: SharpFuzz lane (M1.5) + CI trim

- Trigger: M1 completion gate ("fuzz lane clean for 30 min CI run") plus the
  CI-time objective.
- Evidence: planted reader bug (throw on empty input) and writer bug
  (dropped `"` escape) both crash their targets; two harness-expectation
  bugs found and fixed by fuzzing; full-corpus unit suite, script
  self-tests, and 150 s/target driver runs green (883k/841k execs).
- Findings: (1) The libfuzzer-dotnet parent exits on a dead child WITHOUT
  writing a crash artifact - fuzz hosts must dump crashing inputs themselves
  before rethrowing. (2) pwsh native-arg parsing can mangle libFuzzer's
  `-flag=value` tokens; pass them via a splatted argument array. (3)
  Roundtrip identity is a per-component contract: verbatim payloads
  roundtrip byte-exactly only when callers pass clean value tokens (the
  reader canonically excludes insignificant whitespace; the writer never
  edits bytes). (4) An envelope with no `data` member decodes to an empty
  `Data` slice - payload `TryDecode` contracts cover data objects only.
  (5) SharpFuzz publishes `SharpFuzz.Common.dll` separately; instrumentor
  exclusions must be wildcard-matched, and publish output must be wiped
  between runs or stale instrumented dlls fail the re-run. (6) Restore
  scoping is graph-global: `-p:TargetFramework=` on restore strips every
  referenced project's other TFMs from its assets file (NETSDK1005), so a
  multi-TFM test project forces dual SDK installs on all CI cells - "one
  SDK per cell" is unachievable while `Tests` targets net8.0;net10.0.
- Applied: FuzzTests project (reader/writer targets, crash self-dump,
  frame-text diagnostics), `scripts/fuzz-codec.ps1` (pinned-by-hash driver,
  manifest-pinned sharpfuzz, persistent `.fuzz/` corpus + crashes), weekly
  `fuzz.yml` (PR CI untouched); `dotnet.yml` trimmed to one SDK + one TFM
  build per cell with lints deduped to the coverage cell - measured
  coverage unchanged.
- Open: scheduled-run corpus persistence via actions/cache and crash
  regression-corpus baseline land with M9.4.

## 2026-09-20 - Session 008: property tests + perf baseline + changelog

- Trigger: issue-debt round (#15 CHANGELOG, #9 upstream check, #7 benchmarks)
  plus M1.4/M1.6.
- Findings: (1) FsCheck 2.16's `Gen.Elements` over a char pool can emit lone
  surrogates, but `JsonWriter.WriteString` replaces them with U+FFFD *by
  design* - generators must exclude them or the property asserts an intended
  behavior as a bug. (2) A `ref struct` writer passed **by value** through
  recursive test helpers silently loses nested writes; pass it `ref`. (3) A
  string roundtrip property that slices the inner text between the outer
  quotes cannot detect missing quote escaping (the framing is re-cut by the
  property itself); asserting the scanner consumes the rendered bytes
  byte-exactly catches that whole class. (4) `Encoding.ASCII.GetBytes`
  silently maps non-ASCII to `?` - wire strings must go through the UTF-8
  writer, never ASCII helpers.
- Actions: property suite landed red-green (both reader and writer planted
  bugs caught); BenchmarkDotNet baselines recorded in `docs/benchmarks.md`
  (both hot paths 0 B steady-state); CHANGELOG.md adopted (keep-a-changelog,
  user-visible only); STE user-copy rule added to context.md (rule 16).
- CI: coverage collection/report narrowed to one representative cell (same
  tests on all cells), NuGet package cache added, `concurrency`
  cancel-in-progress added, reportgenerator moved to the tool manifest -
  net runner-time decrease with unchanged measured coverage.
