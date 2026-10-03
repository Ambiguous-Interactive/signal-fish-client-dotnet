<#
.SYNOPSIS
    Self-tests for scripts/lint-webgl-plugin.ps1 (the WebGL plugin contract).
#>
. (Join-Path $PSScriptRoot 'test-common.ps1')

$lint = Join-Path (Split-Path -Parent $PSScriptRoot) 'lint-webgl-plugin.ps1'
$repo = New-TestRepo
try {
    Write-Host 'test-lint-webgl-plugin'

    $pluginRoot = Join-Path $repo 'unity/Packages/test.pkg/Plugins/WebGL'
    $interopPath = Join-Path $pluginRoot 'Interop.cs'
    $jslibPath = Join-Path $pluginRoot 'Test.jslib'

    $interopCs = @(
        'namespace N',
        '{',
        '    using System.Runtime.InteropServices;',
        '    public static class Interop',
        '    {',
        '        [DllImport("__Internal")]',
        '        private static extern int TestOpen(string url);',
        '    ',
        '        [DllImport("__Internal")]',
        '        private static extern void TestClose(int handle, int code);',
        '    }',
        '}'
    )
    $interopJs = @(
        'var TestLibrary = {',
        '    TestOpen: function (url) {',
        '        return 1;',
        '    },',
        '    TestClose: function (handle, code) {',
        '    },',
        '};',
        'mergeInto(LibraryManager.library, TestLibrary);'
    )

    # 1. A matching pair passes (the -NoBuild lane self-tests only exercise
    #    the static contract; the compile lane is CI's job).
    Write-TestFile -Path $interopPath -Content $interopCs
    Write-TestFile -Path $jslibPath -Content $interopJs
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 0 $run.ExitCode 'matching pair passes'

    # 2. A DllImport entry missing from the jslib fails, naming the entry.
    $extraEntryCs = @(
        $interopCs[0..9]
        '        [DllImport("__Internal")]'
        '        private static extern int TestMissing(int handle);'
        $interopCs[10..11]
    )
    Write-TestFile -Path $interopPath -Content $extraEntryCs
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'entry missing from the jslib fails'
    Assert-True (($run.Output -join "`n") -match 'TestMissing') 'failure names the missing entry'

    # 3. A jslib export the C# never imports is harmless (internal helpers).
    $extraExportJs = @(
        $interopJs[0..3]
        '    ExtraHelper: function () {'
        '    },'
        $interopJs[4..7]
    )
    Write-TestFile -Path $interopPath -Content $interopCs
    Write-TestFile -Path $jslibPath -Content $extraExportJs
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 0 $run.ExitCode 'extra jslib exports are not violations'

    # 4. A jslib without a sibling interop *.cs is a dead plugin.
    Remove-Item -LiteralPath $interopPath -Force
    Write-TestFile -Path $jslibPath -Content $interopJs
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'jslib-only folder fails'
    Assert-True (($run.Output -join "`n") -match 'dead plugin') 'failure names the dead plugin'

    # 5. The contract is anchored to export lines: a name that appears only
    #    in a comment (no `<name>: function`) does not count.
    $commentedJs = @(
        $interopJs[0..3]
        '    // TestClose would go here'
        $interopJs[6..7]
    )
    Write-TestFile -Path $interopPath -Content $interopCs
    Write-TestFile -Path $jslibPath -Content $commentedJs
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
    Assert-Equal 1 $run.ExitCode 'a commented-out export is not an export'

    # 6. When node is available, a syntactically broken jslib fails.
    if ($null -ne (Get-Command node -ErrorAction SilentlyContinue)) {
        $brokenJs = @(
            'var TestLibrary = {'
            '    TestOpen: function (url) {'
            '        return 1;'
            '};'
            'mergeInto('
        )
        Write-TestFile -Path $jslibPath -Content $brokenJs
        $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-RepoRoot', $repo, '-NoBuild')
        Assert-Equal 1 $run.ExitCode 'syntax-broken jslib fails (node present)'
        Assert-True (($run.Output -join "`n") -match 'syntax error') 'failure names the syntax gate'
    }
    else {
        Write-Host '  SKIP syntax-broken jslib (node not on PATH)'
    }

    # 7. The real repository plugin stays green (guards against the lint
    #    and the plugin drifting apart independently of CI).
    $run = Invoke-Pwsh -ScriptPath $lint -Arguments @('-NoBuild')
    Assert-Equal 0 $run.ExitCode 'the real repository plugin passes'

    Get-ScriptExitSummary
}
finally {
    Remove-TestRepo -Path $repo
}
