<#
.SYNOPSIS
    Self-tests for scripts/lint-llm-instructions.ps1.
#>
. (Join-Path $PSScriptRoot 'test-common.ps1')

$linter = Join-Path (Split-Path -Parent $PSScriptRoot) 'lint-llm-instructions.ps1'
$generator = Join-Path (Split-Path -Parent $PSScriptRoot) 'generate-skills-index.ps1'

function New-FixtureRepo {
    param([string[]]$Skills = @('alpha-skill', 'beta-skill'))
    $repo = New-TestRepo
    Write-TestFile -Path (Join-Path $repo '.llm/context.md') -Content @(
        '# LLM Agent Instructions'
        ''
        'See the [Skills Index](./skills/index.md).'
    ) | Out-Null
    foreach ($skillName in $Skills) {
        $category = if ($skillName -like '*wire*') { 'protocol' } else { 'core' }
        New-TestSkill -RepoRoot $repo -Name $skillName -Category $category
    }
    Write-TestFile -Path (Join-Path $repo 'CLAUDE.md') -Content @(
        '# Claude Configuration'
        ''
        'See the [AI Agent Guidelines](./.llm/context.md) for all AI agent guidelines.'
    ) | Out-Null
    Write-TestFile -Path (Join-Path $repo 'AGENTS.md') -Content @(
        '# Repository Guidelines'
        ''
        'See the [AI Agent Guidelines](./.llm/context.md) for all AI agent guidelines.'
    ) | Out-Null
    Write-TestFile -Path (Join-Path $repo 'GEMINI.md') -Content @(
        '# Gemini Configuration'
        ''
        'See the [AI Agent Guidelines](./.llm/context.md) for all AI agent guidelines.'
    ) | Out-Null
    Write-TestFile -Path (Join-Path $repo '.cursor/rules/signal-fish.mdc') -Content @(
        '---'
        'description: Signal Fish repository guidelines.'
        'alwaysApply: true'
        '---'
        ''
        'See the [AI Agent Guidelines](../../.llm/context.md) for all AI agent guidelines.'
    ) | Out-Null
    Write-TestFile -Path (Join-Path $repo '.github/copilot-instructions.md') -Content @(
        '# GitHub Copilot Instructions'
        ''
        'See the [AI Agent Guidelines](../.llm/context.md) for all AI agent guidelines.'
    ) | Out-Null
    Write-TestFile -Path (Join-Path $repo 'llms.txt') -Content @(
        '# Signal Fish .NET Client'
        ''
        '- [AI Agent Guidelines](.llm/context.md): canonical agent context.'
    ) | Out-Null
    Invoke-Pwsh -ScriptPath $generator -Arguments @('-RepoRoot', $repo) | Out-Null
    return $repo
}

$repo = New-FixtureRepo
try {
    Write-Host 'test-lint-llm-instructions'

    # 1. Well-formed fixture passes.
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo)
    Assert-Equal 0 $run.ExitCode 'well-formed fixture passes'

    # 2. Missing pointer file fails.
    Remove-Item -LiteralPath (Join-Path $repo 'GEMINI.md') -Force
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo)
    Assert-True ($run.ExitCode -ne 0) 'missing pointer file fails'
    Write-TestFile -Path (Join-Path $repo 'GEMINI.md') -Content "# Gemini`n`nSee the [AI Agent Guidelines](./.llm/context.md).`n" | Out-Null

    # 3. Pointer file without delegation link fails.
    Write-TestFile -Path (Join-Path $repo 'CLAUDE.md') -Content "# Claude Configuration`n`nNo delegation here.`n" | Out-Null
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo)
    Assert-True ($run.ExitCode -ne 0) 'pointer without delegation link fails'
    Write-TestFile -Path (Join-Path $repo 'CLAUDE.md') -Content "# Claude Configuration`n`nSee the [AI Agent Guidelines](./.llm/context.md).`n" | Out-Null

    # 4. Stale index fails; -Fix repairs it.
    Add-Content -LiteralPath (Join-Path $repo '.llm/skills/index.md') -Value 'stale line' -NoNewline
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo)
    Assert-True ($run.ExitCode -ne 0) 'stale index fails'
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Fix')
    Assert-Equal 0 $run.ExitCode 'linter -Fix repairs a stale index'
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo)
    Assert-Equal 0 $run.ExitCode 'fixture passes again after -Fix'

    # 5. Multiple H1s in context.md fail.
    Add-Content -LiteralPath (Join-Path $repo '.llm/context.md') -Value "`n# Second H1`n" -NoNewline
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo)
    Assert-True ($run.ExitCode -ne 0) 'context.md with two H1s fails'
    $fixed = [System.IO.File]::ReadAllLines((Join-Path $repo '.llm/context.md')) | Where-Object { $_ -ne '# Second H1' }
    Write-TestFile -Path (Join-Path $repo '.llm/context.md') -Content ($fixed -join "`n") | Out-Null

    # 6. SKILL.md name/folder mismatch fails.
    $skillPath = Join-Path $repo '.llm/skills/alpha-skill/SKILL.md'
    $skillContent = [System.IO.File]::ReadAllText($skillPath) -replace 'name: alpha-skill', 'name: wrong-name'
    Write-TestFile -Path $skillPath -Content $skillContent | Out-Null
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo)
    Assert-True ($run.ExitCode -ne 0) 'name/folder mismatch fails'
    New-TestSkill -RepoRoot $repo -Name 'alpha-skill' -Category 'core'

    # 7. Broken relative link fails.
    Add-Content -LiteralPath (Join-Path $repo '.llm/context.md') -Value 'See [ghost](./skills/ghost/SKILL.md).' -NoNewline
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo)
    Assert-True ($run.ExitCode -ne 0) 'broken relative link fails'
    $ctx = [System.IO.File]::ReadAllText((Join-Path $repo '.llm/context.md')) -replace [regex]::Escape('See [ghost](./skills/ghost/SKILL.md).'), ''
    Write-TestFile -Path (Join-Path $repo '.llm/context.md') -Content $ctx | Out-Null

    # 8. CR bytes fail.
    Write-TestFile -Path (Join-Path $repo '.llm/skills/beta-skill/SKILL.md') -Content "---`nname: beta-skill`ndescription: crlf test.`nmetadata:`n  category: core`n---`n`n# Beta`r`n`r`nBody.`r`n" | Out-Null
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo)
    Assert-True ($run.ExitCode -ne 0) 'CR bytes in .llm files fail'
    New-TestSkill -RepoRoot $repo -Name 'beta-skill' -Category 'core'

    # 9. Everything green again.
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo)
    Assert-Equal 0 $run.ExitCode 'fixture green after all repairs'
}
finally {
    Remove-TestRepo $repo
}
Get-ScriptExitSummary
