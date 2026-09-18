#!/bin/sh
# Activates the repository git hooks (sets core.hooksPath to .githooks).
set -e
git config core.hooksPath .githooks
echo "Hooks installed: core.hooksPath -> .githooks"
echo "The pre-commit hook requires pwsh (PowerShell 7+)."
