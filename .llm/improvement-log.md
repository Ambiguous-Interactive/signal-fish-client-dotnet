# Improvement Log

Episodic staging log for the mandatory
[reflect-improve](./skills/reflect-improve/SKILL.md) loop. Newest first.
Each entry is an H2 header starting with the date (`## YYYY-MM-DD - scope`)
followed by Trigger / Evidence / Findings / Applied / Open bullets.

Prune an entry once its knowledge has graduated into durable artifacts and
its `Open` items are resolved — this file is staging, not storage (target
under ~150 lines; the 300-line lint ceiling is the hard bound).

## 2026-09-19 - M1.1 golden fixtures (PR #10 Bugbot round)

- Trigger: PR feedback — Cursor Bugbot reported 2 issues in
  `scripts/sync-protocol-fixtures.ps1` after push.
- Evidence: (1) "Array unroll breaks single-file corpus" — reproduced under
  StrictMode: a 1-element function return unrolls to `String` and `.Count`
  throws (the new script violated the already-documented rule 1 in
  `powershell-tooling`; the pre-commit crash was the same class). The
  first fix attempt (comma-prefix + `@()` call sites) NESTED the array
  (`object[1]`, space-joined rendering, `[string]` binding failure) —
  caught by the end-to-end stale-file test before shipping. (2) "Sync
  cannot drop removed fixtures" — `-Sync` overwrote but never deleted, so
  a pin bump removing a fixture could never converge; the assert also ran
  before provenance regeneration, leaving derived output stale.
- Findings: (a) new tooling was written without checking it against the
  repo's own `powershell-tooling` failure-class list — the class was
  already documented with prior evidence; adversarial review rounds tested
  behavior but did not diff new PS code against the known-rules checklist.
  Rules-check new tooling at write time, not at review time. (b) "sync
  must converge (delete included), then assert, then derive" was a
  genuinely new failure class. (c) Two unroll defenses are mutually
  exclusive: bare-return + `@()` call sites (the repo convention) OR
  comma-prefix + plain assignment (binary buffers only) — never both.
- Applied: script fixed (bare name-list returns + `@()` call sites; `-Sync`
  deletes stale `*.jsonl` before the postcondition assert; provenance
  regenerated last); `powershell-tooling` rule 1 extended with both
  instances + the nesting trap, new rule 6 (converge-then-assert-then-
  derive); index regenerated; verified end-to-end (planted stale fixture
  deleted, sync + verify green, corpus byte-identical, build + tests green).
- Open: the network-bound sync script still has no self-test (harness is
  local-only); noted in `progress/session-004` — acceptable while manual,
  revisit if it ever joins CI.

## 2026-09-18 - M0.3 repo linters + docs pipeline skeleton

- Trigger: PLAN M0.3 / issue #2 — mirror the Rust client's repo hygiene
  (markdownlint-cli2, typos, lychee) and prove a docs build pipeline.
- Evidence: first markdownlint run: 16 issues / 7 files (missing fence
  languages, bare URLs, list numbering broken by tables/headings).
  Config experiment: a `.llm/.markdownlint.jsonc` tree override REPLACED
  the root config (no merge) — MD013 flipped back on for `.llm/`.
- Findings: (1) markdownlint-cli2 cascading configs do not merge; scoped
  overrides mean full-config duplication — avoid them unless a tree truly
  needs different rules; (2) `PLAN.md`/`GOAL.md` are gitignored local-only
  docs in this repo — plan-status updates never ship in commits (agents
  should not look for them in PR diffs); (3) content fixes beat config
  relaxations, except where numbering is load-bearing (context.md rules
  1-15 are cross-referenced — MD029 disabled with rationale instead).
- Applied: three lint configs + `docs.yml` (markdownlint / typos / lychee /
  mkdocs-build skeleton, pins mirrored from the green Rust client);
  6 fences got `text`, 3 URLs angle-bracketed, close-code table moved out
  of the behavior-rules list; `site/` gitignored; all linters green locally
  (24 md files, typos clean, lychee 13 OK / 0 errors, mkdocs strict build).
- Open: none for this scope; M1.1 fixtures tracked as issue #3.

## 2026-09-18 - PR feedback round 1 (Bugbot): tooling robustness

- Trigger: PR #1 review (Cursor Bugbot, 3 findings) + instruction to mine
  sibling repo `unity-helpers` for PR-feedback workflow guidance.
- Evidence: (1) `Write-Error` under EAP=Stop inside lint loops aborted at
  the first violation — repro showed the two-file case reported one garbled
  line; (2) `DefaultServerUri("::1")` threw `UriFormatException` (3 RED
  test cases); (3) `[string]$Content` in `Write-TestFile` space-joined
  arrays — fixture became `line one  line three` on ONE line. Also found
  during green: `pwsh -File` sends surplus tokens after a named param to
  positional params (three invocation variants failed before `-Command`).
- Findings: all three findings were instances of classes already latent
  elsewhere; the sweep eliminated the classes repo-wide (no other in-loop
  `Write-Error`, one URI site, one coercible content param). unity-helpers
  has review/ship workflow skills but no fetch-feedback procedure; the
  missing procedure is what GOAL sessions need.
- Applied: report-all-then-fail in both linters (tests assert multiple
  violations all reported); IPv6 host bracketing (data-driven test cases);
  `Write-TestFile` takes `[object]`; `Invoke-PwshCommand` helper; new
  skills `address-pr-feedback` (gh fetch -> verify -> class sweep ->
  red-green -> reply map) and `powershell-tooling` (the 5 PS rules above);
  index regenerated; all linters + 6 self-test files + 10 C# tests green
  on both TFMs.
- Open: none.

## 2026-09-18 - M0 governance sync (skills x locked decisions)

- Trigger: PLAN M0.1 — `.llm` guidance had to match the locked decisions
  (hand-rolled UTF-8 codec, zero-dep, `IBoundedQueue`, struct-event drain).
- Evidence: pre-fix sweep found STJ/`[JsonPropertyName]`/`record` guidance in
  `json-serialization`, `protocol-messages`; `System.Threading.Channels`
  guidance in `unity-compatibility`, `async-threading`,
  `websocket-transport`; `ISystemClock`, `UnknownMessageReceived`,
  `SignalFishConfig`/`HeartbeatOptions`, and "backoff with jitter" drifted
  from PLAN (which locks `ISignalFishClock`, `UnknownMessage`, no-jitter).
- Findings: skill drift is per-file, so single-file rewrites leave stale
  guidance in sibling skills — sweeps must be repo-wide term greps, not
  file-list checks.
- Applied: 7 skills rewritten/amended to locked decisions; naming and
  event-type examples aligned; `net8.0;net10.0` runner TFM noted where
  relevant; index regenerated; all linters + self-tests green.
- Open: none for this scope; M0.3 repo linters tracked as a GitHub issue.

## 2026-09-18 - reflect-improve loop introduced

- Trigger: repository needed a systematic post-work self-improvement loop;
  this first entry applies the loop to its own creation (dogfooding).
- Evidence: lint baseline failed (`GEMINI.md` staged then deleted from the
  worktree; expected by scripts/lint-llm-instructions.ps1:61); `run-all.ps1`
  passed 4/4 before and after; research: Reflexion (arXiv:2303.11366),
  Voyager (arXiv:2305.16291), Anthropic agent-skills and context-engineering
  engineering posts.
- Findings: (1) no mechanism existed to convert session lessons into durable
  knowledge - gaps repeated across sessions; (2) pointer-file inventory is
  lint-enforced yet drifted from the worktree - deletion was invisible until
  the next lint run.
- Applied: new core skill `reflect-improve` (evidence, blameless root cause,
  triage table, red-green promotion gate, log discipline); lint check 7 with
  three new self-tests (log presence, single H1, dated entries); `GEMINI.md`
  restored; context.md rule 15 and structure updated; skills index
  regenerated.
- Open: none.
