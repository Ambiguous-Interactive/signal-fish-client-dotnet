<#
.SYNOPSIS
    Validates the .llm agentic context system.

.DESCRIPTION
    Checks (all must pass):
      1. Every front-end pointer file exists and delegates to .llm/context.md
         via its exact expected relative link.
      2. .llm/context.md has exactly one H1 and links to ./skills/index.md.
      3. Every .llm/skills/<name>/SKILL.md satisfies the agentskills.io
         frontmatter rules (name matches folder, lowercase-hyphen, max 64
         chars; description present, single line, max 1024 chars; metadata
         category in the allowed set).
      4. The generated skills index is fresh AND deterministic (generator run
         twice into temp files produces byte-identical output, which matches
         the committed index).
      5. No CR bytes in any .llm markdown or pointer file (LF endings only).
      6. Relative markdown links inside .llm files resolve to real files.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of the scripts directory.

.PARAMETER Fix
    Regenerate .llm/skills/index.md when it is stale or nondeterministic,
    instead of failing.

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-llm-instructions.ps1
    pwsh -NoProfile -File scripts/lint-llm-instructions.ps1 -Fix
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [switch]$Fix
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

$script:errors = New-Object 'System.Collections.Generic.List[string]'

function Add-LintError {
    param([string]$Message)
    $script:errors.Add($Message) | Out-Null
}

function Test-RepoPath {
    param([string]$RelativePath)
    return Join-Path $RepoRoot ($RelativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
}

# 1. Pointer files delegate to .llm/context.md.

$pointerExpectations = @(
    @{ Path = 'CLAUDE.md';                       Link = '](./.llm/context.md)';   Label = 'Claude Code entrypoint' }
    @{ Path = 'AGENTS.md';                       Link = '](./.llm/context.md)';   Label = 'OpenAI Codex / ChatGPT / opencode entrypoint' }
    @{ Path = 'GEMINI.md';                       Link = '](./.llm/context.md)';   Label = 'Gemini CLI entrypoint' }
    @{ Path = '.cursor/rules/signal-fish.mdc';   Link = '](../../.llm/context.md)'; Label = 'Cursor entrypoint' }
    @{ Path = '.github/copilot-instructions.md'; Link = '](../.llm/context.md)';  Label = 'GitHub Copilot entrypoint' }
    @{ Path = 'llms.txt';                        Link = '](.llm/context.md)';     Label = 'llms.txt entrypoint' }
)

foreach ($pointer in $pointerExpectations) {
    $full = Test-RepoPath -RelativePath $pointer.Path
    if (-not (Test-Path -LiteralPath $full)) {
        Add-LintError "$($pointer.Label) missing: $($pointer.Path)"
        continue
    }
    $content = [System.IO.File]::ReadAllText($full)
    if (-not $content.Contains($pointer.Link)) {
        Add-LintError "$($pointer.Label) ($($pointer.Path)) must delegate to .llm/context.md via '$($pointer.Link)'."
    }
}

# 2. context.md structure.

$contextPath = Test-RepoPath -RelativePath '.llm/context.md'
if (-not (Test-Path -LiteralPath $contextPath)) {
    Add-LintError '.llm/context.md is missing.'
}
else {
    $contextLines = @([System.IO.File]::ReadAllLines($contextPath))
    $h1Count = @($contextLines | Where-Object { $_ -match '^# ' }).Count
    if ($h1Count -ne 1) {
        Add-LintError ".llm/context.md must contain exactly one H1 (found $h1Count)."
    }
    if (-not ($contextLines -match 'skills/index\.md')) {
        Add-LintError '.llm/context.md must reference the generated skills index (skills/index.md).'
    }
}

# 3. Skill frontmatter validity (reuses the generator's validator inline).

$skillsRoot = Test-RepoPath -RelativePath '.llm/skills'
$validCategories = @('core', 'protocol', 'testing')
if (Test-Path -LiteralPath $skillsRoot) {
    foreach ($dir in (Get-ChildItem -LiteralPath $skillsRoot -Directory)) {
        $skillFile = Join-Path $dir.FullName 'SKILL.md'
        if (-not (Test-Path -LiteralPath $skillFile)) {
            Add-LintError "${skillFile}: every .llm/skills/<name>/ folder must contain a SKILL.md."
            continue
        }
        $raw = [System.IO.File]::ReadAllText($skillFile)
        if ($raw -notmatch '(?m)^---\r?\n([\s\S]*?)\r?\n---\r?\n') {
            Add-LintError "${skillFile}: missing YAML frontmatter block."
            continue
        }
        $front = $Matches[1]
        $name = $null
        if ($front -match '(?m)^name:\s*(.+?)\s*$') { $name = $Matches[1].Trim() }
        $description = $null
        if ($front -match '(?m)^description:\s*(.+?)\s*$') { $description = $Matches[1].Trim() }
        $category = $null
        if ($front -match '(?ms)^metadata:\s*?\r?\n((?:[ \t]+.*(?:\r?\n|$))+)') {
            if ($Matches[1] -match '(?m)^[ \t]+category:\s*(\S+)\s*$') {
                $category = $Matches[1].Trim().ToLowerInvariant()
            }
        }
        if (-not $name) { Add-LintError "${skillFile}: frontmatter missing required 'name'." }
        else {
            if ($name -ne $dir.Name) {
                Add-LintError "${skillFile}: 'name' ($name) must match folder name ($($dir.Name))."
            }
            if ($name -notmatch '^[a-z0-9]+(-[a-z0-9]+)*$') {
                Add-LintError "${skillFile}: 'name' must be lowercase-hyphen (got '$name')."
            }
            if ($name.Length -gt 64) { Add-LintError "${skillFile}: 'name' exceeds 64 chars." }
        }
        if (-not $description) { Add-LintError "${skillFile}: frontmatter missing required 'description'." }
        else {
            if ($description.Length -gt 1024) { Add-LintError "${skillFile}: 'description' exceeds 1024 chars." }
            if ($description -match '[|]') { Add-LintError "${skillFile}: 'description' must not contain '|'." }
        }
        if (-not $category) { Add-LintError "${skillFile}: frontmatter missing metadata.category." }
        elseif ($validCategories -notcontains $category) {
            Add-LintError "${skillFile}: unknown metadata.category '$category' (allowed: $($validCategories -join ', '))."
        }
    }
}
else {
    Add-LintError "$skillsRoot is missing."
}

# 4. Index freshness + determinism.

$indexPath = Test-RepoPath -RelativePath '.llm/skills/index.md'
$generator = Join-Path $PSScriptRoot 'generate-skills-index.ps1'
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
try {
    $tempIndex1 = Join-Path $tempDir 'index-1.md'
    $tempIndex2 = Join-Path $tempDir 'index-2.md'
    & $generator -RepoRoot $RepoRoot -OutputPath $tempIndex1 | Out-Null
    & $generator -RepoRoot $RepoRoot -OutputPath $tempIndex2 | Out-Null
    $bytes1 = [System.IO.File]::ReadAllBytes($tempIndex1)
    $bytes2 = [System.IO.File]::ReadAllBytes($tempIndex2)

    $ordinal = [System.StringComparer]::Ordinal
    if (-not [System.Linq.Enumerable]::SequenceEqual([byte[]]$bytes1, [byte[]]$bytes2)) {
        Add-LintError 'generate-skills-index.ps1 is nondeterministic (two runs differ byte-for-byte).'
    }
    elseif (Test-Path -LiteralPath $indexPath) {
        $committedBytes = [System.IO.File]::ReadAllBytes($indexPath)
        if (-not [System.Linq.Enumerable]::SequenceEqual([byte[]]$bytes1, [byte[]]$committedBytes)) {
            if ($Fix) {
                $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
                [System.IO.File]::WriteAllText($indexPath, [System.IO.File]::ReadAllText($tempIndex1), $utf8NoBom)
                Write-Host "Regenerated stale skills index: $indexPath"
            }
            else {
                Add-LintError '.llm/skills/index.md is stale. Regenerate: pwsh -NoProfile -File scripts/generate-skills-index.ps1 (or rerun this linter with -Fix).'
            }
        }
    }
    else {
        if ($Fix) {
            Copy-Item -LiteralPath $tempIndex1 -Destination $indexPath -Force
            Write-Host "Created missing skills index: $indexPath"
        }
        else {
            Add-LintError '.llm/skills/index.md is missing. Run: pwsh -NoProfile -File scripts/generate-skills-index.ps1'
        }
    }
}
finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

# 5. LF-only endings.

$lfScope = @()
if (Test-Path -LiteralPath (Test-RepoPath -RelativePath '.llm')) {
    $lfScope += Get-ChildItem -LiteralPath (Test-RepoPath -RelativePath '.llm') -Recurse -File -Filter '*.md'
}
foreach ($pointer in $pointerExpectations) {
    $full = Test-RepoPath -RelativePath $pointer.Path
    if (Test-Path -LiteralPath $full) { $lfScope += Get-Item -LiteralPath $full }
}
foreach ($file in $lfScope) {
    $raw = [System.IO.File]::ReadAllText($file.FullName)
    if ($raw.Contains("`r")) {
        Add-LintError "$($file.FullName): contains CR bytes; LLM context files must use LF line endings."
    }
}

# 6. Relative markdown links resolve.

$linkScope = @()
if (Test-Path -LiteralPath (Test-RepoPath -RelativePath '.llm')) {
    $linkScope += Get-ChildItem -LiteralPath (Test-RepoPath -RelativePath '.llm') -Recurse -File -Filter '*.md'
}
foreach ($file in $linkScope) {
    $raw = [System.IO.File]::ReadAllText($file.FullName)
    foreach ($match in ([regex]::Matches($raw, '\]\((\.\.?/[^)\s]+)(?:\s+"[^"]*")?\)'))) {
        $target = $match.Groups[1].Value.Split('#')[0]
        if (-not $target) { continue }
        $resolved = [System.IO.Path]::GetFullPath((Join-Path $file.DirectoryName $target))
        if (-not (Test-Path -LiteralPath $resolved)) {
            Add-LintError "$($file.FullName): broken relative link '$($match.Groups[1].Value)'."
        }
    }
}

if ($script:errors.Count -gt 0) {
    foreach ($lintError in $script:errors) {
        Write-Error "lint-llm-instructions: $lintError"
    }
    Write-Error "lint-llm-instructions FAILED with $($script:errors.Count) error(s)."
    exit 1
}

Write-Host 'lint-llm-instructions passed.'
exit 0
