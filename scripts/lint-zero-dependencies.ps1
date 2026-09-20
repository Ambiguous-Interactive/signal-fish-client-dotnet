<#
.SYNOPSIS
    Enforces the zero-dependency decision for the shipped client library.

.DESCRIPTION
    PLAN.md locks "ZERO NuGet deps" for src/SignalFish.Client (Unity and
    other engine consumers may not be able to pull in packages). This linter
    fails when any project under src/ declares a PackageReference (or a
    legacy dotnet.cliartifact-style package reference). Test-only tooling
    dependencies (tests/) are out of scope.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of the scripts directory.

.PARAMETER Paths
    Optional repo-relative (or absolute) csproj paths to check. When
    omitted, every *.csproj under src/ is checked. The pre-commit hook
    passes only staged paths.

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-zero-dependencies.ps1 -VerboseOutput
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
    # Only projects under src/ are in scope (the pre-commit hook passes
    # staged paths; test projects legitimately declare packages).
    $srcPrefix = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'src'))
    $targets = [string[]]@($Paths | ForEach-Object { Resolve-TargetPath -Path $_ } |
        Where-Object { $_ -like '*.csproj' -and $_.StartsWith($srcPrefix, [System.StringComparison]::OrdinalIgnoreCase) })
}
else {
    $srcDir = Join-Path $RepoRoot 'src'
    $targets = [string[]]@(Get-ChildItem -LiteralPath $srcDir -Recurse -File -Filter '*.csproj' |
        ForEach-Object { $_.FullName })
}
[System.Array]::Sort($targets, [System.StringComparer]::Ordinal)

if ($targets.Count -eq 0) {
    if ($Paths -and @($Paths).Count -gt 0) {
        # Staged paths contained no in-scope projects: nothing to enforce.
        if ($VerboseOutput) {
            Write-Host 'lint-zero-dependencies passed: no in-scope (src/) projects among the given paths.'
        }
        exit 0
    }
    Write-Host 'lint-zero-dependencies FAILED: no src/*.csproj found - nothing to enforce.'
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

    [xml]$project = [System.IO.File]::ReadAllText($target)
    # Any element named PackageReference anywhere in the project file,
    # including inside conditions and ItemDefinitionGroups.
    $packageRefs = @($project.SelectNodes('//*[local-name() = "PackageReference"]'))
    foreach ($ref in $packageRefs) {
        $include = $ref.GetAttribute('Include')
        $version = $ref.GetAttribute('Version')
        $violations.Add("$relative : PackageReference '$include' $version - the client library must stay dependency-free (locked decision; vendored hand-rolled codec instead).")
    }

    if ($VerboseOutput -and $packageRefs.Count -eq 0) {
        Write-Host "ok $relative (0 package references)"
    }
}

if ($violations.Count -gt 0) {
    foreach ($violation in $violations) {
        Write-Host "lint-zero-dependencies: $violation"
    }
    Write-Host "lint-zero-dependencies FAILED: checked $checkedCount project(s), $($violations.Count) violation(s)."
    exit 1
}
if ($VerboseOutput) {
    Write-Host "lint-zero-dependencies passed: checked $checkedCount project(s), 0 package references."
}
exit 0
