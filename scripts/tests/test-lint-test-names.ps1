<#
.SYNOPSIS
    Self-tests for scripts/lint-test-names.ps1 (underscore ban).
#>
. (Join-Path $PSScriptRoot 'test-common.ps1')

$linter = Join-Path (Split-Path -Parent $PSScriptRoot) 'lint-test-names.ps1'
$repo = New-TestRepo
try {
    Write-Host 'test-lint-test-names'

    # 1. PascalCase test names pass.
    $clean = (Join-Path $repo 'tests/clean.cs') -replace '\\', '/'
    Write-TestFile -Path $clean -Content @'
namespace X
{
    internal sealed class CleanTests
    {
        [Test]
        public void JoinRoomWithoutCodeCreatesRoom()
        {
            Assert.That(true, Is.True);
        }

        internal static string[] Rows() => System.Array.Empty<string>();
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'tests/clean.cs')
    Assert-Equal 0 $run.ExitCode 'PascalCase names pass'

    # 2. An underscored method name fails.
    $snake = (Join-Path $repo 'tests/snake.cs') -replace '\\', '/'
    Write-TestFile -Path $snake -Content @'
namespace X
{
    internal sealed class SnakeTests
    {
        [Test]
        public void JoinRoom_WithoutCode_CreatesRoom()
        {
            Assert.That(true, Is.True);
        }
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'tests/snake.cs')
    Assert-True ($run.ExitCode -ne 0) 'underscored method name fails'
    Assert-OutputContains -Run $run -Pattern 'PascalCase with no underscores' 'violation names the convention'

    # 3. An underscored helper (any return type, any visibility) fails.
    $helper = (Join-Path $repo 'tests/helper.cs') -replace '\\', '/'
    Write-TestFile -Path $helper -Content @'
namespace X
{
    internal sealed class HelperTests
    {
        private static Task Do_Thing_Async()
        {
            return Task.CompletedTask;
        }

        internal Property Roundtrip_Holds() => default(Property);
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'tests/helper.cs')
    Assert-True ($run.ExitCode -ne 0) 'underscored helpers fail'
    Assert-OutputContains -Run $run -Pattern '2 violation' 'each underscored method is reported'

    # 4. Underscored NUnit display names fail; dot notation passes.
    $display = (Join-Path $repo 'tests/display.cs') -replace '\\', '/'
    Write-TestFile -Path $display -Content @'
namespace X
{
    internal sealed class DisplayTests
    {
        [TestCase(true, TestName = "Refresh_OnDispose.True")]
        public void RefreshOnDisposeEndsNormally(bool value)
        {
            Assert.That(value, Is.True);
        }

        [TestCase(false, TestName = "Refresh.OnDispose.Throws")]
        public void RefreshOnDisposeThrows(bool value)
        {
            Assert.That(value, Is.False);
        }
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'tests/display.cs')
    Assert-True ($run.ExitCode -ne 0) 'underscored display name fails'
    Assert-OutputContains -Run $run -Pattern 'dot notation' 'violation names dot notation'

    # 5. A default full-repo sweep with no tests/ directory fails loudly.
    $empty = Join-Path $repo 'nowhere'
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $empty)
    Assert-True ($run.ExitCode -ne 0) 'empty scan fails loudly'

}
finally {
    Remove-TestRepo -Path $repo
}

Get-ScriptExitSummary
