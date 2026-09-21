<#
.SYNOPSIS
    Self-tests for scripts/lint-comment-form.ps1 (comment form rules).
#>
. (Join-Path $PSScriptRoot 'test-common.ps1')

$linter = Join-Path (Split-Path -Parent $PSScriptRoot) 'lint-comment-form.ps1'
$repo = New-TestRepo
try {
    Write-Host 'test-lint-comment-form'

    # 1. Clean file: single // lines, /// docs, and multi-line blocks pass.
    $clean = (Join-Path $repo 'src/clean.cs') -replace '\\', '/'
    Write-TestFile -Path $clean -Content @'
namespace X
{
    internal sealed class A
    {
        // One standalone line is fine.
        private int _count;

        /// <summary>XML doc stays untouched.</summary>
        internal int Count
        {
            get
            {
                /*
                    Multi-line rationale uses one block.
                    Second line.
                */
                return _count;
            }
        }
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/clean.cs')
    Assert-Equal 0 $run.ExitCode 'clean file passes'

    # 2. A two-line // run inside a type fails.
    $stack = (Join-Path $repo 'src/stack.cs') -replace '\\', '/'
    Write-TestFile -Path $stack -Content @'
namespace X
{
    internal sealed class A
    {
        internal void Run()
        {
            // first line
            // second line
            System.Console.WriteLine(1);
        }
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/stack.cs')
    Assert-True ($run.ExitCode -ne 0) 'multi-line // run fails'
    Assert-OutputContains -Run $run -Pattern 'ONE `/\* \.\.\. \*/` block' 'violation names the block form'

    # 3. A // run at namespace depth (indent 4) passes - outside the rule.
    $shallow = (Join-Path $repo 'src/shallow.cs') -replace '\\', '/'
    Write-TestFile -Path $shallow -Content @'
namespace X
{
    // File-section note spanning
    // two lines is allowed here.
    internal sealed class A
    {
        private int _count;
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/shallow.cs')
    Assert-Equal 0 $run.ExitCode 'namespace-level run passes'

    # 4. Trailing comments never join into a run.
    $trailing = (Join-Path $repo 'src/trailing.cs') -replace '\\', '/'
    Write-TestFile -Path $trailing -Content @'
namespace X
{
    internal sealed class A
    {
        internal int Read() // first
        {
            return 1; // second
        }
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/trailing.cs')
    Assert-Equal 0 $run.ExitCode 'trailing comments do not join'

    # 5. A one-line /* ... */ comment fails.
    $oneLine = (Join-Path $repo 'src/oneline.cs') -replace '\\', '/'
    Write-TestFile -Path $oneLine -Content @'
namespace X
{
    internal sealed class A
    {
        internal void Run()
        {
            /* inline block */
            System.Console.WriteLine(1);
        }
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/oneline.cs')
    Assert-True ($run.ExitCode -ne 0) 'one-line block comment fails'
    Assert-OutputContains -Run $run -Pattern 'single-line form is' 'violation names the single-line form'

    # 6. A // run made of tool directives passes.
    $directives = (Join-Path $repo 'src/directives.cs') -replace '\\', '/'
    Write-TestFile -Path $directives -Content @'
namespace X
{
    internal sealed class A
    {
        // cspell: disable-next-line
        // ReSharper disable once UnusedMember
        private int _count;
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/directives.cs')
    Assert-Equal 0 $run.ExitCode 'directive runs pass'

    # 7. // inside a string literal is not a comment and cannot form a run.
    $strings = (Join-Path $repo 'src/strings.cs') -replace '\\', '/'
    Write-TestFile -Path $strings -Content @'
namespace X
{
    internal sealed class A
    {
        internal string Read()
        {
            const string a = "https://example.local//path";
            const string b = @"verbatim // still a string
continues here";
            return a + b;
        }
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/strings.cs')
    Assert-Equal 0 $run.ExitCode 'string contents are not comments'

    # 8. Full-scan mode with no C# files fails loudly.
    $empty = Join-Path $repo 'nowhere'
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $empty)
    Assert-True ($run.ExitCode -ne 0) 'empty scan fails loudly'

}
finally {
    Remove-TestRepo -Path $repo
}

Get-ScriptExitSummary
