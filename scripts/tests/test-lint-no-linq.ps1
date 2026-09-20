<#
.SYNOPSIS
    Self-tests for scripts/lint-no-linq.ps1 (System.Linq ban under src/).
#>
. (Join-Path $PSScriptRoot 'test-common.ps1')

$linter = Join-Path (Split-Path -Parent $PSScriptRoot) 'lint-no-linq.ps1'
$repo = New-TestRepo
try {
    Write-Host 'test-lint-no-linq'

    # 1. LINQ-free file passes.
    $clean = (Join-Path $repo 'src/lib.cs') -replace '\\', '/'
    Write-TestFile -Path $clean -Content 'namespace X { internal static class A { } }'
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/lib.cs')
    Assert-Equal 0 $run.ExitCode 'LINQ-free file passes'

    # 2. using System.Linq fails.
    $using = (Join-Path $repo 'src/using-linq.cs') -replace '\\', '/'
    Write-TestFile -Path $using -Content 'namespace X { using System.Linq; internal static class A { } }'
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/using-linq.cs')
    Assert-True ($run.ExitCode -ne 0) 'using System.Linq fails'
    Assert-OutputContains -Run $run -Pattern 'System\.Linq reference found' 'using directive message mentions the ban'

    # 3. using alias import fails.
    $alias = (Join-Path $repo 'src/alias-linq.cs') -replace '\\', '/'
    Write-TestFile -Path $alias -Content 'namespace X { using E = System.Linq; internal static class A { } }'
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/alias-linq.cs')
    Assert-True ($run.ExitCode -ne 0) 'aliased using import fails'

    # 4. Fully-qualified System.Linq reference fails.
    $qualified = (Join-Path $repo 'src/qualified-linq.cs') -replace '\\', '/'
    Write-TestFile -Path $qualified -Content 'namespace X { internal static class A { internal static object Pick(int[] xs) { return System.Linq.Enumerable.First(xs); } } }'
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/qualified-linq.cs')
    Assert-True ($run.ExitCode -ne 0) 'fully-qualified reference fails'

    # 4b. Parented-namespace using form fails.
    $parented = (Join-Path $repo 'src/parented-linq.cs') -replace '\\', '/'
    Write-TestFile -Path $parented -Content 'namespace X { using Parent.System.Linq; internal static class A { } }'
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/parented-linq.cs')
    Assert-True ($run.ExitCode -ne 0) 'parented-namespace using fails'

    # 4c. Project file injecting a global LINQ using fails.
    $projUsing = (Join-Path $repo 'src/InjectingLinq.csproj') -replace '\\', '/'
    Write-TestFile -Path $projUsing -Content '<Project><ItemGroup><Using Include="System.Linq" /></ItemGroup></Project>'
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/InjectingLinq.csproj')
    Assert-True ($run.ExitCode -ne 0) 'csproj global Using Include fails'

    # 4d. Project file with ImplicitUsings enabled fails (its .NET 6+ set includes System.Linq).
    $projImplicit = (Join-Path $repo 'src/Implicit.csproj') -replace '\\', '/'
    Write-TestFile -Path $projImplicit -Content '<Project><PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>'
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/Implicit.csproj')
    Assert-True ($run.ExitCode -ne 0) 'csproj ImplicitUsings fails'

    # 4e. A clean project file passes.
    $projClean = (Join-Path $repo 'src/Clean.csproj') -replace '\\', '/'
    Write-TestFile -Path $projClean -Content '<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>'
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/Clean.csproj')
    Assert-Equal 0 $run.ExitCode 'clean csproj passes'

    # 4f. A sibling src-evil/ directory is NOT in scope (prefix guard).
    $evil = (Join-Path $repo 'src-evil/evil.cs') -replace '\\', '/'
    Write-TestFile -Path $evil -Content 'namespace X { using System.Linq; internal static class A { } }'
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src-evil/evil.cs')
    Assert-Equal 0 $run.ExitCode 'sibling src-evil/ directory is out of scope'

    # 5. Tests are out of scope: identical LINQ file under tests/ passes.
    $testFile = (Join-Path $repo 'tests/linq-test.cs') -replace '\\', '/'
    Write-TestFile -Path $testFile -Content 'namespace X { using System.Linq; internal static class A { } }'
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'tests/linq-test.cs')
    Assert-Equal 0 $run.ExitCode 'tests/ paths are out of scope'

    # 6. Staged paths with no in-scope files pass (pre-commit contract).
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'README.md')
    Assert-Equal 0 $run.ExitCode 'no in-scope staged paths pass'

    # 7. Default scope sweeps all of src/ and reports every violation.
    $run = Invoke-PwshCommand -Command "& '$linter' -RepoRoot '$repo'"
    Assert-True ($run.ExitCode -ne 0) 'default scope catches the violating files'
    Assert-OutputContains -Run $run -Pattern 'using-linq\.cs' 'first violation reported'
    Assert-OutputContains -Run $run -Pattern 'alias-linq\.cs' 'second violation reported'
    Assert-OutputContains -Run $run -Pattern 'qualified-linq\.cs' 'third violation reported'
    Assert-OutputContains -Run $run -Pattern 'parented-linq\.cs' 'fourth violation reported'
    Assert-OutputContains -Run $run -Pattern 'InjectingLinq\.csproj' 'csproj violation reported'
    Assert-OutputContains -Run $run -Pattern 'Implicit\.csproj' 'ImplicitUsings violation reported'

    # 8. Default scope passes when src/ is clean.
    Remove-Item -LiteralPath (Join-Path $repo 'src/using-linq.cs'), (Join-Path $repo 'src/alias-linq.cs'), (Join-Path $repo 'src/qualified-linq.cs'), (Join-Path $repo 'src/parented-linq.cs'), (Join-Path $repo 'src/InjectingLinq.csproj'), (Join-Path $repo 'src/Implicit.csproj'), (Join-Path $repo 'src/Clean.csproj') -Force
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo)
    Assert-Equal 0 $run.ExitCode 'default scope passes when src/ is clean'

    # 9. Missing src/ in default mode fails cleanly.
    $empty = New-TestRepo
    try {
        $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $empty)
        Assert-True ($run.ExitCode -ne 0) 'missing src/ directory fails cleanly'
        Assert-OutputContains -Run $run -Pattern 'not found' 'missing src/ message is actionable'
    }
    finally {
        Remove-TestRepo -Path $empty
    }
}
finally {
    Remove-TestRepo -Path $repo
}

Get-ScriptExitSummary
