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

$staged = @(Get-StagedFiles)
if ($staged.Count -eq 0) {
    exit 0
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$pointerFiles = @(
    'CLAUDE.md',
    'AGENTS.md',
    'GEMINI.md',
    '.github/copilot-instructions.md',
    'llms.txt'
)

# The enforced scope of the .llm linters (mirrors lint-file-sizes.ps1's
# Get-EnforcedPaths): .llm/** plus the pointer files plus every Cursor rule.
# Every file a linter can reject must trigger the hook, or the hook blesses
# commits CI rejects.
$llmRelated = @($staged | Where-Object {
        $_ -like '.llm/*' -or $pointerFiles -contains $_ -or $_ -like '.cursor/rules/*.mdc'
    })

$sizeTargets = $llmRelated

$srcProjects = @($staged | Where-Object { $_ -like 'src/*.csproj' -or $_ -like 'src/**/*.csproj' })

$srcCsFiles = @($staged | Where-Object { $_ -like 'src/*.cs' -or $_ -like 'src/**/*.cs' })

# The LINQ linter rejects injected LINQ from project files too, so it gets
# every staged src/ .cs and .csproj (it filters to its own accepted set).
$linqTargets = @($srcCsFiles + $srcProjects)

# Everything CSharpier formats (must match CI's `csharpier check .` scope).
$formattableFiles = @($staged | Where-Object { $_ -match '\.(cs|csproj|props|targets|slnx)$' })

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

if ($srcProjects.Count -gt 0) {
    Write-Host 'pre-commit: enforcing zero dependencies on src/ projects...'
    $depLinter = Join-Path $repoRoot 'scripts/lint-zero-dependencies.ps1'
    $result = Invoke-LintStep -ScriptPath $depLinter -Params @{ RepoRoot = $repoRoot; Paths = $srcProjects }
    if ($result.ExitCode -ne 0) {
        foreach ($line in $result.Output) { Write-Host "    | $line" }
        Write-Host 'pre-commit: zero-dependency lint failed. Run:' -ForegroundColor Red
        Write-Host '  pwsh -NoProfile -File scripts/lint-zero-dependencies.ps1' -ForegroundColor Red
        $failed = $true
    }
}

if ($linqTargets.Count -gt 0) {
    Write-Host 'pre-commit: enforcing the LINQ ban on src/ files...'
    $linqLinter = Join-Path $repoRoot 'scripts/lint-no-linq.ps1'
    $result = Invoke-LintStep -ScriptPath $linqLinter -Params @{ RepoRoot = $repoRoot; Paths = $linqTargets }
    if ($result.ExitCode -ne 0) {
        foreach ($line in $result.Output) { Write-Host "    | $line" }
        Write-Host 'pre-commit: no-linq lint failed. Run:' -ForegroundColor Red
        Write-Host '  pwsh -NoProfile -File scripts/lint-no-linq.ps1' -ForegroundColor Red
        $failed = $true
    }
}

$thisTargets = @($staged | Where-Object { $_ -match '\.cs$' })
if ($thisTargets.Count -gt 0) {
    Write-Host 'pre-commit: enforcing the this.-qualification ban...'
    $thisLinter = Join-Path $repoRoot 'scripts/lint-no-this-qualification.ps1'
    $result = Invoke-LintStep -ScriptPath $thisLinter -Params @{ RepoRoot = $repoRoot; Paths = $thisTargets }
    if ($result.ExitCode -ne 0) {
        foreach ($line in $result.Output) { Write-Host "    | $line" }
        Write-Host 'pre-commit: no-this-qualification lint failed. Run:' -ForegroundColor Red
        Write-Host '  pwsh -NoProfile -File scripts/lint-no-this-qualification.ps1' -ForegroundColor Red
        $failed = $true
    }

    Write-Host 'pre-commit: enforcing the comment form rules...'
    $commentLinter = Join-Path $repoRoot 'scripts/lint-comment-form.ps1'
    $result = Invoke-LintStep -ScriptPath $commentLinter -Params @{ RepoRoot = $repoRoot; Paths = $thisTargets }
    if ($result.ExitCode -ne 0) {
        foreach ($line in $result.Output) { Write-Host "    | $line" }
        Write-Host 'pre-commit: comment form lint failed. Run:' -ForegroundColor Red
        Write-Host '  pwsh -NoProfile -File scripts/lint-comment-form.ps1' -ForegroundColor Red
        $failed = $true
    }
}

$testCsTargets = @($staged | Where-Object { $_ -match '^tests/.*\.cs$' })
if ($testCsTargets.Count -gt 0) {
    Write-Host 'pre-commit: enforcing the test-name underscore ban...'
    $nameLinter = Join-Path $repoRoot 'scripts/lint-test-names.ps1'
    $result = Invoke-LintStep -ScriptPath $nameLinter -Params @{ RepoRoot = $repoRoot; Paths = $testCsTargets }
    if ($result.ExitCode -ne 0) {
        foreach ($line in $result.Output) { Write-Host "    | $line" }
        Write-Host 'pre-commit: test-name lint failed. Run:' -ForegroundColor Red
        Write-Host '  pwsh -NoProfile -File scripts/lint-test-names.ps1' -ForegroundColor Red
        $failed = $true
    }
}

if ($formattableFiles.Count -gt 0) {
    Write-Host 'pre-commit: checking C# formatting (CSharpier)...'
    & dotnet tool restore --verbosity quiet 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'pre-commit: dotnet tool restore failed (offline?). Restore the CSharpier' -ForegroundColor Red
        Write-Host '  tool first (dotnet tool restore) or bypass once with --no-verify.' -ForegroundColor Red
        $failed = $true
    }
    else {
        $checked = & dotnet tool run csharpier -- check @formattableFiles 2>&1
        if ($LASTEXITCODE -ne 0) {
            $joined = (@($checked) | ForEach-Object { $_.ToString() }) -join "`n"
            if ($joined -match 'Cannot find a tool') {
                Write-Host 'pre-commit: CSharpier is not restored (missing .config/dotnet-tools.json entry).' -ForegroundColor Red
                Write-Host '  Run: dotnet tool restore' -ForegroundColor Red
            }
            else {
                foreach ($line in @($checked)) { Write-Host "    | $line" }
                Write-Host 'pre-commit: formatting check failed. Run:' -ForegroundColor Red
                Write-Host '  dotnet tool run csharpier -- format <files>' -ForegroundColor Red
            }
            $failed = $true
        }
    }
}

if ($failed) {
    Write-Host 'pre-commit: commit blocked. Fix the issues above, or bypass once with --no-verify.' -ForegroundColor Red
    exit 1
}

exit 0
