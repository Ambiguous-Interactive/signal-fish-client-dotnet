#!/usr/bin/env bash
# Install/update the agentic coding CLIs via npm — never with sudo.
#
# NPM_CONFIG_PREFIX points at /home/vscode/.npm-global (a user-owned named
# volume), so `npm install -g` is user-writable by construction.
#
# Functions:
#   devcontainer_install_clis            Install latest of every CLI. Resilient:
#                                        one failure does not abort the rest.
#   devcontainer_refresh_clis_if_stale   Daily-throttled refresh (background
#                                        use). DEVCONTAINER_CLI_REFRESH=always
#                                        forces a refresh.
#   devcontainer_fix_npm_ownership       chown guard for the npm volumes.
#   devcontainer_fix_ownership           chown guard for all volume mountpoints
#                                        under $HOME (idempotent, sudo-assisted).

DEVCONTAINER_NPM_PACKAGES=(
  "@openai/codex"
  "@nanocollective/nanocoder"
  "@anthropic-ai/claude-code"
  "@github/copilot"
  "@google/gemini-cli"
  # MCP stdio server (bin: zai-mcp-server) for the zai-vision entries.
  "@z_ai/mcp-server"
)

devcontainer_install_opencode() {
  if npm install -g --no-audit --no-fund "opencode-ai@latest" >/dev/null 2>&1 \
    && opencode --version >/dev/null 2>&1; then
    return 0
  fi
  # opencode's postinstall fails on linux-arm64 glibc: the child npm install
  # it spawns errors out. Install the umbrella without scripts plus the
  # platform binary package explicitly, then run opencode's own postinstall,
  # which finds the sibling package in the global node_modules root and links
  # its binary.
  local platform=""
  case "$(uname -m)" in
    x86_64) platform="opencode-linux-x64" ;;
    aarch64 | arm64) platform="opencode-linux-arm64" ;;
  esac
  npm install -g --no-audit --no-fund --ignore-scripts "opencode-ai@latest" >/dev/null 2>&1 || return 1
  if [ -n "$platform" ]; then
    npm install -g --no-audit --no-fund "${platform}@latest" >/dev/null 2>&1 || true
  fi
  local pkg_dir
  pkg_dir="$(npm prefix -g)/lib/node_modules/opencode-ai"
  (cd "$pkg_dir" && node postinstall.mjs) >/dev/null 2>&1
  opencode --version >/dev/null 2>&1
}

_devcontainer_state_dir() {
  printf '%s/.cache/signal-fish-devcontainer' "${HOME}"
}

devcontainer_fix_ownership() {
  local prefix="${NPM_CONFIG_PREFIX:-$HOME/.npm-global}"
  local dir
  # Covers every devcontainer.json volume mountpoint plus the parent dirs the
  # runtime may have created root-owned before the Dockerfile started
  # pre-creating them (e.g. ~/.cache for the ~/.cache/uv volume).
  for dir in "$prefix" "$HOME/.npm" "$HOME/.codex" "$HOME/.claude" "$HOME/.copilot" \
    "$HOME/.claude-zai" "$HOME/.claude-openrouter" \
    "$HOME/.cache" "$HOME/.cache/uv" \
    "$HOME/.local" "$HOME/.local/share" "$HOME/.local/share/opencode" \
    "$HOME/.config/gh" "$HOME/.nuget/packages" "$HOME/.dotnet"; do
    if [ -d "$dir" ] && [ ! -w "$dir" ]; then
      sudo chown -R "$(id -u):$(id -g)" "$dir" 2>/dev/null || true
    fi
  done
}

devcontainer_npm_install_retry() {
  # npm installs can fail transiently (registry hiccups, cold caches during
  # container create). Bounded retry with backoff; still fails loudly.
  local pkg="$1" attempt
  for attempt in 1 2 3; do
    if npm install -g --no-audit --no-fund "${pkg}@latest" >/dev/null 2>&1; then
      return 0
    fi
    sleep $((attempt * 5))
  done
  return 1
}

devcontainer_install_clis() {
  devcontainer_fix_ownership
  mkdir -p "$(_devcontainer_state_dir)"
  # Parallel installs: each package is an independent `npm install -g` into a
  # shared prefix/cache; npm serializes file locks per-install but wall time
  # is dominated by registry downloads, which parallelize well.
  local pkg pids=() failed=0
  for pkg in "${DEVCONTAINER_NPM_PACKAGES[@]}"; do
    (
      if devcontainer_npm_install_retry "$pkg"; then
        printf 'installed %s\n' "$pkg" >&2
      else
        printf 'WARN: npm install -g %s failed after retries (continuing)\n' "$pkg" >&2
        exit 1
      fi
    ) &
    pids+=("$!")
  done
  (
    if devcontainer_install_opencode; then
      printf 'installed opencode-ai\n' >&2
    else
      printf 'WARN: npm install -g opencode-ai failed (continuing)\n' >&2
      exit 1
    fi
  ) &
  pids+=("$!")
  local pid
  for pid in "${pids[@]}"; do
    if ! wait "$pid"; then
      failed=1
    fi
  done
  date -u +%Y-%m-%dT%H:%M:%SZ > "$(_devcontainer_state_dir)/cli-install.stamp"
  return "$failed"
}

devcontainer_refresh_clis_if_stale() {
  local stamp
  stamp="$(_devcontainer_state_dir)/cli-install.stamp"
  if [ "${DEVCONTAINER_CLI_REFRESH:-}" = "always" ]; then
    devcontainer_install_clis
    return
  fi
  if [ -f "$stamp" ]; then
    local stamp_epoch now_epoch age_hours
    stamp_epoch="$(date -u -d "$(cat "$stamp")" +%s 2>/dev/null || echo 0)"
    now_epoch="$(date -u +%s)"
    age_hours=$(( (now_epoch - stamp_epoch) / 3600 ))
    if [ "$age_hours" -lt 20 ]; then
      return 0
    fi
  fi
  devcontainer_install_clis
}
