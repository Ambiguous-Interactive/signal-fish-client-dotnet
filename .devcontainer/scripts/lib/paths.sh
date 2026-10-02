#!/usr/bin/env bash
# Shared path and lock primitives for the devcontainer lifecycle scripts.
# Keep these resolvers independent of credential parsing so every harness and
# readiness probe agrees on the effective Codex/OpenCode locations.

_devcontainer_path_error() {
  printf 'devcontainer path: %s\n' "$*" >&2
}

devcontainer_absolute_path() {
  local value="${1:-}"
  # Convert the path forms accepted by the CLIs into an absolute path without
  # invoking a shell or evaluating user input. OpenCode and Codex both accept
  # absolute paths; making relative overrides absolute also gives the symlink
  # checks one unambiguous root to inspect.
  # shellcheck disable=SC2088  # case pattern matches a literal tilde
  case "$value" in
    "~") value="${HOME:-}" ;;
    "~/"*) value="${HOME:-}/${value:2}" ;;
  esac
  case "$value" in
    /*) printf '%s\n' "$value" ;;
    *) printf '%s/%s\n' "${PWD:-$(pwd)}" "$value" ;;
  esac
}

# Refuse to manage a path if the target or any existing ancestor is a symlink.
# This prevents a writable config directory from redirecting an atomic rename
# outside the user's home. Callers still perform a post-mkdir check because a
# path can change between the preflight and the write.
devcontainer_assert_no_symlink_path() {
  local target probe
  target="$(devcontainer_absolute_path "${1:-}")"
  probe="$target"
  while [ -n "$probe" ]; do
    if [ -L "$probe" ]; then
      _devcontainer_path_error "refusing symlinked path: $probe"
      return 1
    fi
    [ "$probe" = "/" ] && break
    probe="$(dirname "$probe")"
  done
  return 0
}

devcontainer_codex_home_dir() {
  local value="${CODEX_HOME:-}"
  [ -n "$value" ] || value="${HOME:-}/.codex"
  devcontainer_absolute_path "$value"
}

# OpenCode's global directory follows XDG_CONFIG_HOME. OPENCODE_CONFIG_DIR is
# the later-loaded custom directory and therefore wins this resolver when set.
devcontainer_opencode_config_dir() {
  local value="${OPENCODE_CONFIG_DIR:-}"
  if [ -n "$value" ]; then
    devcontainer_absolute_path "$value"
    return
  fi
  value="${XDG_CONFIG_HOME:-}"
  if [ -n "$value" ]; then
    devcontainer_absolute_path "$value/opencode"
  else
    devcontainer_absolute_path "${HOME:-}/.config/opencode"
  fi
}

devcontainer_opencode_config_file() {
  local value="${OPENCODE_CONFIG:-}"
  if [ -n "$value" ]; then
    devcontainer_absolute_path "$value"
    return
  fi
  value="$(devcontainer_opencode_config_dir)"
  if [ -f "$value/opencode.jsonc" ]; then
    printf '%s\n' "$value/opencode.jsonc"
  elif [ -f "$value/opencode.json" ]; then
    printf '%s\n' "$value/opencode.json"
  elif [ -f "$value/config.json" ]; then
    printf '%s\n' "$value/config.json"
  else
    printf '%s/opencode.json\n' "$value"
  fi
}

devcontainer_state_dir() {
  devcontainer_absolute_path "${HOME:-}/.cache/signal-fish-devcontainer"
}

devcontainer_config_lock_dir() {
  printf '%s/mcp-config.lock.d\n' "$(devcontainer_state_dir)"
}

# One lock guards every managed config writer (JSON harness configs, Codex
# TOML, AI-backend provider blocks) so a failed transaction can never roll a
# concurrent writer back. The Node writer implements the identical protocol in
# write-mcp-configs.mjs: mkdir lock directory + owner token + 10 minute
# staleness takeover. Callers pass the token back to release.
devcontainer_acquire_config_lock() {
  local lock_root lock_dir token attempts=0
  lock_root="$(devcontainer_state_dir)"
  lock_dir="$(devcontainer_config_lock_dir)"
  mkdir -p "$lock_root" || {
    _devcontainer_path_error "cannot create state directory: $lock_root"
    return 1
  }
  devcontainer_assert_no_symlink_path "$lock_dir" || return 1
  token="$(printf '%s-%s-%s' "$$" "$RANDOM" "${EPOCHSECONDS:-$(date +%s)}")"
  while [ "$attempts" -lt 300 ]; do
    attempts=$((attempts + 1))
    if mkdir "$lock_dir" 2>/dev/null; then
      printf '%s\n' "$token" > "$lock_dir/owner" 2>/dev/null \
        || { rmdir "$lock_dir" 2>/dev/null || true; sleep 0.2; continue; }
      printf '%s' "$token"
      return 0
    fi
    if [ -L "$lock_dir" ]; then
      _devcontainer_path_error "refusing symlinked config lock: $lock_dir"
      return 1
    fi
    # Take over a stale lock from a killed writer; a live writer finishes in
    # seconds, so a 10 minute old directory is abandoned.
    if [ -z "$(find "$lock_dir" -maxdepth 0 -mmin +10 2>/dev/null)" ]; then
      sleep 0.2
      continue
    fi
    rm -rf "$lock_dir" 2>/dev/null || true
  done
  _devcontainer_path_error "timed out waiting for the config lock: $lock_dir"
  return 1
}

devcontainer_release_config_lock() {
  local token="$1" lock_dir owner
  [ -n "$token" ] || return 0
  lock_dir="$(devcontainer_config_lock_dir)"
  [ -d "$lock_dir" ] || return 0
  owner="$(cat "$lock_dir/owner" 2>/dev/null || true)"
  if [ "$owner" = "$token" ]; then
    rm -f "$lock_dir/owner" 2>/dev/null || true
    rmdir "$lock_dir" 2>/dev/null || true
  fi
}
