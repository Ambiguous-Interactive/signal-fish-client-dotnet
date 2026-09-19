#!/usr/bin/env bash
# Load devcontainer credentials into the environment.
#
# Sources (later wins): inherited environment < .env < .env.local
# .env.local is gitignored and is the preferred place for secrets.
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
# CONTEXT7_API_KEY (context7 MCP rate limits).

_devcontainer_repo_root() {
  local dir="${BASH_SOURCE[0]}"
  dir="$(cd "$(dirname "$dir")" && pwd)"
  printf '%s' "$(cd "$dir/../../.." && pwd)"
}

_devcontainer_env_parse() {
  # $1 = file; exports KEY=VALUE pairs found in it. Accepts optional
  # `export ` prefix; strips one layer of matching single/double quotes.
  local file="$1"
  [ -f "$file" ] || return 0
  local line key value
  while IFS= read -r line || [ -n "$line" ]; do
    line="${line%$'\r'}"
    # Skip blanks and comments.
    [[ "$line" =~ ^[[:space:]]*$ || "$line" =~ ^[[:space:]]*# ]] && continue
    line="${line#export[[:space:]]}"
    key="${line%%=*}"
    [[ "$key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || continue
    value="${line#*=}"
    # Strip one layer of matching quotes.
    if [[ "$value" == \"*\" && "$value" == *\" ]]; then
      value="${value#\"}"; value="${value%\"}"
    elif [[ "$value" == \'*\' && "$value" == *\' ]]; then
      value="${value#\'}"; value="${value%\'}"
    fi
    printf -v "$key" '%s' "$value"
    export "$key"
  done < "$file"
}

_devcontainer_env_load() {
  local root
  root="$(_devcontainer_repo_root)"
  # Order matters: .env.local is sourced last so it wins.
  # DEVCONTAINER_ENV_SKIP_FILES=1 (self-test seam) skips file parsing so
  # alias/validation checks stay hermetic on machines with a real .env.local.
  if [ "${DEVCONTAINER_ENV_SKIP_FILES:-}" != "1" ]; then
    _devcontainer_env_parse "$root/.env"
    _devcontainer_env_parse "$root/.env.local"
  fi

  # Alias resolution: canonical <- aliases (only when canonical is unset/empty).
  if [ -z "${GITHUB_PERSONAL_ACCESS_TOKEN:-}" ]; then
    for alias_ in GITHUB_PAT GH_TOKEN GITHUB_TOKEN GITHUB_MCP_PAT; do
      if [ -n "${!alias_:-}" ]; then
        GITHUB_PERSONAL_ACCESS_TOKEN="${!alias_}"
        break
      fi
    done
  fi
  if [ -z "${Z_AI_API_KEY:-}" ]; then
    for alias_ in ZAI_API_KEY ZHIPU_API_KEY; do
      if [ -n "${!alias_:-}" ]; then
        Z_AI_API_KEY="${!alias_}"
        break
      fi
    done
  fi

  # Reject corrupted values (CRLF endings, embedded newlines) BEFORE they can
  # reach TOML/JSON configs; a rejected key behaves as unset (with a warning).
  for key_ in GITHUB_PERSONAL_ACCESS_TOKEN Z_AI_API_KEY Z_AI_MODE OPENROUTER_API_KEY CONTEXT7_API_KEY; do
    if [ -n "${!key_:-}" ] && [[ "${!key_}" =~ [[:cntrl:]] ]]; then
      printf 'env.sh: WARNING: %s contains control characters; rejected\n' "$key_" >&2
      unset "$key_"
    fi
  done
  # gh CLI reads GH_TOKEN automatically; mirror the canonical PAT.
  if [ -n "${GITHUB_PERSONAL_ACCESS_TOKEN:-}" ]; then
    GH_TOKEN="${GITHUB_PERSONAL_ACCESS_TOKEN}"
  fi
  export GITHUB_PERSONAL_ACCESS_TOKEN Z_AI_API_KEY GH_TOKEN

  # Z_AI_MODE selects the Z.AI platform base URL: ZAI (api.z.ai, default) or
  # ZHIPU (open.bigmodel.cn). Anything else is rejected with a warning.
  case "${Z_AI_MODE:-}" in
    ZAI | ZHIPU) : ;;
    "")
      Z_AI_MODE="ZAI"
      ;;
    *)
      printf 'env.sh: WARNING: Z_AI_MODE is not ZAI or ZHIPU; using ZAI\n' >&2
      Z_AI_MODE="ZAI"
      ;;
  esac

  # Export well-known optional keys only if present (passthrough).
  for key_ in OPENAI_API_KEY ANTHROPIC_API_KEY OPENROUTER_API_KEY CONTEXT7_API_KEY; do
    if [ -n "${!key_:-}" ]; then export "$key_"; fi
  done
  export Z_AI_MODE
  return 0
}

_devcontainer_env_load
