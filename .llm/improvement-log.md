# Improvement Log

Episodic staging log for the mandatory
[reflect-improve](./skills/reflect-improve/SKILL.md) loop. Newest first.
Each entry is an H2 header starting with the date (`## YYYY-MM-DD - scope`)
followed by Trigger / Evidence / Findings / Applied / Open bullets.

Prune an entry once its knowledge has graduated into durable artifacts and
its `Open` items are resolved — this file is staging, not storage (target
under ~150 lines; the 300-line lint ceiling is the hard bound).

## 2026-09-22 - session 023: issue #48 - graceful close handshake + merge dedup

- Trigger: issue debt (#48: M4.3's "graceful" shutdown aborted the TCP
  wire) plus the origin/main merge carrying PR #51's post-review fixes.
- Findings: (1) a grace claim needs a wire counterpart — the handshake
  lives in the transport's own `DisposeAsync`, so both clients inherit it
  with zero new surface; the issue's capability-interface option was
  rejected as public surface without a capability. (2) Adversarial review
  caught a dead poll condition (`Disposed` can never CAS to `Closed`, so
  the exit check never fired) — the honest exit signal was
  `_closeFrameDelivered`; derive wait-exit conditions from the field that
  actually records the event, not a state that transition is barred from.
  (3) The review also verified red-green empirically (tests run against
  the pre-change code in a worktree), not just asserted.
- Applied: close-out 1000 + bounded echo window in
  `WebSocketTransport.DisposeAsync`; prior-state-returning transition
  (atomic connected-check); `_handshakeActive` gate-dispose guard;
  duplicate session-022 progress brief deleted (merge shipped two).
- Open: two pre-existing transport races filed for follow-up (release
  between receive completion and CloseStatus read; dispose racing
  connect's socket assignment).

## 2026-09-22 - session 022: M4.5 opt-in reconnect policy - three adversarial rounds

- Trigger: PLAN.md M4.5 + M4 gate. Three sub-agent review rounds; two
  majors found and fixed post-implementation, plus minors.
- Findings: (1) nonblocking `TryEnqueue` for scheduling markers silently
  drops them under event-queue backpressure — the never-drop contract must
  hold for synthetic events too; blocking enqueue is safe because disposal
  completes the queue. (2) In a reconnecting session, applying the
  `Disconnected` fact to the machine leaves `Phase == Terminal`
  mid-session — phase-gated consumers stop draining and stall the session;
  a fresh machine per round (plus a severed-admission check) keeps the
  phase truthful. (3) Seat capture at sever must be OR-ed with the pending
  seat: a capture from a membership-less machine (death before
  re-authentication) must not clobber a retained seat, while an
  issued-but-unanswered reclaim still loses it. (4) Session-sticky flags
  that gate one-shot deliveries need per-round resets when rounds repeat.
- Applied: driver session/round split, blocking marker enqueues, machine
  swap at sever, seat OR-semantics, `FinalizeLocked` prefers observed
  death close, `FakeTransport.DoomWithClose`/`FailHeldSends`.
- Open: none.

## 2026-09-22 - session 022b: PR #51 feedback - refused-reclaim livelock (restore+restart class)

- Trigger: Bugbot High on the fix commit: restoring `_autoSeatPending` on
  a refused reclaim while still restarting the round iteration made a
  persistent refusal (fence held, full queue) a same-condition hot loop —
  drain, receive, and heartbeat never ran again.
- Findings: (1) restore-on-refusal + unconditional iteration restart is a
  livelock class: an automatic retry must be paced by external progress
  (a frame, a wake, a heartbeat deadline), never by a same-condition
  `continue`. (2) Re-attempt guards must test the actual blocker
  (`PendingOperation == default`), not just the trigger flag. (3) A
  trigger that a confirmed foreign state supersedes (membership from a
  deliberate join) must be cleared, not retried forever. (4) Sweep: every
  other `continue` in src/ is progress-guaranteed (parser position
  advances, frame consumed, cancelled waiter skipped).
- Applied: fence guard + supersede-clear in `TryIssueAutoReconnect`,
  fall-through instead of restart, deterministic red-green test
  (capacity-1 queue forces a real `SendBufferFull` refusal; recovery is
  asserted; the red state cannot complete at all). Rules folded into
  async-threading and reconnection skills.
- Open: none.

## 2026-09-22 - session 017b: PR feedback round - silent snupkg skip in nuget.org publish (bugbot finding)

- Trigger: Cursor Bugbot flagged the nuget.org push as invalid. Bot's
  mechanism was wrong (two paths parse and both nupkg push); executing the
  command on the CI-pinned SDK 8.0.425 exposed a worse truth: a positional
  `.snupkg` is silently skipped (exit 0, no warning) — symbols would never
  reach nuget.org. A prior sub-agent's "empirically confirmed" defense of
  the one-command form was itself wrong.
- Findings: (1) `dotnet nuget push` (SDK 8) silently ignores `.snupkg`
  positional args; symbol publish needs an explicit snupkg push (nuget.org
  documented flow). (2) Verification must observe effects, not exit codes
  or success output. (3) Tag-only/schedule-only workflows never self-verify
  in CI — their `run:` commands are the class that ships untested.
- Applied: release.yml nuget.org step split into explicit nupkg + snupkg
  pushes with the evidence in a comment; `address-pr-feedback` skill step 3
  gained the execute-on-pinned-toolchain / verify-effects / re-execute-
  claims rules and the sweep-table row for never-exercised workflow
  commands.
- Open: none.

## 2026-09-21 - session 014: issue-debt round (server spec adoption, test-name + comment-form gates)

- Trigger: issue debt after M3.3/M3.4 groundwork (#34 new server spec,
  #26 style directives, #20 comment forms) plus the standing CI-time goal.
- Findings: (1) #34 needed no new work - the pin `07a6fd08` IS server 0.9.2
  (latest upstream main); session 013 had already vendored and routed it;
  byte-identical verify + green corpus closed it with evidence. (2) The
  sibling repos (unity-helpers, DoxReloaded) both ban underscores in test
  method names (unity-helpers lint UNH004) and enforce one `/* */` block
  for any multi-line non-doc comment - our `Method_Scenario_Expectation`
  convention and 42 `//` stacks predated that guidance. (3) A PS7 ternary
  cannot break before `?` - the assignment silently parses as a new
  statement (bit the new lint's path handling). (4) Assertion patterns
  must match rendered text literally: `...` is an ellipsis, not `. . .`.
- Applied: 111 test methods renamed to PascalCase (320 x 2 TFM green,
  discovery count unchanged); `scripts/lint-test-names.ps1` +
  `scripts/lint-comment-form.ps1` (C#-only lexer, string-aware) with
  self-tests, wired into hook + CI; 42 comment runs converted to blocks;
  `.llm` naming table + create-test skill updated (rules 19/20);
  dotnet.yml matrix 4 -> 3 cells (windows+net10 dropped: OS and TFM
  assurances carried by other cells) - drops the measured 202s longest
  cell (last PR run); expected wall ~3m30s -> ~2m, code coverage unchanged.
- Open: DoxReloaded member-ordering lint adoption and the TUnit spike
  remain from #26 (follow-up issues filed); unity-helpers WUH analyzers
  are Unity-object-coupled - lint scripts adopted instead (decision
  recorded on #26).

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
  boundaries (8-4-4-4-12, hyphens at 8/13/18/23 — the group after the
  third hyphen starts at 19, not 18), and the -1 error sentinel aliasing
  an all-FFFF field (validate each pair, never OR combined signed ints).
  (3) required-field
  enforcement needs per-field seen flags — `default(Guid)` is a valid
  value, absence is not. (4) 0 B allocation gates must scope by path:
  join mapping legitimately allocates the membership's room-code string
  (cold); payload-less session facts (per-frame traffic) stay 0 B.
- Applied: mapper + routing tests data-driven from spec-shaped frames;
  `TryReadGuid` pinned against `Guid.Parse` as oracle; fence semantics
  unchanged; appended MessageKinds keep ordinals stable (fuzz seeds).
- Open: fold (2) into json-serialization skill when next edited (300-line
  cap).

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
