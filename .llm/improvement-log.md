# Improvement Log

Episodic staging log for the mandatory
[reflect-improve](./skills/reflect-improve/SKILL.md) loop. Newest first.
Each entry is an H2 header starting with the date (`## YYYY-MM-DD - scope`)
followed by Trigger / Evidence / Findings / Applied / Open bullets.

Prune an entry once its knowledge has graduated into durable artifacts and
its `Open` items are resolved — this file is staging, not storage (target
under ~150 lines; the 300-line lint ceiling is the hard bound).

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
