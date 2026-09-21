---
name: address-pr-feedback
description: Fetch and resolve all PR feedback (human and bot) mechanically - pull reviews and inline comments via gh, verify every claim against the code, sweep for the failure class, fix red-green, and reply with a finding-to-fix map.
metadata:
  category: core
---

# Address PR Feedback

Feedback left on a PR is a work queue, not a verdict. Machines and humans
review different commits, hallucinate, and misread diffs — every finding is
a hypothesis until verified against the code.

## When to Use

- A review, comment, or bot report (e.g. Cursor Bugbot, CodeRabbit) lands on
  an open PR.
- GOAL-driven sessions instruct "address all reviewer feedback before
  moving on".
- Re-review rounds after pushing fixes.

## When NOT to Use

- Feedback on merged/closed PRs — open an issue referencing it instead.
- Style preferences that conflict with repo conventions — note the conflict
  on the PR and follow the repo.

## The loop

### 1. Fetch everything mechanically

Do not rely on the UI or memory; pull the full set:

```bash
gh pr view <N> --json reviews,comments        # review summaries + issue comments
gh api repos/<org>/<repo>/pulls/<N>/comments  # inline review comments (file:line)
gh pr view <N> --json statusCheckRollup       # check conclusions
```

Note the reviewed commit SHA: findings reference the diff at that commit,
which may predate your latest push. Re-check each finding against the
current tree before acting.

### 2. Triage

Order by impact (correctness > usability > performance), and weigh source:
recent human review > bot findings > stale comments. For each finding,
classify like a pre-landing review:

- **AUTO-FIX** — mechanical, no judgment (missing brackets, silent failure
  in a diagnostic tool, missing test). Fix immediately.
- **ASK** — tradeoffs or behavior changes. Present the recommendation on
  the PR and wait, unless a goal explicitly authorizes the change.
- **REJECT** — factually wrong (the code already handles it, or the cited
  line does not do what the finding claims). Say so on the PR with
  evidence; never fix to appease.

### 3. Verify before believing

For each finding: open the cited file:line, reproduce the failure if cheap
(a test, a command). A wrong-but-plausible finding still teaches something —
if the reviewer misread, the code is probably not clear enough; consider a
comment or rename.

### 4. Sweep for the class

Findings are instances of failure classes. After verifying one, grep the
codebase for siblings:

| Reported instance | Sweep for |
| --- | --- |
| Script aborts on first lint error | Other `Write-Error`/`throw` inside loops in tooling |
| Bad URI for one host shape | Every string-interpolated URI/site that takes external input |
| Helper mangles input type | Other narrowly-typed params fed arrays by callers |
| Hook blesses a file CI rejects | Every gate's hook selector vs its linter's accepted inputs vs CI's trigger (see [add-quality-gate](../add-quality-gate/SKILL.md)) |
| Generator/switch breaks after enum renumbering | Every numeric enum construction (`(Enum)(byte % N)`), ordinal loop, and sentinel-guard added in the same change — test/fuzz generators and perf harnesses included |
| Repro artifact lands in the repo tree | Tracked files matching fuzz/crash/seed patterns; artifacts belong in gitignored persistence dirs (`.fuzz/`), never the repo root |

Fix every sibling in the same change. One-off fixes guarantee the reviewer
finds the sibling next round.

### 5. Red-green the fix

Write the failing test that reproduces the reported issue (and one for a
sibling), watch it fail, then fix, then watch it pass. A finding closed
without a test will regress. See [create-test](../create-test/SKILL.md).

### 6. Reply with the map

One concise PR comment mapping each finding to its disposition — fixed
(with file/test), rejected (with evidence), or pending (with the issue
link). Reviewers should never have to diff the branch to learn what
happened to their feedback.

### 7. Capture the class

If the failure class is new knowledge (not already in a skill), fold it
into a skill or rule via the
[reflect-improve](../reflect-improve/SKILL.md) loop before closing out.

## Related Skills

- [reflect-improve](../reflect-improve/SKILL.md) - knowledge capture gate
- [create-test](../create-test/SKILL.md) - red-green regression tests
- [manage-skills](../manage-skills/SKILL.md) - authoring/updating skills
- [powershell-tooling](../powershell-tooling/SKILL.md) - recurring PS failure classes in this repo's tooling
- [add-quality-gate](../add-quality-gate/SKILL.md) - scope contract for gate/hook/CI tooling
