# Improvement Log

Episodic staging log for the mandatory
[reflect-improve](./skills/reflect-improve/SKILL.md) loop. Newest first.
Each entry is an H2 header starting with the date (`## YYYY-MM-DD - scope`)
followed by Trigger / Evidence / Findings / Applied / Open bullets.

Prune an entry once its knowledge has graduated into durable artifacts and
its `Open` items are resolved — this file is staging, not storage (target
under ~150 lines; the 300-line lint ceiling is the hard bound).

## 2026-10-08 - session 062: CI retry loop broke the Windows cell

- Trigger: a test-infrastructure change (mandatory loop).
- Evidence: the first cut of the `dotnet.yml` retry loop used bash
  syntax with no `shell:` pin. Linux cells ran green, but GitHub's
  default shell on Windows is pwsh — the Windows `Test` step failed to
  parse in 0s and `dotnet test` never ran (PR run 37728249104, job
  113151437983). Caught by adversarial review of the PR's own checks,
  not by my local verification.
- Findings: (1) a `run:` block in a matrix workflow must pin `shell:`
  explicitly — the unpinned default is per-OS (pwsh on Windows), so a
  script can silently never execute on a cell that local validation
  cannot see. (2) A workflow change is not verified until **every
  matrix cell** is green on the PR; one green cell proves only that
  cell. (3) In bash, `$?` after an `if` whose condition failed is 0 —
  capture the real exit code inside the `else` branch.
- Applied: `shell: bash` on both Test steps; exit code preserved via
  `else code=$?` and re-raised on exhaustion; warnings gated to
  non-final attempts; `rm -rf TestResults` per attempt (crashed-attempt
  artifacts cannot reach the coverage gate). All cells green on head
  65a451f.
- Open: none here — the e2e-lane retry decision and the upstream
  testfx report live in issue #120.

## 2026-10-08 - session 061: ProtocolInfo observability fields on the frozen surface

- Trigger: a protocol + public API surface change (mandatory loop).
- Evidence: appending two optional parameters to the public
  `ProtocolInfoMessage` ctor *replaced* the released 7-param signature
  (api-compat CP0002 — compiled consumers would miss the symbol), the
  exact trap session 060 recorded for `JoinRoomMessage`; the recipe's
  "additive optional properties" wording invited it. Fixed shape:
  released ctor stays and delegates; new fields ride an all-required
  9-param overload (no defaults → no CS0121). Gate green after.
- Findings: (1) on any released struct, an optional-param append is
  signature-replacing — keep the released ctor and put additive fields
  on a fully-required overload (decode-only structs) or a fluent copy
  (two-way structs, session 060); run `check-api-compat.ps1` locally
  before pushing. (2) Mirror disclosure scope exactly: the server
  sends `game_data_limits` as configured caps ∩ negotiable encodings —
  scope claims like "including encodings the SDK never requests" were
  wrong in doc/changelog/test until review caught them.
- Applied: overload shape shipped; docs right-sized (XML, CHANGELOG,
  quick-reference, events.md); repeated-key + unknown-entry-field
  decode holes pinned by tests. Open: none.

## 2026-10-07 - session 060: join_only on the frozen JoinRoomMessage surface

(Pruned 2026-10-08: the fluent-copy shape and the re-verify-doc-claims
lesson graduated into the 061 entry and the shipped XML docs; the Open
ProtocolInfo shape decision resolved in session 061. Rationale in
`progress/session-060-join-only-collision-safe-joins.md`.)

## 2026-10-07 - sessions 057/059 (stubs)

(Both pruned 2026-10-07: 057's findings were none by design — a locked-
decision edit; rationale in `project-decisions.md` and
`progress/session-057-tunit-decision.md`, shipped in PR #113. 059's
downgrade-notice contract graduated into
`DeliveryGate.ObserveUnsupportedFormatError`, five delivery-gate tests,
and the quick reference; the server-watch item moved to PLAN.md.)

## 2026-10-06 - session 053: M9.5 release checklist (wrapped Write-Error output)

(Pruned 2026-10-07: knowledge graduated into powershell-tooling rule 3
(early-guard case, forward-slash diagnostics) and the lint self-tests;
originals in git history.)

## 2026-10-06 - session 052: M9.4 scheduled bench gate (argument-mode misbind, dispatch-before-default-branch)

(Pruned 2026-10-08: Open was none; knowledge graduated into
`docs/benchmarks.md`, the bench self-test, and the baseline schema.
Originals in git history.)

Entries pruned 2026-10-06 (sessions 042, 042-bugbot, 048, 049, 051):
knowledge graduated into `author-engine-adapter`, `address-pr-feedback`,
`docs/releasing.md`, the runbook page, and the lint self-tests; open
items resolved or tracked as issues; originals in git history.

## 2026-10-04 - session 040: M8.2 Mirror adapter (blind bridge, two-round review)

- Trigger: a second blind-authored engine bridge (Mirror) plus the
  session's adversarial review loop and a Bugbot round on PR #84.
- Evidence: the gates compile only the adapter cores, so a missing
  `using System;` (RoomManager) and a Guid/int router-keying mixup
  (`ServerDisconnect`) both shipped through a fully green local battery —
  the first caught by my reviewer, the second only by Bugbot. The
  two-round loop also caught real contract misses: Mirror callbacks
  raised off the bootstrap thread, `ClientDisconnect` owing no
  disconnect (Mirror's limbo rule), completed-bootstrap-as-live, and
  server-restart membership loss (router cleared while the session
  lived).
- Findings: (1) blind-authored Runtime sources need an owning review
  pass that traces every referenced type's using and every
  cross-module call's signature — presence-check lints cannot; file
  the "compile the bridges against stub SDK surfaces" idea with #83's
  hoist. (2) Mirror (like FishNet) forbids limbo: every start path
  must provably end in exactly one connected or disconnected callback —
  enumerate the interleavings (stop-during-start, start-after-stop,
  restart) rather than the happy path. (3) The router is the room
  membership truth; derive re-announcements from it instead of tracking
  swallowed events in a side set (the set was both redundant and
  wrong).
- Applied: guard lint is region-aware per line; bridge-lifecycle fixes
  above; detector contract pinned for both adapters; PR feedback
  recorded in-session (this entry).
- Open: M8.7 live validation (licensed seat); #83 hoist landed in
  session 041 — entry pruneable once its M8.7 open item resolves.

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

(Pruned 2026-10-06: Open was none and the knowledge is embodied in the
artifacts it guards — the corpus contract test, `*.bin binary` in
`.gitattributes`, and the weekly fuzz gate. Originals in git history.)

Pruned 2026-10-04: the devcontainer rounds, the maintain-plan restructure, and the 027 scope note — graduated into `.devcontainer`, `.llm/skills/maintain-plan`, and git history. The 027 Bugbot round (terminal-latch reset, shared-local decoder corruption, `default`-means-fail) also pruned 2026-10-06: fully folded into [reconnection](./skills/reconnection/SKILL.md) hard rule 6, [json-serialization](./skills/json-serialization/SKILL.md) debugging note 4, and three [address-pr-feedback](./skills/address-pr-feedback/SKILL.md) sweep rows.

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

## 2026-10-06 - session 054: UPM release tarballs (path-constant routing bug)

- Trigger: reflect-improve after the release-lane change (fleet pack
  script + `release.yml` step); the first self-test run failed on the
  core tarball's content.
- Evidence: `pack-unity-packages.ps1` routed the core package to the
  sync-staging branch by comparing a manifest FILE path against a
  package DIRECTORY constant — never equal, so the core was tarred
  verbatim (no fresh mirror, no shipped-asmdef check) while still
  producing a plausible, correctly named tarball. Every success-shaped
  assertion passed (exit 0, tarball exists, fleet count); only the
  fixture assertion on the artifact the staging branch uniquely
  produces (`Runtime/Core/A.cs` in the core tarball) failed. Debugging
  stalled on "why does staging misbehave" until an instrumentation
  line at the top of the supposedly-running child script failed to
  print — the branch never ran at all.
- Findings: (1) a branch that routes on a path constant must be
  compared in the same terms (file vs directory) and pinned by a test
  that fails when the wrong branch is taken — assert the output only
  that branch produces, not generic success. (2) When output looks
  wrong, instrument the branch entry before the branch body: a plausible
  wrong-branch result reads as a subtle right-branch failure.
- Applied: the constant carries `/package.json`; the fresh-mirror and
  asmdef assertions pin the staging branch; the real-fleet run pins
  all nine tarball names.
- Open: none.

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
