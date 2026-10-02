#!/usr/bin/env bash
# Sync MCP server configuration into every supported agentic harness.
#
# JSON-based harnesses (claude, copilot, opencode, nanocoder, gemini, cursor)
# via write-mcp-configs.mjs; codex via a managed TOML block in the effective
# CODEX_HOME (textual replace between managed markers, preserving everything
# else). VS Code/Copilot Chat is configured by the committed .vscode/mcp.json.
#
# Requires: lib/env.sh sourced first (provides credential/path helpers).
# All managed writers share one config lock (paths.sh) so a failed
# transaction can never roll back a concurrent writer's update.

# shellcheck source=paths.sh
source "$(dirname "${BASH_SOURCE[0]}")/paths.sh"

DEVCONTAINER_MANAGED_BEGIN="# >>> signal-fish-devcontainer managed MCP (regenerated on start; do not edit) <<<"
DEVCONTAINER_MANAGED_END="# <<< signal-fish-devcontainer managed MCP <<<"

devcontainer_toml_escape() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  value="${value//$'\n'/\\n}"
  value="${value//$'\r'/\\r}"
  value="${value//$'\t'/\\t}"
  printf '%s' "$value"
}

devcontainer_trim_trailing_blank_lines() {
  local file="$1" trimmed
  trimmed="$(mktemp "${file}.trim.XXXXXX")"
  if ! awk '
    { lines[NR] = $0 }
    END {
      last = 0
      for (i = 1; i <= NR; i++) if (lines[i] !~ /^[[:space:]]*$/) last = i
      for (i = 1; i <= last; i++) print lines[i]
    }
  ' "$file" > "$trimmed"; then
    rm -f "$trimmed"
    return 1
  fi
  if ! mv -f "$trimmed" "$file"; then
    rm -f "$trimmed"
    return 1
  fi
}

_devcontainer_write_codex_mcp_locked() {
  local dir file
  dir="$(devcontainer_codex_home_dir)"
  file="${dir}/config.toml"
  devcontainer_assert_no_symlink_path "$dir" || return 1
  if [ -L "$dir" ] || [ -L "$file" ]; then
    printf 'ERROR: refusing to manage symlinked Codex config path: %s\n' "$file" >&2
    return 1
  fi
  if [ -e "$file" ] && [ ! -f "$file" ]; then
    printf 'ERROR: Codex config path is not a regular file: %s\n' "$file" >&2
    return 1
  fi
  mkdir -p "$dir"
  devcontainer_assert_no_symlink_path "$dir" || return 1

  # Z_AI_MODE selects the platform base URL (mirrors write-mcp-configs.mjs).
  local zai_mode="${Z_AI_MODE:-ZAI}"
  case "$zai_mode" in
    ZAI | ZHIPU) ;;
    *) zai_mode=ZAI ;;
  esac
  local zai_base="https://api.z.ai/api/mcp"
  [ "$zai_mode" = "ZHIPU" ] && zai_base="https://open.bigmodel.cn/api/mcp"

  # Build the block with REAL newlines (array + printf), never "\\n" inside
  # double quotes, which bash keeps as literal backslash-n. Escape credentials
  # before putting them in TOML basic strings; env.sh rejects controls, but the
  # escaper also keeps this function safe when called directly by a test.
  local zai_key
  zai_key="$(devcontainer_toml_escape "${Z_AI_API_KEY:-}")"
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
      "Z_AI_API_KEY = \"${zai_key}\""
      "Z_AI_MODE = \"${zai_mode}\""
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

  # Byte-stable across runs and self-healing. Normalize CRLF while matching
  # marker lines, then emit LF-only TOML; this prevents a CRLF copy of a managed
  # block from surviving beside the newly generated block (which would produce
  # duplicate TOML tables). The output is assembled beside the destination and
  # renamed, so interruption cannot leave a truncated Codex config or follow a
  # symlink.
  local core="signal-fish-devcontainer managed MCP"
  local input="$file"
  [ -e "$file" ] || input=/dev/null
  local tmp
  tmp="$(mktemp "${dir}/.config.toml.XXXXXX")"
  if ! awk -v core="$core" -v begin="$DEVCONTAINER_MANAGED_BEGIN" -v end="$DEVCONTAINER_MANAGED_END" '
    {
      line = $0
      sub(/\r$/, "", line)
      if (line == begin) { inblock = 1; next }
      if (line == end)   { inblock = 0; next }
      if (index(line, core)) { next }
      if (inblock) { next }
      if (line ~ /^\[mcp_servers\.(github|zai-vision|zai-web-search|zai-web-reader|zai-zread|microsoft-docs|context7|git)(\.[a-z]+)?\]$/) { skip = 1; next }
      if (line ~ /^\[/) { skip = 0 }
      if (!skip) { print line }
    }
  ' "$input" > "$tmp"; then
    rm -f "$tmp"
    return 1
  fi
  if ! devcontainer_trim_trailing_blank_lines "$tmp"; then
    rm -f "$tmp"
    return 1
  fi
  if [ -s "$tmp" ]; then
    printf '\n\n' >> "$tmp"
  fi
  printf '%s\n' "$block" >> "$tmp"
  devcontainer_assert_no_symlink_path "$file" || { rm -f "$tmp"; return 1; }
  if ! chmod 600 "$tmp" || ! mv -f "$tmp" "$file"; then
    rm -f "$tmp"
    return 1
  fi
}

devcontainer_write_codex_mcp() {
  if [ "${DEVCONTAINER_CONFIG_LOCK_HELD:-}" = "1" ]; then
    _devcontainer_write_codex_mcp_locked
    return
  fi
  local token result
  token="$(devcontainer_acquire_config_lock)" || return 1
  DEVCONTAINER_CONFIG_LOCK_HELD=1 _devcontainer_write_codex_mcp_locked
  result=$?
  devcontainer_release_config_lock "$token"
  return "$result"
}

devcontainer_sync_mcp_configs() {
  local script_dir="${1:?script dir required}"
  local token result
  if [ "${DEVCONTAINER_CONFIG_LOCK_HELD:-}" = "1" ]; then
    token=""
  else
    token="$(devcontainer_acquire_config_lock)" || return 1
    DEVCONTAINER_CONFIG_LOCK_HELD=1
    export DEVCONTAINER_CONFIG_LOCK_HELD
  fi
  result=0
  node "$script_dir/lib/write-mcp-configs.mjs" || result=$?
  if [ "$result" -eq 0 ]; then
    _devcontainer_write_codex_mcp_locked || result=$?
  fi
  if [ -n "$token" ]; then
    devcontainer_release_config_lock "$token"
    unset DEVCONTAINER_CONFIG_LOCK_HELD
  fi
  return "$result"
}
