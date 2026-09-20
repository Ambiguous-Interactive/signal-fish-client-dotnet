<#
.SYNOPSIS
    Self-tests for scripts/lint-no-this-qualification.ps1 (this. ban).
#>
. (Join-Path $PSScriptRoot 'test-common.ps1')

$linter = Join-Path (Split-Path -Parent $PSScriptRoot) 'lint-no-this-qualification.ps1'
$repo = New-TestRepo
try {
    Write-Host 'test-lint-no-this-qualification'

    # 1. Clean file passes (underscore fields, unqualified access).
    $clean = (Join-Path $repo 'src/clean.cs') -replace '\\', '/'
    Write-TestFile -Path $clean -Content @'
namespace X
{
    internal sealed class A
    {
        private int _count;

        internal int Count
        {
            get { return _count; }
        }
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/clean.cs')
    Assert-Equal 0 $run.ExitCode 'clean file passes'

    # 2. this. on a field fails.
    $field = (Join-Path $repo 'src/field.cs') -replace '\\', '/'
    Write-TestFile -Path $field -Content 'namespace X { internal sealed class A { private int _count; internal int Count { get { return this._count; } } } }'
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/field.cs')
    Assert-True ($run.ExitCode -ne 0) 'this. field access fails'
    Assert-OutputContains -Run $run -Pattern "'this\.' qualification found" 'violation message names the ban'

    # 3. this. on a method fails.
    $method = (Join-Path $repo 'src/method.cs') -replace '\\', '/'
    Write-TestFile -Path $method -Content 'namespace X { internal sealed class A { internal void Run() { this.ToString(); } } }'
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/method.cs')
    Assert-True ($run.ExitCode -ne 0) 'this. method access fails'

    # 4. Constructor chaining (`: this(`) passes - required syntax, no dot.
    $chain = (Join-Path $repo 'src/chain.cs') -replace '\\', '/'
    Write-TestFile -Path $chain -Content @'
namespace X
{
    internal sealed class A
    {
        internal A()
            : this(1)
        {
        }

        private A(int value)
        {
            _value = value;
        }

        private readonly int _value;
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/chain.cs')
    Assert-Equal 0 $run.ExitCode 'constructor chaining passes'

    # 5. Indexer (`this[`) passes - required syntax, no dot.
    $indexer = (Join-Path $repo 'src/indexer.cs') -replace '\\', '/'
    Write-TestFile -Path $indexer -Content 'namespace X { internal sealed class A { internal object this[int i] { get { return i; } } } }'
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/indexer.cs')
    Assert-Equal 0 $run.ExitCode 'indexer passes'

    # 6. tests/ are in scope too.
    $testFile = (Join-Path $repo 'tests/t.cs') -replace '\\', '/'
    Write-TestFile -Path $testFile -Content 'namespace X { internal sealed class A { private int _c; internal int C { get { return this._c; } } } }'
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'tests/t.cs')
    Assert-True ($run.ExitCode -ne 0) 'tests/ files are in scope'

    # 7. Full-repo sweep finds the seeded violation (default mode, no -Paths).
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo)
    Assert-True ($run.ExitCode -ne 0) 'default sweep finds violations'
    Assert-OutputContains -Run $run -Pattern '3 violation' 'violation count reported'

}
finally {
    Remove-TestRepo -Path $repo
}

Get-ScriptExitSummary
