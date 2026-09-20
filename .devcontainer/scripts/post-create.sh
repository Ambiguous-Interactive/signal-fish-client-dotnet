#!/usr/bin/env bash
# onCreateCommand: runs once per container build/rebuild.
# Heavy work: install every agentic CLI at latest, then sync MCP configs.
set -uo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/env.sh
source "$script_dir/lib/env.sh"
# shellcheck source=lib/install-clis.sh
source "$script_dir/lib/install-clis.sh"
# shellcheck source=lib/mcp-config.sh
source "$script_dir/lib/mcp-config.sh"

echo "[devcontainer] installing agentic CLIs (npm, user prefix: ${NPM_CONFIG_PREFIX:-$HOME/.npm-global})..."
if devcontainer_install_clis; then
  echo "[devcontainer] all CLIs installed."
else
  echo "[devcontainer] WARN: some CLI installs failed; see output above."
fi

echo "[devcontainer] installing isolated AI backend launchers (claude-zai, claude-openrouter, codex-zai, codex-openrouter)..."
if bash "$script_dir/../ai-backends.sh" install; then
  echo "[devcontainer] AI backends installed."
else
  echo "[devcontainer] WARN: ai-backends install failed (native claude/codex still work)."
fi

echo "[devcontainer] syncing MCP configs (claude, codex, copilot, opencode, nanocoder, gemini, cursor)..."
devcontainer_sync_mcp_configs "$script_dir"

# Windows-host bind mounts make git flag dubious ownership, which breaks git
# operations (and install-hooks) inside the container.
workspace_dir="$(pwd)"
git config --global --add safe.directory "$workspace_dir" 2>/dev/null || true

# Activate the repo's git hooks (best effort; pwsh ships with the image).
if command -v pwsh >/dev/null 2>&1; then
  pwsh -NoProfile -File scripts/install-hooks.ps1 >/dev/null 2>&1 \
    && echo "[devcontainer] git hooks installed." \
    || echo "[devcontainer] WARN: install-hooks.ps1 failed (run manually)."
fi

echo "[devcontainer] post-create done. Add secrets to .env.local (see .env.example)."
