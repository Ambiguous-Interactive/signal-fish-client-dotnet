<#
.SYNOPSIS
    Self-tests for scripts/lint-member-order.ps1 (canonical member order).
#>
. (Join-Path $PSScriptRoot 'test-common.ps1')

$linter = Join-Path (Split-Path -Parent $PSScriptRoot) 'lint-member-order.ps1'
$repo = New-TestRepo
try {
    Write-Host 'test-lint-member-order'

    # 1. The canonical order passes: nested type, const, static field,
    #    property, instance field, constructor, methods.
    $canonical = (Join-Path $repo 'src/canonical.cs') -replace '\\', '/'
    Write-TestFile -Path $canonical -Content @'
namespace X
{
    public sealed class Canonical
    {
        private sealed class Helper
        {
        }

        public const int Answer = 42;

        private static readonly string[] Names = new string[] { "a" };

        public string Name { get; }

        private readonly int _count;

        public Canonical(string name)
        {
            Name = name;
        }

        public string Describe()
        {
            return Name;
        }

        private int Hidden()
        {
            return _count;
        }
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/canonical.cs')
    Assert-Equal 0 $run.ExitCode 'canonical order passes'

    # 2. An instance field before a property fails.
    $fieldFirst = (Join-Path $repo 'src/field-first.cs') -replace '\\', '/'
    Write-TestFile -Path $fieldFirst -Content @'
namespace X
{
    public sealed class FieldFirst
    {
        private readonly int _count;

        public int Count { get; }
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/field-first.cs')
    Assert-True ($run.ExitCode -ne 0) 'field-before-property fails'
    Assert-OutputContains -Run $run -Pattern 'Count \(property, public\) after _count' 'violation names member and categories'

    # 3. A private method before a public method fails.
    $tiers = (Join-Path $repo 'src/tiers.cs') -replace '\\', '/'
    Write-TestFile -Path $tiers -Content @'
namespace X
{
    public sealed class Tiers
    {
        private void Hidden()
        {
        }

        public void Visible()
        {
        }
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/tiers.cs')
    Assert-True ($run.ExitCode -ne 0) 'private-method-before-public-method fails'

    # 4. A static field after an instance field fails.
    $staticLast = (Join-Path $repo 'src/static-last.cs') -replace '\\', '/'
    Write-TestFile -Path $staticLast -Content @'
namespace X
{
    public sealed class StaticLast
    {
        private int _count;

        private static readonly string Label = "x";
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/static-last.cs')
    Assert-True ($run.ExitCode -ne 0) 'static-after-instance-field fails'

    # 5. A constructor placed after methods fails.
    $lateCtor = (Join-Path $repo 'src/late-ctor.cs') -replace '\\', '/'
    Write-TestFile -Path $lateCtor -Content @'
namespace X
{
    public sealed class LateCtor
    {
        public void Run()
        {
        }

        public LateCtor()
        {
        }
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/late-ctor.cs')
    Assert-True ($run.ExitCode -ne 0) 'ctor among methods fails'
    Assert-OutputContains -Run $run -Pattern 'constructor, public\) after Run' 'violation names the preceding method'

    # 6. A nested type declared after a field fails.
    $nestedLast = (Join-Path $repo 'src/nested-last.cs') -replace '\\', '/'
    Write-TestFile -Path $nestedLast -Content @'
namespace X
{
    public sealed class NestedLast
    {
        private int _count;

        private sealed class Helper
        {
        }
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/nested-last.cs')
    Assert-True ($run.ExitCode -ne 0) 'nested-type-after-field fails'
    Assert-OutputContains -Run $run -Pattern 'Helper \(nested type, private\) after _count' 'violation names the nested type'

    # 7. Code-like text inside comments and strings never false-positives.
    $literals = (Join-Path $repo 'src/literals.cs') -replace '\\', '/'
    Write-TestFile -Path $literals -Content @'
namespace X
{
    public sealed class Literals
    {
        /*
            private static int decoy = 1;
            public void DecoyMethod()
        */
        private const string Verbatim = @"
public struct Decoy2 { get; }
";

        private static readonly string Snippet = "private int decoy2; public class Decoy {";

        public string SnippetText => Snippet;

        public Literals()
        {
        }
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/literals.cs')
    Assert-Equal 0 $run.ExitCode 'comments and strings with code-like text pass'

    # 8. Interface members are implicitly public and pass.
    $iface = (Join-Path $repo 'src/iface.cs') -replace '\\', '/'
    Write-TestFile -Path $iface -Content @'
namespace X
{
    public interface IThing
    {
        string Name { get; }

        string Describe();
    }

    public sealed class Thing : IThing
    {
        public string Name { get; } = "n";

        public string Describe()
        {
            return Name;
        }
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/iface.cs')
    Assert-Equal 0 $run.ExitCode 'interface members pass'

    # 9. Expression-bodied members: properties are properties, methods are
    #    methods (a method declared before a property still fails).
    $expr = (Join-Path $repo 'src/expr.cs') -replace '\\', '/'
    Write-TestFile -Path $expr -Content @'
namespace X
{
    public sealed class Expr
    {
        public int Square() => 4;

        public string Label => "l";
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/expr.cs')
    Assert-True ($run.ExitCode -ne 0) 'expression-bodied method before property fails'
    Assert-OutputContains -Run $run -Pattern 'Label \(property, public\) after Square' 'expression-bodied property recognized as property'

    # 10. An expression-bodied property in canonical position passes.
    $exprOk = (Join-Path $repo 'src/expr-ok.cs') -replace '\\', '/'
    Write-TestFile -Path $exprOk -Content @'
namespace X
{
    public sealed class ExprOk
    {
        public string Label => "l";

        public int Square() => 4;
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/expr-ok.cs')
    Assert-Equal 0 $run.ExitCode 'expression-bodied property before method passes'

    # 11. Every violation in one type is reported.
    $multi = (Join-Path $repo 'src/multi.cs') -replace '\\', '/'
    Write-TestFile -Path $multi -Content @'
namespace X
{
    public sealed class Multi
    {
        private int _count;

        public int Count { get; }

        public void Run()
        {
        }

        public Multi()
        {
        }
    }
}
'@
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/multi.cs')
    Assert-OutputContains -Run $run -Pattern '2 violation' 'each out-of-order member is reported'

    # 12. An empty -Paths selection exits 0.
    $run = Invoke-Pwsh -ScriptPath $linter -Arguments @('-RepoRoot', $repo, '-Paths', 'src/none.md')
    Assert-Equal 0 $run.ExitCode 'non-C# -Paths selection exits 0'
}
finally {
    Remove-TestRepo -Path $repo
}

Get-ScriptExitSummary
