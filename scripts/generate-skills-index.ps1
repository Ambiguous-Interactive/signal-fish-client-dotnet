<#
.SYNOPSIS
    Generates .llm/skills/index.md from the frontmatter of every
    .llm/skills/<name>/SKILL.md, following the Agent Skills standard
    (https://agentskills.io).

.DESCRIPTION
    Output is deterministic: ordinal (culture-invariant) sorting, no
    timestamps, UTF-8 without BOM, LF line endings. Running this script
    twice must produce byte-identical output; scripts/lint-llm-instructions.ps1
    enforces exactly that.

    Each skill folder must match the agentskills.io spec:
      - SKILL.md starts with YAML frontmatter delimited by '---' lines.
      - 'name' is required, lowercase alphanumeric + hyphens, max 64 chars,
        and must equal the parent directory name.
      - 'description' is required, single line, max 1024 chars, no '|' char.
      - Optional 'metadata.category' must be one of: core, protocol, testing.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of the scripts directory.

.PARAMETER OutputPath
    Where to write the index. Defaults to <RepoRoot>/.llm/skills/index.md.
    Tests pass a temp path here.

.EXAMPLE
    pwsh -NoProfile -File scripts/generate-skills-index.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}
if (-not $OutputPath) {
    $OutputPath = Join-Path (Join-Path (Join-Path $RepoRoot '.llm') 'skills') 'index.md'
}

$categoryOrder = @(
    @{ Key = 'core';     Title = 'Core Skills';     Blurb = 'Apply to most tasks in this repository.' },
    @{ Key = 'protocol'; Title = 'Protocol Skills'; Blurb = 'Signal Fish wire protocol, transport, and runtime behavior.' },
    @{ Key = 'testing';  Title = 'Testing Skills';  Blurb = 'Writing and running tests.' }
)

function Get-SkillFrontmatter {
    param([string]$SkillFilePath, [string]$ExpectedDirectoryName)

    $raw = [System.IO.File]::ReadAllText($SkillFilePath)
    if ($raw -notmatch '(?m)^---\r?\n([\s\S]*?)\r?\n---\r?\n') {
        throw "${SkillFilePath}: missing YAML frontmatter block delimited by '---' lines."
    }
    $front = $Matches[1]

    $name = $null
    if ($front -match '(?m)^name:\s*(.+?)\s*$') { $name = $Matches[1].Trim() }
    $description = $null
    if ($front -match '(?m)^description:\s*(.+?)\s*$') { $description = $Matches[1].Trim() }

    $category = $null
    if ($front -match '(?ms)^metadata:\s*?\r?\n((?:[ \t]+.*(?:\r?\n|$))+)' ) {
        $metadataBlock = $Matches[1]
        if ($metadataBlock -match '(?m)^[ \t]+category:\s*(\S+)\s*$') {
            $category = $Matches[1].Trim().ToLowerInvariant()
        }
    }

    if (-not $name) { throw "${SkillFilePath}: frontmatter is missing required 'name'." }
    if (-not $description) { throw "${SkillFilePath}: frontmatter is missing required 'description'." }
    if ($name.Length -gt 64) { throw "${SkillFilePath}: 'name' exceeds 64 characters." }
    if ($name -notmatch '^[a-z0-9]+(-[a-z0-9]+)*$') {
        throw "${SkillFilePath}: 'name' must be lowercase alphanumeric words separated by single hyphens (got '$name')."
    }
    if ($name -ne $ExpectedDirectoryName) {
        throw "${SkillFilePath}: 'name' ($name) must match the parent directory name ($ExpectedDirectoryName)."
    }
    if ($description.Length -gt 1024) { throw "${SkillFilePath}: 'description' exceeds 1024 characters." }
    if ($description -match '[|]') { throw "${SkillFilePath}: 'description' must not contain '|' (index table delimiter)." }

    [pscustomobject]@{
        Name        = $name
        Description = $description
        Category    = $category
    }
}

$skillsRoot = Join-Path (Join-Path $RepoRoot '.llm') 'skills'
$skillDirs = @(Get-ChildItem -LiteralPath $skillsRoot -Directory)
$dirNames = [string[]]@($skillDirs | ForEach-Object { $_.Name })
[System.Array]::Sort($dirNames, [System.StringComparer]::Ordinal)

$skills = New-Object 'System.Collections.Generic.List[object]'
foreach ($dirName in $dirNames) {
    $dir = $skillDirs | Where-Object { $_.Name -eq $dirName }
    $skillFile = Join-Path $dir.FullName 'SKILL.md'
    if (-not (Test-Path -LiteralPath $skillFile)) {
        throw "${skillFile}: every .llm/skills/<name>/ folder must contain a SKILL.md."
    }
    $skills.Add((Get-SkillFrontmatter -SkillFilePath $skillFile -ExpectedDirectoryName $dirName))
}

if ($skills.Count -eq 0) {
    throw "${skillsRoot}: no skill folders found."
}

$validCategories = @($categoryOrder | ForEach-Object { $_.Key })
foreach ($skill in $skills) {
    if (-not $skill.Category) {
        throw "$($skill.Name)/SKILL.md: frontmatter metadata.category is required (one of: $($validCategories -join ', '))."
    }
    if ($validCategories -notcontains $skill.Category) {
        throw "$($skill.Name)/SKILL.md: unknown metadata.category '$($skill.Category)' (expected one of: $($validCategories -join ', '))."
    }
}

$lines = New-Object 'System.Collections.Generic.List[string]'
$lines.Add('<!-- DO NOT EDIT - generated by scripts/generate-skills-index.ps1. -->')
$lines.Add('<!-- Regenerate with: pwsh -NoProfile -File scripts/generate-skills-index.ps1 -->')
$lines.Add('')
$lines.Add('# Skills Index')
$lines.Add('')
$lines.Add('Skills follow the [Agent Skills](https://agentskills.io) standard: each folder in')
$lines.Add('`.llm/skills/` contains a `SKILL.md` with `name` and `description` frontmatter.')
$lines.Add('Start from [context.md](../context.md); open a skill only when its description')
$lines.Add('matches the current task.')

foreach ($category in $categoryOrder) {
    $names = [string[]]@($skills | Where-Object { $_.Category -eq $category.Key } | ForEach-Object { $_.Name })
    if ($names.Count -eq 0) { continue }
    [System.Array]::Sort($names, [System.StringComparer]::Ordinal)
    $lines.Add('')
    $lines.Add("## $($category.Title)")
    $lines.Add('')
    $lines.Add($category.Blurb)
    $lines.Add('')
    $lines.Add('| Skill | When to Use |')
    $lines.Add('| --- | --- |')
    foreach ($name in $names) {
        $skill = $skills | Where-Object { $_.Name -eq $name }
        $lines.Add("| [$($skill.Name)](./$($skill.Name)/SKILL.md) | $($skill.Description) |")
    }
}

$content = ($lines -join "`n") + "`n"
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($OutputPath, $content, $utf8NoBom)

$skillCount = $skills.Count
Write-Host "Wrote $OutputPath ($skillCount skills)."
