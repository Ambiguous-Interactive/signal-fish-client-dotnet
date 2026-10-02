#!/usr/bin/env bash
# Self-test for the devcontainer setup. Run inside the container:
#   bash .devcontainer/scripts/self-test.sh
# Deterministic: backs up any real .env.local, runs generated-config checks in
# a private HOME with throwaway credentials, and restores the caller's state
# afterwards (EXIT trap).
set -uo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
cd "$repo_root" || exit 1

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

# Run a lifecycle/setup command and fail the suite (not just one check) when
# it fails. A silent setup failure would otherwise let downstream assertions
# pass vacuously or mask a broken lifecycle as a green run.
must_run() { # must_run <description> <command...>
  local desc="$1"; shift
  if "$@" >/dev/null 2>&1; then
    pass=$((pass + 1)); printf 'ok   - %s\n' "$desc"
  else
    fail=$((fail + 1)); FAILURES+=("setup: $desc")
    printf 'FAIL - setup: %s\n' "$desc"
    printf 'ABORT: a setup/lifecycle command failed; later checks would be meaningless.\n' >&2
    exit 1
  fi
}

# ---------------------------------------------------------------------------
# Credential isolation: run all generated-config tests under a private HOME,
# back up any real .env.local, inject throwaway values, and restore everything
# on exit (also on interruption). Never print file contents.
# ---------------------------------------------------------------------------
ENV_BACKUP="$repo_root/.env.local.selftest-backup"
ENV_FILE_BACKUP="$repo_root/.env.selftest-backup"
env_state="untouched"
env_file_state="untouched"
selftest_home="$(mktemp -d "${TMPDIR:-/tmp}/signal-fish-devcontainer-selftest.XXXXXX")"
declare -A ORIGINAL_ENV
declare -A ORIGINAL_ENV_SET
for env_name in HOME USERPROFILE HOMEDRIVE HOMEPATH XDG_CONFIG_HOME XDG_DATA_HOME XDG_STATE_HOME XDG_CACHE_HOME XDG_RUNTIME_DIR OPENCODE_TEST_HOME CODEX_HOME OPENCODE_CONFIG OPENCODE_CONFIG_DIR OPENCODE_CONFIG_CONTENT OPENCODE_DISABLE_PROJECT_CONFIG; do
  if declare -p "$env_name" >/dev/null 2>&1; then
    ORIGINAL_ENV["$env_name"]="${!env_name}"
    ORIGINAL_ENV_SET["$env_name"]=1
  else
    ORIGINAL_ENV_SET["$env_name"]=0
  fi
done
restore_env() {
  local env_name
  for env_name in HOME USERPROFILE HOMEDRIVE HOMEPATH XDG_CONFIG_HOME XDG_DATA_HOME XDG_STATE_HOME XDG_CACHE_HOME XDG_RUNTIME_DIR OPENCODE_TEST_HOME CODEX_HOME OPENCODE_CONFIG OPENCODE_CONFIG_DIR OPENCODE_CONFIG_CONTENT OPENCODE_DISABLE_PROJECT_CONFIG; do
    if [ "${ORIGINAL_ENV_SET[$env_name]:-0}" -eq 1 ]; then
      export "$env_name=${ORIGINAL_ENV[$env_name]}"
    else
      unset "$env_name"
    fi
  done
}
cleanup_env() {
  if [ -n "${selftest_home:-}" ] && [ -d "$selftest_home" ]; then
    HOME="$selftest_home" USERPROFILE="$selftest_home" CODEX_HOME="$selftest_home/.codex" \
      timeout 10s opencode service stop >/dev/null 2>&1 || true
  fi
  case "$env_state" in
    created)
      rm -f "$repo_root/.env.local"
      ;;
    backed-up)
      rm -f "$repo_root/.env.local"
      mv -f "$ENV_BACKUP" "$repo_root/.env.local"
      ;;
  esac
  case "$env_file_state" in
    backed-up)
      mv -f "$ENV_FILE_BACKUP" "$repo_root/.env"
      ;;
  esac
  env_state="untouched"
  env_file_state="untouched"
  restore_env
  if [ -n "${selftest_home:-}" ]; then
    rm -rf "$selftest_home"
    selftest_home=""
  fi
}
trap cleanup_env EXIT

if [ -e "$ENV_BACKUP" ]; then
  echo "ABORT: $ENV_BACKUP exists (leftover from an interrupted run?)." >&2
  echo "Inspect it, then remove it and re-run this script." >&2
  exit 1
fi
if [ -e "$ENV_FILE_BACKUP" ]; then
  echo "ABORT: $ENV_FILE_BACKUP exists (leftover from an interrupted run?)." >&2
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
check "flock available (lifecycle locking)" bash -c 'command -v flock && flock --version'
if command -v gh >/dev/null 2>&1; then
  check "gh CLI available" command -v gh
else
  echo "skip - gh CLI is supplied by the declared devcontainer feature"
fi
check "pwsh available" command -v pwsh
check "node >= 22 available" bash -c '[[ $(node -p "process.versions.node.split(\".\")[0]") -ge 22 ]]'
check "OpenCode data volume is V2-isolated" grep -q 'opencode-v2-data' .devcontainer/devcontainer.json
check "legacy OpenCode data volume is not mounted" bash -c '! grep -q "opencode-data,target=/home/vscode/.local/share/opencode" .devcontainer/devcontainer.json'

echo "== no-sudo global npm install =="
check "npm install -g works without sudo" npm install -g --no-audit --no-fund sort-package-json@2
check "globally installed binary on PATH" bash -lc 'command -v sort-package-json'
check "cleanup: uninstall canary" npm uninstall -g sort-package-json

echo "== .NET toolchain (matches CI: 10.0.x + 8.0.x) =="
check "dotnet SDK 10 present" bash -c 'dotnet --list-sdks | grep -q "^10\."'
check "dotnet SDK 8 present" bash -c 'dotnet --list-sdks | grep -q "^8\."'

echo "== nested toolchain invocations (pwsh arch-mismatch class) =="
# Version smokes validate only the top-level apphost. The 2026-09-19
# incident: a valid arm64 pwsh apphost on PATH with an x86-64 managed
# payload behind it - direct invocations passed, but nested `& pwsh ...`
# (what .githooks/pre-commit.ps1 uses for every lint step) died with
# "exec format error" at commit time, uncatchable by CI (x64 runners).
check "pwsh nested invocation works" \
  bash -c "pwsh -NoProfile -Command '& pwsh -NoProfile -Command \"exit 0\"'"
# Assert the ELF machine type of the resolved pwsh binary matches the host,
# so an arch-mismatched install is named here instead of failing opaquely at
# commit time (e_machine at ELF offset 18: 62 = x86-64, 183 = AArch64).
# Repair for a mismatch: reinstall the official tarball matching
# `uname -m` over the install prefix (see .llm/improvement-log.md 2026-09-19).
check "pwsh binary architecture matches host" bash -c '
  bin="$(readlink -f "$(command -v pwsh)")" || exit 1
  [ -n "$bin" ] && [ -f "$bin" ] || exit 1
  head -c 4 "$bin" | od -An -tx1 | grep -q "7f 45 4c 46" || exit 1  # \x7fELF
  em="$(od -An -tu2 -j18 -N2 --endian=little "$bin" | tr -d "[:space:]")" || exit 1
  case "$(uname -m)" in
    x86_64) [ "$em" = "62" ] ;;
    aarch64 | arm64) [ "$em" = "183" ] ;;
    *) exit 0 ;;  # unknown host arch: the nested-invocation check is the guard
  esac'
check "node, uv, and OpenCode binaries match host architecture" bash -c '
  case "$(uname -m)" in
    x86_64) expected=62 ;;
    aarch64 | arm64) expected=183 ;;
    *) exit 0 ;;
  esac
  for name in node uv opencode; do
    bin="$(readlink -f "$(command -v "$name")")" || exit 1
    [ -f "$bin" ] || exit 1
    head -c 4 "$bin" | od -An -tx1 | grep -q "7f 45 4c 46" || exit 1
    em="$(od -An -tu2 -j18 -N2 --endian=little "$bin" | tr -d "[:space:]")" || exit 1
    [ "$em" = "$expected" ] || exit 1
  done'

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
check "opencode v2 CLI package installed" bash -lc 'npm list -g --depth=0 @opencode/cli'
resolved_opencode_version="$(opencode --version 2>/dev/null || true)"
printf 'resolved OpenCode CLI: %s\n' "$resolved_opencode_version"
check "opencode version output is present" test -n "$resolved_opencode_version"
check "opencode reports v2" bash -lc 'opencode --version | grep -Eq "(^|[^0-9])v?2\."'
check "core CLI readiness probe" bash -c 'source .devcontainer/scripts/lib/install-clis.sh && devcontainer_core_clis_ready'
readiness_stub="$(mktemp -d)"
for readiness_binary in codex nanocoder claude copilot gemini zai-mcp-server uvx; do
  ln -s /bin/true "$readiness_stub/$readiness_binary"
done
cat > "$readiness_stub/opencode" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' 'opencode v2.0.16'
STUB
cat > "$readiness_stub/npm" <<'STUB'
#!/usr/bin/env bash
if [[ "${FAIL_CODEX:-0}" == 1 && "$*" == *@openai/codex* ]]; then
  exit 1
fi
# The readiness probe requires legacy OpenCode packages to be ABSENT; a stub
# npm reports the failure exit code npm uses for "not installed".
for legacy in opencode-ai opencode-linux-arm64 opencode-linux-x64; do
  if [[ "$*" == *"$legacy"* ]]; then
    exit 1
  fi
done
exit 0
STUB
chmod 755 "$readiness_stub/opencode" "$readiness_stub/npm"
check "core readiness rejects a missing npm package" \
  env PATH="$readiness_stub:$PATH" FAIL_CODEX=1 \
  bash -c 'source "$1"; ! devcontainer_core_clis_ready' _ "$script_dir/lib/install-clis.sh"
check "core readiness accepts the complete stub set" \
  env PATH="$readiness_stub:$PATH" \
  bash -c 'source "$1"; devcontainer_core_clis_ready' _ "$script_dir/lib/install-clis.sh"
rm -rf "$readiness_stub"
readiness_legacy_stub="$(mktemp -d)"
for readiness_binary in codex nanocoder claude copilot gemini zai-mcp-server uvx; do
  ln -s /bin/true "$readiness_legacy_stub/$readiness_binary"
done
cat > "$readiness_legacy_stub/opencode" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' 'opencode v2.0.16'
STUB
cat > "$readiness_legacy_stub/npm" <<'STUB'
#!/usr/bin/env bash
# Legacy OpenCode package present (npm list exit 0), everything else absent.
case "$*" in
  *opencode-ai*) exit 0 ;;
  *) exit 1 ;;
esac
STUB
chmod 755 "$readiness_legacy_stub/opencode" "$readiness_legacy_stub/npm"
check "core readiness rejects a legacy opencode-ai installation" \
  env PATH="$readiness_legacy_stub:$PATH" \
  bash -c 'source "$1"; ! devcontainer_core_clis_ready' _ "$script_dir/lib/install-clis.sh"
rm -rf "$readiness_legacy_stub"
check "isolated AI backend readiness probe" bash -c 'source .devcontainer/scripts/lib/install-clis.sh && devcontainer_ai_backends_ready'
check "version predicate accepts product-prefixed v2" bash -c 'printf "opencode v2.0.16\\n" | grep -Eq "(^|[^0-9])v?2\\."'
check "version predicate rejects v1" bash -c '! printf "opencode v1.18.32\\n" | grep -Eq "(^|[^0-9])v?2\\."'
version_stub="$(mktemp -d)"
cat > "$version_stub/opencode" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "${OPENCODE_STUB_VERSION:-opencode v2.0.16}"
STUB
chmod 755 "$version_stub/opencode"
check "installer probe accepts product-prefixed v2" \
  env PATH="$version_stub:$PATH" OPENCODE_STUB_VERSION='opencode v2.0.16' \
  bash -c 'source "$1" && devcontainer_opencode_is_v2' _ "$script_dir/lib/install-clis.sh"
check "installer probe rejects v1" \
  env PATH="$version_stub:$PATH" OPENCODE_STUB_VERSION='opencode v1.18.32' \
  bash -c 'source "$1"; ! devcontainer_opencode_is_v2' _ "$script_dir/lib/install-clis.sh"
rm -rf "$version_stub"
for package in opencode-ai opencode-linux-arm64 opencode-linux-x64; do
  check "legacy global package absent: $package" bash -lc "! npm list -g --depth=0 '$package'"
done
check "nanocoder --version" bash -lc 'nanocoder --version'
check "claude --version" bash -lc 'claude --version'
check "copilot --version" bash -lc 'copilot --version'
check "gemini --version" bash -lc 'gemini --version'

# All remaining checks exercise generated files and services. Keep those
# artifacts and any OpenCode service state out of the caller's real home.
# USERPROFILE matters off Linux too: Node's os.homedir() follows it on win32.
export HOME="$selftest_home"
export USERPROFILE="$selftest_home"
unset HOMEDRIVE HOMEPATH XDG_CONFIG_HOME XDG_DATA_HOME XDG_STATE_HOME XDG_CACHE_HOME XDG_RUNTIME_DIR
export CODEX_HOME="$HOME/.codex"
unset OPENCODE_CONFIG OPENCODE_CONFIG_DIR OPENCODE_CONFIG_CONTENT OPENCODE_DISABLE_PROJECT_CONFIG
selftest_opencode_port="$((40000 + RANDOM % 20000))"
mkdir -p "$HOME/.config/opencode"
printf '{"port":%s}\n' "$selftest_opencode_port" > "$HOME/.config/opencode/service.json"
chmod 600 "$HOME/.config/opencode/service.json"
mkdir -p "$HOME/.cache/signal-fish-devcontainer"
date -u +%Y-%m-%dT%H:%M:%SZ > "$HOME/.cache/signal-fish-devcontainer/cli-install.stamp"
# AI backend launchers must install into the disposable bin: install_launchers
# prefers the directory holding `codex` (the real npm-global bin) otherwise.
selftest_bin="$selftest_home/bin"
mkdir -p "$selftest_bin"
export AI_BACKENDS_BIN_DIR="$selftest_bin"
case ":$PATH:" in
  *":$selftest_bin:"*) ;;
  *) export PATH="$selftest_bin:$PATH" ;;
esac
unset AI_BACKENDS_BIN_DIR AI_BACKENDS_ENV_LOCAL AI_BACKENDS_CONTAINER_MODE CLAUDE_SUBPROCESS_ENV_SCRUB \
  CLAUDE_ZAI_CONFIG_DIR CLAUDE_OPENROUTER_CONFIG_DIR CLAUDE_CONFIG_DIR CURSOR_HOME NANOCODER_CONFIG \
  OPENCODE_CONFIG OPENCODE_CONFIG_DIR OPENCODE_CONFIG_CONTENT OPENCODE_DISABLE_PROJECT_CONFIG \
  DEVCONTAINER_ENV_SKIP_FILES GITHUB_PERSONAL_ACCESS_TOKEN GITHUB_PAT GH_TOKEN GITHUB_TOKEN \
  GITHUB_MCP_PAT Z_AI_API_KEY ZAI_API_KEY ZHIPU_API_KEY Z_AI_MODE OPENAI_API_KEY \
  ANTHROPIC_API_KEY OPENROUTER_API_KEY CONTEXT7_API_KEY DEVCONTAINER_CLI_REFRESH \
  CODEX_ZAI_MODEL CODEX_ZAI_REASONING_EFFORT CODEX_OPENROUTER_MODEL \
  ZAI_API_TIMEOUT_MS OPENROUTER_API_TIMEOUT_MS \
  CLAUDE_ZAI_SONNET_MODEL CLAUDE_ZAI_OPUS_MODEL CLAUDE_ZAI_HAIKU_MODEL \
  CLAUDE_OPENROUTER_FABLE_MODEL CLAUDE_OPENROUTER_OPUS_MODEL \
  CLAUDE_OPENROUTER_SONNET_MODEL CLAUDE_OPENROUTER_HAIKU_MODEL \
  DEVCONTAINER_NPM_LOCK_HELD DEVCONTAINER_CONFIG_LOCK_HELD

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
check "validation: malformed GitHub credential does not clear the Z.AI family" \
  bash -c 'unset GITHUB_PERSONAL_ACCESS_TOKEN GITHUB_PAT GH_TOKEN GITHUB_TOKEN GITHUB_MCP_PAT Z_AI_API_KEY ZAI_API_KEY ZHIPU_API_KEY; export GITHUB_PAT=$(printf "bad\rpat") Z_AI_API_KEY=zai-key; DEVCONTAINER_ENV_SKIP_FILES=1 source .devcontainer/scripts/lib/env.sh && [[ $Z_AI_API_KEY == zai-key && -z "${GITHUB_PERSONAL_ACCESS_TOKEN:-}" ]]'
check "validation: malformed Z.AI credential does not clear the GitHub family" \
  bash -c 'unset GITHUB_PERSONAL_ACCESS_TOKEN GITHUB_PAT GH_TOKEN GITHUB_TOKEN GITHUB_MCP_PAT Z_AI_API_KEY ZAI_API_KEY ZHIPU_API_KEY; export ZAI_API_KEY=$(printf "bad\rzai") GITHUB_PAT=pat-key; DEVCONTAINER_ENV_SKIP_FILES=1 source .devcontainer/scripts/lib/env.sh && [[ $GITHUB_PERSONAL_ACCESS_TOKEN == pat-key && -z "${Z_AI_API_KEY:-}" ]]'
check "validation: competing GitHub aliases leave the Z.AI family intact" \
  bash -c 'unset GITHUB_PERSONAL_ACCESS_TOKEN GITHUB_PAT GH_TOKEN GITHUB_TOKEN GITHUB_MCP_PAT Z_AI_API_KEY ZAI_API_KEY ZHIPU_API_KEY; export GITHUB_PAT=a GITHUB_TOKEN=b Z_AI_API_KEY=zai-key; DEVCONTAINER_ENV_SKIP_FILES=1 source .devcontainer/scripts/lib/env.sh && [[ $Z_AI_API_KEY == zai-key && -z "${GITHUB_PERSONAL_ACCESS_TOKEN:-}" ]]'
env_allowlist_file="$(mktemp)"
printf 'NPM_CONFIG_PREFIX=/etc\nBASH_ENV=/tmp/evil\nLD_PRELOAD=/tmp/evil.so\nSAFE_NOT_ALLOWED=bad\nOPENAI_API_KEY=allowed\nCODEX_ZAI_MODEL=glm-test\n' > "$env_allowlist_file"
check "env loader ignores dangerous and unknown file keys" bash -c '
  unset NPM_CONFIG_PREFIX BASH_ENV LD_PRELOAD SAFE_NOT_ALLOWED OPENAI_API_KEY
  export DEVCONTAINER_ENV_SKIP_FILES=1
  source "$1"
  _devcontainer_env_parse "$2"
  [[ -z "${NPM_CONFIG_PREFIX:-}" && -z "${BASH_ENV:-}" && -z "${LD_PRELOAD:-}" && -z "${SAFE_NOT_ALLOWED:-}" && "$OPENAI_API_KEY" == allowed && "$CODEX_ZAI_MODEL" == glm-test ]]
' _ "$script_dir/lib/env.sh" "$env_allowlist_file"
rm -f "$env_allowlist_file"
check "install scripts pin the user-writable npm prefix" bash -c '
  NPM_CONFIG_PREFIX=/etc DEVCONTAINER_ENV_SKIP_FILES=1 source "$1"
  [[ "$DEVCONTAINER_NPM_PREFIX" == /home/vscode/.npm-global && "$NPM_CONFIG_PREFIX" == /home/vscode/.npm-global ]]
' _ "$script_dir/lib/install-clis.sh"
check "env.sh is stdout-silent" bash -c '[ -z "$(source .devcontainer/scripts/lib/env.sh)" ]'

# Source-level semantics against throwaway repo fixtures (never the caller's
# real .env/.env.local): a later source must override, a malformed source must
# clear rather than resurrect, and blank mentions must not clear.
env_source_home="$(mktemp -d)"
mkdir -p "$env_source_home/.devcontainer/scripts/lib"
cp "$script_dir/lib/env.sh" "$script_dir/lib/paths.sh" "$env_source_home/.devcontainer/scripts/lib/"
printf 'GITHUB_PERSONAL_ACCESS_TOKEN=host-token\n' > "$env_source_home/.env"
printf 'GITHUB_PAT=bad\rvalue\n' > "$env_source_home/.env.local"
check "source semantics: malformed later source clears the credential" \
  bash -c "cd '$env_source_home' && unset GITHUB_PERSONAL_ACCESS_TOKEN GITHUB_PAT GH_TOKEN GITHUB_TOKEN GITHUB_MCP_PAT && source .devcontainer/scripts/lib/env.sh && [ -z \"\${GITHUB_PERSONAL_ACCESS_TOKEN:-}\" ]"
printf 'GITHUB_PAT=local-token\n' > "$env_source_home/.env.local"
check "source semantics: later alias source overrides inherited" \
  bash -c "cd '$env_source_home' && unset GITHUB_PERSONAL_ACCESS_TOKEN GITHUB_PAT GH_TOKEN GITHUB_TOKEN GITHUB_MCP_PAT && source .devcontainer/scripts/lib/env.sh && [[ \$GITHUB_PERSONAL_ACCESS_TOKEN == local-token && \$GH_TOKEN == local-token ]]"
printf 'GITHUB_PERSONAL_ACCESS_TOKEN=\n' > "$env_source_home/.env.local"
check "source semantics: blank later mention keeps the inherited key" \
  bash -c "cd '$env_source_home' && unset GITHUB_PERSONAL_ACCESS_TOKEN GITHUB_PAT GH_TOKEN GITHUB_TOKEN GITHUB_MCP_PAT && source .devcontainer/scripts/lib/env.sh && [[ \$GITHUB_PERSONAL_ACCESS_TOKEN == host-token ]]"
printf 'Z_AI_API_KEY=env-key\n' > "$env_source_home/.env"
printf 'OPENAI_API_KEY=x\n' > "$env_source_home/.env.local"
check "source semantics: unmentioned later file keeps the .env value" \
  bash -c "cd '$env_source_home' && unset Z_AI_API_KEY ZAI_API_KEY ZHIPU_API_KEY OPENAI_API_KEY && source .devcontainer/scripts/lib/env.sh && [[ \$Z_AI_API_KEY == env-key ]]"
printf 'ZAI_API_KEY=a\nZHIPU_API_KEY=b\n' > "$env_source_home/.env.local"
check "source semantics: competing aliases in one source are cleared" \
  bash -c "cd '$env_source_home' && unset Z_AI_API_KEY ZAI_API_KEY ZHIPU_API_KEY && source .devcontainer/scripts/lib/env.sh && [ -z \"\${Z_AI_API_KEY:-}\" ]"
printf 'Z_AI_MODE=ZHIPU\n' > "$env_source_home/.env"
printf 'OPENAI_API_KEY=x\n' > "$env_source_home/.env.local"
check "source semantics: mode persists when a later file omits it" \
  bash -c "cd '$env_source_home' && unset Z_AI_MODE Z_AI_API_KEY ZAI_API_KEY ZHIPU_API_KEY && source .devcontainer/scripts/lib/env.sh && [[ \$Z_AI_MODE == ZHIPU ]]"
rm -rf "$env_source_home"

echo "== shared path + lock resolvers (lockstep with the Node writer) =="
check "opencode path: OPENCODE_CONFIG_DIR outranks OPENCODE_CONFIG" \
  bash -c '
    home="$(mktemp -d)"
    export HOME="$home" OPENCODE_CONFIG_DIR="$home/custom" OPENCODE_CONFIG="$home/other/opencode.json"
    source "$1"
    resolved="$(devcontainer_opencode_config_file)"
    [[ "$resolved" == "$home/custom/opencode.json" ]]
  ' _ "$script_dir/lib/paths.sh"
check "opencode path: OPENCODE_CONFIG still resolves when no custom dir is set" \
  bash -c '
    home="$(mktemp -d)"
    export HOME="$home" OPENCODE_CONFIG="$home/other/opencode.jsonc"
    unset OPENCODE_CONFIG_DIR
    source "$1"
    [[ "$(devcontainer_opencode_config_file)" == "$home/other/opencode.jsonc" ]]
  ' _ "$script_dir/lib/paths.sh"
check "config lock: dead-owner lock is taken over immediately" \
  bash -c '
    set -eu
    home="$(mktemp -d)"
    export HOME="$home"
    source "$1"
    lock_dir="$home/.cache/signal-fish-devcontainer/mcp-config.lock.d"
    mkdir -p "$lock_dir"
    ( sleep 0 ) & dead_pid=$!
    wait "$dead_pid" || true
    printf "%s-1-1\n" "$dead_pid" > "$lock_dir/owner"
    token="$(devcontainer_acquire_config_lock)" || exit 1
    devcontainer_release_config_lock "$token"
  ' _ "$script_dir/lib/paths.sh"

echo "== MCP config sync (with injected throwaway credentials) =="
ENV_FILE_BACKUP="$repo_root/.env.selftest-backup"
env_file_state="untouched"
if [ -f "$repo_root/.env.local" ]; then
  env_state="backed-up"
  mv -f "$repo_root/.env.local" "$ENV_BACKUP"
else
  env_state="created"
fi
if [ -f "$repo_root/.env" ]; then
  env_file_state="backed-up"
  mv -f "$repo_root/.env" "$ENV_FILE_BACKUP"
fi
printf 'GITHUB_PERSONAL_ACCESS_TOKEN=self-test-gh-pat\nZ_AI_API_KEY=self-test-zai-key\n' > "$repo_root/.env.local"
HOME="$selftest_home" AI_BACKENDS_BIN_DIR="$selftest_bin" bash "$script_dir/../ai-backends.sh" install >/dev/null 2>&1
must_run "post-start.sh (throwaway credentials, private HOME)" \
  env -u DEVCONTAINER_ENV_SKIP_FILES bash "$script_dir/post-start.sh"
check "AI backends installed into the disposable bin" \
  bash -c "test -x '$selftest_bin/claude-zai' && test -x '$selftest_bin/codex-openrouter'"
check "disposable launchers resolve to the repository installer" \
  bash -c "test -x '$selftest_bin/claude-zai' && grep -q 'isolated AI backends' \"\$(readlink -f '$selftest_bin/claude-zai')\""

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
json_assert "opencode: native v2 server map" "$HOME/.config/opencode/opencode.json" \
  '(() => { const names = ["github", "zai-vision", "zai-web-search", "zai-web-reader", "zai-zread", "microsoft-docs", "context7", "git"]; return obj.$schema === "https://opencode.ai/config.json" && obj.mcp?.servers && names.every(name => Object.hasOwn(obj.mcp.servers, name)) && names.every(name => !Object.hasOwn(obj.mcp, name)); })()'
json_assert "opencode: v2 enablement and Code Mode defaults" "$HOME/.config/opencode/opencode.json" \
  '["github", "zai-vision", "zai-web-search", "zai-web-reader", "zai-zread", "microsoft-docs", "context7", "git"].every(name => !Object.hasOwn(obj.mcp.servers[name], "enabled") && obj.mcp.servers[name].codemode !== false)'
json_assert "opencode: github remote with oauth disabled" "$HOME/.config/opencode/opencode.json" \
  'obj.mcp.servers.github.type === "remote" && obj.mcp.servers.github.oauth === false && obj.mcp.servers.github.headers.Authorization.includes("{env:GITHUB_PERSONAL_ACCESS_TOKEN}")'
json_assert "opencode: zai-vision local via global bin" "$HOME/.config/opencode/opencode.json" \
  'obj.mcp.servers["zai-vision"].command[0] === "zai-mcp-server"'
json_assert "opencode: zai remote auth via env ref (never a literal)" "$HOME/.config/opencode/opencode.json" \
  'obj.mcp.servers["zai-web-search"].headers.Authorization.includes("{env:Z_AI_API_KEY}")'
json_assert "opencode: keyless remote servers disable OAuth" "$HOME/.config/opencode/opencode.json" \
  'obj.mcp.servers["microsoft-docs"].oauth === false && obj.mcp.servers.context7.oauth === false'
check "opencode debug config exposes managed server" bash -lc 'opencode debug config | grep -q "github"'
check "opencode MCP list eventually registers managed servers" bash -lc '
  for attempt in 1 2 3 4 5; do
    if output="$(timeout 10s opencode mcp list 2>/dev/null)"; then
      if ! grep -Fxq "No MCP servers configured" <<<"$output"; then
        exit 0
      fi
    fi
    sleep 1
  done
  exit 1
'
check "opencode config is owner-only" bash -c 'test "$(stat -c %a "$HOME/.config/opencode/opencode.json")" = 600'
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
check "MCP integration fixture discovers its tool" \
  node "$script_dir/lib/mcp-probe.mjs" -- node "$script_dir/lib/mcp-test-server.mjs"

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
json_assert "opencode: missing GitHub credential disables remote" "$prune_home/.config/opencode/opencode.json" \
  'obj.mcp.servers.github.disabled === true'
json_assert "opencode: missing Z.AI credential disables managed servers" "$prune_home/.config/opencode/opencode.json" \
  '["zai-vision", "zai-web-search", "zai-web-reader", "zai-zread"].every(name => obj.mcp.servers[name].disabled === true)'
json_assert "opencode: keyless servers remain enabled" "$prune_home/.config/opencode/opencode.json" \
  '["microsoft-docs", "context7", "git"].every(name => obj.mcp.servers[name].disabled !== true)'
rm -rf "$prune_home"

echo "== config writer fail-closed behavior =="
malformed_home="$(mktemp -d)"
mkdir -p "$malformed_home/.config/opencode"
printf '{' > "$malformed_home/.config/opencode/opencode.json"
if env -u GITHUB_PERSONAL_ACCESS_TOKEN -u GITHUB_PAT -u GH_TOKEN -u GITHUB_TOKEN -u GITHUB_MCP_PAT \
  -u Z_AI_API_KEY -u ZAI_API_KEY -u ZHIPU_API_KEY HOME="$malformed_home" \
  node "$script_dir/lib/write-mcp-configs.mjs" >/dev/null 2>&1; then
  check "malformed managed config is rejected" false
else
  check "malformed managed config is rejected" true
fi
rm -rf "$malformed_home"

echo "== config writer transaction and JSONC edge cases =="
transaction_home="$(mktemp -d)"
mkdir -p "$transaction_home/.copilot" "$transaction_home/.config/opencode" \
  "$transaction_home/.config/nanocoder" "$transaction_home/.gemini" "$transaction_home/.cursor"
printf '{"sentinel":"claude"}\n' > "$transaction_home/.claude.json"
printf '{"sentinel":"copilot"}\n' > "$transaction_home/.copilot/mcp-config.json"
printf '{"sentinel":"opencode"}\n' > "$transaction_home/.config/opencode/opencode.json"
printf '{"sentinel":"nanocoder"}\n' > "$transaction_home/.config/nanocoder/.mcp.json"
printf '{"sentinel":"cursor"}\n' > "$transaction_home/.cursor/mcp.json"
printf '{' > "$transaction_home/.gemini/settings.json"
transaction_files=("$transaction_home/.claude.json" "$transaction_home/.copilot/mcp-config.json" \
  "$transaction_home/.config/opencode/opencode.json" "$transaction_home/.config/nanocoder/.mcp.json" \
  "$transaction_home/.cursor/mcp.json")
transaction_before="$(sha256sum "${transaction_files[@]}")"
if env "${unset_gh[@]}" "${unset_zai[@]}" HOME="$transaction_home" \
  node "$script_dir/lib/write-mcp-configs.mjs" >/dev/null 2>&1; then
  check "config writer rejects a malformed later target" false
else
  check "config writer rejects a malformed later target" true
fi
transaction_after="$(sha256sum "${transaction_files[@]}")"
check "config writer preflights all targets before writing" test "$transaction_before" = "$transaction_after"
rm -rf "$transaction_home"

jsonc_edge_home="$(mktemp -d)"
mkdir -p "$jsonc_edge_home/.config/opencode"
cat > "$jsonc_edge_home/.config/opencode/opencode.jsonc" <<'JSONC'
{
  // This file has no mcp object yet.
  "model": "test/model"
}
JSONC
env "${unset_gh[@]}" "${unset_zai[@]}" HOME="$jsonc_edge_home" Z_AI_MODE=ZAI \
  node "$script_dir/lib/write-mcp-configs.mjs" >/dev/null 2>&1
json_assert "opencode: JSONC without mcp receives managed servers" "$jsonc_edge_home/.config/opencode/opencode.jsonc" \
  'obj.model === "test/model" && obj.mcp.servers.github.type === "remote"'
check "opencode: JSONC edge comment is preserved" grep -q 'no mcp object yet' "$jsonc_edge_home/.config/opencode/opencode.jsonc"
jsonc_edge_hash="$(sha256sum "$jsonc_edge_home/.config/opencode/opencode.jsonc" | cut -d" " -f1)"
env "${unset_gh[@]}" "${unset_zai[@]}" HOME="$jsonc_edge_home" Z_AI_MODE=ZAI \
  node "$script_dir/lib/write-mcp-configs.mjs" >/dev/null 2>&1
jsonc_edge_hash_after="$(sha256sum "$jsonc_edge_home/.config/opencode/opencode.jsonc" | cut -d" " -f1)"
check "opencode: JSONC without mcp is byte-idempotent" test "$jsonc_edge_hash" = "$jsonc_edge_hash_after"
rm -rf "$jsonc_edge_home"

echo "== OpenCode config preservation and JSONC selection =="
preserve_home="$(mktemp -d)"
mkdir -p "$preserve_home/.config/opencode"
cat > "$preserve_home/.config/opencode/opencode.jsonc" <<'JSONC'
{
  // Keep user policy and unrelated servers.
  "update": "disable",
  "mcp": {
    "servers": {
      "custom": {
        "type": "remote",
        "url": "https://example.test/mcp",
        "codemode": false
      }
    }
  }
}
JSONC
preserve_env=(env "${unset_gh[@]}" "${unset_zai[@]}" HOME="$preserve_home" Z_AI_MODE=ZAI GITHUB_PERSONAL_ACCESS_TOKEN=self-test-gh-pat Z_AI_API_KEY=self-test-zai-key)
"${preserve_env[@]}" node "$script_dir/lib/write-mcp-configs.mjs" >/dev/null 2>&1
json_assert "opencode: preserves user update policy" "$preserve_home/.config/opencode/opencode.jsonc" \
  'obj.update === "disable"'
json_assert "opencode: preserves unrelated JSONC server" "$preserve_home/.config/opencode/opencode.jsonc" \
  'obj.mcp.servers.custom.type === "remote" && obj.mcp.servers.custom.codemode === false'
preserve_hash_before="$(sha256sum "$preserve_home/.config/opencode/opencode.jsonc" | cut -d" " -f1)"
"${preserve_env[@]}" node "$script_dir/lib/write-mcp-configs.mjs" >/dev/null 2>&1
preserve_hash_after="$(sha256sum "$preserve_home/.config/opencode/opencode.jsonc" | cut -d" " -f1)"
check "opencode: JSONC sync is byte-idempotent" test "$preserve_hash_before" = "$preserve_hash_after"
both_home="$(mktemp -d)"
mkdir -p "$both_home/.config/opencode"
printf '{}\n' > "$both_home/.config/opencode/opencode.json"
printf '{}\n' > "$both_home/.config/opencode/opencode.jsonc"
if env -u GITHUB_PERSONAL_ACCESS_TOKEN -u GITHUB_PAT -u GH_TOKEN -u GITHUB_TOKEN -u GITHUB_MCP_PAT \
  -u Z_AI_API_KEY -u ZAI_API_KEY -u ZHIPU_API_KEY HOME="$both_home" \
  node "$script_dir/lib/write-mcp-configs.mjs" >/dev/null 2>&1; then
  check "opencode: rejects ambiguous json and jsonc files" false
else
  check "opencode: rejects ambiguous json and jsonc files" true
fi
rm -rf "$preserve_home" "$both_home"

echo "== OpenCode effective configuration paths (overrides) =="
xdg_home="$(mktemp -d)"
mkdir -p "$xdg_home/xdg/opencode"
printf '{"model":"xdg/model"}\n' > "$xdg_home/xdg/opencode/opencode.json"
env "${unset_gh[@]}" "${unset_zai[@]}" HOME="$xdg_home" XDG_CONFIG_HOME="$xdg_home/xdg" \
  node "$script_dir/lib/write-mcp-configs.mjs" >/dev/null 2>&1
json_assert "opencode: XDG_CONFIG_HOME target receives managed servers" "$xdg_home/xdg/opencode/opencode.json" \
  'obj.model === "xdg/model" && obj.mcp.servers.github.type === "remote"'
check "opencode: default config untouched under XDG override" \
  bash -c '[ ! -e "$1/.config/opencode/opencode.json" ]' _ "$xdg_home"
rm -rf "$xdg_home"

custom_config_home="$(mktemp -d)"
mkdir -p "$custom_config_home/conf"
printf '{"model":"custom/model"}\n' > "$custom_config_home/conf/custom.json"
env "${unset_gh[@]}" "${unset_zai[@]}" HOME="$custom_config_home" OPENCODE_CONFIG="$custom_config_home/conf/custom.json" \
  node "$script_dir/lib/write-mcp-configs.mjs" >/dev/null 2>&1
json_assert "opencode: OPENCODE_CONFIG target receives managed servers" "$custom_config_home/conf/custom.json" \
  'obj.model === "custom/model" && obj.mcp.servers["microsoft-docs"].url === "https://learn.microsoft.com/api/mcp"'
check "opencode: default config untouched under OPENCODE_CONFIG" \
  bash -c '[ ! -e "$1/.config/opencode/opencode.json" ]' _ "$custom_config_home"
rm -rf "$custom_config_home"

custom_dir_home="$(mktemp -d)"
mkdir -p "$custom_dir_home/customdir"
printf '{"model":"dir/model"}\n' > "$custom_dir_home/customdir/opencode.jsonc"
env "${unset_gh[@]}" "${unset_zai[@]}" HOME="$custom_dir_home" OPENCODE_CONFIG_DIR="$custom_dir_home/customdir" \
  node "$script_dir/lib/write-mcp-configs.mjs" >/dev/null 2>&1
json_assert "opencode: OPENCODE_CONFIG_DIR target receives managed servers" "$custom_dir_home/customdir/opencode.jsonc" \
  'obj.model === "dir/model" && obj.mcp.servers.github.type === "remote"'
rm -rf "$custom_dir_home"

content_conflict_home="$(mktemp -d)"
if env "${unset_gh[@]}" "${unset_zai[@]}" HOME="$content_conflict_home" \
  OPENCODE_CONFIG_CONTENT='{"mcp":{"servers":{"github":{"type":"remote","url":"https://override.test/mcp"}}}}' \
  node "$script_dir/lib/write-mcp-configs.mjs" >/dev/null 2>&1; then
  check "opencode: rejects OPENCODE_CONFIG_CONTENT managed override" false
else
  check "opencode: rejects OPENCODE_CONFIG_CONTENT managed override" true
fi
check "opencode: inline-conflict rejection writes nothing" \
  bash -c '[ ! -e "$1/.config/opencode/opencode.json" ]' _ "$content_conflict_home"
rm -rf "$content_conflict_home"

symlinked_opencode_home="$(mktemp -d)"
mkdir -p "$symlinked_opencode_home/real-opencode" "$symlinked_opencode_home/.config"
ln -s "$symlinked_opencode_home/real-opencode" "$symlinked_opencode_home/.config/opencode"
if env "${unset_gh[@]}" "${unset_zai[@]}" HOME="$symlinked_opencode_home" \
  node "$script_dir/lib/write-mcp-configs.mjs" >/dev/null 2>&1; then
  check "opencode: refuses a symlinked config directory" false
else
  check "opencode: refuses a symlinked config directory" true
fi
check "opencode: symlinked directory target stays untouched" \
  bash -c '[ ! -e "$1/real-opencode/opencode.json" ]' _ "$symlinked_opencode_home"
rm -rf "$symlinked_opencode_home"

concurrent_home="$(mktemp -d)"
env "${unset_gh[@]}" "${unset_zai[@]}" HOME="$concurrent_home" GITHUB_PERSONAL_ACCESS_TOKEN=writer-a \
  node "$script_dir/lib/write-mcp-configs.mjs" >/dev/null 2>&1
env "${unset_zai[@]}" HOME="$concurrent_home" GITHUB_PERSONAL_ACCESS_TOKEN=writer-b \
  node "$script_dir/lib/write-mcp-configs.mjs" >/dev/null 2>&1 &
writer_a_pid=$!
env "${unset_zai[@]}" HOME="$concurrent_home" GITHUB_PERSONAL_ACCESS_TOKEN=writer-c \
  node "$script_dir/lib/write-mcp-configs.mjs" >/dev/null 2>&1 &
writer_b_pid=$!
wait "$writer_a_pid" "$writer_b_pid"
json_assert "opencode: concurrent writers serialize on one final credential" "$concurrent_home/.claude.json" \
  '["writer-b","writer-c"].includes(obj.mcpServers.github.headers.Authorization.slice("Bearer ".length))'
rm -rf "$concurrent_home"

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

codex_symlink_home="$(mktemp -d)"
mkdir -p "$codex_symlink_home/.codex"
printf 'sentinel-codex\n' > "$codex_symlink_home/.codex/target.toml"
ln -s "$codex_symlink_home/.codex/target.toml" "$codex_symlink_home/.codex/config.toml"
if env -u CODEX_HOME HOME="$codex_symlink_home" Z_AI_API_KEY=k Z_AI_MODE=ZAI \
  bash -c 'source "$1"; devcontainer_write_codex_mcp' _ "$script_dir/lib/mcp-config.sh" >/dev/null 2>&1; then
  check "codex: refuses a symlinked config" false
else
  check "codex: refuses a symlinked config" true
fi
check "codex: symlink target remains unchanged" grep -q '^sentinel-codex$' "$codex_symlink_home/.codex/target.toml"
rm -rf "$codex_symlink_home"

codex_escape_home="$(mktemp -d)"
codex_escape_key="quote\"slash\\\\"
env HOME="$codex_escape_home" CODEX_HOME="$codex_escape_home/custom-codex" Z_AI_API_KEY="$codex_escape_key" Z_AI_MODE=ZAI \
  bash -c 'source "$1"; devcontainer_write_codex_mcp' _ "$script_dir/lib/mcp-config.sh" >/dev/null 2>&1
check "codex: TOML credential escaping parses" env CODEX_HOME="$codex_escape_home/custom-codex" codex mcp list
check "codex: generated config is owner-only" bash -c 'test "$(stat -c %a "$1")" = 600' _ "$codex_escape_home/custom-codex/config.toml"
check "codex: default HOME config untouched by custom CODEX_HOME" \
  bash -c '[ ! -e "$1/.codex/config.toml" ]' _ "$codex_escape_home"
rm -rf "$codex_escape_home"

codex_crlf_home="$(mktemp -d)"
mkdir -p "$codex_crlf_home/.codex"
printf 'user_setting = "keep"\r\n\r\n# >>> signal-fish-devcontainer managed MCP (regenerated on start; do not edit) <<<\r\n[mcp_servers.github]\r\nurl = "https://stale.test/mcp"\r\n# <<< signal-fish-devcontainer managed MCP <<<\r\n' \
  > "$codex_crlf_home/.codex/config.toml"
env -u CODEX_HOME HOME="$codex_crlf_home" Z_AI_API_KEY=crlf-key Z_AI_MODE=ZAI \
  bash -c 'source "$1"; devcontainer_write_codex_mcp' _ "$script_dir/lib/mcp-config.sh" >/dev/null 2>&1
check "codex: CRLF managed block is replaced, not duplicated" \
  bash -c '[[ $(grep -cF "signal-fish-devcontainer managed MCP" "$1/.codex/config.toml") -eq 2 ]]' _ "$codex_crlf_home"
check "codex: CRLF stale table is removed" \
  bash -c '! grep -q "stale.test" "$1/.codex/config.toml"' _ "$codex_crlf_home"
check "codex: CRLF user content preserved" \
  bash -c 'grep -qF "user_setting = \"keep\"" "$1/.codex/config.toml"' _ "$codex_crlf_home"
check "codex: CRLF config parses" env CODEX_HOME="$codex_crlf_home/.codex" codex mcp list
rm -rf "$codex_crlf_home"

codex_symlink_parent_home="$(mktemp -d)"
mkdir -p "$codex_symlink_parent_home/real-codex"
ln -s "$codex_symlink_parent_home/real-codex" "$codex_symlink_parent_home/.codex"
if env -u CODEX_HOME HOME="$codex_symlink_parent_home" Z_AI_API_KEY=k Z_AI_MODE=ZAI \
  bash -c 'source "$1"; devcontainer_write_codex_mcp' _ "$script_dir/lib/mcp-config.sh" >/dev/null 2>&1; then
  check "codex: refuses a symlinked config directory" false
else
  check "codex: refuses a symlinked config directory" true
fi
check "codex: symlinked directory target stays empty" \
  bash -c '[ ! -e "$1/real-codex/config.toml" ]' _ "$codex_symlink_parent_home"
rm -rf "$codex_symlink_parent_home"

echo "== sync is all-or-nothing across Node and Codex targets =="
atomic_home="$(mktemp -d)"
mkdir -p "$atomic_home/.config/opencode"
printf '{' > "$atomic_home/.config/opencode/opencode.json"
if env "${unset_gh[@]}" "${unset_zai[@]}" HOME="$atomic_home" Z_AI_API_KEY=k Z_AI_MODE=ZAI \
  bash -c 'source "$1"; devcontainer_sync_mcp_configs "$2"' \
  _ "$script_dir/lib/mcp-config.sh" "$script_dir" >/dev/null 2>&1; then
  check "sync: malformed managed config fails the whole sync" false
else
  check "sync: malformed managed config fails the whole sync" true
fi
check "sync: codex config absent after failed sync" \
  bash -c '[ ! -e "$1/.codex/config.toml" ]' _ "$atomic_home"
check "sync: claude config absent after failed sync" \
  bash -c '[ ! -e "$1/.claude.json" ]' _ "$atomic_home"
rm -rf "$atomic_home"

echo "== isolated AI backend launchers (claude-zai, claude-openrouter, codex-zai, codex-openrouter) =="
check "ai-backends.sh syntax" bash -n "$script_dir/../ai-backends.sh"
for launcher in claude-zai claude-openrouter codex-zai codex-openrouter; do
  check "launcher installed: $launcher" test -x "$selftest_bin/$launcher"
done
check "launchers resolve the installer script" \
  bash -c "grep -q 'isolated AI backends' \"\$(readlink -f '$selftest_bin/claude-zai')\""
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
  # Throwaway test credentials only; the exact value proves WHICH alias won.
  printf 'auth_token=%s\n' "${ANTHROPIC_AUTH_TOKEN-unset}"
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
  # Throwaway test credentials only; the exact value proves WHICH alias won.
  printf 'zai_key=%s\n' "${ZAI_API_KEY-unset}"
  printf 'or_key=%s\n' "${OPENROUTER_API_KEY-unset}"
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
  bash -c 'grep -q "^auth_token=self-test-zai-key$" "'"${claude_stub_log}"'"'
check "claude-zai: zai anthropic base url" \
  bash -c 'grep -q "^base_url=https://api.z.ai/api/anthropic$" "'"${claude_stub_log}"'"'
check "claude-zai: canonical zai keys scrubbed" \
  bash -c 'grep -q "^zai_key=unset$" "'"${claude_stub_log}"'" && grep -q "^zai_alias_key=unset$" "'"${claude_stub_log}"'"'
check "claude-zai: isolated config dir" \
  bash -c "grep -q '^config_dir=${stub_home}/.claude-zai\$' '${claude_stub_log}'"
check "claude-zai: glm model aliases mapped" \
  bash -c 'grep -q "^sonnet=glm-5.3\[1m\]$" "'"${claude_stub_log}"'" && grep -q "^haiku=glm-5.3-flash\[1m\]$" "'"${claude_stub_log}"'"'

# Exact credential selection: the file's ZAI_API_KEY alias must reach the
# launcher verbatim (env.sh clears alias names in lifecycle shells, so the
# launcher's own file resolution is what these assertions pin down).
check "claude-zai: file alias key selected exactly" \
  bash -c 'grep -q "^auth_token=self-test-zai-key$" "'"${claude_stub_log}"'"'

# Precedence: canonical Z_AI_API_KEY beats an ambient ZAI_API_KEY alias.
claude_stub_log="${stub_home}/claude-zai-precedence.log"
PATH="${stub_bin}:${PATH}" HOME="${stub_home}" STUB_LOG="${claude_stub_log}" \
  AI_BACKENDS_ENV_LOCAL="${stub_env}" AI_BACKENDS_CONTAINER_MODE=yes \
  Z_AI_API_KEY=self-test-zai-canonical ZAI_API_KEY=self-test-zai-stale \
  claude-zai --print hello 2>/dev/null || true
check "claude-zai: canonical Z_AI_API_KEY wins over ambient alias" \
  bash -c 'grep -q "^auth_token=self-test-zai-canonical$" "'"${claude_stub_log}"'"'

# Conflicting aliases without a canonical value must be rejected, not guessed.
if PATH="${stub_bin}:${PATH}" HOME="${stub_home}" \
  AI_BACKENDS_ENV_LOCAL="${stub_env}" AI_BACKENDS_CONTAINER_MODE=yes \
  ZAI_API_KEY=self-test-zai-a ZHIPU_API_KEY=self-test-zai-b \
  claude-zai --print hello >"${stub_home}/claude-zai-conflict.log" 2>&1; then
  check "claude-zai: competing env aliases are rejected" false
else
  check "claude-zai: competing env aliases are rejected" true
fi
check "claude-zai: competing env alias conflict is explained" \
  bash -c 'grep -q "competing Z.AI aliases disagree" "'"${stub_home}/claude-zai-conflict.log"'"'
conflict_env="$(mktemp)"
printf 'ZAI_API_KEY=self-test-zai-a\nZHIPU_API_KEY=self-test-zai-b\n' >"$conflict_env"
if PATH="${stub_bin}:${PATH}" HOME="${stub_home}" \
  AI_BACKENDS_ENV_LOCAL="${conflict_env}" AI_BACKENDS_CONTAINER_MODE=yes \
  claude-zai --print hello >"${stub_home}/claude-zai-conflict2.log" 2>&1; then
  check "claude-zai: competing file aliases are rejected" false
else
  check "claude-zai: competing file aliases are rejected" true
fi
check "claude-zai: file alias conflict is explained" \
  bash -c 'grep -q "competing Z.AI aliases disagree" "'"${stub_home}/claude-zai-conflict2.log"'"'
rm -f "$conflict_env"

claude_stub_log="${stub_home}/claude-or.log"
PATH="${stub_bin}:${PATH}" HOME="${stub_home}" STUB_LOG="${claude_stub_log}" \
  AI_BACKENDS_ENV_LOCAL="${stub_env}" AI_BACKENDS_CONTAINER_MODE=yes \
  claude-openrouter --print hello 2>/dev/null || true
check "claude-openrouter: openrouter token via ANTHROPIC_AUTH_TOKEN" \
  bash -c 'grep -q "^auth_token=self-test-or-key$" "'"${claude_stub_log}"'"'
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
check "codex-zai: zai key exported to codex (exact alias value)" \
  bash -c 'grep -q "^zai_key=self-test-zai-key$" "'"${stub_home}/codex-zai.log"'"'
check "codex-zai: profile + model + catalog via argv" \
  bash -c 'grep -q "^--profile$" "'"${codex_stub_args}"'" && grep -q "^zai$" "'"${codex_stub_args}"'" && grep -q "^glm-5.3$" "'"${codex_stub_args}"'" && grep -q "model_catalog_json" "'"${codex_stub_args}"'"'
check "codex-zai: key never appears in argv" \
  bash -c '! grep -q "self-test-zai-key" "'"${codex_stub_args}"'"'

codex_stub_args="${stub_home}/codex-or.args"
PATH="${stub_bin}:${PATH}" HOME="${stub_home}" STUB_LOG="${stub_home}/codex-or.log" \
  STUB_ARGS="${codex_stub_args}" AI_BACKENDS_ENV_LOCAL="${stub_env}" \
  codex-openrouter exec hello 2>/dev/null || true
check "codex-openrouter: openrouter key exported to codex (exact value)" \
  bash -c 'grep -q "^or_key=self-test-or-key$" "'"${stub_home}/codex-or.log"'"'
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

echo "== OpenCode service restart propagation (stubbed CLI) =="
# The restart/readiness path must fail the lifecycle visibly instead of
# downgrading failures to warnings.
poststart_stub="$(mktemp -d)"
cat > "$poststart_stub/opencode" <<'STUB'
#!/usr/bin/env bash
case "$1 $2" in
  "--version "*) printf '%s\n' 'opencode v2.0.16'; exit 0 ;;
  "service status") printf '%s\n' 'opencode service is running at http://127.0.0.1:4096'; exit 0 ;;
  "service restart")
    if [ "${RESTART_FAIL:-0}" = "1" ]; then
      printf 'restart failed\n' >&2
      exit 7
    fi
    exit 0
    ;;
  "mcp list")
    if [ "${MCP_EMPTY:-0}" = "1" ]; then
      printf '%s\n' 'No MCP servers configured'
    else
      printf '%s\n' '✓ github connected' '✓ zai-vision connected' '✓ zai-web-search connected' \
        '✓ zai-web-reader connected' '✓ zai-zread connected' '✓ microsoft-docs connected' \
        '✓ context7 connected' '✓ git connected'
    fi
    exit 0
    ;;
esac
printf 'unexpected stub call: %s\n' "$*" >&2
exit 9
STUB
chmod 755 "$poststart_stub/opencode"
poststart_case_home="$(mktemp -d)"
mkdir -p "$poststart_case_home/.cache/signal-fish-devcontainer"
date -u +%Y-%m-%dT%H:%M:%SZ > "$poststart_case_home/.cache/signal-fish-devcontainer/cli-install.stamp"
poststart_case_bin="$(mktemp -d)"

if env -u CODEX_HOME PATH="$poststart_stub:$PATH" HOME="$poststart_case_home" \
  AI_BACKENDS_BIN_DIR="$poststart_case_bin" RESTART_FAIL=1 \
  bash "$script_dir/post-start.sh" >/dev/null 2>&1; then
  check "post-start: failed service restart fails the lifecycle" false
else
  check "post-start: failed service restart fails the lifecycle" true
fi
check "post-start: failed restart leaves a pending marker for the next start" \
  bash -c 'test -f "$1/.cache/signal-fish-devcontainer/opencode-restart.pending"' _ "$poststart_case_home"

must_run "post-start: successful restart with full MCP registration passes" \
  env -u CODEX_HOME PATH="$poststart_stub:$PATH" HOME="$poststart_case_home" \
    AI_BACKENDS_BIN_DIR="$poststart_case_bin" RESTART_FAIL=0 \
    bash "$script_dir/post-start.sh"
check "post-start: successful restart clears the pending marker" \
  bash -c '[ ! -f "$1/.cache/signal-fish-devcontainer/opencode-restart.pending" ]' _ "$poststart_case_home"

# Nothing changed since the passing run: only a pending marker forces the
# reconciliation path, which is exactly the retry semantics under test here.
: > "$poststart_case_home/.cache/signal-fish-devcontainer/opencode-restart.pending"
if env -u CODEX_HOME PATH="$poststart_stub:$PATH" HOME="$poststart_case_home" \
  AI_BACKENDS_BIN_DIR="$poststart_case_bin" MCP_EMPTY=1 \
  bash "$script_dir/post-start.sh" >/dev/null 2>&1; then
  check "post-start: exhausted MCP registration wait fails the lifecycle" false
else
  check "post-start: exhausted MCP registration wait fails the lifecycle" true
fi
check "post-start: exhausted MCP wait leaves a pending marker" \
  bash -c 'test -f "$1/.cache/signal-fish-devcontainer/opencode-restart.pending"' _ "$poststart_case_home"
rm -rf "$poststart_stub" "$poststart_case_home" "$poststart_case_bin"

echo "== idempotency (double sync produces byte-identical configs) =="
files=("$HOME/.claude.json" "$HOME/.copilot/mcp-config.json" "$HOME/.config/opencode/opencode.json" "$HOME/.config/nanocoder/.mcp.json" "$HOME/.gemini/settings.json" "$HOME/.cursor/mcp.json" "$HOME/.codex/config.toml")
before="$(sha256sum "${files[@]}" 2>/dev/null)"
must_run "second post-start.sh (idempotency run)" bash "$script_dir/post-start.sh"
after="$(sha256sum "${files[@]}" 2>/dev/null)"
check "second sync is a no-op" test "$before" = "$after"

echo "== restoring your .env.local and removing isolated service state =="
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
