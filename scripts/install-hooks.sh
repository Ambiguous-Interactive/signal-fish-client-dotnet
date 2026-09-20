#!/bin/sh
# Activates the repository git hooks (sets core.hooksPath to .githooks).
# Anchored to the repository the script belongs to, not the caller's
# current directory, so it works when launched by absolute path.
set -e
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repo_root=$(CDPATH= cd -- "$script_dir/.." && pwd)
git -C "$repo_root" config core.hooksPath .githooks
echo "Hooks installed: core.hooksPath -> .githooks"
echo "The pre-commit hook requires pwsh (PowerShell 7+)."
