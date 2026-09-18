<#
.SYNOPSIS
    Enforces the hard 300-line limit on LLM context files.

.DESCRIPTION
    Scope (all text files, any extension):
      - everything under .llm/ (context, skills, references, code samples)
      - front-end pointer files (CLAUDE.md, AGENTS.md, GEMINI.md,
        .cursor/rules/*.mdc, .github/copilot-instructions.md)
      - llms.txt

    > MaxLines (default 300): hard error, exit code 1.
    >= WarnLines (default 270): warning ("consider splitting").

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of the scripts directory.

.PARAMETER Paths
    Optional repo-relative (or absolute) paths to check. When omitted, the
    entire scope is checked. The pre-commit hook passes only staged paths.

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-file-sizes.ps1 -VerboseOutput
    pwsh -NoProfile -File scripts/lint-file-sizes.ps1 -Paths .llm/context.md
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string[]]$Paths,
    [int]$MaxLines = 300,
    [int]$WarnLines = 270,
    [switch]$VerboseOutput
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

$pointerFiles = @(
    'CLAUDE.md',
    'AGENTS.md',
    'GEMINI.md',
    '.github/copilot-instructions.md',
    'llms.txt'
)

function Get-EnforcedPaths {
    $found = New-Object 'System.Collections.Generic.List[string]'
    $llmDir = Join-Path $RepoRoot '.llm'
    if (Test-Path -LiteralPath $llmDir) {
        foreach ($file in (Get-ChildItem -LiteralPath $llmDir -Recurse -File)) {
            $found.Add($file.FullName)
        }
    }
    foreach ($pointer in $pointerFiles) {
        $full = Join-Path $RepoRoot $pointer
        if (Test-Path -LiteralPath $full) { $found.Add($full) }
    }
    $cursorRules = Join-Path (Join-Path $RepoRoot '.cursor') 'rules'
    if (Test-Path -LiteralPath $cursorRules) {
        foreach ($file in (Get-ChildItem -LiteralPath $cursorRules -File -Filter '*.mdc')) {
            $found.Add($file.FullName)
        }
    }
    return @($found)
}

function Resolve-TargetPath {
    param([string]$Path)
    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    return Join-Path $RepoRoot ($Path -replace '/', [System.IO.Path]::DirectorySeparatorChar)
}

if ($Paths -and @($Paths).Count -gt 0) {
    $targets = [string[]]@($Paths | ForEach-Object { Resolve-TargetPath -Path $_ })
}
else {
    $targets = [string[]](Get-EnforcedPaths)
}
[System.Array]::Sort($targets, [System.StringComparer]::Ordinal)

$errorCount = 0
$warningCount = 0
$checkedCount = 0

foreach ($target in $targets) {
    if (-not (Test-Path -LiteralPath $target)) {
        Write-Error "lint-file-sizes: path not found: $target"
        $errorCount++
        continue
    }
    $relative = [System.IO.Path]::GetFullPath($target).Substring(
        [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/').Length + 1)
    $relative = $relative -replace '\\', '/'
    $lineCount = @([System.IO.File]::ReadAllLines($target)).Count
    $checkedCount++
    if ($lineCount -gt $MaxLines) {
        Write-Error "$relative : $lineCount lines (hard limit: $MaxLines) - MUST split into smaller files."
        $errorCount++
    }
    elseif ($lineCount -ge $WarnLines) {
        Write-Warning "$relative : $lineCount lines (warning threshold: $WarnLines) - consider splitting."
        $warningCount++
    }
    elseif ($VerboseOutput) {
        Write-Host "ok $relative ($lineCount lines)"
    }
}

$summary = "checked $checkedCount file(s), $errorCount error(s), $warningCount warning(s)."
if ($errorCount -gt 0) {
    Write-Error "lint-file-sizes FAILED: $summary"
    exit 1
}
if ($VerboseOutput) {
    Write-Host "lint-file-sizes passed: $summary"
}
exit 0
