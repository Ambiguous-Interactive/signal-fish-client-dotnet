<#
.SYNOPSIS
    Self-tests for scripts/generate-skills-index.ps1.
#>
. (Join-Path $PSScriptRoot 'test-common.ps1')

$generator = Join-Path (Split-Path -Parent $PSScriptRoot) 'generate-skills-index.ps1'
$repo = New-TestRepo
try {
    Write-Host 'test-generate-skills-index'

    # 1. Generates grouped, sorted, deterministic output.
    New-TestSkill -RepoRoot $repo -Name 'alpha-skill' -Category 'core' -Description 'Alpha does alpha things.'
    New-TestSkill -RepoRoot $repo -Name 'zulu-skill' -Category 'core' -Description 'Zulu does zulu things.'
    New-TestSkill -RepoRoot $repo -Name 'wire-protocol' -Category 'protocol' -Description 'Wire protocol details.'
    $out1 = Join-Path $repo 'index-1.md'
    $out2 = Join-Path $repo 'index-2.md'
    $run1 = Invoke-Pwsh -ScriptPath $generator -Arguments @('-RepoRoot', $repo, '-OutputPath', $out1)
    Assert-Equal 0 $run1.ExitCode 'generator exits 0 on a valid skill set'

    $content = [System.IO.File]::ReadAllText($out1)
    Assert-True ($content -match '\[alpha-skill\]\(\./alpha-skill/SKILL\.md\) \| Alpha does alpha things\. \|') 'index contains a table row per skill'
    Assert-True ($content -match '## Core Skills') 'index contains the Core Skills section'
    Assert-True ($content -match '## Protocol Skills') 'index contains the Protocol Skills section'
    Assert-True ($content.IndexOf('## Core Skills') -lt $content.IndexOf('## Protocol Skills')) 'categories appear in fixed order'
    Assert-True ($content.IndexOf('alpha-skill') -lt $content.IndexOf('zulu-skill')) 'skills sorted ordinally within a category'
    Assert-True (-not $content.Contains("`r")) 'output uses LF line endings'

    Invoke-Pwsh -ScriptPath $generator -Arguments @('-RepoRoot', $repo, '-OutputPath', $out2) | Out-Null
    $bytes1 = [System.IO.File]::ReadAllBytes($out1)
    $bytes2 = [System.IO.File]::ReadAllBytes($out2)
    Assert-True ([System.Linq.Enumerable]::SequenceEqual([byte[]]$bytes1, [byte[]]$bytes2)) 'two runs are byte-identical'

    # 2. name must match directory name.
    $badRepo = New-TestRepo
    try {
        $dir = Join-Path (Join-Path (Join-Path $badRepo '.llm') 'skills') 'folder-name'
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Write-TestFile -Path (Join-Path $dir 'SKILL.md') -Content "---`nname: other-name`ndescription: mismatched.`n---`n"
        $run = Invoke-Pwsh -ScriptPath $generator -Arguments @('-RepoRoot', $badRepo, '-OutputPath', (Join-Path $badRepo 'idx.md'))
        Assert-True ($run.ExitCode -ne 0) 'generator fails when name does not match folder'
    }
    finally { Remove-TestRepo $badRepo }

    # 3. Unknown category rejected.
    $badRepo = New-TestRepo
    try {
        New-TestSkill -RepoRoot $badRepo -Name 'odd-skill' -Category 'unexpected'
        $run = Invoke-Pwsh -ScriptPath $generator -Arguments @('-RepoRoot', $badRepo, '-OutputPath', (Join-Path $badRepo 'idx.md'))
        Assert-True ($run.ExitCode -ne 0) 'generator rejects unknown metadata.category'
    }
    finally { Remove-TestRepo $badRepo }

    # 4. Missing description rejected.
    $badRepo = New-TestRepo
    try {
        $dir = Join-Path (Join-Path (Join-Path $badRepo '.llm') 'skills') 'no-desc'
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Write-TestFile -Path (Join-Path $dir 'SKILL.md') -Content "---`nname: no-desc`n---`n"
        $run = Invoke-Pwsh -ScriptPath $generator -Arguments @('-RepoRoot', $badRepo, '-OutputPath', (Join-Path $badRepo 'idx.md'))
        Assert-True ($run.ExitCode -ne 0) 'generator rejects missing description'
    }
    finally { Remove-TestRepo $badRepo }

    # 5. Empty skills dir rejected.
    $badRepo = New-TestRepo
    try {
        New-Item -ItemType Directory -Path (Join-Path (Join-Path $badRepo '.llm') 'skills') -Force | Out-Null
        $run = Invoke-Pwsh -ScriptPath $generator -Arguments @('-RepoRoot', $badRepo, '-OutputPath', (Join-Path $badRepo 'idx.md'))
        Assert-True ($run.ExitCode -ne 0) 'generator rejects an empty skills directory'
    }
    finally { Remove-TestRepo $badRepo }

    # 6. Uppercase name rejected (agentskills.io rule).
    $badRepo = New-TestRepo
    try {
        $dir = Join-Path (Join-Path (Join-Path $badRepo '.llm') 'skills') 'upper-case'
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Write-TestFile -Path (Join-Path $dir 'SKILL.md') -Content "---`nname: Upper-Case`ndescription: bad casing.`n---`n"
        $run = Invoke-Pwsh -ScriptPath $generator -Arguments @('-RepoRoot', $badRepo, '-OutputPath', (Join-Path $badRepo 'idx.md'))
        Assert-True ($run.ExitCode -ne 0) 'generator rejects uppercase name'
    }
    finally { Remove-TestRepo $badRepo }
}
finally {
    Remove-TestRepo $repo
}
Get-ScriptExitSummary
