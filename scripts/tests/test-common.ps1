<#
.SYNOPSIS
    Shared helpers for the LLM tooling self-tests. Dot-source from test files.
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:failCount = 0
$script:passCount = 0

function Assert-True {
    param([bool]$Condition, [string]$Name)
    if ($Condition) {
        $script:passCount++
        Write-Host "  PASS $Name"
    }
    else {
        $script:failCount++
        Write-Host "  FAIL $Name" -ForegroundColor Red
    }
}

function Assert-Equal {
    param($Expected, $Actual, [string]$Name)
    Assert-True ($Expected -eq $Actual) "$Name (expected '$Expected', got '$Actual')"
}

function New-TestRepo {
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("llmtest-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    return $dir
}

function Remove-TestRepo {
    param([string]$Path)
    if ($Path -and (Test-Path -LiteralPath $Path)) {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Write-TestFile {
    param([string]$Path, [object]$Content)
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $text = (@($Content) -join "`n") -replace "`r`n", "`n"
    [System.IO.File]::WriteAllText($Path, $text, [System.Text.UTF8Encoding]::new($false))
}

function New-TestSkill {
    param(
        [string]$RepoRoot,
        [string]$Name,
        [string]$Description = 'Test skill used by the automation self-tests.',
        [string]$Category = 'core',
        [string]$ExtraBody = ''
    )
    $dir = Join-Path (Join-Path (Join-Path $RepoRoot '.llm') 'skills') $Name
    $content = @"
---
name: $Name
description: $Description
metadata:
  category: $Category
---

# Test Skill: $Name

$ExtraBody
"@
    Write-TestFile -Path (Join-Path $dir 'SKILL.md') -Content $content
}

function Assert-OutputContains {
    param([object]$Run, [string]$Pattern, [string]$Name)
    $joined = (@($Run.Output) | ForEach-Object { $_.ToString() }) -join "`n"
    Assert-True ($joined -match $Pattern) $Name
}

function Invoke-Pwsh {
    param([string]$ScriptPath, [string[]]$Arguments = @())
    $output = & pwsh -NoProfile -File $ScriptPath @Arguments 2>&1
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output   = @($output | ForEach-Object { $_.ToString() })
    }
}

function Invoke-PwshCommand {
    param([string]$Command)
    $output = & pwsh -NoProfile -Command $Command 2>&1
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output   = @($output | ForEach-Object { $_.ToString() })
    }
}

function Get-ScriptExitSummary {
    Assert-True ($script:failCount -eq 0) "no assertion failures ($($script:passCount) passed, $($script:failCount) failed)"
    if ($script:failCount -gt 0) { exit 1 }
    exit 0
}
