<#
.SYNOPSIS
    Pre-commit gate for the .llm agentic context system.

.DESCRIPTION
    Runs only what is relevant to the staged changes:
      1. If any .llm file or front-end pointer file is staged, validates the
         whole instruction system. A stale generated index is auto-fixed and
         re-staged; anything else fails the commit.
      2. Enforces the 300-line limit on every staged file inside the
         enforced scope (.llm/** plus pointer files and llms.txt).

    Install once with: pwsh -NoProfile -File scripts/install-hooks.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-StagedFiles {
    $raw = [string](& git diff --cached --name-only --diff-filter=ACMR -z)
    return @($raw -split "`0" | Where-Object { $_ -ne '' })
}

$staged = Get-StagedFiles
if ($staged.Count -eq 0) {
    exit 0
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$pointerFiles = @(
    'CLAUDE.md',
    'AGENTS.md',
    'GEMINI.md',
    '.cursor/rules/signal-fish.mdc',
    '.github/copilot-instructions.md',
    'llms.txt'
)

$llmRelated = @($staged | Where-Object {
        $_ -like '.llm/*' -or $pointerFiles -contains $_
    })

$sizeTargets = @($staged | Where-Object {
        $_ -like '.llm/*' -or $pointerFiles -contains $_
    })

function Invoke-LintStep {
    param([string]$ScriptPath, [hashtable]$Params = @{})

    function Convert-ToQuoted([string]$Value) {
        return "'" + ($Value -replace "'", "''") + "'"
    }

    $paramText = @()
    foreach ($entry in $Params.GetEnumerator()) {
        if ($entry.Value -is [switch] -or $entry.Value -is [bool]) {
            if ($entry.Value) { $paramText += "-$($entry.Key)" }
        }
        elseif ($entry.Value -is [System.Collections.IEnumerable] -and $entry.Value -isnot [string]) {
            $items = (@($entry.Value) | ForEach-Object { Convert-ToQuoted ("$_") }) -join ','
            $paramText += "-$($entry.Key) @($items)"
        }
        else {
            $paramText += "-$($entry.Key) $(Convert-ToQuoted ("$($entry.Value)"))"
        }
    }

    $command = "& '$($ScriptPath -replace "'", "''")' $($paramText -join ' ')"
    $output = & pwsh -NoProfile -Command $command 2>&1
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output   = @($output | ForEach-Object { $_.ToString() })
    }
}

$failed = $false

if ($llmRelated.Count -gt 0) {
    Write-Host 'pre-commit: validating LLM instruction system...'
    $linter = Join-Path $repoRoot 'scripts/lint-llm-instructions.ps1'
    $result = Invoke-LintStep -ScriptPath $linter -Params @{ RepoRoot = $repoRoot }
    if ($result.ExitCode -ne 0) {
        Write-Host 'pre-commit: instruction lint failed; retrying with -Fix (regenerates stale index)...'
        $result = Invoke-LintStep -ScriptPath $linter -Params @{ RepoRoot = $repoRoot; Fix = $true }
        if ($result.ExitCode -ne 0) {
            foreach ($line in $result.Output) { Write-Host "    | $line" }
            Write-Host 'pre-commit: LLM instruction validation failed. Run:' -ForegroundColor Red
            Write-Host '  pwsh -NoProfile -File scripts/lint-llm-instructions.ps1' -ForegroundColor Red
            $failed = $true
        }
        else {
            $indexPath = Join-Path $repoRoot '.llm/skills/index.md'
            & git add -- $indexPath
            Write-Host 'pre-commit: regenerated and re-staged .llm/skills/index.md'
        }
    }
}

if ($sizeTargets.Count -gt 0) {
    $sizer = Join-Path $repoRoot 'scripts/lint-file-sizes.ps1'
    $result = Invoke-LintStep -ScriptPath $sizer -Params @{ RepoRoot = $repoRoot; Paths = $sizeTargets }
    if ($result.ExitCode -ne 0) {
        foreach ($line in $result.Output) { Write-Host "    | $line" }
        Write-Host 'pre-commit: file size limit (300 lines) violated. Run:' -ForegroundColor Red
        Write-Host '  pwsh -NoProfile -File scripts/lint-file-sizes.ps1 -Paths <file>' -ForegroundColor Red
        $failed = $true
    }
}

if ($failed) {
    Write-Host 'pre-commit: commit blocked. Fix the issues above, or bypass once with --no-verify.' -ForegroundColor Red
    exit 1
}

exit 0
