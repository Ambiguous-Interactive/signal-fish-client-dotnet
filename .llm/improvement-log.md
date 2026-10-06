# Improvement Log

Episodic staging log for the mandatory
[reflect-improve](./skills/reflect-improve/SKILL.md) loop. Newest first.
Each entry is an H2 header starting with the date (`## YYYY-MM-DD - scope`)
followed by Trigger / Evidence / Findings / Applied / Open bullets.

Prune an entry once its knowledge has graduated into durable artifacts and
its `Open` items are resolved — this file is staging, not storage (target
under ~150 lines; the 300-line lint ceiling is the hard bound).

## 2026-10-06 - session 049: M8.7 validation runbook (summary-page drift)

- Trigger: the reflect-improve loop after a 14-file docs change (the
  M8.7 runbook page + nine pages' validation sections re-pointed).
- Evidence: the adversarial review caught three factual errors in the
  new summary page — a borrowed watch item from the wrong sibling
  page, an inverted component attribution, and an invented
  unverifiable gloss on a version pin — plus a sourcing overclaim
  ("taken verbatim") in the session record. All were prose claims no
  compiler or lint can see.
- Findings: a summary page that aggregates claims from many source
  pages re-introduces the "never write from memory" failure class
  (already an adapter-code rule) at the docs layer: every borrowed
  claim needs re-verification against its source page, and a pin
  field must never carry framing the repo has not itself asserted.
- Applied: all four findings fixed in the same change; the
  verification step is recorded here — the existing
  `author-engine-adapter` rule ("verify against the source, never
  memory") is the durable knowledge, now understood to cover docs.
- Open: none.

## 2026-10-04 - session 042 PR feedback: Bugbot round on #90 (invisible-to-CI engine-gated code)

- Trigger: Cursor Bugbot's three high-severity findings on PR #90,
  verified against the code, the E2E conformance suite, and the UGS
  Relay API surface.
- Evidence: (1) `joinCode = await AwaitHook(...)` awaited a void `Task`
  — a compile error in the `#if SIGNALFISH_NGO` bridge that no compiler
  in CI sees; the round-2 refactor that introduced it shipped green.
  (2) The drain failed on every `AuthorityChanged`, including the
  server's grant broadcast (`you_are_authority: true` follows a granted
  `AuthorityResponse` for the grantee, in either order — proven by
  `AuthorityClaimMovesTheSeatAndGatesStart`), so every healthy host
  start tore itself down on its first `Update`. (3) The sample returned
  `allocation.JoinCode`, which Unity Relay's `Allocation` does not
  carry — the host must call `GetJoinCodeAsync(allocationId)`.
  Bonus catch by the new stub lane itself: `Environment.TickCount64`
  does not exist on the netstandard2.1 API floor Unity compiles
  against, despite the dotnet build passing.
- Findings: engine-gated sources need a compile contract (shape stubs),
  not review discipline; event-to-fatal classification must be verified
  against the conformance suite (the executable spec of server
  broadcasts), not intuition; external-SDK member names must be checked
  against the SDK's source or docs, never memory.
- Applied: generic + void `AwaitHook` overloads; the drain now fails
  only when `you_are_authority != _isHost`; the sample calls
  `GetJoinCodeAsync`; waits use `SystemClock` instead of `TickCount64`;
  `lint-unity-adapter` grew a shape-stub bridge-compile lane
  (`BridgeCompile`/`BridgeStubs`, NGO first) that type-checks
  engine-gated bridges at the netstandard2.1 floor on every CI run;
  knowledge folded into the new `author-engine-adapter` skill and two
  new sweep rows in `address-pr-feedback`.
- Open: Mirror/FishNet bridges have no stub-compile lane yet (large
  engine surfaces); tracked as a follow-up issue.

## 2026-10-04 - session 042: M8.3 NGO coordinator (third-entry lint generalization + floor-portable core)

- Trigger: the reflect-improve loop after authoring the third adapter
  package (the NGO + Relay coordinator).
- Evidence: (1) the adapter lint's SDK reference pattern was a
  FishNet/Mirror if/else — a third adapter would have been silently
  linted with the Mirror pattern (false negatives, no failure shape);
  generalized to a pin field before it could misfire. (2) `string.Create`
  with a span state compiled on net10.0 and failed CS9244 on net8.0 —
  pure-core sources ride the oldest supported floor, and only the
  multi-TFM test suite catches newest-framework-only API shapes. (3)
  Writing the consumer sample exposed a real API race (a synchronous
  binder hook vs an inherently async Unity Relay join) that the runtime
  API review had missed; the sample is the cheapest API reviewer for
  hook-shaped surfaces. (4) the adversarial review pass verified the
  NGO bridge against NGO's own 1.2.0 source and found the approval gate
  dead: `NetworkConfig.ConnectionApproval` gates the whole mechanism
  (without it the host auto-approves everything and the client never
  sends ConnectionData), the callback setter throws on a two-target
  delegate (so `+=` breaks every restart), and `StartHost/StartClient`
  report refusal as `false` + a log, not an exception.
- Findings: generalize lint pins when the second consumer becomes a
  third; avoid newest-framework-only API shapes in sources that must
  compile on older floors; author the sample call sites first when an
  adapter API is hook-shaped; engine-gated sources need their feature
  flags verified against the engine's actual source — presence of the
  callback is not activation, and restart cycles break on engine-side
  state the adapter never clears.
- Applied: `SdkReferencePattern`/`WirePin` pin fields + optional
  channel pin + fixture lane (72 assertions); the envelope decode
  rewritten to `Encoding.ASCII.GetString`; the client Relay binder
  contract became `Func<string, Task>`, awaited before the NGO client
  start stages; the approval flag, host self-payload, callback
  assignment, and start-result checks all landed with the adversarial
  round.
- Open: none.

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

## 2026-10-06 - session 048: M8.6 Facepunch adapter (second Steamworks half)

- Trigger: new adapter module (12+ files); two adversarial rounds
  against the cloned Facepunch.Steamworks 2.5.2 sources.
- Findings: (1) member-name pinning is not semantic pinning — the
  binding's dispatch/threading mode (Facepunch's Init defaults to a
  background-thread callback pump), refusal mechanics (a refused
  create throws from a registry setter; the returned-default check is
  dead code), and registry lifetimes (static dictionaries never
  remove entries; stale dispatch + recycled handles) all needed the
  same clone-and-verify discipline as the member surface; (2) the
  sibling's hard-won teardown guarantee (close accepted-but-
  unestablished connections) was lost in translation — port every
  cleanup loop, not just the structure; (3) an exposed "the game owns
  the traffic" surface needs its receive path proven (Facepunch's
  managers drop OnMessage without an Interface — the contract had to
  be documented, not implied).
- Applied: all 8 first-round + 3 second-round findings in the
  bootstrap/lint/docs; "Pin semantics, not just names" folded into
  author-engine-adapter.
- Verified: lint-unity-adapter (8 packages), 128-assertion self-test,
  dotnet test 931x2 green, csharpier + conventions clean.
