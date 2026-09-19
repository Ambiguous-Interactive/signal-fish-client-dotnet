#!/usr/bin/env bash
# Sync MCP server configuration into every supported agentic harness.
#
# JSON-based harnesses (claude, copilot, opencode, nanocoder, gemini, cursor)
# via write-mcp-configs.mjs; codex via a managed TOML block in
# ~/.codex/config.toml (textual replace between managed markers, preserving
# everything else). VS Code/Copilot Chat is configured by the committed
# .vscode/mcp.json.
#
# Requires: lib/env.sh sourced first (provides the credential variables).

DEVCONTAINER_MANAGED_BEGIN="# >>> signal-fish-devcontainer managed MCP (regenerated on start; do not edit) <<<"
DEVCONTAINER_MANAGED_END="# <<< signal-fish-devcontainer managed MCP <<<"

devcontainer_write_codex_mcp() {
  local dir="${HOME}/.codex"
  local file="${dir}/config.toml"
  mkdir -p "$dir"
  [ -f "$file" ] || : > "$file"

  # Z_AI_MODE selects the platform base URL (mirrors write-mcp-configs.mjs).
  local zai_base="https://api.z.ai/api/mcp"
  [ "${Z_AI_MODE:-ZAI}" = "ZHIPU" ] && zai_base="https://open.bigmodel.cn/api/mcp"

  # Build the block with REAL newlines (array + printf), never "\n" inside
  # double quotes, which bash keeps as literal backslash-n.
  local lines=()
  lines+=(
    "${DEVCONTAINER_MANAGED_BEGIN}"
    ""
    "# GitHub (remote, PAT via environment)"
    "[mcp_servers.github]"
    "url = \"https://api.githubcopilot.com/mcp/\""
    "bearer_token_env_var = \"GITHUB_PERSONAL_ACCESS_TOKEN\""
    ""
    "# Microsoft Learn docs (remote, keyless)"
    "[mcp_servers.microsoft-docs]"
    "url = \"https://learn.microsoft.com/api/mcp\""
    ""
    "# Context7 library docs (remote; keyless works, Bearer when key is set)"
    "[mcp_servers.context7]"
    "url = \"https://mcp.context7.com/mcp\""
  )
  if [ -n "${CONTEXT7_API_KEY:-}" ]; then
    lines+=("bearer_token_env_var = \"CONTEXT7_API_KEY\"")
  fi
  lines+=(
    ""
    "# Local git operations (uv tool runner; entry only managed when uvx exists)"
  )
  if command -v uvx >/dev/null 2>&1; then
    lines+=(
      "[mcp_servers.git]"
      "command = \"uvx\""
      "args = [\"mcp-server-git\"]"
    )
  fi
  if [ -n "${Z_AI_API_KEY:-}" ]; then
    lines+=(
      ""
      "# Z.AI vision/image understanding (local stdio, global npm bin)"
      "[mcp_servers.zai-vision]"
      "command = \"zai-mcp-server\""
      ""
      "[mcp_servers.zai-vision.env]"
      "Z_AI_API_KEY = \"${Z_AI_API_KEY}\""
      "Z_AI_MODE = \"${Z_AI_MODE}\""
      ""
      "# Z.AI remote servers (Bearer via environment)"
      "[mcp_servers.zai-web-search]"
      "url = \"${zai_base}/web_search_prime/mcp\""
      "bearer_token_env_var = \"Z_AI_API_KEY\""
      ""
      "[mcp_servers.zai-web-reader]"
      "url = \"${zai_base}/web_reader/mcp\""
      "bearer_token_env_var = \"Z_AI_API_KEY\""
      ""
      "[mcp_servers.zai-zread]"
      "url = \"${zai_base}/zread/mcp\""
      "bearer_token_env_var = \"Z_AI_API_KEY\""
    )
  fi
  lines+=("${DEVCONTAINER_MANAGED_END}")

  local block
  block="$(printf '%s\n' "${lines[@]}")"

  # Byte-stable across runs and self-healing. Line classes:
  #   exact real BEGIN/END markers  -> strip, tracking block state
  #   any other line containing the marker core -> legacy flattened-junk
  #   orphaned sections for our server names (from legacy bugs) -> strip
  # Then trim trailing blanks and append exactly one fresh block.
  local core="signal-fish-devcontainer managed MCP"
  local tmp
  tmp="$(mktemp)"
  awk -v core="$core" -v begin="$DEVCONTAINER_MANAGED_BEGIN" -v end="$DEVCONTAINER_MANAGED_END" '
    $0 == begin { inblock = 1; next }
    $0 == end   { inblock = 0; next }
    index($0, core) { next }
    inblock { next }
    /^\[mcp_servers\.(github|zai-vision|zai-web-search|zai-web-reader|zai-zread|microsoft-docs|context7|git)(\.[a-z]+)?\]$/ { skip = 1; next }
    /^\[/ { skip = 0 }
    !skip { print }
  ' "$file" > "$tmp"
  printf '%s' "$(cat "$tmp")" > "$file"
  rm -f "$tmp"
  [ -s "$file" ] && printf '\n\n' >> "$file"
  printf '%s\n' "$block" >> "$file"
  chmod 600 "$file" 2>/dev/null || true
}

devcontainer_sync_mcp_configs() {
  local script_dir="${1:?script dir required}"
  node "$script_dir/lib/write-mcp-configs.mjs"
  devcontainer_write_codex_mcp
}
