# SignalFish Dev Container

One container, five terminal agent CLIs, and MCP servers pre-wired into every
agentic frontend this repo supports — reproducible after a full rebuild or a
fresh clone.

## What you get

| Tool | Provided by | Notes |
| --- | --- | --- |
| .NET SDK 10 + 8 | image + Dockerfile | same majors as the CI matrix in `.github/workflows/dotnet.yml` |
| Node 22 | Dockerfile (official `node:22-bookworm` stage) | required by the agent CLIs; no nvm (it conflicts with the sudo-free npm prefix) |
| uv / uvx | Dockerfile (official image stage) | runs the git MCP server (`uvx mcp-server-git`); Python resolves lazily on first use |
| GitHub CLI (`gh`) | `github-cli` feature | authed automatically via `GH_TOKEN` |
| PowerShell (`pwsh`) | included in the base image | runs the repo automation scripts |

| Agentic harness | Install | MCP config written to |
| --- | --- | --- |
| Claude Code (`claude`) | npm | `~/.claude.json` |
| Codex (`codex`) | npm | `~/.codex/config.toml` |
| GitHub Copilot CLI (`copilot`) | npm | `~/.copilot/mcp-config.json` |
| OpenCode (`opencode`) | npm | `~/.config/opencode/opencode.json` |
| Nanocoder (`nanocoder`) | npm | `~/.config/nanocoder/.mcp.json` |
| Gemini CLI (`gemini`) | npm | `~/.gemini/settings.json` |
| Cursor (config only; GUI runs on the host) | — | `~/.cursor/mcp.json` |
| VS Code + Copilot Chat | extensions | `.vscode/mcp.json` (committed) |

Every harness receives the same eight MCP servers (the committed VS Code
workspace config carries seven — it omits `context7` because an empty-Bearer
header cannot be expressed conditionally there):

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
| `claude-zai` | Z.ai GLM Coding Plan (`api.z.ai/api/anthropic`) | `ZAI_API_KEY` / `Z_AI_API_KEY` |
| `claude-openrouter` | OpenRouter Anthropic-compatible endpoint | `OPENROUTER_API_KEY` |
| `codex-zai` | Z.ai Responses endpoint (profile `zai`, glm-5.3) | `ZAI_API_KEY` / `Z_AI_API_KEY` |
| `codex-openrouter` | OpenRouter Responses endpoint (profile `openrouter`) | `OPENROUTER_API_KEY` |

- **Model switching**: `claude-zai` reads `CLAUDE_ZAI_{SONNET,OPUS,HAIKU}_MODEL`;
  `claude-openrouter` reads `CLAUDE_OPENROUTER_{FABLE,OPUS,SONNET,HAIKU}_MODEL`
  (defaults: `~anthropic/claude-{fable,opus,sonnet}-latest[1m]`), and exposes the
  gateway `/model` picker via `CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY=1`.
  Codex models: `CODEX_ZAI_MODEL` (default `glm-5.3`),
  `CODEX_ZAI_REASONING_EFFORT` (low/high/max), `CODEX_OPENROUTER_MODEL`
  (default `openrouter/auto`).
- **Secret hygiene**: keys resolve from the environment first, then the
  repository `.env.local`; values never appear in argv, in
  `~/.codex/config.toml` (providers use `env_key`/command-based auth), or in
  any written file. Each Claude backend gets an isolated
  `CLAUDE_CONFIG_DIR` (`~/.claude-zai`, `~/.claude-openrouter`, both volumes)
  so cached logins and provider routing can never cross-contaminate.
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
3. Restart the container (or run `bash .devcontainer/scripts/post-start.sh`).
   Configs regenerate on every start, so new keys are picked up immediately.

Prefer not to keep secrets in the repo folder? Set the same variable names in
your host environment or `~/.bashrc` instead — `.env.local` simply wins when
present.

## Design

- **No sudo, ever.** `NPM_CONFIG_PREFIX` points at
  `/home/vscode/.npm-global` (user-owned named volume), so `npm install -g`
  works as the `vscode` user.
- **Fast starts.** Heavy installs run once per build (`onCreateCommand`); the
  CLI installs run in parallel; `postStartCommand` only syncs configs (about a
  second) and refreshes CLIs in the background at most once per day. Set
  `DEVCONTAINER_CLI_REFRESH=always` in `.env.local` to force refreshes on
  every start.
- **Fast, reproducible image builds.** SDK 8, Node 22, and uv come from
  pinned multi-stage `COPY --from` image stages (registry-cached by digest,
  no tarball downloads, no version scraping). Measured on a clean local
  build: ~29s repeat / ~88s first pull vs ~80s for every clean build of the
  old curl-based approach; warm rebuilds are ~5s. CI adds a `type=gha`
  build layer cache so PR rebuilds reuse unchanged layers.
- **Durable across rebuilds.** Named volumes persist npm globals, npm cache,
  the uv cache, `.nuget/packages`, HTTPS dev certs, and each CLI's auth/session
  state (`.claude`, `.claude-zai`, `.claude-openrouter`, `.codex`, `.copilot`,
  `.local/share/opencode`, `.config/gh`). Every mountpoint is pre-created in
  the image as `vscode`-owned, so empty volumes inherit that ownership and the
  runtime never creates root-owned parents (a past `EACCES` under `~/.cache`).
  Everything else (configs, env) is regenerated idempotently from the repo on
  every start — config as code, nothing to drift.
- **Secret hygiene.** Managed entries are pruned from every harness when their
  credential disappears from `.env.local`; values with control characters
  (e.g. CRLF paste artifacts) are rejected before they can reach a config.
- **One credential loader.** `.devcontainer/scripts/lib/env.sh` resolves
  aliases (e.g. `GH_TOKEN` → `GITHUB_PERSONAL_ACCESS_TOKEN`), is silent on
  stdout, and is reused by the CLIs and by `.vscode/mcp.json` (via
  `shellCommand` inputs), so Z.AI servers work in VS Code from the same
  `.env.local`.
- **Real handshake testing.** The self-test doesn't just grep configs: it
  spawns the configured `zai-mcp-server` command and performs a real MCP
  `initialize` + `tools/list` handshake (`.devcontainer/scripts/lib/mcp-probe.mjs`).

## Verifying

Run the built-in suite inside the container:

```bash
bash .devcontainer/scripts/self-test.sh
```

It asserts: non-root user, sudo-free global npm install, all seven CLIs plus
`zai-mcp-server` on PATH, .NET 8 + 10 SDKs, correct MCP entries for every
harness (claude, codex, copilot, opencode, nanocoder, gemini, cursor,
VS Code), a real MCP probe of the vision server (full handshake with a valid
key, or an explicit "key-gated" result with a test key), ZHIPU base-URL
switching, pruning of entries whose credential vanished, alias resolution,
and byte-identical configs after a double sync. It backs up your `.env.local`
while running (throwaway credentials only) and restores it afterwards, and
is hermetic against ambient credentials that editor exec/terminal wrappers
legitimately carry.

## Troubleshooting

- **MCP server shows 401** — the matching key in `.env.local` is missing,
  expired, or has no quota. Re-run `post-start.sh` after fixing it.
- **`github` server in VS Code asks for sign-in** — expected once; it uses
  your VS Code GitHub account (OAuth), not the PAT.
- **npm warn about running as root** — you are not in the devcontainer; use
  the Dev Containers: Reopen in Container flow.
- **CLI missing** — see `~/.cache/signal-fish-devcontainer/refresh.log`, then
  run `bash .devcontainer/scripts/post-create.sh`.
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
  `gh`, `claude`, `codex`, etc. are authenticated in interactive shells and
  in editor-spawned terminals without any manual sourcing.
