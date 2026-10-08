# Session 064 — codify merge-on-green and the post-merge git sync

Date: 2026-10-08
Branch: `merge-green-pr`
PR: (this session)
Issue: #124

## Drift check

- origin/main already merged (working tree clean at `76bb7f8`, #123);
  main fully green (dotnet, e2e, Docs, LLM Context). No open or draft
  PRs.
- Open issues triaged: #122 (watch item — first actions land after the
  2026-10-19 runner migration), #115 (operator-blocked: npm names +
  `NPM_TOKEN`), #80 (dormant by its own text). No code surface is
  actionable this round, so the session's deliverable is the
  user-directed process work: codify the merge-on-green endgame
  (issue #124).
- Stale branches swept: local `e2e-mtp-startup-retry` and remote
  tracking refs for #121/#123's branches were squash-merge leftovers —
  content already in main, refs deleted, `fetch --prune` clean.

## Data: the merge endgame was tribal knowledge

- The repo's merge policy lives only in GitHub settings: squash-only
  (merge commits and rebase disabled), squash commit title = PR title,
  body = PR body, delete-branch-on-merge on, auto-merge allowed. No
  repo doc stated it; `gh pr merge --squash` folklore did.
- The post-merge local sync was unstated too, and its failure mode is
  already on record: session 031's improvement-log entry — a stale
  local main after squash #67 turned the next sync into four
  both-added conflicts, and it took tree-diff (not commit log) to
  prove the duplication. The fix lived in no durable artifact.
- Parked green PRs rot: the base moves, the green goes stale on an old
  SHA, and the next session pays re-discovery. GOAL.md asked for green
  PRs but never said to land them.

## What shipped

- `.llm/skills/merge-green-pr/SKILL.md` (93 lines): the green gate
  (checks green on the head SHA — not an older commit — mergeable
  state CLEAN, feedback clear, non-draft), the squash merge under the
  repo settings (with `--auto` for still-running checks), the
  immediate post-merge resync (`checkout main`, `--ff-only` pull,
  `branch -D`, `fetch --prune`, status check), and the main-green
  verification on the squash commit. Never-bypass guardrails included.
- `.llm/context.md` rule 23: green PRs merge in-session, local git
  re-syncs, main re-verifies — pointing at the skill.
- `GOAL.md` objective bullet: same contract, work-side.
- `.llm/improvement-log.md`: session 064 entry; session 031's entry
  trimmed — its stale-main finding graduated into the skill (finding
  (2), the keyed-map decoder storage rule, has no skill home and stays
  logged verbatim).
- Skills index regenerated (17 skills).

## Adversarial review findings applied

The adversarial pass verified every command live against the repo and
found two broken gates plus three gaps:

- The main-green check (`run list --limit 1`) saw 1 of 4 workflows and
  no SHA pin - a previous commit's green could impersonate the new one.
  Now pins `--commit "$(git rev-parse main)"` and names the four
  expected workflows; fewer entries means runs have not registered
  yet.
- `--auto` is inert here: auto-merge needs a branch-protection rule to
  attach to and this repo has none, so the skill says to wait instead.
- The state gloss was wrong: live, `UNSTABLE` showed with zero failing
  checks (a bot check still `IN_PROGRESS`); pending is not failing,
  `SKIPPED` entries are not gates, and with no branch protection the
  required-check `BLOCKED` state cannot occur. Green is now defined as
  every rollup entry with a real conclusion being `SUCCESS`.
- The resync had no recovery path: added the pre-`-D` guard
  (`git diff <branch> origin/main --stat` must be empty) and the
  diverged-main recipe (tree-diff vs the squash, then
  `git reset --hard origin/main`).
- GOAL.md is annotated local-only at the skill's first mention; rule
  23 and GOAL.md say "squash commit", not "merge commit".
- Round 2: path filters broke the four-workflow expectation — dotnet,
  e2e, and LLM Context trigger only on their paths, so this PR's own
  `.llm`-only squash runs just Docs + LLM Context and the old gate
  would deadlock ("fewer entries" was misread as not-registered-yet).
  The gate is now "every returned run completed/success", with Docs
  (no path filter) the floor; Dependabot entries ignored; advisory bot
  checks excluded regardless of conclusion; `branch -D` skips an
  absent local branch.
- Bugbot round (on c78580e; two findings verified and fixed, three
  already closed by later commits): the diverged-main recovery
  tree-diffed the branch against the squash and then hard-reset main -
  the wrong ref; session 031's proof was local main vs origin/main
  (empty diff, nothing to discard). The pre-delete guard diffed the
  branch against origin/main, which false-alarms once other PRs move
  the base - it now diffs against the PR's mergeCommit, and both
  guards precede the deletion snippet.

## Verification

- `lint-file-sizes` (29 files ok; context.md 237, log 265 — both under
  the 270 warn), `lint-llm-instructions`, index freshness, and all 18
  automation self-tests pass locally.
- The skill is dogfooded in this very session: this PR is gated,
  merged, and synced by its own steps; the merge commit is the
  skill's main-green verification.

## Deliberately not done

- No merge-method change (repo settings already encode the policy).
- No CI gate on "PR must be merged" — unforceable and unneeded; the
  skill + rule 23 carry it.
- No new skill categories: `core` fits; the process is repo-wide, not
  protocol/testing.
