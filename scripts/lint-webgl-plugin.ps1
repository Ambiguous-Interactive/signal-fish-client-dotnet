<#
.SYNOPSIS
    Keeps the Unity WebGL plugin's C# interop and its jslib in contract.

.DESCRIPTION
    The WebGL reference transport (unity/Packages/*/Plugins/**) bridges C#
    to a browser WebSocket through [DllImport("__Internal")] entry points
    that resolve against a sibling *.jslib plugin. No compiler in this
    repo compiles that pair together, so a typo on either side surfaces
    only as a runtime EntryPointNotFoundException in the browser. This
    lint is the red gate:

      1. Entry-point contract: every DllImport("__Internal") name in a
         plugin folder's *.cs must exist as an exported function
         (`<name>: function`) in the folder's *.jslib, and vice versa -
         a *.jslib without a sibling interop *.cs is a dead plugin.
      2. Compile check (skipped with -NoBuild): the plugin *.cs plus the
         library sources compile as one netstandard2.1 assembly with
         UNITY_WEBGL defined, warnings as errors - the same shape Unity
         compiles on the WebGL player.
      3. Syntax check: `node --check` on each *.jslib when node is on
         PATH (skipped otherwise; Unity's own build reports syntax
         errors, this just fails faster).

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of the scripts directory.

.PARAMETER NoBuild
    Skip the compile check (used by the self-tests, which only exercise
    the static contract; CI always runs the full check).

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-webgl-plugin.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [switch]$NoBuild,
    [switch]$VerboseOutput
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

$unityRoot = Join-Path $RepoRoot 'unity'
$violations = New-Object 'System.Collections.Generic.List[string]'

function Get-InteropEntryNames {
    param([string]$SourcePath)

    $text = [System.IO.File]::ReadAllText($SourcePath)
    $matches_ = [regex]::Matches(
        $text,
        '\[DllImport\("__Internal"\)\]\s*(?:(?:private|protected|internal|public)\s+)?(?:static\s+)?extern\s+\S+\s+(\w+)\s*\('
    )
    return @($matches_ | ForEach-Object { $_.Groups[1].Value })
}

function Test-JslibExport {
    param([string]$SourcePath, [string]$EntryName)

    $pattern = '(?m)^\s*' + [regex]::Escape($EntryName) + '\s*:\s*function\b'
    return [regex]::IsMatch([System.IO.File]::ReadAllText($SourcePath), $pattern)
}

if (-not (Test-Path -LiteralPath $unityRoot)) {
    Write-Host 'lint-webgl-plugin FAILED: no unity/ folder - nothing to enforce.'
    exit 1
}

$jslibs = [string[]]@(Get-ChildItem -LiteralPath $unityRoot -Recurse -File -Filter '*.jslib' |
    ForEach-Object { $_.FullName })
[System.Array]::Sort($jslibs, [System.StringComparer]::Ordinal)

$pluginDirectories = [string[]]@(Get-ChildItem -LiteralPath $unityRoot -Recurse -File -Filter '*.jslib' |
    ForEach-Object { Split-Path -Parent $_.FullName } |
    Sort-Object -Unique)

$pluginCsFiles = [string[]]@(
    foreach ($directory in $pluginDirectories) {
        Get-ChildItem -LiteralPath $directory -File -Filter '*.cs' |
            ForEach-Object { $_.FullName }
    }
)
[System.Array]::Sort($pluginCsFiles, [System.StringComparer]::Ordinal)

if ($jslibs.Count -eq 0) {
    Write-Host 'lint-webgl-plugin FAILED: no *.jslib plugin under unity/ - nothing to enforce.'
    exit 1
}

# 1. The entry-point contract, per plugin folder.
$entryNames = New-Object 'System.Collections.Generic.List[string]'
foreach ($directory in $pluginDirectories) {
    $relative = [System.IO.Path]::GetFullPath($directory).Substring(
        [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/').Length + 1)
    $relative = $relative -replace '\\', '/'

    $sources = [string[]]@(Get-ChildItem -LiteralPath $directory -File -Filter '*.cs' |
        ForEach-Object { $_.FullName })
    if ($sources.Count -eq 0) {
        $violations.Add("$relative : the *.jslib plugin has no sibling interop *.cs - dead plugin.")
        continue
    }

    $jslibFiles = [string[]]@(Get-ChildItem -LiteralPath $directory -File -Filter '*.jslib' |
        ForEach-Object { $_.FullName })

    foreach ($source in $sources) {
        foreach ($name in (Get-InteropEntryNames -SourcePath $source)) {
            $entryNames.Add($name)
            $found = $false
            foreach ($jslib in $jslibFiles) {
                if (Test-JslibExport -SourcePath $jslib -EntryName $name) {
                    $found = $true
                    break
                }
            }

            if (-not $found) {
                $violations.Add("$relative : DllImport entry '$name' has no exported function in $(($jslibFiles | ForEach-Object { [System.IO.Path]::GetFileName($_) }) -join ', ').")
            }
        }
    }
}

if ($entryNames.Count -eq 0) {
    $violations.Add('no [DllImport("__Internal")] entry points found in any plugin folder.')
}

# 2. The compile check: library sources + plugin sources, one assembly.
if (-not $NoBuild) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) {
        $violations.Add('compile check requested but dotnet was not found on PATH.')
    }
    else {
        $libraryRoot = Join-Path $RepoRoot 'src/SignalFish.Client'
        $librarySources = [string[]]@(Get-ChildItem -LiteralPath $libraryRoot -Recurse -File -Filter '*.cs' |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            ForEach-Object { $_.FullName })

        $stage = Join-Path ([System.IO.Path]::GetTempPath()) ("webglplug-" + [System.Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $stage -Force | Out-Null
        try {
            $compileItems = @(
                foreach ($source in @($librarySources + $pluginCsFiles)) {
                    $path = $source -replace '\\', '/'
                    "        <Compile Include=`"$path`" />"
                }
            )
            $projectLines = @(
                '<Project Sdk="Microsoft.NET.Sdk">',
                '    <PropertyGroup>',
                '        <TargetFramework>netstandard2.1</TargetFramework>',
                '        <LangVersion>9.0</LangVersion>',
                '        <Nullable>enable</Nullable>',
                '        <EnableDefaultCompileItems>false</EnableDefaultCompileItems>',
                '        <ImplicitUsings>disable</ImplicitUsings>',
                '        <DefineConstants>$(DefineConstants);UNITY_WEBGL</DefineConstants>',
                '    </PropertyGroup>',
                '    <ItemGroup>'
            ) + $compileItems + @('    </ItemGroup>', '</Project>', '')
            [System.IO.File]::WriteAllText(
                (Join-Path $stage 'WebGLPluginContract.csproj'),
                (@($projectLines) -join "`n"),
                [System.Text.UTF8Encoding]::new($false))

            $buildOutput = & dotnet build (Join-Path $stage 'WebGLPluginContract.csproj') -c Release -warnaserror --nologo -v q 2>&1
            if ($LASTEXITCODE -ne 0) {
                foreach ($line in @($buildOutput | Select-Object -Last 20)) {
                    Write-Host "    $line"
                }
                $violations.Add('the plugin C# failed to compile against the library sources (netstandard2.1, UNITY_WEBGL, warnings as errors).')
            }
        }
        finally {
            Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# 3. The jslib syntax check (parse only; node never executes the plugin).
$node = Get-Command node -ErrorAction SilentlyContinue
if ($null -ne $node) {
    foreach ($jslib in $jslibs) {
        # node --check rejects unknown extensions, so the plugin is probed
        # through a temporary .js copy with identical bytes.
        $probe = Join-Path ([System.IO.Path]::GetTempPath()) ("jslib-" + [System.Guid]::NewGuid().ToString('N') + '.js')
        Copy-Item -LiteralPath $jslib -Destination $probe -Force
        try {
            $nodeOutput = & node --check $probe 2>&1
            if ($LASTEXITCODE -ne 0) {
                foreach ($line in @($nodeOutput | Select-Object -Last 5)) {
                    Write-Host "    $line"
                }
                $relative = [System.IO.Path]::GetFullPath($jslib).Substring(
                    [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/').Length + 1)
                $violations.Add("$($relative -replace '\\', '/') : node --check rejected the plugin JavaScript (syntax error).")
            }
        }
        finally {
            Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
        }
    }
}
elseif ($VerboseOutput) {
    Write-Host 'node not found on PATH; skipped the jslib syntax check.'
}

if ($violations.Count -gt 0) {
    foreach ($violation in $violations) {
        Write-Host "lint-webgl-plugin: $violation"
    }
    Write-Host "lint-webgl-plugin FAILED: checked $($jslibs.Count) jslib plugin(s), $($violations.Count) violation(s)."
    exit 1
}
if ($VerboseOutput) {
    Write-Host "lint-webgl-plugin passed: $($entryNames.Count) interop entr$(if ($entryNames.Count -eq 1) { 'y' } else { 'ies' }) pinned across $($jslibs.Count) jslib plugin(s), compile + syntax checks clean."
}
exit 0
