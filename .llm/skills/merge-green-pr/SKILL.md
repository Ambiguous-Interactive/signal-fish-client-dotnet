---
name: merge-green-pr
description: Merge a fully green PR (every CI check green on the head SHA, no unaddressed review feedback) and re-sync local git with the remote - the green gate, this repo's squash-only merge policy, the post-merge sync steps, and the main-green verification. Use when a session's PR is ready to land or after merging any PR.
metadata:
  category: core
---

# Merge a Green PR

A session's deliverable is merged work, not a parked PR. `GOAL.md`
(local-only, gitignored, like PLAN.md) aggregates one session into one
PR; the session that drove the PR green also merges it, then converges
local git with origin so the next session starts clean.

## When to Use

- A PR reached full green: every CI check green on the head SHA and all
  review feedback is addressed (see
  [address-pr-feedback](../address-pr-feedback/SKILL.md)).

## When NOT to Use

- Any check red, failing, or pending on the head SHA - fix or wait;
  never merge red.
- Unaddressed feedback or an unresolved review thread - resolve it
  first.
- Someone else's PR without an explicit owner request.

## The green gate (verify, then merge)

1. **Green on the head SHA, not an older commit.** Compare the SHA you
   pushed against `headRefOid`, then read the rollup:
   `gh pr view <N> --json headRefOid,statusCheckRollup`. Green means
   every rollup entry with a real conclusion is `SUCCESS`. Workflows
   run per their path filters, so a narrow PR triggers only the
   workflows whose paths it touches. `SKIPPED` entries (Deploy Pages
   on PRs, for one) and advisory bot checks are excluded regardless of
   conclusion; a bot check still `IN_PROGRESS` is pending, not failing
   - wait for it.
2. **Mergeable**: `mergeStateStatus` is `CLEAN`. `UNSTABLE` means a
   non-required check is pending or failing. This repo has no branch
   protection, so the required-check `BLOCKED` state cannot occur.
3. **Feedback clear**: no unresolved threads; the finding-to-fix map
   (when a round produced one) is posted.
4. **Not a draft.**

## Merge per repo policy

The repo settings are the policy; do not work around them:

- Squash-only - merge commits and rebase merges are disabled.
- Squash commit title = PR title, body = PR body (so keep the PR title
  and body clean; they become the history entry).
- Delete-branch-on-merge is on; auto-merge is allowed in settings but
  inert here (see below).

```bash
gh pr merge <N> --squash   # settings supply the title/body
```

If checks are still running, wait for them (gate 1). `--auto` needs a
branch-protection rule to attach to, which this repo does not have, so
it is not an option - there is no required-check "green" for GitHub to
wait for.

Never bypass: no `--admin`, no protection overrides, no merging over a
red base. A red merge is a liability, not a shortcut - the base's red is
the PR's red.

## Re-sync local git (immediately after the merge)

Squash merges are the trap: the content lands in main as one new commit
while the branch commits are not ancestors of it, and a stale local main
grows duplicate content that turns the next sync into both-added
conflict archaeology (session 031 paid for this lesson; tree-diff
against the squash, not the commit log, proved the duplication).

```bash
git checkout main
git pull --ff-only       # fails loudly instead of inventing a merge commit
git branch -D <branch>   # -d refuses a squashed branch; skip if absent locally
git fetch --prune        # drop stale remote-tracking refs
git status               # expect: up to date with origin/main, clean tree
```

Two guards make it safe:

- Before `-D`, `git diff <branch> origin/main --stat` must print
  nothing - that proves every local commit reached the PR. Non-empty
  means unpushed work exists: stop and sort it out first.
- If `--ff-only` refuses, local main diverged (the session-031 trap).
  Tree-diff the branch against the squash commit to prove zero unique
  content, then `git reset --hard origin/main` and continue.

Do this at merge time, not at the next session start - the stale window
is where the damage happens.

## Verify main went green on the squash commit

The merge is not done until main's CI is green on the squash commit.
Pin the SHA so an older commit's green cannot impersonate it:

```bash
gh run list --branch main --commit "$(git rev-parse main)" \
  --json workflowName,status,conclusion
```

Every returned run must be `completed` / `success` - that is the gate.
Workflows run per path filters, so a narrow commit (docs- or
`.llm`-only) triggers only Docs plus the workflows whose paths it
touches; fewer than four entries is normal, not a registration delay.
Docs has no path filter and runs on every push, so at minimum expect
it - missing Docs a few minutes after the merge means the runs have
not registered yet; wait and re-check. Ignore `Dependabot Updates`
entries. If a run goes red, fixing it becomes the top priority:
root-cause and fix forward; a revert is an honest fix when the root
cause exceeds the remaining session budget.

## Related Skills

- [address-pr-feedback](../address-pr-feedback/SKILL.md) - the feedback
  gate that must be clear before merging
- [maintain-plan](../maintain-plan/SKILL.md) - collapse the landed work
  in PLAN.md after the merge
- [reflect-improve](../reflect-improve/SKILL.md) - the retrospective
  loop after the session's change
