# Session 003 — M0.3 repo linters + docs pipeline skeleton

Date: 2026-09-18. Branch: `repo-linters-m0.3`. Goal: advance PLAN.md to
a green PR. Closes issue #2 (M0.3), completing milestone M0.

## Drift check

- `origin/main` up to date; working tree clean; PR #1 already merged, no
  draft/in-progress PRs, no in-progress work to carry forward.
- Main CI green at session start (dotnet + LLM Context both success).
- Open issues triaged: #2 (M0.3 linters) and #3 (M1.1 fixtures). M0.3 taken
  first per PLAN milestone order — it completes M0 and gates M1 on a fully
  green no-op workflow set.

## This session's surface: M0.3 (issue #2)

- `.markdownlint-cli2.jsonc` + `.markdownlint.json` mirroring the Rust
  client (MD013/033/041/046 off; MD024/007 relaxed; style rules
  standardized; MD060/058 off for mkdocs tables). Ignores: build dirs,
  `progress/**` (historical session notes), `PLAN.md` (local-only doc).
- `.typos.toml`: en-us, excludes for `bin/`, `obj/`, `TestResults/`,
  `site/`, node/modules; project word list (signalfish, netstandard,
  IL2CPP, asmdef, jslib) plus the Rust repo's shell-flag carries.
- `.lychee.toml`: CI-reliability tuning (429 accepted, retries, loopback/
  private excluded, UA header); excludes shields.io, anchor-only links,
  and not-yet-published GitHub repo/Pages URLs for this repo.
- `.github/workflows/docs.yml`: four jobs — Markdownlint, Spell Check,
  Link Check, MkDocs Build (strict, stock mkdocs skeleton: `mkdocs.yml` +
  `docs/index.md` + `requirements-docs.txt`). Always-on (no path filters)
  so the gate is stable; concurrency-canceled per branch. Action pins
  mirrored from the Rust client's green pipeline (markdownlint-cli2-action
  v24.2.0, typos v1.50.2, lychee-action v2.9.0).
- Linters are CI-only by design: markdownlint needs node, lychee is a Go
  binary — not assumed contributor-local (issue explicitly allows this);
  the pre-commit hook stays `.llm`-scoped and fast.

## Red → green (16 issues found, all fixed)

- 6 × MD040: added `text` language to ASCII diagram/tree/shell fences.
- 3 × MD034: bare URLs angle-bracketed (protocol-quick-reference).
- 2 × MD029 in `websocket-transport`: root cause was a table interrupting
  the behavior-rules list — moved the close-code table below the list
  (content fix, no config change).
- 2 × MD029 in `context.md` (rules 11-15 continuing across a heading):
  numbering is load-bearing (3 cross-references say "rule 15"); MD029
  disabled repo-wide with rationale in `.markdownlint.json`. A scoped
  `.llm/.markdownlint.jsonc` override was tried first and rejected:
  markdownlint-cli2 cascading configs REPLACE (not merge) the root config,
  which silently re-enabled MD013 for `.llm/`.

## Verification

- markdownlint-cli2: 0 issues / 24 files.
- typos v1.50.2: clean.
- lychee v0.24.2: 13 OK, 0 errors (`**/*.md` + `llms.txt`).
- `mkdocs build --strict`: green; `site/` added to `.gitignore`.
- `scripts/lint-file-sizes.ps1` + `lint-llm-instructions.ps1`: pass
  (163/94-line files under limit; log entry added).
- `scripts/tests/run-all.ps1`: 6/6 self-test files.
- `dotnet build -c Release -warnaserror`: 0 w/0 e; `dotnet test`:
  10/10 on net8.0 + net10.0.

## Notes for future sessions

- `PLAN.md` and `GOAL.md` are gitignored local-only docs — plan-status
  updates are worktree edits, never commits (recorded in improvement log).
- Docs action pins should track the Rust client's pins when bumped.

## Leftovers / follow-ups

- Issue #3 (M1.1 golden fixtures + sync script) is the next surface; it
  seeds M1.2/M1.3 codec red-green work.
- When the repo is established/public, revisit the pre-publish lychee
  excludes (dotnet repo/Pages URLs) — the config comments self-document
  their removal conditions.
- M7.5 replaces the stock mkdocs skeleton with the real mkdocs-material
  site; `requirements-docs.txt` pins `mkdocs~=1.6.0` (compatible-release,
  blocks a future mkdocs 2.x from breaking the always-on gate) until then.

## Adversarial review round (sub-agent)

Findings and dispositions:

| Finding | Severity | Disposition |
| --- | --- | --- |
| `docs/index.md` linked `.../blob/main/PLAN.md` — PLAN.md is gitignored, so the URL 404s forever (and lychee excludes dotnet-repo URLs, so nothing would catch it) | MAJOR | Fixed: links now point to the issue tracker / sibling repos only |
| `mkdocs>=1.6` admits a future mkdocs 2.x into an always-on CI gate | MINOR | Fixed: `mkdocs~=1.6.0` (exact Rust-mirror semantics) |
| Lychee actions-badge exclude fully shadowed by the repo-URL prefix exclude | NIT | Fixed: dead pattern removed |
| No `required` sentinel job (Rust mirror has one) | NIT | Rejected for now: skeleton; branch protection not yet configured — revisit when it is |
| checkout@v4 / setup-python@v5 vs Rust's v7 pins | NIT | Rejected: checkout@v4 matches this repo's existing workflows; v5 is valid and green elsewhere |
| typos job lives in docs.yml, not a separate always-on CI file like Rust | NIT | Rejected: docs.yml has no path filters, so coverage is identical; placement documented in the workflow header |

Reviewer also empirically confirmed: action inputs exist at the pinned
versions; `site/` isolation is airtight per-job and via excludes; MD029
disable is necessary (Rust's `one_or_ordered` still fails a `11.`-start
list); all rule IDs and JSON/YAML valid; repo linters + self-tests green.
