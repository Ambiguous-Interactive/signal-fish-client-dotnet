# Improvement Log

Episodic staging log for the mandatory
[reflect-improve](./skills/reflect-improve/SKILL.md) loop. Newest first.
Each entry is an H2 header starting with the date (`## YYYY-MM-DD - scope`)
followed by Trigger / Evidence / Findings / Applied / Open bullets.

Prune an entry once its knowledge has graduated into durable artifacts and
its `Open` items are resolved — this file is staging, not storage (target
under ~150 lines; the 300-line lint ceiling is the hard bound).

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
