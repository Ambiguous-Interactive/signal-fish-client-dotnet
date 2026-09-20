<#
.SYNOPSIS
    Bans LINQ in the shipped client library (perf budget enforcement).

.DESCRIPTION
    PLAN.md locks "zero allocations when idle" for src/SignalFish.Client.
    LINQ operators allocate (closures, iterators, delegate objects), so the
    library must not adopt them on hot paths. For every C# file under src/,
    this linter fails on a System.Linq using directive (plain, aliased, or
    parented-namespace form) or a fully-qualified `System.Linq.` reference.
    For every project file under src/, it fails on a global
    `<Using Include="System.Linq" />` and on ImplicitUsings (whose .NET 6+
    set includes System.Linq). Test projects are out of scope: tests may
    use LINQ freely (they are not on the allocation budget).

    Issue #7. Run standalone, from CI (dotnet.yml), or from the pre-commit
    hook (which passes only staged paths).

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of the scripts directory.

.PARAMETER Paths
    Optional repo-relative (or absolute) file paths to check. When omitted,
    every *.cs file under src/ is checked. The pre-commit hook passes only
    staged paths.

.PARAMETER VerboseOutput
    Print per-file pass confirmations.

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-no-linq.ps1 -VerboseOutput
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
    # Only C# sources and projects under src/ are in scope (the pre-commit
    # hook passes staged paths; tests legitimately use LINQ).
    $srcPrefix = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'src')).TrimEnd('\', '/') +
        [System.IO.Path]::DirectorySeparatorChar
    $targets = [string[]]@($Paths | ForEach-Object { Resolve-TargetPath -Path $_ } |
        Where-Object { $_.EndsWith('.cs', [System.StringComparison]::OrdinalIgnoreCase) -or
            $_.EndsWith('.csproj', [System.StringComparison]::OrdinalIgnoreCase) })
    $targets = [string[]]@($targets |
        Where-Object { $_.StartsWith($srcPrefix, [System.StringComparison]::OrdinalIgnoreCase) })
}
else {
    $srcDir = Join-Path $RepoRoot 'src'
    if (-not (Test-Path -LiteralPath $srcDir)) {
        Write-Host 'lint-no-linq FAILED: src/ directory not found - nothing to enforce.'
        exit 1
    }
    $targets = [string[]]@(Get-ChildItem -LiteralPath $srcDir -Recurse -File -Include '*.cs', '*.csproj' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object { $_.FullName })
}
[System.Array]::Sort($targets, [System.StringComparer]::Ordinal)

if ($targets.Count -eq 0) {
    if ($Paths -and @($Paths).Count -gt 0) {
        # Staged paths contained no in-scope files: nothing to enforce.
        if ($VerboseOutput) {
            Write-Host 'lint-no-linq passed: no in-scope (src/) C# files among the given paths.'
        }
        exit 0
    }
    Write-Host 'lint-no-linq FAILED: no src/**/*.cs found - nothing to enforce.'
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
    $fileViolated = $false
    $isProject = $target.EndsWith('.csproj', [System.StringComparison]::OrdinalIgnoreCase)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        $isQualifiedLinq = $false
        if ($isProject) {
            # A src project can inject LINQ invisibly: a global
            # `<Using Include="System.Linq" />` or ImplicitUsings (whose
            # .NET 6+ set includes System.Linq).
            $isUsingLinq = $line -match '(?i)<Using\s+[^>]*Include\s*=\s*"[^"]*system\.linq'
            $isImplicitUsings = $line -match '(?i)<ImplicitUsings>\s*(enable|true)\s*</ImplicitUsings>'
        }
        else {
            # Both the plain/aliased form and a parented-namespace form
            # (`using Parent.System.Linq;`). Comments or strings mentioning
            # System.Linq. also trip this - deliberately conservative.
            $isUsingLinq = $line -match '(?i)\busing\s+(?:[A-Za-z0-9_.]+\s*=\s*)?(?:[A-Za-z0-9_.]+\.)?system\.linq\s*;'
            $isImplicitUsings = $false
            $isQualifiedLinq = $line -match 'system\.linq\.'
        }
        if ($isUsingLinq -or $isImplicitUsings -or $isQualifiedLinq) {
            if (-not $fileViolated) {
                $violations.Add(
                    "$relative($($i + 1)): System.Linq reference found - the client library must stay " +
                    'LINQ-free (locked zero-allocation perf budget; use spans, loops, or pooled buffers instead).')
                $fileViolated = $true
            }
        }
    }

    if ($VerboseOutput -and -not $fileViolated) {
        Write-Host "ok $relative (no System.Linq references)"
    }
}

if ($violations.Count -gt 0) {
    foreach ($violation in $violations) {
        Write-Host "lint-no-linq: $violation"
    }
    Write-Host "lint-no-linq FAILED: checked $checkedCount file(s), $($violations.Count) violation(s)."
    exit 1
}
if ($VerboseOutput) {
    Write-Host "lint-no-linq passed: checked $checkedCount file(s), 0 System.Linq references."
}
exit 0
