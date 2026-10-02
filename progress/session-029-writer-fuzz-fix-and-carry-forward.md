# Session 029 — Writer fuzz fix and carry-forward

Date: 2026-10-02. Scope: restore main CI (red scheduled fuzz lane), commit
and carry forward session 028's uncommitted devcontainer work, incorporate
dependabot #64, resolve the AI-disclosure issue (#63), and scope the M6.3
integration (#61) to an executable design.

## Starting state

- `main` CI was red: the scheduled writer fuzz lane crashed (run
  36376269486, 2026-09-28) and no one had triaged it.
- Session 028's devcontainer/OpenCode-v2 work (18 modified + 5 new files,
  ~2.4k insertions) sat uncommitted in the working tree, verified
  211/211 locally but never pushed.
- No valid GitHub token existed in the environment; `.env.local` with a
  PAT was added by the operator mid-session (restricted to mode 600 —
  it was briefly mode 0777).

## Delivered

- **Writer fuzz lane RCA + fix.** The crash input was two bytes
  (`22 07`). Replay pinned it: the M6.2 work moved `GameData` payload
  validation into the `GameDataMessage` constructor (a refusal must
  precede every send), but the codec fuzz host still wrapped only the
  encode call in its documented-misuse catch, so the constructor's
  `ArgumentException` escaped and killed the driver. Test bug, not a
  production bug: the host now treats a construction refusal as the
  same documented misuse, keeps the valid-JSON-seed invariant at both
  refusal points, and the exact crash payload is graduated into a
  regression test pinning the refusal contract (ArgumentException, at
  construction). Sweep: only one other constructor validates
  (`SignalMessage`, not exercised by the writer target).
- **Carry-forward committed** as two coherent commits: the devcontainer
  OpenCode v2/MCP migration, and the plan-updates (maintain-plan skill,
  project-decisions reference, session 028 record).
- **Dependabot #64 incorporated**: docs workflow Pages actions bumped
  4 → 5 on this branch; the branch supersedes the PR.
- **AI disclosure (#63)**: sibling-pattern blockquote (README top +
  docs landing page) naming the agents, replacing the buried one-line
  section.
- **M6.3 integration scoped**: an exploration pass confirmed the seam
  (`FramePipeline` is the one translate point both drivers call; the
  engine hangs off a per-session component fed there), fixed three
  design facts in PLAN.md (policy gated by `ProtocolInfo` arrival, the
  mapping layer owns the paired epoch/seq refusal, GoingAway =
  surfaced deadline + no teardown), and surfaced one open question:
  JSON `IncomingGameData` carries no seq/epoch stamp while
  `RecordGameData` refuses null stamps on v3 — reconcile with the Rust
  client before wiring.

## Verification

- Crash replay red→green: the CI artifact reproduced the
  `ArgumentException` escape standalone; after the fix the same input
  replays clean (exit 0).
- 656/656 unit tests pass (net8.0 + net10.0); all nine repo lints pass;
  CSharpier via pre-commit.

## Follow-ups

- M6.3 integration (#61) is the next session's focused surface; the
  PLAN section now carries the confirmed design and the open question.
- The fuzz lane's own scheduled run is the final proof; a
  `workflow_dispatch` run on the PR validates the lane early.
- `.env.local` was created world-readable (0777) and restricted to 600
  within the hour; rotate the PAT if the workspace is shared.
