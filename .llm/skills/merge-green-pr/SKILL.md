---
name: merge-green-pr
description: Merge a fully green PR (every CI check passing on the head SHA, no unaddressed review feedback) and re-sync local git with the remote - the green gate, this repo's squash-only merge policy, the post-merge sync steps, and the main-green verification. Use when a session's PR is ready to land or after merging any PR.
metadata:
  category: core
---

# Merge a Green PR

A session's deliverable is merged work, not a parked PR. `GOAL.md`
aggregates one session into one PR; the session that drove the PR green
also merges it, then converges local git with origin so the next session
starts clean.

## When to Use

- A PR reached full green: every required CI check passes on the head
  SHA and all review feedback is addressed (see
  [address-pr-feedback](../address-pr-feedback/SKILL.md)).

## When NOT to Use

- Any check red, failing, or pending on the head SHA - fix or wait;
  never merge red.
- Unaddressed feedback or an unresolved review thread - resolve it
  first.
- Someone else's PR without an explicit owner request.

## The green gate (verify, then merge)

1. **Green on the head SHA**, not an older commit:
   `gh pr view <N> --json headRefOid,statusCheckRollup` - the check
   conclusions must belong to the current head.
2. **Mergeable**: `mergeStateStatus` is `CLEAN` (`BLOCKED` means a
   required check or review is missing; `UNSTABLE` means a check is
   failing).
3. **Feedback clear**: no unresolved threads; the finding-to-fix map
   (when a round produced one) is posted.
4. **Not a draft.**

## Merge per repo policy

The repo settings are the policy; do not work around them:

- Squash-only - merge commits and rebase merges are disabled.
- Squash commit title = PR title, body = PR body (so keep the PR title
  and body clean; they become the history entry).
- Delete-branch-on-merge is on; auto-merge is allowed.

```bash
gh pr merge <N> --squash          # settings supply the title/body
gh pr merge <N> --squash --auto   # checks still running: GitHub merges when green
```

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
git branch -D <branch>   # -d refuses a squashed branch; the content IS in main
git fetch --prune        # drop stale remote-tracking refs
git status               # expect: up to date with origin/main, clean tree
```

Do this at merge time, not at the next session start - the stale window
is where the damage happens.

## Verify main went green on the merge commit

The merge is not done until main's CI is green on the squash commit
(`gh run list --branch main --limit 1`). If it goes red, fixing it
becomes the top priority - root-cause and fix forward per `GOAL.md`; a
revert is an honest fix when the root cause exceeds the remaining
session budget.

## Related Skills

- [address-pr-feedback](../address-pr-feedback/SKILL.md) - the feedback
  gate that must be clear before merging
- [maintain-plan](../maintain-plan/SKILL.md) - collapse the landed work
  in PLAN.md after the merge
- [reflect-improve](../reflect-improve/SKILL.md) - the retrospective
  loop after the session's change
