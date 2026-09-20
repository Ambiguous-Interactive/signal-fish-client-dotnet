<#
.SYNOPSIS
    Bans `this.` qualification in all C# sources (src/ and tests/).

.DESCRIPTION
    Repo style: member access never needs `this.` disambiguation - private
    fields carry the `_camelCase` prefix, so unqualified access is always
    unambiguous (also enforced by the dotnet_style_qualification_for_*
    .editorconfig rules for IDEs and `dotnet format`). Required forms of
    `this` never use the dotted syntax (constructor chaining writes
    `: this(...)` and indexers write `this[...]`), so a bare `this.` token
    is always a violation. IDE0003 cannot enforce this at build time
    (Roslyn computes it IDE-side only), hence this linter.

    Run standalone, from CI (dotnet.yml), or from the pre-commit hook
    (which passes only staged paths).

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of the scripts directory.

.PARAMETER Paths
    Optional repo-relative (or absolute) file paths to check. When omitted,
    every *.cs file under src/ and tests/ is checked.

.PARAMETER VerboseOutput
    Print per-file pass confirmations.

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-no-this-qualification.ps1 -VerboseOutput
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string[]]$Paths,
    [switch]$VerboseOutput
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
    $targets = [string[]]@('src', 'tests' | ForEach-Object { Join-Path $RepoRoot $_ } |
        Where-Object { Test-Path -LiteralPath $_ } |
        ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -File -Include '*.cs' } |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object { $_.FullName })
}
[System.Array]::Sort($targets, [System.StringComparer]::Ordinal)

if ($targets.Count -eq 0) {
    if ($Paths -and @($Paths).Count -gt 0) {
        if ($VerboseOutput) {
            Write-Host 'lint-no-this-qualification passed: no C# files among the given paths.'
        }
        exit 0
    }
    Write-Host 'lint-no-this-qualification FAILED: no *.cs found under src/ or tests/.'
    exit 1
}

$violations = New-Object 'System.Collections.Generic.List[string]'
$checkedCount = 0

foreach ($target in $targets) {
    if (-not (Test-Path -LiteralPath $target)) {
        $violations.Add("path not found: $target")
        continue
    }

    $relative = [System.IO.Path]::GetFullPath($target).Substring(
        [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/').Length + 1)
    $relative = $relative -replace '\\', '/'
    $checkedCount++

    $lines = [System.IO.File]::ReadAllLines($target)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        # Dotted `this.` only: ctor chaining (`: this(`) and indexers
        # (`this[`) are required syntax and carry no dot.
        if ($lines[$i] -match '\bthis\s*\.') {
            $violations.Add(
                "$relative($($i + 1)): 'this.' qualification found - members are unambiguous " +
                'without it (private fields use the _camelCase prefix).')
        }
    }

    if ($VerboseOutput) {
        Write-Host "ok $relative (no this. qualification)"
    }
}

if ($violations.Count -gt 0) {
    foreach ($violation in $violations) {
        Write-Host "lint-no-this-qualification: $violation"
    }
    Write-Host "lint-no-this-qualification FAILED: checked $checkedCount file(s), $($violations.Count) violation(s)."
    exit 1
}
if ($VerboseOutput) {
    Write-Host "lint-no-this-qualification passed: checked $checkedCount file(s), 0 violations."
}
exit 0
