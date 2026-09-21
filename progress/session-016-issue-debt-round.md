# Session 016 — Issue-debt round: Ping/Pong truth, Pages, auto-release, perf close

Date: 2026-09-21. Branch: `issue-debt-round`.

## Scope

Drive down issue debt (goal: 3+ issues) with small, verified deliverables and
zero PR-CI-time increase. No new library surface: correctness round by
documentation-of-record, plus two workflow deliverables. M3.5 stays next.

## Delivered

- **#38 closed won't-fix with evidence (correctness)**: the issue's premise —
  server-initiated `Ping` needs a `Pong` reply — fails against all three
  sources of truth. The server AsyncAPI `receive` channel has `Pong` and no
  `Ping`; `docs/guides/building-a-client.md` says the *client* sends `Ping`
  and the server replies `Pong` (server liveness watches client pings, close
  `4003`); the Rust parity client's `ServerMessage` enum has no `Ping`
  variant. Per the issue's own decision tree: won't-fix. Sweep for the same
  class of problem (wrong direction claims) found exactly three spots —
  `MessageKind.Ping/Pong` XML docs and the `protocol-messages` skill table —
  now corrected to C→S / S→C so the false premise cannot recur.
- **#40 closed (GitHub Pages)**: `docs.yml` gains a `deploy-pages` job
  (upload-pages-artifact in `mkdocs-build` -> `actions/deploy-pages`), gated
  to main pushes. Repo Pages was already `build_type: workflow`. PR checks
  unchanged — the deploy job and artifact upload are skipped on PRs.
- **#17 closed — NuGet half (auto-release)**: package metadata
  (`PackageId`, description, tags, repo URLs, MIT license expression,
  README + snupkg) added to the library csproj — still zero `PackageReference`
  (zero-deps lint untouched). New tag-driven `release.yml` (`v*` only):
  semver tag guard, `dotnet pack` with the tag version + deterministic CI
  flag, publish `.nupkg`/`.snupkg` to GitHub Packages, attach both to a
  GitHub Release (`--generate-notes`) as the no-auth fallback. Follow-up
  issue filed for the UPM/NPM half (needs M7 Unity packaging).
- **#7 closed (high performance)**: every ask in the issue is landed and
  enforced — LINQ banned under `src/` (linter + CI + hook), allocation-gate
  tests pin codec/state-machine/mapper/polling hot paths at 0 B steady
  state, BenchmarkDotNet baselines recorded in `docs/benchmarks.md`, and
  per-poll budgets bound worst-case polling cost. Remaining ideas
  (M4.1 buffer spike data, M9.4 scheduled bench regression) are PLAN tasks,
  not open questions.

## CI time

- PR CI: byte-for-byte unchanged job set (release.yml is tag-only; Pages
  deploy is main-push-only). Coverage and test matrix untouched.
- Main pushes: docs.yml adds the Pages deploy (a few seconds of upload +
  deploy on an already-green build). dotnet.yml/llm-context.yml untouched.
- The csproj property additions change the NuGet cache key — one expected
  cache miss on the first run after merge.

## Verification

- `dotnet build -warnaserror` clean; full test suite green on net8.0
  (local) — CI re-runs the 3-cell matrix.
- Convention lints (all six), file-size lint, LLM-instructions lint, skills
  index freshness, automation self-tests all green.
- No C# behavior changes: docs-only edits plus workflows/csproj metadata.
