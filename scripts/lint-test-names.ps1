<#
.SYNOPSIS
    Bans underscores in test method names under tests/ (issue #26).

.DESCRIPTION
    Repo convention (aligned with the sibling repos): test method names are
    PascalCase with no underscores - scenario and expectation read as one
    name (`DecodeExplicitNullOptionalsDecodeAsAbsent`), and NUnit display
    names use dot notation (`TestName = "Input.Null.Throws"`). Renames the
    failure mode where half the suite says `Method_Scenario` and half says
    `MethodScenario`.

    Checks method declarations (any visibility, any return type) in test
    sources, plus underscored NUnit display names in `TestName = "..."`,
    `SetName("...")`, and `SetArgDisplayNames("...")` positions.

    Known constraints (documented, not accidental): declarations are
    anchored on an access modifier - repo style puts one on every member -
    and signatures must carry the name and `(` on the same line (the
    CSharpier shape). The scan is line-based, so a commented-out
    declaration would be flagged; there are none to begin with.

    Run standalone, from CI (dotnet.yml), or from the pre-commit hook
    (which passes only staged paths).

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of the scripts directory.

.PARAMETER Paths
    Optional repo-relative (or absolute) file paths to check. When omitted,
    every *.cs file under tests/ is checked.

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-test-names.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string[]]$Paths
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

function Resolve-TargetPath {
    param([string]$Path)
    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    return Join-Path $RepoRoot ($Path -replace '/', [System.IO.Path]::DirectorySeparatorChar)
}

if ($Paths -and @($Paths).Count -gt 0) {
    $targets = [string[]]@($Paths | ForEach-Object { Resolve-TargetPath -Path $_ } |
        Where-Object { $_.EndsWith('.cs', [System.StringComparison]::OrdinalIgnoreCase) })
}
else {
    $targets = [string[]]@(Join-Path $RepoRoot 'tests' |
        Where-Object { Test-Path -LiteralPath $_ } |
        ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -File -Include '*.cs' } |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object { $_.FullName })
}
[System.Array]::Sort($targets, [System.StringComparer]::Ordinal)

if ($targets.Count -eq 0) {
    if ($Paths -and @($Paths).Count -gt 0) {
        exit 0
    }
    Write-Host 'lint-test-names FAILED: no *.cs found under tests/.'
    exit 1
}

# Method declaration: an access modifier, optional modifiers, explicit
# return type (generics/arrays/qualified names included), then the name.
# `var` is banned repo-wide, so the name is always preceded by a type
# token. Type keywords (record/class/struct/...) are skipped below. The
# access-modifier anchor is what keeps control-flow statements out.
$methodPattern = '^(?<lead>\s*(?:\[[^\]]*\]\s*)?(?:public|internal|protected|private)\s+(?:(?:static|async|override|sealed|new|partial|extern|unsafe|virtual)\s+)*(?<type>[A-Za-z_][\w\.<>\[\],\s]*?)\s+)(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<tail>\s*<[^>]*>\s*)?\('
$typeKeywordPattern = '\b(record|class|struct|interface|enum|delegate)\b'
$displayPattern = 'TestName\s*=\s*"(?<name>[^"]*)"|SetName\s*\(\s*"(?<name>[^"]*)"|SetArgDisplayNames\s*\(\s*"(?<name>[^"]*)"'

$violations = New-Object 'System.Collections.Generic.List[string]'
$checkedCount = 0

foreach ($target in $targets) {
    if (-not (Test-Path -LiteralPath $target)) {
        $violations.Add("path not found: $target")
        continue
    }

    $fullPath = [System.IO.Path]::GetFullPath($target)
    $rootPath = [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/')
    if ($fullPath.StartsWith($rootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        $relative = $fullPath.Substring($rootPath.Length + 1)
    }
    else {
        $relative = $fullPath
    }
    $relative = $relative -replace '\\', '/'
    $checkedCount++

    $lines = [System.IO.File]::ReadAllLines($target)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $method = [regex]::Match($lines[$i], $methodPattern)
        if ($method.Success) {
            $typeToken = [regex]::Match($method.Groups['type'].Value, $typeKeywordPattern)
            if ($method.Groups['name'].Value -match '_' -and -not $typeToken.Success) {
                $violations.Add(
                    "$relative($($i + 1)): method '$($method.Groups['name'].Value)' contains an underscore - " +
                    'test names are PascalCase with no underscores.')
            }
        }

        foreach ($display in [regex]::Matches($lines[$i], $displayPattern)) {
            if ($display.Groups['name'].Value -match '_') {
                $violations.Add(
                    "$relative($($i + 1)): display name '$($display.Groups['name'].Value)' contains an " +
                    'underscore - use dot notation (e.g. "Input.Null.Throws").')
            }
        }
    }
}

if ($violations.Count -gt 0) {
    foreach ($violation in $violations) {
        Write-Host "lint-test-names: $violation"
    }
    Write-Host "lint-test-names FAILED: checked $checkedCount file(s), $($violations.Count) violation(s)."
    exit 1
}
Write-Host "lint-test-names passed: checked $checkedCount file(s), 0 violations."
exit 0
