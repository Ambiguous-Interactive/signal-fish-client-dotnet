#!/usr/bin/env bash
# Load devcontainer credentials into the environment.
#
# Sources (later source wins): inherited environment < .env < .env.local
# .env.local is gitignored and is the preferred place for secrets.
#
# Source semantics (per source, for each credential family):
#   - A source that does not mention the family leaves the previous value.
#   - A source that mentions the family with a non-empty value replaces the
#     previous value (the last source wins, including alias spellings).
#   - A source that mentions the family with only blank values is treated as
#     "no override" (the common copy-.env.example workflow must not clear a
#     real inherited key).
#   - A source whose values are malformed (control characters) or whose
#     aliases disagree CLEARS the family instead of silently keeping an older
#     credential, and warns on stderr.
#   - When the canonical name and an alias are both set, the canonical value
#     wins and a conflicting alias is warned about.
#
# Contract: MUST stay silent on stdout (VS Code mcp.json shellCommand inputs
# capture stdout); diagnostics go to stderr. Always exits 0.
#
# Canonical variables produced:
#   GITHUB_PERSONAL_ACCESS_TOKEN  (aliases: GITHUB_PAT, GH_TOKEN, GITHUB_TOKEN,
#                                  GITHUB_MCP_PAT — the sibling-repo name)
#   Z_AI_API_KEY                  (aliases: ZAI_API_KEY, ZHIPU_API_KEY)
#   Z_AI_MODE                     (ZAI, default; or ZHIPU → bigmodel.cn base URL)
#   GH_TOKEN                      (mirrored from the canonical GitHub PAT for gh CLI)
# Passthrough (only re-exported when already set somewhere): OPENAI_API_KEY,
# ANTHROPIC_API_KEY, OPENROUTER_API_KEY (ai-backends launchers),
# CONTEXT7_API_KEY (context7 MCP rate limits), and the allowlisted model/timeout
# controls consumed by the isolated AI backend launchers.

_devcontainer_repo_root() {
  local dir="${BASH_SOURCE[0]}"
  dir="$(cd "$(dirname "$dir")" && pwd)"
  printf '%s' "$(cd "$dir/../../.." && pwd)"
}

# shellcheck source=paths.sh
source "$(dirname "${BASH_SOURCE[0]}")/paths.sh"

_devcontainer_env_key_allowed() {
  case "$1" in
    GITHUB_PERSONAL_ACCESS_TOKEN | GITHUB_PAT | GH_TOKEN | GITHUB_TOKEN | GITHUB_MCP_PAT | \
      Z_AI_API_KEY | ZAI_API_KEY | ZHIPU_API_KEY | Z_AI_MODE | \
      OPENAI_API_KEY | ANTHROPIC_API_KEY | OPENROUTER_API_KEY | CONTEXT7_API_KEY | \
      CODEX_ZAI_MODEL | CODEX_ZAI_REASONING_EFFORT | CODEX_OPENROUTER_MODEL | \
      ZAI_API_TIMEOUT_MS | OPENROUTER_API_TIMEOUT_MS | \
      CLAUDE_ZAI_SONNET_MODEL | CLAUDE_ZAI_OPUS_MODEL | CLAUDE_ZAI_HAIKU_MODEL | \
      CLAUDE_OPENROUTER_FABLE_MODEL | CLAUDE_OPENROUTER_OPUS_MODEL | \
      CLAUDE_OPENROUTER_SONNET_MODEL | CLAUDE_OPENROUTER_HAIKU_MODEL | \
      DEVCONTAINER_CLI_REFRESH)
      return 0
      ;;
    *)
      return 1
      ;;
  esac
}

# Keys mentioned by the most recent _devcontainer_env_parse call.
DEVCONTAINER_ENV_PARSED_KEYS=""

_devcontainer_env_note_key() {
  case " $DEVCONTAINER_ENV_PARSED_KEYS " in
    *" $1 "*) ;;
    *) DEVCONTAINER_ENV_PARSED_KEYS="$DEVCONTAINER_ENV_PARSED_KEYS $1" ;;
  esac
}

_devcontainer_env_key_mentioned() {
  case " $DEVCONTAINER_ENV_PARSED_KEYS " in
    *" $1 "*) return 0 ;;
    *) return 1 ;;
  esac
}

_devcontainer_env_parse() {
  # $1 = file; exports only the documented credential/control variables.
  # Unknown names are deliberately ignored: this file is untrusted input and
  # must not be able to replace PATH, HOME, BASH_ENV, LD_PRELOAD, or npm's
  # global prefix before lifecycle scripts run privileged operations.
  local file="$1"
  [ -f "$file" ] || return 0
  local line key value
  while IFS= read -r line || [ -n "$line" ]; do
    line="${line%$'\r'}"
    line="${line#"${line%%[![:space:]]*}"}"
    # Skip blanks and comments.
    [[ "$line" =~ ^[[:space:]]*$ || "$line" =~ ^[[:space:]]*# ]] && continue
    case "$line" in
      export\ *) line="${line#export}"; line="${line#"${line%%[![:space:]]*}"}" ;;
    esac
    case "$line" in
      *=*) ;;
      *) continue ;;
    esac
    key="${line%%=*}"
    key="${key#"${key%%[![:space:]]*}"}"
    key="${key%"${key##*[![:space:]]}"}"
    [[ "$key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || continue
    _devcontainer_env_key_allowed "$key" || continue
    value="${line#*=}"
    # Strip one layer of matching quotes; a quote opened but never closed
    # disqualifies the line rather than leaking a raw quote into a key.
    if [[ "$value" == \"*\" && "$value" == *\" ]]; then
      value="${value#\"}"; value="${value%\"}"
    elif [[ "$value" == \'*\' && "$value" == *\' ]]; then
      value="${value#\'}"; value="${value%\'}"
    elif [[ "$value" == \"* || "$value" == \'* ]]; then
      printf 'env.sh: WARNING: %s in %s has an unterminated quote; line ignored\n' "$key" "$file" >&2
      continue
    else
      # Match the ai-backends.sh parser: an inline " #..." comment outside
      # quotes is not part of the value.
      case "$value" in
        *' #'*) value="${value%%' #'*}" ;;
      esac
      value="${value%"${value##*[![:space:]]}"}"
    fi
    _devcontainer_env_note_key "$key"
    printf -v "$key" '%s' "$value"
    # shellcheck disable=SC2163  # export the variable named by $key
    export "$key"
  done < "$file"
}

_devcontainer_env_clear_credential_families() {
  unset GITHUB_PERSONAL_ACCESS_TOKEN GITHUB_PAT GH_TOKEN GITHUB_TOKEN GITHUB_MCP_PAT
  unset Z_AI_API_KEY ZAI_API_KEY ZHIPU_API_KEY
}

_devcontainer_env_reset_source_state() {
  _devcontainer_env_clear_credential_families
  unset Z_AI_MODE
  DEVCONTAINER_ENV_PARSED_KEYS=""
}

# Family resolvers return 0 when a usable value is selected (exported into
# GITHUB_PERSONAL_ACCESS_TOKEN / Z_AI_API_KEY), 1 when nothing is set, and 2
# when the family is malformed or self-contradictory (the family is left
# unset so a later source can never resurrect a stale credential).
_devcontainer_env_resolve_github_family() {
  local name value canonical resolved="" alias
  local invalid=0
  for name in GITHUB_PERSONAL_ACCESS_TOKEN GITHUB_PAT GH_TOKEN GITHUB_TOKEN GITHUB_MCP_PAT; do
    value="${!name-}"
    if [ -n "$value" ] && [[ "$value" =~ [[:cntrl:]] ]]; then
      invalid=1
    fi
  done
  if [ "$invalid" -ne 0 ]; then
    printf 'env.sh: WARNING: GitHub credential contains control characters; credential cleared\n' >&2
    _devcontainer_env_clear_credential_families
    return 2
  fi

  canonical="${GITHUB_PERSONAL_ACCESS_TOKEN-}"
  if [ -n "$canonical" ]; then
    resolved="$canonical"
    for alias in GITHUB_PAT GH_TOKEN GITHUB_TOKEN GITHUB_MCP_PAT; do
      value="${!alias-}"
      if [ -n "$value" ] && [ "$value" != "$canonical" ]; then
        printf 'env.sh: WARNING: %s conflicts with GITHUB_PERSONAL_ACCESS_TOKEN; canonical value wins\n' "$alias" >&2
      fi
    done
  else
    for alias in GITHUB_PAT GH_TOKEN GITHUB_TOKEN GITHUB_MCP_PAT; do
      value="${!alias-}"
      [ -n "$value" ] || continue
      if [ -n "$resolved" ] && [ "$value" != "$resolved" ]; then
        printf 'env.sh: WARNING: competing GitHub aliases disagree; credential cleared\n' >&2
        _devcontainer_env_clear_credential_families
        return 2
      fi
      resolved="$value"
    done
  fi
  [ -n "$resolved" ] || return 1
  GITHUB_PERSONAL_ACCESS_TOKEN="$resolved"
  return 0
}

_devcontainer_env_resolve_zai_family() {
  local name value canonical resolved="" alias
  local invalid=0
  for name in Z_AI_API_KEY ZAI_API_KEY ZHIPU_API_KEY; do
    value="${!name-}"
    if [ -n "$value" ] && [[ "$value" =~ [[:cntrl:]] ]]; then
      invalid=1
    fi
  done
  if [ "$invalid" -ne 0 ]; then
    printf 'env.sh: WARNING: Z.AI credential contains control characters; credential cleared\n' >&2
    _devcontainer_env_clear_credential_families
    return 2
  fi

  canonical="${Z_AI_API_KEY-}"
  if [ -n "$canonical" ]; then
    resolved="$canonical"
    for alias in ZAI_API_KEY ZHIPU_API_KEY; do
      value="${!alias-}"
      if [ -n "$value" ] && [ "$value" != "$canonical" ]; then
        printf 'env.sh: WARNING: %s conflicts with Z_AI_API_KEY; canonical value wins\n' "$alias" >&2
      fi
    done
  else
    for alias in ZAI_API_KEY ZHIPU_API_KEY; do
      value="${!alias-}"
      [ -n "$value" ] || continue
      if [ -n "$resolved" ] && [ "$value" != "$resolved" ]; then
        printf 'env.sh: WARNING: competing Z.AI aliases disagree; credential cleared\n' >&2
        _devcontainer_env_clear_credential_families
        return 2
      fi
      resolved="$value"
    done
  fi
  [ -n "$resolved" ] || return 1
  Z_AI_API_KEY="$resolved"
  return 0
}

_devcontainer_env_normalize_mode() {
  case "${Z_AI_MODE-}" in
    ZAI | ZHIPU) return 0 ;;
    "")
      Z_AI_MODE="ZAI"
      ;;
    *)
      printf 'env.sh: WARNING: Z_AI_MODE is not ZAI or ZHIPU; using ZAI\n' >&2
      Z_AI_MODE="ZAI"
      ;;
  esac
}

_devcontainer_env_validate_passthrough() {
  local key value
  for key in OPENAI_API_KEY ANTHROPIC_API_KEY OPENROUTER_API_KEY CONTEXT7_API_KEY \
    CODEX_ZAI_MODEL CODEX_ZAI_REASONING_EFFORT CODEX_OPENROUTER_MODEL \
    ZAI_API_TIMEOUT_MS OPENROUTER_API_TIMEOUT_MS \
    CLAUDE_ZAI_SONNET_MODEL CLAUDE_ZAI_OPUS_MODEL CLAUDE_ZAI_HAIKU_MODEL \
    CLAUDE_OPENROUTER_FABLE_MODEL CLAUDE_OPENROUTER_OPUS_MODEL \
    CLAUDE_OPENROUTER_SONNET_MODEL CLAUDE_OPENROUTER_HAIKU_MODEL; do
    value="${!key-}"
    if [ -n "$value" ] && [[ "$value" =~ [[:cntrl:]] ]]; then
      printf 'env.sh: WARNING: %s contains control characters; rejected\n' "$key" >&2
      unset "$key"
    fi
  done
}

_devcontainer_env_load() {
  local root selected_github="" selected_zai="" selected_mode="" mode_selected=0
  local source_file status
  root="$(_devcontainer_repo_root)"

  _devcontainer_env_apply_source() {
    # Resolve the current (per-source) environment into the selected state.
    # A malformed source clears the credential family rather than keeping a
    # stale value; Z_AI_MODE is independent of credential health.
    status=0
    if [ -n "${GITHUB_PERSONAL_ACCESS_TOKEN-}" ] || [ -n "${GITHUB_PAT-}" ] || [ -n "${GH_TOKEN-}" ] \
      || [ -n "${GITHUB_TOKEN-}" ] || [ -n "${GITHUB_MCP_PAT-}" ]; then
      _devcontainer_env_resolve_github_family || status=$?
      if [ "$status" -eq 0 ]; then
        selected_github="$GITHUB_PERSONAL_ACCESS_TOKEN"
      elif [ "$status" -eq 2 ]; then
        selected_github=""
      fi
    fi
    status=0
    if [ -n "${Z_AI_API_KEY-}" ] || [ -n "${ZAI_API_KEY-}" ] || [ -n "${ZHIPU_API_KEY-}" ]; then
      _devcontainer_env_resolve_zai_family || status=$?
      if [ "$status" -eq 0 ]; then
        selected_zai="$Z_AI_API_KEY"
      elif [ "$status" -eq 2 ]; then
        selected_zai=""
      fi
    fi
    if [ -n "${Z_AI_MODE-}" ]; then
      _devcontainer_env_normalize_mode
      selected_mode="$Z_AI_MODE"
      mode_selected=1
    fi
    return 0
  }

  # Source 1: the inherited environment. Any set family member counts as a
  # mention; a source that mentions nothing leaves the default state.
  _devcontainer_env_apply_source

  # DEVCONTAINER_ENV_SKIP_FILES=1 is a hermetic self-test seam. It also keeps
  # alias behavior independent of a developer's real .env.local.
  if [ "${DEVCONTAINER_ENV_SKIP_FILES:-}" != "1" ]; then
    for source_file in "$root/.env" "$root/.env.local"; do
      [ -f "$source_file" ] || continue
      _devcontainer_env_reset_source_state
      _devcontainer_env_parse "$source_file"
      _devcontainer_env_apply_source
    done
  fi

  _devcontainer_env_clear_credential_families
  if [ -n "$selected_github" ]; then
    GITHUB_PERSONAL_ACCESS_TOKEN="$selected_github"
    export GITHUB_PERSONAL_ACCESS_TOKEN
  fi
  if [ -n "$selected_zai" ]; then
    Z_AI_API_KEY="$selected_zai"
    export Z_AI_API_KEY
  fi
  if [ "$mode_selected" -eq 0 ]; then
    Z_AI_MODE="ZAI"
  else
    Z_AI_MODE="$selected_mode"
  fi
  export Z_AI_MODE
  if [ -n "${GITHUB_PERSONAL_ACCESS_TOKEN:-}" ]; then
    GH_TOKEN="$GITHUB_PERSONAL_ACCESS_TOKEN"
    export GH_TOKEN
  fi
  # Do not leave a stale competing alias available to a launcher. GH_TOKEN is
  # re-created above as the deliberate gh CLI mirror.
  unset GITHUB_PAT GITHUB_TOKEN GITHUB_MCP_PAT ZAI_API_KEY ZHIPU_API_KEY

  _devcontainer_env_validate_passthrough
  for source_file in OPENAI_API_KEY ANTHROPIC_API_KEY OPENROUTER_API_KEY CONTEXT7_API_KEY \
    CODEX_ZAI_MODEL CODEX_ZAI_REASONING_EFFORT CODEX_OPENROUTER_MODEL \
    ZAI_API_TIMEOUT_MS OPENROUTER_API_TIMEOUT_MS \
    CLAUDE_ZAI_SONNET_MODEL CLAUDE_ZAI_OPUS_MODEL CLAUDE_ZAI_HAIKU_MODEL \
    CLAUDE_OPENROUTER_FABLE_MODEL CLAUDE_OPENROUTER_OPUS_MODEL \
    CLAUDE_OPENROUTER_SONNET_MODEL CLAUDE_OPENROUTER_HAIKU_MODEL; do
    if [ -n "${!source_file-}" ]; then
      # shellcheck disable=SC2163  # export the variable named by $source_file
      export "$source_file"
    fi
  done
  return 0
}

_devcontainer_env_load
