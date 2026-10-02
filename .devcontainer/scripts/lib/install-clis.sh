#!/usr/bin/env bash
# Install/update the agentic coding CLIs via npm — never with sudo.
#
# NPM_CONFIG_PREFIX points at /home/vscode/.npm-global (a user-owned named
# volume), so `npm install -g` is user-writable by construction. All global
# npm mutations are serialized by one lock and the completion stamp is written
# only after the complete core set passes its readiness probe.

# shellcheck source=paths.sh
source "$(dirname "${BASH_SOURCE[0]}")/paths.sh"

# Keep npm's global prefix canonical even if a host environment or a malformed
# env file supplies another value. The Dockerfile owns this directory and the
# devcontainer user, so it is the only prefix lifecycle scripts may write to.
DEVCONTAINER_NPM_PREFIX="/home/vscode/.npm-global"
export NPM_CONFIG_PREFIX="$DEVCONTAINER_NPM_PREFIX"

DEVCONTAINER_NPM_PACKAGES=(
  "@openai/codex"
  "@nanocollective/nanocoder"
  "@anthropic-ai/claude-code"
  "@github/copilot"
  "@google/gemini-cli"
  "jsonc-parser"
  # MCP stdio server (bin: zai-mcp-server) for the zai-vision entries.
  "@z_ai/mcp-server"
)

_devcontainer_npm_lock_file() {
  printf '%s/cli-install.lock\n' "$(devcontainer_state_dir)"
}

_devcontainer_acquire_npm_lock() {
  local lock_file lock_fd
  lock_file="$(_devcontainer_npm_lock_file)"
  mkdir -p "$(dirname "$lock_file")"
  devcontainer_assert_no_symlink_path "$lock_file" || return 1
  if [ -L "$lock_file" ]; then
    printf 'ERROR: refusing symlinked npm lock: %s\n' "$lock_file" >&2
    return 1
  fi
  command -v flock >/dev/null 2>&1 || {
    printf 'ERROR: flock is required to serialize npm lifecycle installs\n' >&2
    return 1
  }
  exec {lock_fd}>"$lock_file" || return 1
  if ! flock -x "$lock_fd"; then
    exec {lock_fd}>&-
    return 1
  fi
  printf '%s' "$lock_fd"
}

_devcontainer_release_npm_lock() {
  local lock_fd="$1"
  [ -n "$lock_fd" ] || return 0
  flock -u "$lock_fd" 2>/dev/null || true
  exec {lock_fd}>&- 2>/dev/null || true
}

devcontainer_opencode_is_v2() {
  local version_output
  version_output="$(opencode --version 2>/dev/null)" || return 1
  printf '%s\n' "$version_output" | grep -Eq '(^|[^[:alnum:]])v?2\.[0-9]'
}

_devcontainer_install_opencode_locked() {
  # Install and verify the replacement before removing the legacy packages.
  # If the registry is unavailable, a previously working OpenCode command
  # remains available for rollback and diagnostics.
  if ! devcontainer_npm_install_retry "@opencode/cli@2"; then
    printf 'ERROR: npm could not install @opencode/cli@2 after retries\n' >&2
    return 1
  fi
  if ! devcontainer_opencode_is_v2; then
    # A legacy package can own the `opencode` bin. Remove it only after the
    # new package has installed successfully, then repair the bin once.
    npm uninstall -g --no-audit --no-fund opencode-ai opencode-linux-arm64 opencode-linux-x64 >/dev/null 2>&1 || true
    if ! devcontainer_npm_install_retry "@opencode/cli@2" || ! devcontainer_opencode_is_v2; then
      local version_output
      version_output="$(opencode --version 2>&1 || true)"
      printf 'ERROR: @opencode/cli is installed but opencode is not reporting a v2 release (observed: %s)\n' "$version_output" >&2
      return 1
    fi
  fi
  npm uninstall -g --no-audit --no-fund opencode-ai opencode-linux-arm64 opencode-linux-x64 >/dev/null 2>&1 || true
  # npm uninstall can remove a shared bin entry on older npm versions. Ensure
  # the verified v2 command is still present after legacy cleanup.
  if ! devcontainer_opencode_is_v2; then
    devcontainer_npm_install_retry "@opencode/cli@2" || return 1
    devcontainer_opencode_is_v2 || return 1
  fi
}

devcontainer_install_opencode() {
  if [ "${DEVCONTAINER_NPM_LOCK_HELD:-}" = "1" ]; then
    _devcontainer_install_opencode_locked
    return
  fi
  local lock_fd result=0
  lock_fd="$(_devcontainer_acquire_npm_lock)" || return 1
  # The marker is set in this shell (not a subshell) so the locked worker and
  # any nested helpers see it without re-acquiring.
  DEVCONTAINER_NPM_LOCK_HELD=1
  export DEVCONTAINER_NPM_LOCK_HELD
  _devcontainer_install_opencode_locked || result=$?
  unset DEVCONTAINER_NPM_LOCK_HELD
  _devcontainer_release_npm_lock "$lock_fd"
  return "$result"
}

devcontainer_core_clis_ready() {
  local package binary codex_home legacy
  command -v opencode >/dev/null 2>&1 || return 1
  devcontainer_opencode_is_v2 || return 1
  npm list -g --depth=0 @opencode/cli >/dev/null 2>&1 || return 1
  # A legacy OpenCode package can shadow the V2 bin; readiness must treat its
  # presence as incomplete state so the installer's cleanup path runs.
  for legacy in opencode-ai opencode-linux-arm64 opencode-linux-x64; do
    if npm list -g --depth=0 "$legacy" >/dev/null 2>&1; then
      return 1
    fi
  done
  for package in "${DEVCONTAINER_NPM_PACKAGES[@]}"; do
    npm list -g --depth=0 "$package" >/dev/null 2>&1 || return 1
  done
  for binary in codex nanocoder claude copilot gemini zai-mcp-server uvx; do
    command -v "$binary" >/dev/null 2>&1 || return 1
  done
  codex_home="$(devcontainer_codex_home_dir)"
  devcontainer_assert_no_symlink_path "$codex_home" || return 1
}

devcontainer_ai_backends_ready() {
  local launcher codex_home
  for launcher in claude-zai claude-openrouter codex-zai codex-openrouter; do
    command -v "$launcher" >/dev/null 2>&1 || return 1
  done
  codex_home="$(devcontainer_codex_home_dir)"
  devcontainer_assert_no_symlink_path "$codex_home" || return 1
  [ -f "$codex_home/zai-models.json" ] || return 1
  [ -f "$codex_home/config.toml" ] || return 1
  grep -qF "signal-fish-devcontainer managed AI backends" "$codex_home/config.toml" || return 1
  grep -qF "[profiles.zai]" "$codex_home/config.toml" || return 1
  grep -qF "[profiles.openrouter]" "$codex_home/config.toml" || return 1
}

devcontainer_fix_ownership() {
  local dir
  # The image provisions the remote account at /home/vscode. Do not perform
  # sudo repairs for an inherited or test HOME that points outside it.
  [ "$HOME" = "/home/vscode" ] || return 0
  # Covers every devcontainer.json volume mountpoint plus the parent dirs the
  # runtime may have created root-owned before the Dockerfile started
  # pre-creating them (e.g. ~/.cache for the ~/.cache/uv volume). The prefix
  # is fixed above rather than read from an untrusted environment.
  for dir in "$DEVCONTAINER_NPM_PREFIX" "$HOME/.npm" "$HOME/.codex" "$HOME/.claude" "$HOME/.copilot" \
    "$HOME/.claude-zai" "$HOME/.claude-openrouter" \
    "$HOME/.cache" "$HOME/.cache/uv" \
    "$HOME/.local" "$HOME/.local/share" "$HOME/.local/share/opencode" \
    "$HOME/.config" "$HOME/.config/gh" "$HOME/.nuget/packages" "$HOME/.dotnet"; do
    if [ -d "$dir" ] && [ ! -w "$dir" ]; then
      sudo chown -R "$(id -u):$(id -g)" "$dir" 2>/dev/null || true
    fi
  done
}

_devcontainer_npm_install_retry_locked() {
  # $1 is a complete npm package specification (for example @scope/name or
  # @opencode/cli@2). npm installs can fail transiently; preserve the final
  # error so lifecycle logs explain a failure instead of only saying "WARN".
  local spec="$1" attempt log_file
  log_file="$(mktemp)"
  for attempt in 1 2 3; do
    if NPM_CONFIG_PREFIX="$DEVCONTAINER_NPM_PREFIX" npm install -g --no-audit --no-fund "$spec" >"$log_file" 2>&1; then
      rm -f "$log_file"
      return 0
    fi
    sleep $((attempt * 5))
  done
  printf 'ERROR: npm install -g %s failed after retries\n' "$spec" >&2
  sed -n '1,40p' "$log_file" >&2 || true
  rm -f "$log_file"
  return 1
}

devcontainer_npm_install_retry() {
  if [ "${DEVCONTAINER_NPM_LOCK_HELD:-}" = "1" ]; then
    _devcontainer_npm_install_retry_locked "$@"
    return
  fi
  local lock_fd result=0
  lock_fd="$(_devcontainer_acquire_npm_lock)" || return 1
  DEVCONTAINER_NPM_LOCK_HELD=1
  export DEVCONTAINER_NPM_LOCK_HELD
  _devcontainer_npm_install_retry_locked "$@" || result=$?
  unset DEVCONTAINER_NPM_LOCK_HELD
  _devcontainer_release_npm_lock "$lock_fd"
  return "$result"
}

devcontainer_install_clis() {
  devcontainer_fix_ownership
  mkdir -p "$(devcontainer_state_dir)"
  local state_dir lock_fd result=0
  state_dir="$(devcontainer_state_dir)"

  # Keep the entire install transaction under one lock. This prevents a
  # post-start repair from uninstalling a legacy OpenCode package while a
  # parallel lifecycle is installing another CLI into the same prefix.
  lock_fd="$(_devcontainer_acquire_npm_lock)" || return 1
  DEVCONTAINER_NPM_LOCK_HELD=1
  export DEVCONTAINER_NPM_LOCK_HELD
  # Invalidate the previous receipt first: a failed refresh must be retried on
  # the next start rather than silenced by a recent-but-invalid stamp. The new
  # stamp is published only after the full readiness probe passes.
  rm -f "$state_dir/cli-install.stamp"
  _devcontainer_install_core_locked || result=$?
  unset DEVCONTAINER_NPM_LOCK_HELD
  _devcontainer_release_npm_lock "$lock_fd"
  return "$result"
}

_devcontainer_install_core_locked() {
  local pkg stamp_tmp
  for pkg in "${DEVCONTAINER_NPM_PACKAGES[@]}"; do
    if ! devcontainer_npm_install_retry "${pkg}@latest"; then
      printf 'ERROR: npm install -g %s failed after retries\n' "${pkg}" >&2
      return 1
    fi
    printf 'installed %s\n' "$pkg" >&2
  done
  if ! _devcontainer_install_opencode_locked; then
    printf 'ERROR: OpenCode v2 CLI installation failed\n' >&2
    return 1
  fi
  printf 'installed @opencode/cli\n' >&2
  if ! devcontainer_core_clis_ready; then
    printf 'ERROR: CLI installation completed but the core readiness probe failed; not stamping success\n' >&2
    return 1
  fi
  stamp_tmp="$(mktemp "$(devcontainer_state_dir)/.cli-install.stamp.XXXXXX")"
  date -u +%Y-%m-%dT%H:%M:%SZ > "$stamp_tmp"
  chmod 600 "$stamp_tmp" 2>/dev/null || true
  mv -f "$stamp_tmp" "$(devcontainer_state_dir)/cli-install.stamp"
}

devcontainer_refresh_clis_if_stale() {
  local stamp
  stamp="$(devcontainer_state_dir)/cli-install.stamp"
  if [ "${DEVCONTAINER_CLI_REFRESH:-}" = "always" ]; then
    devcontainer_install_clis
    return
  fi
  if [ -f "$stamp" ]; then
    local stamp_epoch now_epoch age_hours
    stamp_epoch="$(date -u -d "$(cat "$stamp")" +%s 2>/dev/null || echo 0)"
    now_epoch="$(date -u +%s)"
    if [ "$stamp_epoch" -gt "$now_epoch" ]; then
      # A future-dated (corrupt or clock-skewed) stamp is stale by definition.
      devcontainer_install_clis
      return
    fi
    age_hours=$(( (now_epoch - stamp_epoch) / 3600 ))
    if [ "$age_hours" -lt 20 ]; then
      return 0
    fi
  fi
  devcontainer_install_clis
}
