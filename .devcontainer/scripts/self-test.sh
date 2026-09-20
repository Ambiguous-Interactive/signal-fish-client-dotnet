#!/usr/bin/env bash
# Self-test for the devcontainer setup. Run inside the container:
#   bash .devcontainer/scripts/self-test.sh
# Deterministic: backs up any real .env.local, injects throwaway credentials
# for the config assertions, and restores your file afterwards (EXIT trap).
set -uo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
cd "$repo_root"

pass=0
fail=0
FAILURES=()

check() { # check <description> <command...>
  local desc="$1"; shift
  if "$@" >/dev/null 2>&1; then
    pass=$((pass + 1))
    printf 'ok   - %s\n' "$desc"
  else
    fail=$((fail + 1))
    FAILURES+=("$desc")
    printf 'FAIL - %s\n' "$desc"
  fi
}

expect_contains() { # expect_contains <description> <file-or-literal:...> <needle>
  local desc="$1" haystack="$2" needle="$3" src
  if [[ "$haystack" == literal:* ]]; then src="${haystack#literal:}"; else src="$(cat "$haystack" 2>/dev/null || true)"; fi
  if [[ "$src" == *"$needle"* ]]; then
    pass=$((pass + 1)); printf 'ok   - %s\n' "$desc"
  else
    fail=$((fail + 1)); FAILURES+=("$desc"); printf 'FAIL - %s\n' "$desc"
  fi
}

json_assert() { # json_assert <description> <file> <node-expr-over-obj>
  local desc="$1" file="$2" expr="$3"
  if node -e '
    const fs = require("fs");
    // Tolerate JSONC (//) comments, e.g. in .vscode/mcp.json.
    const src = fs.readFileSync(process.argv[1], "utf8").replace(/^\s*\/\/.*$/gm, "");
    const obj = JSON.parse(src);
    process.exit(Boolean(eval(process.argv[2])) ? 0 : 1);
  ' "$file" "$expr" 2>/dev/null; then
    pass=$((pass + 1)); printf 'ok   - %s\n' "$desc"
  else
    fail=$((fail + 1)); FAILURES+=("$desc"); printf 'FAIL - %s\n' "$desc"
  fi
}

# ---------------------------------------------------------------------------
# Credential isolation: back up any real .env.local, inject throwaway values,
# restore on exit (also on interruption). Never prints file contents.
# ---------------------------------------------------------------------------
ENV_BACKUP="$repo_root/.env.local.selftest-backup"
env_state="untouched"
cleanup_env() {
  case "$env_state" in
    created)
      rm -f "$repo_root/.env.local"
      ;;
    backed-up)
      rm -f "$repo_root/.env.local"
      mv -f "$ENV_BACKUP" "$repo_root/.env.local"
      ;;
    *)
      return 0
      ;;
  esac
  env_state="untouched"
  # Converge configs back to the restored credentials.
  bash "$script_dir/post-start.sh" >/dev/null 2>&1 || true
}
trap cleanup_env EXIT

if [ -e "$ENV_BACKUP" ]; then
  echo "ABORT: $ENV_BACKUP exists (leftover from an interrupted run?)." >&2
  echo "Inspect it, then remove it and re-run this script." >&2
  exit 1
fi

echo "== environment =="
check "running as non-root user" test "$(id -u)" -ne 0
check "npm global prefix is user-writable" test -w "${NPM_CONFIG_PREFIX:-$HOME/.npm-global}"
check "npm cache directory is user-writable" test -w "$HOME/.npm"
# Volume-mount ownership regression (2026-09-19: runtime created ~/.cache
# root-owned for the ~/.cache/uv volume; opencode then failed with EACCES).
check "cache directory is user-writable" test -w "$HOME/.cache"
check "uv cache directory is user-writable" test -w "$HOME/.cache/uv"
check "opencode data directory is user-writable" test -w "$HOME/.local/share/opencode"
check "claude-zai config directory is user-writable" test -w "$HOME/.claude-zai"
check "claude-openrouter config directory is user-writable" test -w "$HOME/.claude-openrouter"
# Generic guard: whatever future volumes get mounted under $HOME, the runtime
# must never leave a mountpoint the remote user cannot write.
check "every mountpoint under HOME is user-writable" bash -c '
  prefix="/home/$(id -un)/"
  status=0
  while read -r _ mp _; do
    case "$mp" in
      "$prefix"*)
        if [ ! -w "$mp" ]; then
          printf "not user-writable: %s\n" "$mp" >&2
          status=1
        fi
        ;;
    esac
  done < /proc/mounts
  exit "$status"'
check "git available" command -v git
check "gh CLI available" command -v gh
check "pwsh available" command -v pwsh
check "node >= 22 available" bash -c '[[ $(node -p "process.versions.node.split(\".\")[0]") -ge 22 ]]'

echo "== no-sudo global npm install =="
check "npm install -g works without sudo" npm install -g --no-audit --no-fund sort-package-json@2
check "globally installed binary on PATH" bash -lc 'command -v sort-package-json'
check "cleanup: uninstall canary" npm uninstall -g sort-package-json

echo "== .NET toolchain (matches CI: 10.0.x + 8.0.x) =="
check "dotnet SDK 10 present" bash -c 'dotnet --list-sdks | grep -q "^10\."'
check "dotnet SDK 8 present" bash -c 'dotnet --list-sdks | grep -q "^8\."'

echo "== agentic CLIs =="
check "codex installed" bash -lc 'command -v codex'
check "opencode installed" bash -lc 'command -v opencode'
check "nanocoder installed" bash -lc 'command -v nanocoder'
check "claude installed" bash -lc 'command -v claude'
check "copilot installed" bash -lc 'command -v copilot'
check "gemini installed" bash -lc 'command -v gemini'
check "zai-mcp-server installed" bash -lc 'command -v zai-mcp-server'
check "codex --version" bash -lc 'codex --version'
check "opencode --version" bash -lc 'opencode --version'
check "nanocoder --version" bash -lc 'nanocoder --version'
check "claude --version" bash -lc 'claude --version'
check "copilot --version" bash -lc 'copilot --version'
check "gemini --version" bash -lc 'gemini --version'

echo "== env loader (aliases + precedence + validation) =="
# unset-then-source, never prefix-assignment-only: bash makes prefix
# assignments to `source` temporary and RESTORES ambient values after the
# builtin returns (and devcontainer/VS Code exec wrappers legitimately carry
# real credentials in the ambient env), so isolation must remove the ambient
# names for the whole subshell.
check "alias: ZAI_API_KEY resolves to Z_AI_API_KEY" \
  bash -c 'unset Z_AI_API_KEY ZAI_API_KEY ZHIPU_API_KEY; export ZAI_API_KEY=alias-test; DEVCONTAINER_ENV_SKIP_FILES=1 source .devcontainer/scripts/lib/env.sh && [[ $Z_AI_API_KEY == alias-test ]]'
check "alias: GITHUB_PAT resolves to GITHUB_PERSONAL_ACCESS_TOKEN" \
  bash -c 'unset GITHUB_PERSONAL_ACCESS_TOKEN GITHUB_PAT GH_TOKEN GITHUB_TOKEN GITHUB_MCP_PAT; export GITHUB_PAT=pat-test; DEVCONTAINER_ENV_SKIP_FILES=1 source .devcontainer/scripts/lib/env.sh && [[ $GITHUB_PERSONAL_ACCESS_TOKEN == pat-test && $GH_TOKEN == pat-test ]]'
check "alias: GITHUB_MCP_PAT (sibling-repo name) resolves" \
  bash -c 'unset GITHUB_PERSONAL_ACCESS_TOKEN GITHUB_PAT GH_TOKEN GITHUB_TOKEN GITHUB_MCP_PAT; export GITHUB_MCP_PAT=pat-test; DEVCONTAINER_ENV_SKIP_FILES=1 source .devcontainer/scripts/lib/env.sh && [[ $GITHUB_PERSONAL_ACCESS_TOKEN == pat-test && $GH_TOKEN == pat-test ]]'
check "validation: control-character value is rejected" \
  bash -c 'unset GITHUB_PERSONAL_ACCESS_TOKEN GH_TOKEN GITHUB_PAT GITHUB_TOKEN GITHUB_MCP_PAT; export GITHUB_PERSONAL_ACCESS_TOKEN=$(printf "bad\rpat"); DEVCONTAINER_ENV_SKIP_FILES=1 source .devcontainer/scripts/lib/env.sh && [ -z "${GITHUB_PERSONAL_ACCESS_TOKEN:-}" ]'
check "validation: bogus Z_AI_MODE falls back to ZAI" \
  bash -c 'unset Z_AI_MODE; export Z_AI_MODE=NOPE; DEVCONTAINER_ENV_SKIP_FILES=1 source .devcontainer/scripts/lib/env.sh && [[ $Z_AI_MODE == ZAI ]]'
check "env.sh is stdout-silent" bash -c '[ -z "$(source .devcontainer/scripts/lib/env.sh)" ]'

echo "== MCP config sync (with injected throwaway credentials) =="
if [ -f "$repo_root/.env.local" ]; then
  env_state="backed-up"
  mv -f "$repo_root/.env.local" "$ENV_BACKUP"
else
  env_state="created"
fi
printf 'GITHUB_PERSONAL_ACCESS_TOKEN=self-test-gh-pat\nZ_AI_API_KEY=self-test-zai-key\n' > "$repo_root/.env.local"
bash "$script_dir/post-start.sh" >/dev/null 2>&1

json_assert "claude: github http server with PAT" "$HOME/.claude.json" \
  'obj.mcpServers.github.url === "https://api.githubcopilot.com/mcp/" && obj.mcpServers.github.headers.Authorization.startsWith("Bearer self-test-gh-pat")'
json_assert "claude: zai-vision stdio via global bin" "$HOME/.claude.json" \
  'obj.mcpServers["zai-vision"].command === "zai-mcp-server" && obj.mcpServers["zai-vision"].env.Z_AI_API_KEY === "self-test-zai-key" && obj.mcpServers["zai-vision"].env.Z_AI_MODE === "ZAI"'
json_assert "claude: z.ai web-search remote" "$HOME/.claude.json" \
  'obj.mcpServers["zai-web-search"].url === "https://api.z.ai/api/mcp/web_search_prime/mcp" && obj.mcpServers["zai-web-search"].headers.Authorization === "Bearer self-test-zai-key"'
json_assert "claude: zread remote" "$HOME/.claude.json" \
  'obj.mcpServers["zai-zread"].url === "https://api.z.ai/api/mcp/zread/mcp" && obj.mcpServers["zai-zread"].headers.Authorization === "Bearer self-test-zai-key"'
json_assert "claude: microsoft-docs keyless remote" "$HOME/.claude.json" \
  'obj.mcpServers["microsoft-docs"].url === "https://learn.microsoft.com/api/mcp"'
json_assert "claude: context7 remote (keyless without CONTEXT7_API_KEY)" "$HOME/.claude.json" \
  'obj.mcpServers["context7"].url === "https://mcp.context7.com/mcp"'
json_assert "claude: git stdio via uvx" "$HOME/.claude.json" \
  'obj.mcpServers["git"].command === "uvx" && obj.mcpServers["git"].args[0] === "mcp-server-git"'
json_assert "copilot: github-mcp-server replaces builtin" "$HOME/.copilot/mcp-config.json" \
  'obj.mcpServers["github-mcp-server"].url === "https://api.githubcopilot.com/mcp/"'
json_assert "copilot: web-reader remote" "$HOME/.copilot/mcp-config.json" \
  'obj.mcpServers["zai-web-reader"].url === "https://api.z.ai/api/mcp/web_reader/mcp" && obj.mcpServers["zai-web-reader"].headers.Authorization === "Bearer self-test-zai-key"'
json_assert "opencode: github remote with oauth disabled" "$HOME/.config/opencode/opencode.json" \
  'obj.mcp.github.type === "remote" && obj.mcp.github.oauth === false && obj.mcp.github.headers.Authorization.includes("{env:GITHUB_PERSONAL_ACCESS_TOKEN}")'
json_assert "opencode: zai-vision local via global bin" "$HOME/.config/opencode/opencode.json" \
  'obj.mcp["zai-vision"].command[0] === "zai-mcp-server"'
json_assert "opencode: zai remote auth via env ref (never a literal)" "$HOME/.config/opencode/opencode.json" \
  'obj.mcp["zai-web-search"].headers.Authorization.includes("{env:Z_AI_API_KEY}")'
json_assert "nanocoder: github http transport" "$HOME/.config/nanocoder/.mcp.json" \
  'obj.mcpServers.github.transport === "http" && obj.mcpServers.github.headers.Authorization.includes("${GITHUB_PERSONAL_ACCESS_TOKEN}")'
json_assert "nanocoder: web-search http" "$HOME/.config/nanocoder/.mcp.json" \
  'obj.mcpServers["zai-web-search"].url === "https://api.z.ai/api/mcp/web_search_prime/mcp" && obj.mcpServers["zai-web-search"].headers.Authorization.includes("${Z_AI_API_KEY}")'
json_assert "gemini: github httpUrl" "$HOME/.gemini/settings.json" \
  'obj.mcpServers.github.httpUrl === "https://api.githubcopilot.com/mcp/"'
json_assert "gemini: zai-vision stdio via global bin" "$HOME/.gemini/settings.json" \
  'obj.mcpServers["zai-vision"].command === "zai-mcp-server"'
json_assert "gemini: web-search httpUrl" "$HOME/.gemini/settings.json" \
  'obj.mcpServers["zai-web-search"].httpUrl === "https://api.z.ai/api/mcp/web_search_prime/mcp" && obj.mcpServers["zai-web-search"].headers.Authorization === "Bearer self-test-zai-key"'
json_assert "cursor: github remote" "$HOME/.cursor/mcp.json" \
  'obj.mcpServers.github.url === "https://api.githubcopilot.com/mcp/"'
json_assert "cursor: zai-vision via portable npx" "$HOME/.cursor/mcp.json" \
  'obj.mcpServers["zai-vision"].command === "npx" && obj.mcpServers["zai-vision"].args[1] === "@z_ai/mcp-server"'
json_assert "cursor: zai-zread remote carries the key" "$HOME/.cursor/mcp.json" \
  'obj.mcpServers["zai-zread"].headers.Authorization === "Bearer self-test-zai-key"'

# The vision server validates its API key at startup, so with throwaway test
# credentials a full handshake is impossible by design. Exit 0 = full MCP
# handshake; exit 3 = key-gated startup (binary launches and initializes;
# key validity is out of scope). Anything else is a real failure.
probe_rc=0
node "$script_dir/lib/mcp-probe.mjs" --env "Z_AI_API_KEY=self-test-zai-key" --env "Z_AI_MODE=ZAI" -- zai-mcp-server >/dev/null 2>&1 || probe_rc=$?
check "zai-vision command speaks MCP (handshake or key-gated startup)" \
  bash -c "test $probe_rc -eq 0 -o $probe_rc -eq 3"

echo "== Z_AI_MODE=ZHIPU switches the platform base URL (isolated HOME) =="
zhipu_home="$(mktemp -d)"
env -u Z_AI_MODE HOME="$zhipu_home" Z_AI_MODE=ZHIPU Z_AI_API_KEY=self-test-zai-key \
  GITHUB_PERSONAL_ACCESS_TOKEN=self-test-gh-pat \
  node "$script_dir/lib/write-mcp-configs.mjs" >/dev/null 2>&1
json_assert "zhipu: claude search URL uses bigmodel.cn" "$zhipu_home/.claude.json" \
  'obj.mcpServers["zai-web-search"].url === "https://open.bigmodel.cn/api/mcp/web_search_prime/mcp"'
json_assert "zhipu: gemini reader URL uses bigmodel.cn" "$zhipu_home/.gemini/settings.json" \
  'obj.mcpServers["zai-web-reader"].httpUrl === "https://open.bigmodel.cn/api/mcp/web_reader/mcp"'
rm -rf "$zhipu_home"

echo "== missing credentials prune managed entries (isolated HOME) =="
# env -u strips ambient credentials (devcontainer/VS Code exec wrappers carry
# them by design); without this the prune assertions would see inherited keys.
unset_gh=(-u GITHUB_PERSONAL_ACCESS_TOKEN -u GITHUB_PAT -u GH_TOKEN -u GITHUB_TOKEN -u GITHUB_MCP_PAT)
unset_zai=(-u Z_AI_API_KEY -u ZAI_API_KEY -u ZHIPU_API_KEY)
prune_home="$(mktemp -d)"
env "${unset_zai[@]}" "${unset_gh[@]}" HOME="$prune_home" Z_AI_API_KEY=k GITHUB_PERSONAL_ACCESS_TOKEN=p \
  node "$script_dir/lib/write-mcp-configs.mjs" >/dev/null 2>&1
env "${unset_gh[@]}" HOME="$prune_home" Z_AI_API_KEY=k \
  node "$script_dir/lib/write-mcp-configs.mjs" >/dev/null 2>&1
json_assert "prune: github removed when PAT missing" "$prune_home/.claude.json" \
  'obj.mcpServers.github === undefined'
json_assert "prune: zai entries kept when key present" "$prune_home/.claude.json" \
  'obj.mcpServers["zai-vision"].command === "zai-mcp-server"'
env "${unset_gh[@]}" "${unset_zai[@]}" HOME="$prune_home" \
  node "$script_dir/lib/write-mcp-configs.mjs" >/dev/null 2>&1
json_assert "prune: zai entries removed when key missing too" "$prune_home/.claude.json" \
  'obj.mcpServers["zai-vision"] === undefined'
json_assert "prune: keyless servers survive credential pruning" "$prune_home/.claude.json" \
  'obj.mcpServers["microsoft-docs"].url === "https://learn.microsoft.com/api/mcp" && obj.mcpServers["context7"].url === "https://mcp.context7.com/mcp"'
rm -rf "$prune_home"

echo "== uv tool runner (git MCP server) =="
check "uvx installed" bash -lc 'command -v uvx'
check "uv installed" bash -lc 'command -v uv'

echo "== codex (managed TOML block) =="
expect_contains "codex: managed github block" "$HOME/.codex/config.toml" '[mcp_servers.github]'
expect_contains "codex: PAT via bearer_token_env_var" "$HOME/.codex/config.toml" 'bearer_token_env_var = "GITHUB_PERSONAL_ACCESS_TOKEN"'
expect_contains "codex: zai-web-search url" "$HOME/.codex/config.toml" 'web_search_prime/mcp'
expect_contains "codex: zai-vision uses global bin" "$HOME/.codex/config.toml" 'command = "zai-mcp-server"'
expect_contains "codex: microsoft-docs keyless block" "$HOME/.codex/config.toml" '[mcp_servers.microsoft-docs]'
expect_contains "codex: context7 block" "$HOME/.codex/config.toml" '[mcp_servers.context7]'
expect_contains "codex: git block via uvx" "$HOME/.codex/config.toml" '[mcp_servers.git]'
expect_contains "codex: managed markers present" "$HOME/.codex/config.toml" 'signal-fish-devcontainer managed MCP'
check "codex: exactly one managed block, no legacy junk" \
  bash -c '[[ $(grep -cF "signal-fish-devcontainer managed MCP" "$HOME/.codex/config.toml") -eq 2 ]]'
check "codex: no orphaned duplicate tables" \
  bash -c '[[ $(grep -c "^\[mcp_servers\." "$HOME/.codex/config.toml") -le 9 ]]'
check "codex: no literal backslash-n junk lines" \
  bash -c '! grep -qF "\\n" "$HOME/.codex/config.toml"'
check "codex accepts the generated TOML" bash -lc 'codex mcp list'

echo "== isolated AI backend launchers (claude-zai, claude-openrouter, codex-zai, codex-openrouter) =="
check "ai-backends.sh syntax" bash -n "$script_dir/../ai-backends.sh"
for launcher in claude-zai claude-openrouter codex-zai codex-openrouter; do
  check "launcher installed: $launcher" bash -lc "command -v $launcher"
done
check "launchers resolve the installer script" bash -lc 'command -v claude-zai >/dev/null && grep -q "isolated AI backends" "$(readlink -f "$(command -v claude-zai)")"'
check "help documents all four launchers" bash -c 'bash .devcontainer/ai-backends.sh help | grep -q claude-openrouter'

check "codex: managed AI backend block present" \
  bash -c '[[ $(grep -cF "signal-fish-devcontainer managed AI backends" "$HOME/.codex/config.toml") -eq 2 ]]'
expect_contains "codex: zai provider uses env key" "$HOME/.codex/config.toml" 'env_key = "ZAI_API_KEY"'
expect_contains "codex: zai provider responses endpoint" "$HOME/.codex/config.toml" 'base_url = "https://api.z.ai/api/v1"'
expect_contains "codex: openrouter provider responses endpoint" "$HOME/.codex/config.toml" 'base_url = "https://openrouter.ai/api/v1"'
expect_contains "codex: openrouter command-based auth (model catalog)" "$HOME/.codex/config.toml" '[model_providers.openrouter.auth]'
expect_contains "codex: zai profile" "$HOME/.codex/config.toml" '[profiles.zai]'
expect_contains "codex: openrouter profile" "$HOME/.codex/config.toml" '[profiles.openrouter]'
expect_contains "codex: provider keys excluded from tool subprocesses" "$HOME/.codex/config.toml" 'filters = { ZAI_API_KEY = "exclude", Z_AI_API_KEY = "exclude", OPENROUTER_API_KEY = "exclude" }'
# Provider (model) keys must never persist in the config file; MCP server env
# values (zai-vision) are the one documented exception (chmod 600 file).
check "codex: openrouter key never persisted in config" \
  bash -c '! grep -Eq "self-test-or-key" "$HOME/.codex/config.toml"'
check "codex: provider auth stays environment-based" \
  bash -c '! grep -Eq "experimental_bearer_token|sk-or-" "$HOME/.codex/config.toml"'
check "codex: zai model catalog written" \
  bash -c 'test -f "$HOME/.codex/zai-models.json"'
json_assert "codex: zai model catalog is valid JSON for glm-5.3" "$HOME/.codex/zai-models.json" \
  'obj.models.length === 1 && obj.models[0].slug === "glm-5.3"'

# Stub-based launch tests: verify env wiring without real credentials.
stub_bin="$(mktemp -d)"
cat >"${stub_bin}/claude" <<'STUB'
#!/usr/bin/env bash
{
  printf 'auth_token=%s\n' "${ANTHROPIC_AUTH_TOKEN:+set}"
  printf 'api_key_len=%s\n' "${#ANTHROPIC_API_KEY}"
  printf 'base_url=%s\n' "${ANTHROPIC_BASE_URL-unset}"
  printf 'zai_key=%s\n' "${ZAI_API_KEY-unset}"
  printf 'zai_alias_key=%s\n' "${Z_AI_API_KEY-unset}"
  printf 'config_dir=%s\n' "${CLAUDE_CONFIG_DIR-unset}"
  printf 'sonnet=%s\n' "${ANTHROPIC_DEFAULT_SONNET_MODEL-unset}"
  printf 'haiku=%s\n' "${ANTHROPIC_DEFAULT_HAIKU_MODEL-unset}"
  printf 'gateway_discovery=%s\n' "${CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY-unset}"
  printf 'managed=%s\n' "${CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST-unset}"
} >"${STUB_LOG:?}"
STUB
cat >"${stub_bin}/codex" <<'STUB'
#!/usr/bin/env bash
{
  printf 'zai_key=%s\n' "${ZAI_API_KEY:+set}"
  printf 'or_key=%s\n' "${OPENROUTER_API_KEY:+set}"
  : >"${STUB_ARGS:?}"
  for a in "$@"; do printf '%s\n' "$a" >>"${STUB_ARGS}"; done
} >"${STUB_LOG:?}"
STUB
chmod 755 "${stub_bin}/claude" "${stub_bin}/codex"

stub_home="$(mktemp -d)"
stub_env="$(mktemp)"
printf 'ZAI_API_KEY=self-test-zai-key\nOPENROUTER_API_KEY=self-test-or-key\n' >"$stub_env"

claude_stub_log="${stub_home}/claude-zai.log"
PATH="${stub_bin}:${PATH}" HOME="${stub_home}" STUB_LOG="${claude_stub_log}" \
  AI_BACKENDS_ENV_LOCAL="${stub_env}" AI_BACKENDS_CONTAINER_MODE=yes \
  claude-zai --print hello 2>/dev/null || true
check "claude-zai: zai token via ANTHROPIC_AUTH_TOKEN" \
  bash -c 'grep -q "^auth_token=set$" "'"${claude_stub_log}"'"'
check "claude-zai: zai anthropic base url" \
  bash -c 'grep -q "^base_url=https://api.z.ai/api/anthropic$" "'"${claude_stub_log}"'"'
check "claude-zai: canonical zai keys scrubbed" \
  bash -c 'grep -q "^zai_key=unset$" "'"${claude_stub_log}"'" && grep -q "^zai_alias_key=unset$" "'"${claude_stub_log}"'"'
check "claude-zai: isolated config dir" \
  bash -c "grep -q '^config_dir=${stub_home}/.claude-zai\$' '${claude_stub_log}'"
check "claude-zai: glm model aliases mapped" \
  bash -c 'grep -q "^sonnet=glm-5.3\[1m\]$" "'"${claude_stub_log}"'" && grep -q "^haiku=glm-5.3-flash\[1m\]$" "'"${claude_stub_log}"'"'

claude_stub_log="${stub_home}/claude-or.log"
PATH="${stub_bin}:${PATH}" HOME="${stub_home}" STUB_LOG="${claude_stub_log}" \
  AI_BACKENDS_ENV_LOCAL="${stub_env}" AI_BACKENDS_CONTAINER_MODE=yes \
  claude-openrouter --print hello 2>/dev/null || true
check "claude-openrouter: openrouter token via ANTHROPIC_AUTH_TOKEN" \
  bash -c 'grep -q "^auth_token=set$" "'"${claude_stub_log}"'"'
check "claude-openrouter: openrouter anthropic-compatible base url" \
  bash -c 'grep -q "^base_url=https://openrouter.ai/api$" "'"${claude_stub_log}"'"'
check "claude-openrouter: ANTHROPIC_API_KEY explicitly empty" \
  bash -c 'grep -q "^api_key_len=0$" "'"${claude_stub_log}"'"'
check "claude-openrouter: isolated config dir" \
  bash -c "grep -q '^config_dir=${stub_home}/.claude-openrouter\$' '${claude_stub_log}'"
check "claude-openrouter: gateway model discovery enabled" \
  bash -c 'grep -q "^gateway_discovery=1$" "'"${claude_stub_log}"'"'

codex_stub_args="${stub_home}/codex-zai.args"
PATH="${stub_bin}:${PATH}" HOME="${stub_home}" STUB_LOG="${stub_home}/codex-zai.log" \
  STUB_ARGS="${codex_stub_args}" AI_BACKENDS_ENV_LOCAL="${stub_env}" \
  codex-zai exec hello 2>/dev/null || true
check "codex-zai: zai key exported to codex" \
  bash -c 'grep -q "^zai_key=set$" "'"${stub_home}/codex-zai.log"'"'
check "codex-zai: profile + model + catalog via argv" \
  bash -c 'grep -q "^--profile$" "'"${codex_stub_args}"'" && grep -q "^zai$" "'"${codex_stub_args}"'" && grep -q "^glm-5.3$" "'"${codex_stub_args}"'" && grep -q "model_catalog_json" "'"${codex_stub_args}"'"'
check "codex-zai: key never appears in argv" \
  bash -c '! grep -q "self-test-zai-key" "'"${codex_stub_args}"'"'

codex_stub_args="${stub_home}/codex-or.args"
PATH="${stub_bin}:${PATH}" HOME="${stub_home}" STUB_LOG="${stub_home}/codex-or.log" \
  STUB_ARGS="${codex_stub_args}" AI_BACKENDS_ENV_LOCAL="${stub_env}" \
  codex-openrouter exec hello 2>/dev/null || true
check "codex-openrouter: openrouter key exported to codex" \
  bash -c 'grep -q "^or_key=set$" "'"${stub_home}/codex-or.log"'"'
check "codex-openrouter: profile + provider via argv" \
  bash -c 'grep -q "^--profile$" "'"${codex_stub_args}"'" && grep -q "^openrouter$" "'"${codex_stub_args}"'" && grep -q "openrouter/auto" "'"${codex_stub_args}"'"'
check "codex-openrouter: key never appears in argv" \
  bash -c '! grep -q "self-test-or-key" "'"${codex_stub_args}"'"'

missing_key_out="${stub_home}/missing.log"
if env -u OPENROUTER_API_KEY AI_BACKENDS_ENV_LOCAL="${stub_home}/absent.env" \
  PATH="${stub_bin}:${PATH}" HOME="${stub_home}" \
  codex-openrouter --version >"${missing_key_out}" 2>&1; then
  check "codex-openrouter: refuses missing key" false
else
  check "codex-openrouter: refuses missing key" true
fi
check "codex-openrouter: missing key gets guidance" \
  bash -c 'grep -q "Set OPENROUTER_API_KEY" "'"${missing_key_out}"'"'

rm -rf "${stub_bin}" "${stub_home}" "${stub_env}"

echo "== idempotency (double sync produces byte-identical configs) =="
files=("$HOME/.claude.json" "$HOME/.copilot/mcp-config.json" "$HOME/.config/opencode/opencode.json" "$HOME/.config/nanocoder/.mcp.json" "$HOME/.gemini/settings.json" "$HOME/.cursor/mcp.json" "$HOME/.codex/config.toml")
before="$(sha256sum "${files[@]}" 2>/dev/null)"
bash "$script_dir/post-start.sh" >/dev/null 2>&1
after="$(sha256sum "${files[@]}" 2>/dev/null)"
check "second sync is a no-op" test "$before" = "$after"

echo "== restoring your .env.local and reconverging configs =="
cleanup_env

echo "== VS Code workspace MCP config =="
json_assert "vscode: valid JSON with github http server" ".vscode/mcp.json" \
  'obj.servers.github.url === "https://api.githubcopilot.com/mcp/"'
json_assert "vscode: z.ai servers present" ".vscode/mcp.json" \
  'obj.servers["zai-web-search"] && obj.servers["zai-web-reader"] && obj.servers["zai-zread"] && obj.servers["zai-vision"]'
json_assert "vscode: z.ai servers use mode-aware base URL" ".vscode/mcp.json" \
  'obj.servers["zai-web-search"].url === "${input:zai-base}/web_search_prime/mcp"'
json_assert "vscode: microsoft-docs keyless server" ".vscode/mcp.json" \
  'obj.servers["microsoft-docs"].url === "https://learn.microsoft.com/api/mcp"'
json_assert "vscode: git server via uvx" ".vscode/mcp.json" \
  'obj.servers["git"].args[1].includes("uvx")'
json_assert "vscode: zai api key input reads .env.local via env.sh" ".vscode/mcp.json" \
  'obj.inputs.some(i => i.id === "zai-api-key" && i.command === "shellCommand")'
json_assert "vscode: zai base/mode inputs defined" ".vscode/mcp.json" \
  'obj.inputs.some(i => i.id === "zai-base") && obj.inputs.some(i => i.id === "zai-mode")'

echo
echo "passed: $pass  failed: $fail"
if [ "$fail" -gt 0 ]; then
  printf 'failed checks:\n'
  for f in "${FAILURES[@]}"; do printf '  - %s\n' "$f"; done
  exit 1
fi
echo "ALL CHECKS PASSED"
