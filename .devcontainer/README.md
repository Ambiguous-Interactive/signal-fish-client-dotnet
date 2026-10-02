# SignalFish Dev Container

One container, five terminal agent CLIs, and MCP servers pre-wired into every
agentic frontend this repo supports — reproducible after a full rebuild or a
fresh clone. The official OpenCode V2 VS Code extension is included alongside
the CLI; the terminal remains the fallback if the beta extension is disabled.
If an older `sst-dev.opencode` extension is also installed locally, disable
that legacy extension so its commands do not compete with the V2 extension.

## What you get

| Tool | Provided by | Notes |
| --- | --- | --- |
| .NET SDK 10 + 8 | image + Dockerfile | same majors as the CI matrix in `.github/workflows/dotnet.yml` |
| Node 22 | Dockerfile (official `node:22-bookworm` stage) | required by the agent CLIs; no nvm (it conflicts with the sudo-free npm prefix) |
| uv / uvx | Dockerfile (official image stage) | runs the git MCP server (`uvx mcp-server-git`); Python resolves lazily on first use |
| GitHub CLI (`gh`) | `github-cli` feature | authed automatically via `GH_TOKEN` |
| PowerShell (`pwsh`) | Dockerfile (official checksum-verified archive) | runs the repo automation scripts; fixes the ARM64 nested-payload mismatch |

| Agentic harness | Install | MCP config written to |
| --- | --- | --- |
| Claude Code (`claude`) | npm | `~/.claude.json` |
| Codex (`codex`) | npm | `~/.codex/config.toml` |
| GitHub Copilot CLI (`copilot`) | npm | `~/.copilot/mcp-config.json` |
| OpenCode (`opencode`) | npm (`@opencode/cli`) | `~/.config/opencode/opencode.json` or `.jsonc` |
| Nanocoder (`nanocoder`) | npm | `~/.config/nanocoder/.mcp.json` |
| Gemini CLI (`gemini`) | npm | `~/.gemini/settings.json` |
| Cursor (container-local config; host GUI requires a manual copy) | — | `~/.cursor/mcp.json` |
| VS Code + Copilot Chat | extensions | `.vscode/mcp.json` (committed) |

Every harness receives the same eight MCP servers (the committed VS Code
workspace config carries seven — it omits `context7` because an empty-Bearer
header cannot be expressed conditionally there). OpenCode uses the native V2
`mcp.servers` shape, `disabled` instead of V1 `enabled`, environment references
instead of literal credentials, and the V2 Code Mode default. The writer
preserves unrelated global OpenCode settings and follows an existing
`opencode.jsonc` file; it refuses to manage both `.json` and `.jsonc` at once.

| Server | Type | Purpose |
| --- | --- | --- |
| `github` | remote | GitHub issues/PRs/Actions/API (`api.githubcopilot.com/mcp/`) |
| `zai-vision` | stdio | Z.AI image/video understanding (globally installed `zai-mcp-server`) |
| `zai-web-search` | remote | Z.AI web search |
| `zai-web-reader` | remote | Z.AI webpage extraction |
| `zai-zread` | remote | Z.AI GitHub repo reader (`zread.ai`) |
| `microsoft-docs` | remote | Microsoft Learn docs + code samples, keyless (`learn.microsoft.com/api/mcp`) |
| `context7` | remote | Up-to-date library docs (Unity, .NET, npm); keyless, optional `CONTEXT7_API_KEY` for rate limits |
| `git` | stdio | Official git MCP server for local repo operations (`uvx mcp-server-git`); managed only while `uvx` exists |

## Isolated AI backends (claude-zai, claude-openrouter, codex-zai, codex-openrouter)

`bash .devcontainer/ai-backends.sh install` (run automatically by
`post-create.sh`) drops four launchers next to `claude`/`codex`. The ordinary
`claude` and `codex` commands keep their native backends.

| Launcher | Backend | Auth (env → `.env.local`) |
| --- | --- | --- |
| `claude-zai` | Z.ai GLM Coding Plan (`api.z.ai/api/anthropic`) | `Z_AI_API_KEY` / `ZAI_API_KEY` / `ZHIPU_API_KEY` |
| `claude-openrouter` | OpenRouter Anthropic-compatible endpoint | `OPENROUTER_API_KEY` |
| `codex-zai` | Z.ai Responses endpoint (profile `zai`, glm-5.3) | `Z_AI_API_KEY` / `ZAI_API_KEY` / `ZHIPU_API_KEY` |
| `codex-openrouter` | OpenRouter Responses endpoint (profile `openrouter`) | `OPENROUTER_API_KEY` |

The Z.ai launchers always target the `api.z.ai` coding-plan endpoints; with
`Z_AI_MODE=ZHIPU` the MCP servers switch to `open.bigmodel.cn` while the
launchers keep the coding-plan endpoint (they warn about it). `Z_AI_API_KEY`
wins when set; aliases are fallbacks, and two aliases that disagree without a
canonical value are rejected instead of guessed.

- **Model switching**: `claude-zai` reads `CLAUDE_ZAI_{SONNET,OPUS,HAIKU}_MODEL`;
  `claude-openrouter` reads `CLAUDE_OPENROUTER_{FABLE,OPUS,SONNET,HAIKU}_MODEL`
  (defaults: `~anthropic/claude-{fable,opus,sonnet}-latest[1m]`), and exposes the
  gateway `/model` picker via `CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY=1`.
  Codex models: `CODEX_ZAI_MODEL` (default `glm-5.3`),
  `CODEX_ZAI_REASONING_EFFORT` (low/high/max), `CODEX_OPENROUTER_MODEL`
  (default `openrouter/auto`).
- **Secret hygiene**: keys resolve from the environment first, then the
  repository `.env.local`; provider keys never appear in argv or in the Codex
  provider configuration. Harness formats that require a literal MCP credential
  may store it in their owner-only config file; OpenCode uses environment
  references. Each Claude backend gets an isolated `CLAUDE_CONFIG_DIR`
  (`~/.claude-zai`, `~/.claude-openrouter`, both volumes) so cached logins and
  provider routing can never cross-contaminate.
- **Container-aware isolation**: inside the devcontainer the inner Claude
  sandbox is disabled (the container is the boundary); on a host the launcher
  keeps Claude's subprocess isolation and preflights bubblewrap.

### Unity MCP note (survey)

Both major Unity MCP servers — [CoderGamester/mcp-unity](https://github.com/CoderGamester/mcp-unity)
and Unity's official MCP beta — require a **running Unity Editor** to connect
to, which this container intentionally does not host. When Unity work starts,
run the editor on the host and bridge MCP over `host.docker.internal`
(the qora-redux relay pattern), or drive Unity builds headlessly through
GitHub Actions via the existing `github` MCP server.

The three remote Z.AI servers follow `Z_AI_MODE`: `ZAI` (default) targets
`api.z.ai`, `ZHIPU` targets `open.bigmodel.cn` — set the mode in `.env.local`
and the configs switch on the next start.

CI builds this container on every PR that touches `.devcontainer/**` (and
monthly, to catch upstream image/feature drift) and runs the full self-test
inside it: see `.github/workflows/devcontainer-build.yml`.

## Quick start

1. Reopen the repo in VS Code and choose **Reopen in Container**.
2. Copy the credential template and fill in what you use:

   ```bash
   cp .env.example .env.local
   ```

   - `GITHUB_PERSONAL_ACCESS_TOKEN` — powers the GitHub MCP server and `gh`
     (fine-grained PAT with `repo` is enough; aliases: `GITHUB_PAT`,
     `GH_TOKEN`, `GITHUB_TOKEN`, `GITHUB_MCP_PAT`).
   - `Z_AI_API_KEY` — powers the four Z.AI servers plus `claude-zai` and
     `codex-zai` (aliases: `ZAI_API_KEY`, `ZHIPU_API_KEY`).
   - `OPENROUTER_API_KEY` — powers `claude-openrouter` and `codex-openrouter`.
   - `CONTEXT7_API_KEY` — optional; raises context7 MCP rate limits.
   - `Z_AI_MODE` — `ZAI` (default) or `ZHIPU` for bigmodel.cn keys.
3. On Linux/macOS, restrict the file (`chmod 600 .env.local`); on Windows,
   restrict the file with the host ACLs (the Linux container cannot change
   them). Rotate any credential that has ever been readable by another user.
4. Restart the container (or run `bash .devcontainer/scripts/post-start.sh`).
   Configs regenerate on every start, so new keys are picked up immediately.
   The first V2 rebuild intentionally starts with a fresh OpenCode data
   volume; re-authenticate if you used V1 sessions. The old V1 volume remains
   available for an explicit backup or rollback.

Prefer not to keep secrets in the repo folder? Set the same variable names in
your host environment or `~/.bashrc` instead — `.env.local` simply wins when
present.

## Design

- **No sudo for normal setup.** `NPM_CONFIG_PREFIX` points at
  `/home/vscode/.npm-global` (a user-owned volume), so `npm install -g` works
  as the `vscode` user.
- **Fast starts.** Heavy installs run once per build (`onCreateCommand`); the
  CLI installs run in parallel. `postStartCommand` repairs an incomplete core
  install, syncs configs in the foreground, and refreshes CLIs in the
  background at most once per day. If an OpenCode service is already running,
  config changes trigger a bounded wait for its location-scoped MCP registry;
  an exhausted wait (or a failed restart) fails the lifecycle and leaves a
  pending marker so the next start retries instead of silently degrading.
  Set `DEVCONTAINER_CLI_REFRESH=always` in `.env.local` to force refreshes on
  every start. `waitFor` holds the editor's first terminal until the sync
  completes.
- **Fast, reproducible image builds.** SDK 8, Node 22, and uv come from
  pinned multi-stage `COPY --from` image stages (registry-cached by digest,
  no tarball downloads, no version scraping). PowerShell is installed from a
  pinned, checksum-verified official archive because the current ARM64 .NET
  base image contains a mismatched nested payload. CI adds a `type=gha`
  build layer cache so PR rebuilds reuse unchanged layers.
- **Durable across rebuilds.** Named volumes persist npm globals, npm cache,
  the uv cache, `.nuget/packages`, HTTPS dev certs, and each CLI's auth/session
  state (`.claude`, `.claude-zai`, `.claude-openrouter`, `.codex`, `.copilot`,
  `.local/share/opencode`, `.config/gh`). OpenCode uses a versioned
  `opencode-v2-data` volume so a V1 database is never handed to the V2 runtime;
  the old `signal-fish-client-dotnet-opencode-data` volume is left untouched
  for backup/rollback. Every mountpoint is
  pre-created in the image as `vscode`-owned, so empty volumes inherit that
  ownership and the runtime never creates root-owned parents (a past `EACCES`
  under `~/.cache`). Everything else (configs, env) is regenerated idempotently
  from the repo on every start — config as code, nothing to drift.
- **Secret hygiene.** Managed entries are pruned from every harness when their
  credential disappears from `.env.local`; values with control characters
  (e.g. CRLF paste artifacts) are rejected before they can reach a config.
  When credentials or the OpenCode config change, `post-start.sh` restarts an
  already-running OpenCode service so `{env:...}` substitutions see the new
  process environment; it does not disturb a stopped service.
- **One credential loader.** `.devcontainer/scripts/lib/env.sh` resolves
  aliases (e.g. `GH_TOKEN` → `GITHUB_PERSONAL_ACCESS_TOKEN`), accepts only the
  documented credential/control names from `.env` files, is silent on stdout,
  and is reused by the CLIs and by `.vscode/mcp.json` (via `shellCommand`
  inputs), so Z.AI servers work in VS Code from the same `.env.local`.
  Source precedence is explicit: a later source that mentions a family with a
  value wins; a later source with only blank mentions leaves the previous
  value; a later source whose aliases disagree or whose value is malformed
  clears the credential instead of silently keeping an older one.
- **Effective tool config paths.** `CODEX_HOME` selects the Codex root for
  every managed writer (MCP block, provider profiles, model catalog); the
  OpenCode writer follows `OPENCODE_CONFIG_DIR`, `OPENCODE_CONFIG`, and
  `XDG_CONFIG_HOME` before the default `~/.config/opencode`, and refuses an
  inline `OPENCODE_CONFIG_CONTENT` that overrides managed MCP servers. All
  managed writers share one lock, so a failed sync rolls back atomically and
  can never erase a concurrent writer's update; symlinked config paths and
  symlinked parent directories are refused rather than followed.
- **Real handshake testing.** The self-test doesn't just grep configs: it
  spawns the configured `zai-mcp-server` command and performs a real MCP
  `initialize` + `tools/list` handshake (`.devcontainer/scripts/lib/mcp-probe.mjs`).

## Verifying

Run the built-in suite inside the container:

```bash
bash .devcontainer/scripts/self-test.sh
```

For a quick interactive smoke check:

```bash
opencode --version                 # opencode v2.x
opencode debug config              # confirms the global config source
opencode mcp list                  # shows MCP connection/auth state
# On a cold service boot, repeat once if the first list races MCP registration.
```

`opencode debug config` lists the source documents; `opencode mcp list` is the
runtime check. The V2 service initializes location-scoped MCP connections
asynchronously, so a first-list result of `No MCP servers configured` can be a
startup race even when the generated document is correct. The lifecycle
retries this check after restarting an already-running service.

It asserts: non-root user, sudo-free global npm install, all seven CLIs plus
`zai-mcp-server` on PATH, .NET 8 + 10 SDKs, an actual OpenCode v2 binary and
native V2 config, correct MCP entries for every harness (claude, codex,
copilot, opencode, nanocoder, gemini, cursor, VS Code), a real MCP probe of
the vision server (full handshake with a valid key, or an explicit
"key-gated" result with a test key), ZHIPU base-URL switching, pruning of
entries whose credential vanished, alias resolution and per-source credential
semantics (later overrides win, malformed or competing aliases clear the
credential), effective OpenCode/Codex config path overrides (`XDG_CONFIG_HOME`,
`OPENCODE_CONFIG`, `OPENCODE_CONFIG_DIR`, `CODEX_HOME`), JSONC/user-setting
preservation, fail-closed/transactional config writes with concurrent-writer
serialization, env allowlisting, atomic CRLF-safe Codex TOML handling with
symlink refusal, service-restart failure propagation, architecture checks, and
byte-identical configs after a double sync. It backs up `.env.local` and
`.env`, installs the AI-backend launchers into a disposable bin, and is
hermetic against ambient credentials that editor exec/terminal wrappers
legitimately carry.

## Troubleshooting

- **`opencode mcp list` says `No MCP servers configured` immediately after a
  restart** — compare `opencode debug config`; if the managed `mcp.servers`
  entries are present, wait a moment and run `opencode mcp list` again. V2
  registers location-scoped servers asynchronously on a cold service. If the
  entries are absent, inspect the lifecycle error and re-run `post-start.sh`.
- **Config written to an unexpected OpenCode file** — the writer follows
  `OPENCODE_CONFIG_DIR`, `OPENCODE_CONFIG`, and `XDG_CONFIG_HOME` in that
  order before `~/.config/opencode`. Unset those overrides (or point them at
  the file you want managed) and re-run `post-start.sh`. An inline
  `OPENCODE_CONFIG_CONTENT` redefining a managed MCP server is rejected
  because the lifecycle cannot reconcile an inline layer.
- **MCP server shows 401** — the matching key in `.env.local` is missing,
  expired, or has no quota. Re-run `post-start.sh` after fixing it; if an
  OpenCode service was already running, the script restarts it so the new
  environment is visible.
- **`github` server in VS Code asks for sign-in** — expected once; it uses
  your VS Code GitHub account (OAuth), not the PAT.- **npm warn about running as root** — you are not in the devcontainer; use
  the Dev Containers: Reopen in Container flow.
- **CLI missing or OpenCode is not v2** — see
  `~/.cache/signal-fish-devcontainer/refresh.log`, then run
  `bash .devcontainer/scripts/post-create.sh`. `post-start.sh` also repairs a
  missing core CLI automatically on the next start.
- **`EACCES: permission denied, mkdir ~/.cache/...` (any tool failing under
  `$HOME`)** — a volume mountpoint created root-owned (pre-fix container, or a
  hand-added mount). Run `bash .devcontainer/scripts/post-start.sh` (its chown
  guard repairs the known paths) or rebuild; the Dockerfile pre-creates every
  mountpoint as `vscode`, so fresh builds cannot hit this. The self-test's
  "mountpoint user-writable" checks guard against regressions.
- **`zai-vision` fails to start** — two known causes: the `zai-mcp-server`
  global binary is missing (run `bash .devcontainer/scripts/post-create.sh`),
  or the server's startup API-key validation rejected your `Z_AI_API_KEY`
  (it must be a real key for your selected `Z_AI_MODE`); fix the key in
  `.env.local` and re-run `post-start.sh`.
- **Credentials in every terminal** — `post-start.sh` persists the resolved
  variables to `~/.container-env.sh` and sources it from your shell rc, so
  `gh`, `claude`, `codex`, etc. are authenticated without manual sourcing.
