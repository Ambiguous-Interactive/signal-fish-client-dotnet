# Improvement Log

Episodic staging log for the mandatory
[reflect-improve](./skills/reflect-improve/SKILL.md) loop. Newest first.
Each entry is an H2 header starting with the date (`## YYYY-MM-DD - scope`)
followed by Trigger / Evidence / Findings / Applied / Open bullets.

Prune an entry once its knowledge has graduated into durable artifacts and
its `Open` items are resolved — this file is staging, not storage (target
under ~150 lines; the 300-line lint ceiling is the hard bound).

## 2026-10-03 - session 036b: Bugbot round on PR #75 (jslib value serialization)

- Trigger: Cursor Bugbot review of the WebGL transport PR; three findings
  (one high), verified against Emscripten 2.0.19 sources before acting.
- Evidence: `src/jsifier.js` `stringifyWithFunctions` prints library-object
  values — functions via `.toString()` (any depth), primitives via
  `JSON.stringify`, plain objects/arrays by recursion — so the shipped
  `new TextEncoder()` reached the player as `{}` (every text frame dead,
  CI green). The round-1 adversarial reviewer's contrary "AST-reprinted"
  claim was accepted without source.
- Findings: (1) verify bot claims about compiler/toolchain semantics
  against the toolchain's own source, not plausibility arguments — and
  never put constructed objects in jslib value positions (lazy-init
  pattern). (2) Failure paths must own their resources: the connect-fail
  throw kept the JS registry entry while the cancel path released its own
  — sweep every exit of a state machine, not just the path the reviewer
  reads. (3) Normalize wire facts at one boundary point (close codes):
  the receive path bypassed the 1005→1006 rule the send/observe paths
  used.
- Applied: jslib lazy codecs + 1005 normalization at `onclose`; C#
  `NormalizeCloseCode` single point + connect-fail dispose; lint gained
  the `new`-in-value-position rule (13 self-test assertions); the
  authoring contract lives in `unity-compatibility` skill. Open: live
  browser validation (M7.4).

## 2026-10-03 - mesh core (M6.5) adversarial review catches

- Trigger: session 033 (M6.5 mesh core); adversarial review over the
  parallel-agent implementation against the Rust spec and server docs.
- Evidence: `Guid.TryParse` admitted non-canonical UUID targets the
  encoder's `RequireUuid` then threw on (an "admitted" send that throws);
  plan state survived a tolerant re-baseline (stale-room signal fencing);
  legacy Server-0.4 generation-less plans decoded as violations where the
  Rust spec decodes them by design; `RoomSnapshot.IceServers` joined the
  struct but not its equality; the ICE-array walker was the third copy of
  the same scanner loop.
- Findings: a fence and its encoder must share one strictness predicate
  (`IsCanonicalUuid`); every new struct field joins `Equals`/`GetHashCode`
  or value-semantics consumers silently break; porting an Option-typed
  wire field as non-nullable turns legacy deployments into violations;
  duplicated scanner walkers drift.
- Applied: strict shared `IsCanonicalUuid`, plan cleared on every
  membership baseline, nullable `SessionPlanMessage.Generation` with a
  stand-down fence, `fallback != relay` rejected at decode, ICE in
  snapshot equality, `ProtocolArrays.TryReadObjectArray` as the SSOT
  walker, driver-level fence tests.
- Open: retired-generation fence (stale re-ordered plans) and inbound
  stale-signal suppression are Rust behaviors consciously deferred —
  tracked in the M6.6 follow-up issue.

## 2026-10-02 - msgpack-frame fuzz corpus red-green catches

- Trigger: session 032 (issue #68, fuzz the MessagePack binary frame
  decoder); adversarial review hunted the committed corpus and lane.
- Evidence: the seed generator's `Str8` helper dropped the str8 length
  byte — the corpus-contract test failed on first run and named the
  exact seed. Review then found `reject-seq-on-v2.bin` byte-identical
  to `reject-missing-key.bin` (the count gate masks the key branch the
  name claimed), and `.gitattributes` left NUL-free `.bin` seeds on the
  `text=auto` path where a future `0d 0a` byte pair would be rewritten
  per-platform.
- Findings: name-labeled binary data can silently duplicate; a
  contract test over the labels catches both generator and duplication
  bugs for free. Seed classes whose failure fires at an earlier gate
  than the name claims need shape choices that reach the named branch.
  Any committed binary format needs an explicit `.gitattributes`
  `binary` rule — NUL-heuristics are not a guarantee.
- Applied: rebuilt the seed as fixmap(3) (v2 count gate passes, the
  seq key reaches the unknown-key branch; same shape in the unit
  matrix), added `*.bin binary`, and class-minimum asserts to the
  corpus-contract test.
- Open: none.

## 2026-09-24 - devcontainer OpenCode v2 lifecycle RCA

- Trigger: Dev Containers log showed `onCreateCommand` exit 1 while opening
  the ARM64 workspace; user requested a seamless OpenCode V2 experience.
- Evidence: the actionable tail is log lines 7782/7786/7792: npm installed
  `@opencode/cli`, but the anchored `^v?2\.` probe rejected the real output
  `opencode v2.0.16`. A second red reproduction hit `readJson is not defined`
  after the config reader was renamed. ARM64 self-test also reproduced nested
  `pwsh` `Exec format error` (ARM64 apphost, x86-64 managed payload).
- Findings: lifecycle failures need effect-level assertions and actionable
  diagnostics; renamed helpers need a runtime smoke test; direct binary version
  checks miss architecture-mismatched nested payloads; generated global config
  must preserve unrelated policy and JSONC, and active OpenCode services need
  a restart when environment-backed credentials change.
- Applied: fixed the V2 probe, added npm failure output and recovery preflight,
  repaired the MCP writer (nested paths, atomic writes, JSONC preservation,
  V2 `mcp.servers`/Code Mode, OAuth policy), added `waitFor` plus the official
  V2 VS Code extension, isolated V2 data from the existing V1 database,
  made config/credential changes restart an active OpenCode service, and
  installed checksum-verified PowerShell archives for ARM64/x64. Follow-up
  hardening made JSONC deletion conditional, preflighted/rolled back all JSON
  targets, allowlisted env-file keys, pinned the npm prefix, made Codex TOML
  writes atomic/escaped, required every core CLI in readiness checks, isolated
  self-test HOME/config state, and made CI platform selection explicit. The
  disposable self-test now passes 167/167 checks; direct Dockerfile smoke skips
  only the feature-provided `gh` binary.
- Evidence: the first V2 `mcp list` after a cold service boot can race
  location-scoped MCP registration and briefly print `No MCP servers
  configured`; a second call succeeds. The lifecycle now retries this bounded
  check after restarting an active service, and the README distinguishes
  `debug config` source output from runtime status. The follow-up review also
  reproduced JSONC-without-`mcp`, partial-write, env-prefix, TOML-escape,
  symlink, and self-test-contamination failure classes before their fixes.
- Open: rebuild the user's existing container once so its lifecycle marker is
  rerun and re-authenticate against the fresh V2 data volume; native x86_64 CI
  is still the cross-build authority (the local ARM builder cannot emulate
  amd64). Rotate credentials in `.env.local` if it has been readable by
  another user.

## 2026-09-24 - devcontainer follow-up hardening (credential paths, locks, propagation)

- Trigger: second review pass over the same session found nine unresolved
  failure classes: a malformed later `.env.local` could resurrect a stale
  inherited credential; `CODEX_HOME` and OpenCode override paths
  (`XDG_CONFIG_HOME`, `OPENCODE_CONFIG`, `OPENCODE_CONFIG_DIR`,
  `OPENCODE_CONFIG_CONTENT`) were ignored by the writers; `ai-backends.sh`
  used a different Z.AI key precedence and ignored `ZHIPU_API_KEY`; global npm
  installs had no lock; the install stamp was written before any readiness
  probe; a failed OpenCode restart was downgraded to a warning and never
  retried; CRLF Codex configs could produce duplicate TOML tables; the Node
  writer's rollback could clobber a concurrent writer; and the CI `runCmd`
  ran three commands without `errexit` so only the last failure was visible.
- Evidence: red reproductions for each class — stale-credential resurrection
  (inherited token survives a corrupt `.env.local`), split-brain `CODEX_HOME`
  (readiness checked `$CODEX_HOME`, MCP writer wrote `~/.codex`), ai-backends
  selecting an ambient `ZAI_API_KEY` over the canonical file value, parallel
  writers interleaving, a failed restart leaving unchanged fingerprints (never
  retried), CRLF markers not matching (old block kept + duplicate tables), and
  a concurrent-writer rollback race in the Node writer.
- Applied: per-source credential semantics in `env.sh` (later value wins,
  blank = no override, malformed/competing = clear + warn; one parser aligned
  with the ai-backends reader); a shared `paths.sh` resolver layer
  (`CODEX_HOME`, OpenCode override chain) used by both writers, readiness
  probes, and the post-start fingerprint; fail-closed rejection of
  managed-name overrides via `OPENCODE_CONFIG_CONTENT`; a single
  mkdir-token config lock shared by the Node writer, Codex TOML writers, and
  `ai-backends.sh` (10-minute stale takeover); parent-directory symlink
  checks plus post-`mkdir` re-checks and a CRLF-normalizing marker match; one
  serialized npm transaction that stamps only after the full readiness probe
  (legacy OpenCode packages now fail readiness), invalidates the old stamp
  first, and treats future-dated stamps as stale; split core-CLI vs
  AI-backend repair paths; a restart-pending marker so the next start retries
  a failed OpenCode reconciliation; strict expected-name gating of
  `opencode mcp list` after restart; self-test `USERPROFILE`/XDG/launcher-bin
  isolation, `.env` backup, `must_run` setup gating, exact-value
  launcher-key assertions, `set -euo pipefail` in the CI `runCmd`, and a
  Dockerfile `flock` assertion.
- Evidence: 17 disposable env.sh source-semantics tests and 28 disposable
  Linux writer/lock/symlink/CRLF tests pass (Debian bash 5.2 + Node 22);
  ShellCheck warnings cleared; bash syntax verified on Debian and Git Bash.
  The rebuilt ARM64 image passes the full in-container lifecycle
  (post-create → post-start → self-test) with 211/211 checks green,
  including the new source-semantics, override-path, lock, CRLF, symlink,
  propagation, and hermeticity regressions.
- Open: native x86_64 CI run is still the cross-build authority (the local
  ARM builder cannot emulate amd64); rebuild the user's existing container
  once so its lifecycle marker reruns, then re-authenticate OpenCode and
  rotate any `.env.local` credential that has been readable by another user.

## 2026-09-23 - PLAN.md bloat: rolling-wave restructure + maintain-plan guard

- Trigger: audit request — PLAN.md had grown to 640 lines; keep it simple,
  in-progress + future only, and prevent recurrence.
- Evidence: ~440 lines (~69%) narrated completed work (M0-M6.3, sessions
  001-027) duplicating `progress/session-*.md`; decisions/product scope/
  references duplicated `.llm` context. GOAL.md already said "keep PLAN.md
  current" but nothing enforced or operationalized it — 27 sessions of
  unbounded growth.
- Findings: (1) PLAN.md and GOAL.md are gitignored local-only docs, so
  hook/CI can never enforce them — the guard must live in committed
  agentic knowledge (rule + skill), with the existing 300-line lint as the
  local budget check (`lint-file-sizes.ps1 -Paths PLAN.md`; no tool change
  needed). (2) A plan that narrates completion stops being a plan — the
  rolling-wave shape (status table + in-progress + coarse future) keeps
  the next task visible. (3) Collapsing task IDs breaks references;
  remaining IDs (M6.4+, M7.x, M8.x, M9.x) stay stable, past IDs are
  history recorded in progress/ and git.
- Applied: PLAN.md rewritten 640 -> 138 lines (red-green: lint failed at
  640, passes at 138); locked decisions/scope/upstream/checkpoints moved
  to [project-decisions](./references/project-decisions.md); rule 22 added
  to context.md; new [maintain-plan](./skills/maintain-plan/SKILL.md)
  skill with the session upkeep workflow; GOAL.md progress bullet now
  points at the skill; dangling `PLAN.md M1.6` reference in
  docs/benchmarks.md repointed to session 008; UUID-decode trap classes
  from pruned session-012 entry folded into json-serialization.
- Open: none.

## 2026-09-23 - session 027 scope note

Pruned 2026-10-03: resolved and graduated (originals in git history).

## 2026-10-02 - session 030b: Bugbot review round on PR #67

- Trigger: Cursor Bugbot flagged 2 High findings on commit 0a36a6e;
  addressed per [address-pr-feedback](./skills/address-pr-feedback/SKILL.md).
- Evidence: both findings reproduced red before fixing (a stale-engine
  RelayStats interval refusal; a NullReferenceException from a default
  struct) and green after. The red-green run also caught the fix's own
  first draft: a nullable-struct signal turned every routed fact
  (`Authenticated`) into a violation — caught by the suite, re-cut to
  the Try/out idiom.
- Findings: (1) a "swap once" guard keyed only on the negotiated
  version is not once-per-connection — the latch must reset at the
  terminal boundary, or a reconnect inherits dead monotonic state and
  quarantines on arrival. (2) A mapper can validate a frame with a
  looser decoder than the consumer's typed re-decode (join decoder
  ignores `sender_watermarks`; reconnect decoder validates them), so an
  ignored `TryDecode` feeding a default struct is authoritative-input
  poison — `default` bypasses the ctor's null-coalescing. (3) `return
  default;` in a `T?`-returning method means *fail*, not "empty but
  valid".
- Applied: `DeliveryGate.ObserveTerminal` resets the negotiation latch;
  the pipeline guards all snapshot re-decodes (routed-fact violation on
  failure); `MapRoster` is null-safe. Two regression tests pinned red
  (verified against the unfixed tree). Classes folded into
  [reconnection](./skills/reconnection/SKILL.md) hard rule 6,
  [json-serialization](./skills/json-serialization/SKILL.md) debugging
  note 4, and three [address-pr-feedback](./skills/address-pr-feedback/SKILL.md)
  sweep rows. Open: none.

## 2026-10-02 - session 031: M6.4 binary game data + stale-main convergence

- Trigger: session start found local main 5 commits ahead / 1 behind
  origin/main mid-merge with four both-added conflicts — session 030's
  work had landed as squash #67 while local main kept the pre-review
  iteration; the branch then gained adversarial-review findings on its
  own fresh code.
- Evidence: tree equality checks (`git diff` against the squash and the
  PR branch tip) proved local main held zero unique content; the merge
  was abandoned and main reset to origin/main — no resolution commit
  needed. On the new work, the adversarial reviewer confirmed a
  silent-corruption blocker with a scratch probe: both `from_player`
  and `payload` bound to one slice local, so any MessagePack key order
  but the server's delivered the UUID bytes as the payload.
- Findings: (1) when a squash merge lands, reset local main
  immediately — a stale local main duplicates the squash content and
  turns the next sync into both-added conflict archaeology; verify
  with tree-diff, not commit log. (2) In a keyed map decoder, every
  field binds to its own storage: shared locals turn key order into
  data corruption, and a test suite that only builds one key order
  cannot see it — permutation tests are the regression pin.
- Applied: main converged to origin/main (session opened clean); the
  decoder uses a dedicated sender slice pinned by
  `AnyKeyOrderDecodesTheSameFrame`; admission-refused Observe frames
  stop at the refusal (cursor continuity pinned). Open: a coverage-
  guided fuzz target for the MessagePack scanner (filed).

Entries pruned 2026-09-23 (sessions 011-014, 017b, 022-023, 025b-026):
knowledge graduated into skills/rules; open items resolved or tracked as
issues; originals in git history.

## 2026-10-03 - session 039: M8.1 FishNet adapter

- Trigger: the first authored-blind engine bridge (FishNet `Transport`)
  concentrated risk in three places CI cannot compile: the SDK's API
  shape, the define plumbing that activates the code, and the lifecycle
  of an async bootstrap under a synchronous transport contract.
- Findings: (1) `versionDefines` resources are **packages**, not asmdef
  names, and an empty expression is invalid — the define silently never
  fires and a guarded bridge compiles to nothing while every gate stays
  green; gate the whole assembly with `defineConstraints` on the same
  symbol so a missing SDK can never leave a dangling reference. (2) A
  transport contract that raises events must stage them for the engine's
  own thread (FishNet's handlers mutate non-thread-safe collections; its
  own transports queue exactly this way). (3) An async bootstrap under a
  synchronous start/stop API needs a generation counter checked after
  every await, or stop-then-restart leaks a joined session and stale
  identity. (4) Never fall back to a reserved identity (host connection
  0) for an unrouted sender — misattribution is corruption; drop and
  count.
- Applied: all of the above in the bridge + lint (which now pins the
  corrected define shape and the bridge's member completeness);
  composition test pins the adapter UUID spelling against the library's
  own binary game-data decoder. The pattern (pure CI-compiled core +
  guarded thin bridge + structural gate) is the template for M8.2
  Mirror.
