---
name: maintain-plan
description: Keep PLAN.md a lean rolling-wave plan (in-progress and future work only) - collapse completed milestones into the status table, move durable context into .llm/references, enforce the 300-line budget, and write session records to progress/. Use when updating PLAN.md after a session, planning the next milestone, or when the plan starts accumulating done-work narrative.
metadata:
  category: core
---

# Maintain Plan

PLAN.md is a local-only (gitignored) rolling-wave plan: a status table,
the in-progress surface, and coarse future work. It is a working index,
not a record — records live in `progress/`, context lives in `.llm/`
(decisions: [project-decisions](../../references/project-decisions.md)).

## Why this exists

27 sessions grew PLAN.md to 640 lines: ~70% was done-work narrative
duplicating `progress/session-*.md`, and decisions/product scope
duplicated `.llm` context. Bloat hides the actual next task and burns
agent context on every session start (rule 22 in
[context](../../context.md)).

## Rules

1. **In-progress + future work only.** One "In progress" section (the
   current surface + its issue number) and lean milestone sections for
   what has not shipped.
2. **Completed work collapses to a status row** (milestone, done date,
   session numbers). Never write DONE narratives, verification details,
   or session summaries into the plan — the `progress/session-NNN-*.md`
   file is the record.
3. **Durable facts live in `.llm/references/project-decisions.md`**
   (locked decisions, product scope, upstream references, CI shape,
   shipping checkpoints). PLAN links there; never copy them back.
4. **300-line budget, warn at 270.** Check after every edit:
   `pwsh -NoProfile -File scripts/lint-file-sizes.ps1 -Paths PLAN.md`
   (PLAN.md is gitignored, so hook/CI never see it — this local check is
   the enforcement).
5. **Status rows must cite session numbers** so `progress/` stays the
   discoverable record.
6. **Keep tasks task-shaped**: verb + surface + gate. Implementation
   detail that only matters once belongs in the task's GitHub issue or
   the relevant skill, not the plan.
7. **Keep remaining task IDs stable** (M6.4, M7.1, ...) — other docs and
   session notes reference them.

## Session workflow

1. Start: read the status table + "In progress" section only.
2. Do the work red-green (`GOAL.md` is the work contract).
3. Write `progress/session-NNN-brief-description.md` first.
4. Update PLAN: flip the status row(s), move the next task into
   "In progress", collapse anything finished, link newly opened issues
   by number.
5. Run the budget check (rule 4). If it warns, collapse before adding.

## When NOT to Use

- `progress/` session notes — they follow their own per-session format.
- `.llm` skills and references — see
  [manage-skills](../manage-skills/SKILL.md).

## Related Skills

- [reflect-improve](../reflect-improve/SKILL.md) - the retrospective loop
  that decides what collapses vs. what stays open
- [manage-skills](../manage-skills/SKILL.md) - the same anti-bloat
  discipline for `.llm` files (300-line limit, splitting)
