# Improvement Log

Episodic staging log for the mandatory
[reflect-improve](./skills/reflect-improve/SKILL.md) loop. Newest first.
Each entry is an H2 header starting with the date (`## YYYY-MM-DD - scope`)
followed by Trigger / Evidence / Findings / Applied / Open bullets.

Prune an entry once its knowledge has graduated into durable artifacts and
its `Open` items are resolved — this file is staging, not storage (target
under ~150 lines; the 300-line lint ceiling is the hard bound).

## 2026-09-19 - devcontainer EACCES: root-owned volume mountpaths

- Trigger: `opencode --yolo` in the devcontainer died with
  `EACCES: permission denied, mkdir /home/vscode/.cache/opencode`.
- Evidence: base image has no `~/.cache`/`~/.local`; the runtime created both
  root-owned when mounting the `~/.cache/uv` volume. Census: `uv-cache`,
  `claude-zai-home`, `claude-openrouter-home` volumes were root:root
  (npm/gh/opencode volumes were vscode-owned only because the chown guard
  happened to list them). Clean-room RED reproduced 5/5 EACCES as vscode.
  post-create/post-start `mkdir -p ~/.cache/signal-fish-devcontainer` failed
  silently (no `set -e`), breaking the CLI-refresh stamp/lock.
- Findings: (1) the chown guard healed symptoms path-by-path — every new
  nested volume target re-introduced the bug class; fix the root cause in the
  image. (2) `install -d -o` applies ownership only to the FINAL path
  component — parents must be listed before children (caught by the new
  build-time assertions). (3) `opencode --version` succeeds without the cache
  dir: --version smokes do not cover state-writing paths; assert writability
  of the dirs directly.
- Applied: Dockerfile pre-creates all 12 volume mountpoints vscode-owned
  (+ build-time assertions); chown guard extended (`.cache`, `.cache/uv`,
  `.claude-zai`, `.claude-openrouter`); 3 root-owned volumes deleted;
  self-test +7 checks incl. a generic every-mount-under-$HOME guard;
  lifecycle run green 121/121; all 12 volumes verified vscode-owned; facts in
  [devcontainer-tooling](./references/devcontainer-tooling.md).
- Open: operator to confirm `opencode --yolo` after Reopen in Container
  (VS Code rebuilds the image with the fix).

## 2026-09-19 - devcontainer upgrade (MCP survey, AI backends, build speed)

- Trigger: user-requested devcontainer upgrade: MCP landscape survey
  (Unity/dotnet/git), Z.ai + OpenRouter launchers for Claude Code/Codex,
  VSCode theming/tooling, build speedups; red-green, data-backed workflow.
- Evidence: baseline clean build 79.9s vs multi-stage 29.3s repeat / 88.4s
  first-pull / 4.7s warm (local arm64 BuildKit); BuildKit rejected
  `COPY --from=${ARG}`; `node:22-noble` does not exist; os.homedir() win32
  HOME incident (six real-profile configs polluted, then surgically cleaned:
  5 run-created files deleted, injected `mcpServers` keys stripped from the
  pre-existing `~/.claude.json`); split-container verification produced 49
  false failures, single-container run passed 114/115 (gh = feature-only).
- Findings: (a) `os.homedir()` ignores `HOME` on win32 - sandboxing by HOME
  alone is a live pollution hazard for host-side test runs; (b) harness
  verification that spans two ephemeral container instances tests nothing;
  (c) curl-installers re-download on every clean build while `COPY --from`
  stages stay locally cached - the "obvious" speedup (curl) was the slow path
  on repeat builds; (d) codex MCP env values intentionally persist the
  zai-vision key (chmod 600) while provider keys must not persist - a blanket
  no-secrets grep false-positives on the documented exception.
- Applied: 3 new keyless/gated MCP servers (microsoft-docs, context7, git via
  uvx) across all 8 harnesses; `.devcontainer/ai-backends.sh` (4 launchers,
  isolated CLAUDE_CONFIG_DIRs, codex profiles via managed TOML block, no keys
  in argv/config); Dockerfile multi-stage rewrite (+uv, build-time SDK checks);
  parallel CLI installs; CI `type=gha` build cache; VSCode extensions/settings;
  self-test extended to 114 checks; facts recorded in
  [devcontainer-tooling](./references/devcontainer-tooling.md).
- Open: (a) propose a repo rule: host-side tests of home-relative writers must
  override `USERPROFILE` as well as `HOME` (or run in Linux); (b) consider a
  Unity MCP relay (qora-redux pattern) when Unity work starts; (c) `gh`
  self-test check could skip gracefully under plain `docker build`.

## 2026-09-19 - M1.1 golden fixtures (PR #10 Bugbot round)

- Trigger: PR feedback — Cursor Bugbot reported 2 issues in
  `scripts/sync-protocol-fixtures.ps1` after push.
- Evidence: (1) "Array unroll breaks single-file corpus" — reproduced under
  StrictMode: a 1-element function return unrolls to `String` and `.Count`
  throws (the new script violated the already-documented rule 1 in
  `powershell-tooling`; the pre-commit crash was the same class). The
  first fix attempt (comma-prefix + `@()` call sites) NESTED the array
  (`object[1]`, space-joined rendering, `[string]` binding failure) —
  caught by the end-to-end stale-file test before shipping. (2) "Sync
  cannot drop removed fixtures" — `-Sync` overwrote but never deleted, so
  a pin bump removing a fixture could never converge; the assert also ran
  before provenance regeneration, leaving derived output stale.
- Findings: (a) new tooling was written without checking it against the
  repo's own `powershell-tooling` failure-class list — the class was
  already documented with prior evidence; adversarial review rounds tested
  behavior but did not diff new PS code against the known-rules checklist.
  Rules-check new tooling at write time, not at review time. (b) "sync
  must converge (delete included), then assert, then derive" was a
  genuinely new failure class. (c) Two unroll defenses are mutually
  exclusive: bare-return + `@()` call sites (the repo convention) OR
  comma-prefix + plain assignment (binary buffers only) — never both.
- Applied: script fixed (bare name-list returns + `@()` call sites; `-Sync`
  deletes stale `*.jsonl` before the postcondition assert; provenance
  regenerated last); `powershell-tooling` rule 1 extended with both
  instances + the nesting trap, new rule 6 (converge-then-assert-then-
  derive); index regenerated; verified end-to-end (planted stale fixture
  deleted, sync + verify green, corpus byte-identical, build + tests green).
- Open: the network-bound sync script still has no self-test (harness is
  local-only); noted in `progress/session-004` — acceptable while manual,
  revisit if it ever joins CI.

## 2026-09-18 - M0.3 repo linters + docs pipeline skeleton

- Trigger: PLAN M0.3 / issue #2 — mirror the Rust client's repo hygiene
  (markdownlint-cli2, typos, lychee) and prove a docs build pipeline.
- Evidence: first markdownlint run: 16 issues / 7 files (missing fence
  languages, bare URLs, list numbering broken by tables/headings).
  Config experiment: a `.llm/.markdownlint.jsonc` tree override REPLACED
  the root config (no merge) — MD013 flipped back on for `.llm/`.
- Findings: (1) markdownlint-cli2 cascading configs do not merge; scoped
  overrides mean full-config duplication — avoid them unless a tree truly
  needs different rules; (2) `PLAN.md`/`GOAL.md` are gitignored local-only
  docs in this repo — plan-status updates never ship in commits (agents
  should not look for them in PR diffs); (3) content fixes beat config
  relaxations, except where numbering is load-bearing (context.md rules
  1-15 are cross-referenced — MD029 disabled with rationale instead).
- Applied: three lint configs + `docs.yml` (markdownlint / typos / lychee /
  mkdocs-build skeleton, pins mirrored from the green Rust client);
  6 fences got `text`, 3 URLs angle-bracketed, close-code table moved out
  of the behavior-rules list; `site/` gitignored; all linters green locally
  (24 md files, typos clean, lychee 13 OK / 0 errors, mkdocs strict build).
- Open: none for this scope; M1.1 fixtures tracked as issue #3.

## 2026-09-18 - PR feedback round 1 (Bugbot): tooling robustness

- Trigger: PR #1 review (Cursor Bugbot, 3 findings) + instruction to mine
  sibling repo `unity-helpers` for PR-feedback workflow guidance.
- Evidence: (1) `Write-Error` under EAP=Stop inside lint loops aborted at
  the first violation — repro showed the two-file case reported one garbled
  line; (2) `DefaultServerUri("::1")` threw `UriFormatException` (3 RED
  test cases); (3) `[string]$Content` in `Write-TestFile` space-joined
  arrays — fixture became `line one  line three` on ONE line. Also found
  during green: `pwsh -File` sends surplus tokens after a named param to
  positional params (three invocation variants failed before `-Command`).
- Findings: all three findings were instances of classes already latent
  elsewhere; the sweep eliminated the classes repo-wide (no other in-loop
  `Write-Error`, one URI site, one coercible content param). unity-helpers
  has review/ship workflow skills but no fetch-feedback procedure; the
  missing procedure is what GOAL sessions need.
- Applied: report-all-then-fail in both linters (tests assert multiple
  violations all reported); IPv6 host bracketing (data-driven test cases);
  `Write-TestFile` takes `[object]`; `Invoke-PwshCommand` helper; new
  skills `address-pr-feedback` (gh fetch -> verify -> class sweep ->
  red-green -> reply map) and `powershell-tooling` (the 5 PS rules above);
  index regenerated; all linters + 6 self-test files + 10 C# tests green
  on both TFMs.
- Open: none.

## 2026-09-18 - M0 governance sync (skills x locked decisions)

- Trigger: PLAN M0.1 — `.llm` guidance had to match the locked decisions
  (hand-rolled UTF-8 codec, zero-dep, `IBoundedQueue`, struct-event drain).
- Evidence: pre-fix sweep found STJ/`[JsonPropertyName]`/`record` guidance in
  `json-serialization`, `protocol-messages`; `System.Threading.Channels`
  guidance in `unity-compatibility`, `async-threading`,
  `websocket-transport`; `ISystemClock`, `UnknownMessageReceived`,
  `SignalFishConfig`/`HeartbeatOptions`, and "backoff with jitter" drifted
  from PLAN (which locks `ISignalFishClock`, `UnknownMessage`, no-jitter).
- Findings: skill drift is per-file, so single-file rewrites leave stale
  guidance in sibling skills — sweeps must be repo-wide term greps, not
  file-list checks.
- Applied: 7 skills rewritten/amended to locked decisions; naming and
  event-type examples aligned; `net8.0;net10.0` runner TFM noted where
  relevant; index regenerated; all linters + self-tests green.
- Open: none for this scope; M0.3 repo linters tracked as a GitHub issue.

## 2026-09-19 - devcontainer with prewired agentic harnesses and MCP

- Trigger: no devcontainer existed; goal was a fast, reliable VS Code
  container with codex/opencode/nanocoder/claude/copilot at latest on every
  build/rebuild/launch, sudo-free npm, and GitHub + Z.AI MCP servers wired
  into all supported agent frontends, durable across rebuilds and fresh
  clones, with credentials preferring a gitignored `.env.local`.
- Evidence: built and iterated with `@devcontainers/cli` (0.89, arm64 Docker);
  red-green via an in-container `self-test.sh` (47 checks, all green) plus
  real-tool validation (`codex mcp list`, `opencode mcp list`,
  `install-hooks.ps1`).
- Findings: (1) `containerEnv` cannot self-reference `PATH` - the literal
  reaches docker and breaks the container; use Dockerfile `ENV`. (2) the
  dotnet feature v1 installs a parallel SDK root that hides the image SDK and
  breaks pwsh - install extra SDKs into `/usr/share/dotnet` in the Dockerfile
  instead. (3) the node feature's nvm hard-fails when `NPM_CONFIG_PREFIX` is
  set - install Node from official binaries instead. (4) opencode's npm
  postinstall mis-selects libc on linux-arm64 - preinstall the platform
  package with `--ignore-scripts`, then run its postinstall. (5) bash `"\n"`
  inside double quotes is a literal backslash-n - the resulting TOML
  corruption was caught by the idempotency + real-parser checks, not by
  substring greps; assert exact block counts. (6) named volumes and parents
  of volume targets can be root-owned - chown guard list in lifecycle
  scripts. (7) Windows bind mounts need `git config safe.directory` for
  hooks.
- Applied: `.devcontainer/` (devcontainer.json, Dockerfile, lifecycle +
  lib scripts, self-test, README); committed `.vscode/mcp.json` (GitHub OAuth +
  `shellCommand` input reading `.env.local` via the shared env loader);
  `.env.example` with `!.env.example` gitignore exception.
- Open: confirm VS Code Z.AI http servers pick up the `shellCommand` input on
  a first real session; GitHub MCP in VS Code uses account OAuth rather than
  the PAT by design.

## 2026-09-19 - devcontainer MCP round 2: convergence with sibling repos

- Trigger: verify the devcontainer MCP wiring end to end on a second machine
  (Windows host) and fold in proven patterns from sibling signal-fish repos
  (cloud's install-mcp-servers.sh, server's CI build workflow, rust's MCP
  handshake checker).
- Evidence: red found before any run - the operator's real `.env.local` used
  `GITHUB_MCP_PAT`, which env.sh did not treat as an alias, so the github MCP
  server would have been silently skipped; self-test also asserted injected
  throwaway credentials and would fail on any machine with a real
  `.env.local`. Green after fixes: full container build + self-test on
  linux/arm64 Docker (Windows ARM64 host).
- Findings: (1) sibling orgs converged on a globally installed
  `zai-mcp-server` bin (fast, offline-safe) instead of runtime `npx`; ported
  for in-container harnesses, kept `npx` for host-targeted configs (Cursor,
  .vscode/mcp.json) that cannot rely on the container's npm volume. (2)
  `Z_AI_MODE=ZHIPU` must switch remote URLs to `open.bigmodel.cn` - was
  hardcoded to api.z.ai everywhere; now one base-URL decision per writer plus
  a `shellCommand` input in .vscode/mcp.json. (3) configs are now pruned when
  their credential disappears (secret hygiene, cloud-repo contract). (4)
  control-character values (CRLF paste) are rejected in env.sh before they
  can corrupt JSON/TOML. (5) hermetic self-tests need a file-parse skip seam
  (DEVCONTAINER_ENV_SKIP_FILES) plus .env.local backup/restore under an EXIT
  trap; alias tests otherwise depend on the host's real file. (6) without a
  devcontainer-build CI workflow, devcontainer-only breakage reaches main
  undetected; ported server-repo workflow (PR paths filter + monthly drift
  cron) running post-start + self-test via devcontainers/ci.
- Applied: env.sh (aliases, validation, seam), write-mcp-configs.mjs (gemini +
  cursor targets, ZHIPU base, pruning), mcp-config.sh (codex ZHIPU + global
  bin), install-clis.sh (@google/gemini-cli, @z_ai/mcp-server + bounded npm
  retries), mcp-probe.mjs (real MCP probe), self-test.sh (backup/restore,
  67 checks), .github/workflows/devcontainer-build.yml, README/.env.example
  updates.
- Evidence (round 3, same day): 67/67 self-test green in three scenarios -
  main container, cold-start fresh-clone simulation (copied tree, new
  volumes, placeholder keys), and full rebuild with warm volumes; real-tool
  validation `codex mcp list` (5 servers) and `opencode mcp list`
  (github/zai-vision/zai-web-search show connected with live credentials).
- Findings (round 3): (8) devcontainer CLI and VS Code exec/terminals run
  commands through a login-interactive wrapper and inject the probed user
  env - once post-start wires `~/.container-env.sh` into the rc files, real
  credentials are ambient in every session (good for auth, but tests that
  rely on prefix assignments to `source` are unsound: bash restores ambient
  values after the builtin returns; isolate with `unset`/`env -u` for the
  whole subshell). (9) `@z_ai/mcp-server` validates its API key at startup
  and exits on a fake key, so an MCP handshake probe must treat "key-gated
  startup" (server logs an api-key diagnostic on either stream and exits) as
  a distinct, passing outcome; a missing/broken binary or protocol failure
  remains red. (10) transient npm registry failures during container create
  silently skipped late packages (resilience design) - bounded retries with
  backoff in install-clis.sh cover the common case.
- Open: confirm CI workflow passes on the first real PR; confirm VS Code
  resolves the three `shellCommand` inputs on a first real session; commit
  the untracked `.devcontainer/`, `.vscode/`, `.env.example` so fresh clones
  actually get this setup.
