# Session 001 — Scaffold landing + M0 governance/CI baseline

Date: 2026-09-18. Branch: `scaffold-governance-m0`. Goal: advance PLAN.md to
a green PR.

## Carried forward

The previous session staged (never committed) the full repo scaffold and a
reflect-improve governance layer, and left `GEMINI.md` deleted in the
worktree while the linter + improvement log expected it restored. This
session restored `GEMINI.md`, verified the whole tree green
(`dotnet build` 0w/0e, `dotnet test` 6/6, `scripts/tests/run-all.ps1` 4/4,
both linters), and committed the carried work as two commits:

1. `Add repo scaffold: agent context system, automation scripts, CI, client skeleton`
2. `Add reflect-improve loop: retrospective skill, improvement log, lint check`

## This session's surface: M0 (partial)

- **M0.1 (done)** — `.llm` skills synced to PLAN's locked decisions:
  `json-serialization` rewritten to the hand-rolled UTF-8 codec (two-pass
  decode, static snake_case tokens, span writer, fuzz discipline);
  `unity-compatibility` (zero-dep achieved, STJ removed); `async-threading`
  (`IBoundedQueue`, struct-event drain); `api-design` (struct events,
  `DrainEvents`, net8.0 tooling TFM note); `create-test` (golden fixtures,
  SharpFuzz/FsCheck). Adversarial review then caught stale drift in
  `websocket-transport` (channel wording), `protocol-messages` (records +
  `[JsonPropertyName]`, `UnknownMessageReceived`), `reconnection` (jitter),
  `api-design` (`SignalFishConfig`/`HeartbeatOptions`), and the
  `net8.0`-only runner notes — all fixed; repo-wide stale-term sweep clean.
- **M0.2 (done)** — `dotnet.yml` hardened: matrix {ubuntu, windows} x
  {net8.0, net10.0}, `-warnaserror`, trx + coverlet coverage artifacts,
  ReportGenerator HTML report (Linux, pinned 5.4.1). Tests multi-target
  `net8.0;net10.0`; both TFMs verified locally (build 0 warnings with
  `-warnaserror`, 6/6 tests each, coverage collection exercised on net10).
- **M0.3 (open)** — markdownlint/typos/lychee + docs.yml skeleton: deferred
  to next session (issue filed) to keep this deliverable small and green.

## Verification

- `dotnet build -c Release -warnaserror`: 0 warnings, 0 errors (both TFMs).
- `dotnet test` (net8.0 + net10.0): 6/6 passed each.
- `scripts/tests/run-all.ps1`: 4/4 self-test files pass.
- `scripts/lint-file-sizes.ps1` + `scripts/lint-llm-instructions.ps1`: pass.
- Skills index regenerated (linter-verified byte-identical).

## Leftovers / follow-ups

- M0.3 repo linters (see issue), then the M0 gate: "all workflows green on a
  no-op PR".
- M1.1 golden fixtures is the next implementation surface.
- `plan/` file note: PLAN.md status line updated to reflect M0.1+M0.2 done.
