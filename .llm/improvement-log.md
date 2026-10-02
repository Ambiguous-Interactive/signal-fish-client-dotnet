# Improvement Log

Episodic staging log for the mandatory
[reflect-improve](./skills/reflect-improve/SKILL.md) loop. Newest first.
Each entry is an H2 header starting with the date (`## YYYY-MM-DD - scope`)
followed by Trigger / Evidence / Findings / Applied / Open bullets.

Prune an entry once its knowledge has graduated into durable artifacts and
its `Open` items are resolved — this file is staging, not storage (target
under ~150 lines; the 300-line lint ceiling is the hard bound).

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

## 2026-09-23 - session 027: M6.3 core (accountability engine + v3 decodes)

- Trigger: M6.3 core (engine + decode surface), issue-debt round, and the
  local-iteration speed mandate; one deliverable PR.
- Evidence: fast-check 23.4 s -> 11.3 s full / ~5 s filtered. The
  allocation gate caught a real 24 B/call hot-path allocation on day one;
  the adversarial review caught a lint blocker + a broken exclusion switch
  pre-PR; 26 Rust scenarios ported green on first full run.
- Findings: (1) a `List.RemoveAll(lambda)` allocates its display class at
  method entry even on early-return paths — use explicit loops on gated
  hot paths. (2) NUnit3TestAdapter's vstest `TestCaseFilter` negation is
  unreliable (`Category!=X` selects everything); signal exclusions via an
  env-var + `OneTimeSetUp` `Assert.Ignore` instead. (3) PowerShell `@()`
  array literals split `("a:" + $var)` elements at the `+` — compute the
  token into a variable first. (4) Parallel sub-agents duplicated wire
  types across namespaces when the contract pinned types but not their
  file/namespace home — pin the home too.
- Applied: engine + decodes + 33 tests landed (M6.3 core); #58 closed via
  the shared `MaxVerbatimPayloadDepth` contract; integration follow-up
  issue filed. Open: none.

## 2026-10-02 - session 030: M6.3 integration (delivery gate wiring)

- Trigger: M6.3 integration (#61) + #66 resolution; one deliverable PR.
- Evidence: the fixture sweep surfaced 38-47 reds for "one reason" that
  were really TWO reasons — a product bug (v2 rosters mapped to zeroed
  baselines; every v2 join refused) hiding behind fixture incoherence
  (v2 joins fed into v3-negotiated sessions). The adversarial review
  then caught a silently-broken capacity floor (eventCapacity 1-2 made
  Poll() consume nothing) and a ProtocolInfo re-echo that wiped sender
  cursors.
- Findings: (1) when a test sweep fails for "one reason", fix the
  product first, then re-derive the fixture changes — the first-pass
  fixture edits (empty rosters) were pure churn once the gate was
  fixed. (2) A policy layer over a reshaped engine API must reproduce
  the absent-vs-present refusals the API cannot see (v2 must SKIP, not
  feed zeros). (3) NUnit `Assert.That` inside an allocation-gate loop
  allocates ~2 B/assert even on pass — count failures, assert outside.
  (4) Enumerating a ref-struct enumerator is illegal in async test
  bodies under C# 12 even in `Release`-only projects — CI builds
  configurations local fast-check never touches.
- Applied: gate + policy + pipeline wiring landed (PR #67); #66 closed;
  integration remainder narrowed to the M6.6 live gap leg. Open: none.

Entries pruned 2026-09-23 (sessions 011-014, 017b, 022-023, 025b-026):
knowledge graduated into skills/rules; open items resolved or tracked as
issues; originals in git history.
