#!/usr/bin/env bash
# onCreateCommand: runs once per container build/rebuild.
# Heavy work: install every agentic CLI at latest, then sync MCP configs.
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/env.sh
source "$script_dir/lib/env.sh"
# shellcheck source=lib/install-clis.sh
source "$script_dir/lib/install-clis.sh"
# shellcheck source=lib/mcp-config.sh
source "$script_dir/lib/mcp-config.sh"

echo "[devcontainer] installing agentic CLIs (npm, user prefix: ${DEVCONTAINER_NPM_PREFIX:-$NPM_CONFIG_PREFIX})..."
devcontainer_install_clis \
  || { echo "[devcontainer] ERROR: one or more CLI installs failed." >&2; exit 1; }
echo "[devcontainer] all CLIs installed."

echo "[devcontainer] installing isolated AI backend launchers (claude-zai, claude-openrouter, codex-zai, codex-openrouter)..."
bash "$script_dir/../ai-backends.sh" install \
  || { echo "[devcontainer] ERROR: AI backend launcher installation failed." >&2; exit 1; }
echo "[devcontainer] AI backends installed."

# Postcondition checks: the readiness probes are what post-start.sh trusts to
# skip repairs, so create must not succeed without proving the same state.
if ! devcontainer_core_clis_ready; then
  echo "[devcontainer] ERROR: core CLI readiness failed after installation." >&2
  exit 1
fi
if ! devcontainer_ai_backends_ready; then
  echo "[devcontainer] ERROR: AI backend readiness failed after installation." >&2
  exit 1
fi

echo "[devcontainer] syncing MCP configs (claude, codex, copilot, opencode, nanocoder, gemini, cursor)..."
devcontainer_sync_mcp_configs "$script_dir" \
  || { echo "[devcontainer] ERROR: MCP config sync failed." >&2; exit 1; }

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
