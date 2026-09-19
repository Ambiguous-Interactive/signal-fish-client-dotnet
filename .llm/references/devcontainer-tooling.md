# Devcontainer & AI-Backend Tooling Facts

Verified, session-backed facts about the devcontainer toolchain. Each item
cites its evidence; do not add unevidenced trivia here.

## Node / os.homedir() ignores HOME on win32

`os.homedir()` in Node on Windows reads `USERPROFILE` (then the registry), not
`HOME`. Overriding only `HOME` to sandbox `os.homedir()`-based writers silently
targets the real user profile.

- Evidence (2026-09-19): a test run set `$env:HOME` to a temp dir before
  `write-mcp-configs.mjs`; all six configs were written to the real profile
  (file creation timestamps all equal to the run time; fake credentials found
  in the real `~/.claude.json`).
- Rule: when sandboxing home-relative writers on Windows, override
  `USERPROFILE` too — or run inside a Linux container where `HOME` is
  authoritative. The devcontainer self-test runs on Linux and is unaffected.

## BuildKit forbids ARG expansion in COPY --from

`COPY --from=${MY_ARG}` fails with: "variable expansion is not supported for
--from, define a new stage with FROM using ARG from global scope as a
workaround".

- Evidence (2026-09-19): `.devcontainer/Dockerfile` build failure.
- Fix: declare one `FROM ${ARG} AS alias` per source image (ARGs before the
  first FROM are allowed in FROM), then `COPY --from=alias`.

## Node official images have no Ubuntu (noble) variants

`node:22-noble` does not exist on Docker Hub; official Node images ship Debian
(`bookworm`, `bullseye`, `slim`) and Alpine variants.

- Evidence (2026-09-19): `docker build` failed resolving
  `docker.io/library/node:22-noble`.
- Note: bookworm-built Node binaries run on noble (glibc forward
  compatibility: older glibc build → newer glibc host is safe).

## Multi-stage COPY vs curl-installers: measured build times

Replacing per-build downloads (dotnet-install.sh ~130MB, node tarball ~25MB)
with `COPY --from` pinned image stages (SDK 8, node:22-bookworm, uv), measured
locally with BuildKit (arm64):

| Scenario | curl-installers | multi-stage COPY |
| --- | --- | --- |
| Clean build, sources not local | 79.9s | 88.4s (one-time pull) |
| Clean build, source images local | ~80s (downloads repeat) | 29.3s |
| Warm rebuild (layer cache) | — | 4.7s |

Takeaway: `RUN curl` re-downloads on every `--no-cache` build; `COPY --from`
stages stay cached in the local image store. CI additionally passes
`cacheFrom`/`cacheTo: type=gha` to `devcontainers/ci@v0.3` (inputs verified in
its `action.yml`), so PR rebuilds reuse layers across runs.

## Devcontainer lifecycle verification must stay in one container

Each `docker run` is a fresh filesystem: anything an earlier `docker run`
installed (npm-global CLIs, launchers, generated configs) is invisible to the
next. CI (`devcontainers/ci`) runs `postCreateCommand` and `runCmd` in one
container; local replication must do the same, e.g.

```bash
docker run --rm -u vscode -w /workspaces/repo -v "$PWD:/workspaces/repo" IMG \
  bash -lc 'bash .devcontainer/scripts/post-create.sh &&
            bash .devcontainer/scripts/post-start.sh &&
            bash .devcontainer/scripts/self-test.sh'
```

- Evidence (2026-09-19): splitting post-create and self-test across two
  `docker run` invocations produced 49 false failures (CLIs, launchers, and
  managed config "missing"); the same sequence in one container passed 114/115
  (the remaining `gh` check fails only under plain `docker build`, which does
  not apply devcontainer features — CI does).

## Named volumes at missing HOME targets create root-owned paths

Mounting a named volume at `~/a/b` when `~/a` is absent from the image: the
container runtime creates the missing mountpoint ancestors as `root:root
0755`, and an empty volume whose target is absent from the image initializes
`root:root` too. The remote user is then locked out of the whole subtree
(e.g. all of `~/.cache`), so any tool writing there fails with EACCES — not
just the tool the volume was added for.

- Evidence (2026-09-19): the `~/.cache/uv` volume mount made the runtime
  create `~/.cache` root-owned; `opencode --yolo` died with `EACCES: mkdir
  /home/vscode/.cache/opencode`. Volume census: `uv-cache`,
  `claude-zai-home`, `claude-openrouter-home` were root:root; npm/gh/opencode
  volumes were vscode-owned only because `devcontainer_fix_ownership`
  happened to list them. Clean-room RED: 5/5 `mkdir`/`touch` EACCES as
  vscode with fresh volumes; GREEN after the fix: all 12 volumes
  vscode-owned, lifecycle self-test 121/121.
- Fix: pre-create every `devcontainer.json` mount target in the Dockerfile
  with `install -d -o vscode -g vscode` (parents BEFORE children — `install
  -d -o` applies ownership only to the final path component, a bug the new
  build-time assertions caught) plus a `stat -c %U` assertion per subtree.
  Empty volumes then inherit vscode ownership from the image target
  (copy-on-first-use) and the runtime creates nothing.
- Defense-in-depth: keep the `devcontainer_fix_ownership` chown guard for
  volumes created before the fix, and the self-test's writability checks —
  per-path plus a generic "every mountpoint under `$HOME` is user-writable"
  scan of `/proc/mounts`.
- Testing note: `opencode --version` succeeds without its cache dir; --version
  smokes do not exercise state-writing paths. Assert directory writability
  (or run a state-creating command) instead.

## AI backend launch patterns (verified against vendors)

- Claude Code on Z.ai: `ANTHROPIC_BASE_URL=https://api.z.ai/api/anthropic`,
  key in `ANTHROPIC_AUTH_TOKEN`, model aliases mapped to `glm-5.3[1m]` /
  `glm-5.3-flash[1m]`. Proven in qora-redux and mirrored in
  `.devcontainer/ai-backends.sh`.
- Claude Code on OpenRouter: `ANTHROPIC_BASE_URL=https://openrouter.ai/api`,
  key in `ANTHROPIC_AUTH_TOKEN`, and **`ANTHROPIC_API_KEY` must be exported
  empty** to prevent direct-Anthropic fallback
  (source: openrouter.ai/docs/cookbook/coding-agents/claude-code-integration).
- Codex on OpenRouter: provider `base_url=https://openrouter.ai/api/v1` with
  `wire_api = "responses"` (`wire_api = "chat"` fails on current Codex);
  command-based `[model_providers.X.auth]` (not `env_key`) is what makes Codex
  fetch the OpenRouter model catalog
  (source: openrouter.ai/docs/cookbook/coding-agents/codex-cli).
- Codex on Z.ai: Responses endpoint `https://api.z.ai/api/v1` plus a
  `model_catalog_json` file for glm model metadata (contract proven in
  qora-redux's `tests/ci/test-ai-backends.sh`).
