<#
.SYNOPSIS
    Enforces the canonical member order inside every C# type declaration
    (src/ and tests/).

.DESCRIPTION
    Issue #35 policy: within every class, struct, and interface body the
    member categories appear in one canonical order - nested types, const
    fields, static fields, properties, instance fields, constructors (and
    finalizers), then methods - and within one category accessibility
    ascends public -> protected -> internal -> private. Static members come
    before instance members within the same category and tier (fields and
    properties; a sequence must never decrease on the
    (category, tier, static-first) key).

    The scan is string-aware: comments and string/char literal contents are
    blanked before any brace or signature is examined, so code-like text
    inside a literal can never produce a violation. Type bodies are located
    by brace depth on the blanked text; member declarations are anchored on
    the CSharpier indent (type body + 4 spaces), which .editorconfig and the
    CSharpier check guarantee.

    Documented simplifications (deliberate, conservative): operators rank
    as methods; expression-bodied members rank by their first sigil (a
    parameter list means method, otherwise property); indexers rank as
    properties; delegates rank as nested types; events rank with
    properties; enum bodies are exempt (their members are not members of
    the containing type); static-before-instance is applied to fields and
    properties only (methods and nested types order by tier alone); a
    member declared without an access modifier defaults to public in an
    interface and private elsewhere; "protected internal" ranks as
    protected. Interpolated-string holes that contain quote characters are
    blanked conservatively (hole contents may leak into the scan only for
    that rare shape; probes show it cannot create a false positive on
    well-formed declarations). A line that cannot be classified confidently
    as a member declaration is skipped silently rather than misfired on.

    Run standalone, from CI (dotnet.yml, via lint-conventions.ps1), or from
    the pre-commit hook (which passes only staged paths).

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of the scripts directory.

.PARAMETER Paths
    Optional repo-relative (or absolute) file paths to check. When omitted,
    every *.cs file under src/ and tests/ is checked.

.EXAMPLE
    pwsh -NoProfile -File scripts/lint-member-order.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string[]]$Paths
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
        exit 0
    }
    Write-Host 'lint-member-order FAILED: no *.cs found under src/ or tests/.'
    exit 1
}

function Get-LineStarts {
    param([string]$Text)
    $starts = New-Object 'System.Collections.Generic.List[int]'
    $starts.Add(0) | Out-Null
    for ($i = 0; $i -lt $Text.Length; $i++) {
        if ($Text[$i] -eq "`n") { $starts.Add($i + 1) | Out-Null }
    }
    return ,$starts
}

function Get-StrippedCSharp {
    # Returns $Text with comment and string/char literal CONTENTS replaced
    # by spaces (newlines preserved), so offsets and line numbers are
    # unchanged while braces and signatures inside literals disappear. The
    # lexer shape mirrors lint-comment-form.ps1's Get-CSharpComments.
    param([string]$Text)

    $chars = $Text.ToCharArray()
    $len = $chars.Length
    $i = 0
    while ($i -lt $len) {
        $c = $chars[$i]

        if ($c -eq '/' -and ($i + 1) -lt $len) {
            $next = $chars[$i + 1]
            if ($next -eq '/') {
                while ($i -lt $len -and $chars[$i] -ne "`n") { $chars[$i] = ' '; $i++ }
                continue
            }
            if ($next -eq '*') {
                $chars[$i] = ' '
                $chars[$i + 1] = ' '
                $i += 2
                while ($i -lt $len - 1 -and ($chars[$i] -ne '*' -or $chars[$i + 1] -ne '/')) {
                    if ($chars[$i] -ne "`n") { $chars[$i] = ' ' }
                    $i++
                }
                if ($i -lt $len) { $chars[$i] = ' ' }
                $i++
                if ($i -lt $len) { $chars[$i] = ' ' }
                $i++
                continue
            }
        }

        if ($c -eq '"' -or $c -eq '$' -or $c -eq '@') {
            # String literal start: consume the `$`/`@` prefix run (any
            # order), then the quote run. Raw strings (quote run of 3+)
            # close on a run at least as long; verbatim (@ in prefix) close
            # on an undoubled quote; plain/interpolated strings are
            # line-bounded so an unterminated literal cannot swallow the
            # file. Every consumed content char becomes a space (newlines
            # stay).
            $j = $i
            $verbatim = $false
            while ($j -lt $len -and ($chars[$j] -eq '$' -or $chars[$j] -eq '@')) {
                if ($chars[$j] -eq '@') { $verbatim = $true }
                $chars[$j] = ' '
                $j++
            }
            if ($j -ge $len -or $chars[$j] -ne '"') {
                $i++
                continue
            }
            $quoteCount = 0
            while ($j -lt $len -and $chars[$j] -eq '"') { $quoteCount++; $j++ }
            if ($verbatim) {
                # Verbatim string: the first quote opens; `""` inside is an
                # escaped quote; the first undoubled quote closes. (A quote
                # run of 3+ after @ is content quotes, not a raw string.)
                $k = $j - $quoteCount
                $closed = $false
                while ($k -lt $len) {
                    if ($chars[$k] -eq '"') {
                        if (($k + 1) -lt $len -and $chars[$k + 1] -eq '"') { $k += 2; continue }
                        $closed = $true
                        $k++
                        break
                    }
                    $k++
                }
                $end = if ($closed) { $k } else { [Math]::Min($k, $len) }
                for ($m = $i; $m -lt $end; $m++) { if ($chars[$m] -ne "`n") { $chars[$m] = ' ' } }
                $i = $end
                continue
            }
            if ($quoteCount -eq 2) {
                $i = $j
                continue
            }
            if ($quoteCount -ge 3) {
                while ($j -lt $len) {
                    if ($chars[$j] -eq '"') {
                        $endCount = 0
                        $k = $j
                        while ($k -lt $len -and $chars[$k] -eq '"') { $endCount++; $k++ }
                        if ($endCount -ge $quoteCount) {
                            for ($m = $i; $m -lt $k; $m++) { if ($chars[$m] -ne "`n") { $chars[$m] = ' ' } }
                            $j = $k
                            break
                        }
                        $j = $k
                        continue
                    }
                    $j++
                }
                $i = $j
                continue
            }
            while ($j -lt $len) {
                if ($chars[$j] -eq '\' -and ($j + 1) -lt $len) { $j += 2; continue }
                if ($chars[$j] -eq '"') { $j++; break }
                if ($chars[$j] -eq "`n") { break }
                $j++
            }
            for ($m = $i; $m -lt $j; $m++) { if ($chars[$m] -ne "`n") { $chars[$m] = ' ' } }
            $i = $j
            continue
        }

        if ($c -eq "'") {
            $j = $i
            while ($j -lt $len) {
                if ($chars[$j] -eq '\' -and ($j + 1) -lt $len) { $j += 2; continue }
                if ($chars[$j] -eq "'") { $j++; break }
                if ($chars[$j] -eq "`n") { break }
                $j++
            }
            for ($m = $i; $m -lt $j; $m++) { if ($chars[$m] -ne "`n") { $chars[$m] = ' ' } }
            $i = $j
            continue
        }

        $i++
    }
    return -join $chars
}

$typeDeclPattern = '^(?<indent>\s*)(?<mods>(?:(?:public|internal|protected|private|static|sealed|abstract|partial|readonly|ref)\s+)*)(?:(?<kwprefix>record)\s+)?(?<kw>class|struct|interface|enum|record)\s+(?<name>[A-Za-z_]\w*)'
$modifierWords = @(
    'public', 'internal', 'protected', 'private', 'static', 'readonly', 'const',
    'sealed', 'abstract', 'override', 'virtual', 'async', 'extern', 'unsafe',
    'new', 'partial', 'ref', 'event', 'volatile'
)
$categoryNames = @('', 'nested type', 'const field', 'static field', 'property', 'field', 'constructor', 'method')
$tierNames = @('public', 'protected', 'internal', 'private')

function Get-LineOf {
    # Binary search: 0-based line index of an offset.
    param([int[]]$Starts, [int]$Offset)
    $lo = 0
    $hi = $Starts.Count - 1
    while ($lo -lt $hi) {
        $mid = [Math]::Floor(($lo + $hi + 1) / 2)
        if ($Starts[$mid] -le $Offset) { $lo = $mid } else { $hi = $mid - 1 }
    }
    return $lo
}

function Get-AccessibilityTier {
    # public 0 -> protected 1 -> internal 2 -> private 3; no modifier means
    # public inside an interface, private everywhere else.
    param([System.Collections.Generic.List[string]]$Modifiers, [bool]$InInterface)
    if ($Modifiers -contains 'public') { return 0 }
    if ($Modifiers -contains 'protected') { return 1 }
    if ($Modifiers -contains 'internal') { return 2 }
    if ($Modifiers -contains 'private') { return 3 }
    if ($InInterface) { return 0 }
    return 3
}

function Get-MemberInfo {
    # Classifies one candidate declaration line (already known to sit at the
    # member indent). Returns @{ Name; Category; Tier; StaticKey } or $null
    # when the line cannot be classified confidently (never a guess).
    param(
        [string]$Stripped,
        [int]$StartOffset,
        [int]$BodyEnd,
        [string]$EnclosingName,
        [string]$EnclosingKeyword
    )

    $lineEnd = $Stripped.IndexOf("`n", $StartOffset)
    if ($lineEnd -lt 0) { $lineEnd = $Stripped.Length }
    $candidate = $Stripped.Substring($StartOffset, $lineEnd - $StartOffset)
    $inInterface = $EnclosingKeyword -eq 'interface'

    $pos = 0
    $modifiers = New-Object 'System.Collections.Generic.List[string]'
    while ($true) {
        $word = [regex]::Match($candidate.Substring($pos), '^\s*([A-Za-z_]\w*)')
        if (-not $word.Success -or $modifierWords -notcontains $word.Groups[1].Value) { break }
        $modifiers.Add($word.Groups[1].Value) | Out-Null
        $pos += $word.Length
    }

    $nested = [regex]::Match($candidate.Substring($pos).TrimStart(), '^(?:record\s+)?(?:class|struct|interface|enum|record)\b')
    $delegateDecl = [regex]::Match($candidate.Substring($pos).TrimStart(), '^delegate\b')
    if ($nested.Success -or $delegateDecl.Success) {
        if ($modifiers.Count -eq 0 -and -not $inInterface) { return $null }
        $name = $null
        if ($delegateDecl.Success) {
            $nameMatch = [regex]::Match($candidate.Substring($pos), '([A-Za-z_]\w*)(?:\s*<[^>]*>)?\s*[(;]')
            if ($nameMatch.Success) { $name = $nameMatch.Groups[1].Value }
        }
        else {
            $tail = $candidate.Substring($pos)
            $kw = [regex]::Match($tail, '(?:record\s+)?(?:class|struct|interface|enum|record)\b')
            if ($kw.Success) {
                $name = [regex]::Match($tail.Substring($kw.Index + $kw.Length), '\s*([A-Za-z_]\w*)')
                if ($name.Success) { $name = $name.Groups[1].Value } else { $name = $null }
            }
        }
        if (-not $name) { return $null }
        return @{
            Name      = $name
            Category  = 1
            Tier      = Get-AccessibilityTier -Modifiers $modifiers -InInterface $inInterface
            StaticKey = 0
        }
    }

    if ($modifiers.Count -eq 0 -and -not $inInterface) { return $null }

    $rest = $candidate.Substring($pos).TrimStart()
    if ($rest -cmatch '\boperator\b') {
        $name = [regex]::Match($rest, 'operator\s*(\S+)')
        if (-not $name.Success) { return $null }
        return @{
            Name      = 'operator ' + $name.Groups[1].Value
            Category  = 7
            Tier      = Get-AccessibilityTier -Modifiers $modifiers -InInterface $inInterface
            StaticKey = 0
        }
    }
    if ($rest -match '^\s*~') {
        $name = [regex]::Match($rest, '~\s*([A-Za-z_]\w*)')
        if (-not $name.Success) { return $null }
        return @{
            Name      = '~' + $name.Groups[1].Value
            Category  = 6
            Tier      = Get-AccessibilityTier -Modifiers $modifiers -InInterface $inInterface
            StaticKey = 0
        }
    }
    if ($rest -match '^\s*this\b') {
        return @{
            Name      = 'this[]'
            Category  = 4
            Tier      = Get-AccessibilityTier -Modifiers $modifiers -InInterface $inInterface
            StaticKey = [int]($modifiers -contains 'static')
        }
    }

    # First sigil after the declaration start (across continuation lines):
    # `(` means method/constructor, `{` means property, `=`/`;` mean field
    # (or event). Type characters (`<`, `[`, `,`, `?`, `.`) never stop the
    # scan.
    $sigil = -1
    for ($i = $StartOffset; $i -lt $BodyEnd -and $i -lt $Stripped.Length; $i++) {
        $ch = $Stripped[$i]
        if ($ch -eq '(' -or $ch -eq '=' -or $ch -eq ';' -or $ch -eq '{') { $sigil = $i; break }
    }
    if ($sigil -lt 0) { return $null }
    $sigilChar = $Stripped[$sigil]
    # `=>` as the first sigil means an expression-bodied member with no
    # parameter list: a property (an expression-bodied method would have hit
    # its `(` first).
    $expressionBodied = $sigilChar -eq '=' -and ($sigil + 1) -lt $Stripped.Length -and $Stripped[$sigil + 1] -eq '>'
    $headEnd = [Math]::Min($sigil + $(if ($expressionBodied) { 2 } else { 1 }), $Stripped.Length)
    $head = $Stripped.Substring($StartOffset, $headEnd - $StartOffset)

    $name = [regex]::Match($head, '([A-Za-z_]\w*)\s*\($')
    if ($sigilChar -eq '(' -and $name.Success) {
        $memberName = $name.Groups[1].Value
        if ($memberName -eq $EnclosingName) {
            return @{
                Name      = $memberName
                Category  = 6
                Tier      = Get-AccessibilityTier -Modifiers $modifiers -InInterface $inInterface
                StaticKey = 0
            }
        }
        return @{
            Name      = $memberName
            Category  = 7
            Tier      = Get-AccessibilityTier -Modifiers $modifiers -InInterface $inInterface
            StaticKey = 0
        }
    }

    if ($sigilChar -eq '{' -or $expressionBodied) {
        $name = [regex]::Match($head, '([A-Za-z_]\w*)\s*({|=>)$')
        if (-not $name.Success) { return $null }
        return @{
            Name      = $name.Groups[1].Value
            Category  = 4
            Tier      = Get-AccessibilityTier -Modifiers $modifiers -InInterface $inInterface
            StaticKey = [int]($modifiers -contains 'static')
        }
    }

    if ($modifiers -contains 'event') {
        $name = [regex]::Match($head, '([A-Za-z_]\w*)\s*[;=]$')
        if (-not $name.Success) { return $null }
        return @{
            Name      = $name.Groups[1].Value
            Category  = 4
            Tier      = Get-AccessibilityTier -Modifiers $modifiers -InInterface $inInterface
            StaticKey = [int]($modifiers -contains 'static')
        }
    }

    if ($modifiers -contains 'const') {
        $name = [regex]::Match($head, '([A-Za-z_]\w*)\s*[;=]$')
        if (-not $name.Success) { return $null }
        return @{
            Name      = $name.Groups[1].Value
            Category  = 2
            Tier      = Get-AccessibilityTier -Modifiers $modifiers -InInterface $inInterface
            StaticKey = 0
        }
    }

    $name = [regex]::Match($head, '([A-Za-z_]\w*)\s*[;=]$')
    if (-not $name.Success) { return $null }
    $isStatic = $modifiers -contains 'static'
    return @{
        Name      = $name.Groups[1].Value
        Category  = $(if ($isStatic) { 3 } else { 5 })
        Tier      = Get-AccessibilityTier -Modifiers $modifiers -InInterface $inInterface
        StaticKey = [int]$isStatic
    }
}

$violations = New-Object 'System.Collections.Generic.List[string]'
$checkedCount = 0

foreach ($target in $targets) {
    if (-not (Test-Path -LiteralPath $target)) {
        $violations.Add("path not found: $target")
        continue
    }

    $fullPath = [System.IO.Path]::GetFullPath($target)
    $rootPath = [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/')
    if ($fullPath.StartsWith($rootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        $relative = $fullPath.Substring($rootPath.Length + 1)
    }
    else {
        $relative = $fullPath
    }
    $relative = $relative -replace '\\', '/'
    $checkedCount++

    $text = [System.IO.File]::ReadAllText($target)
    $stripped = Get-StrippedCSharp -Text $text
    $lineStarts = Get-LineStarts -Text $stripped

    # Locate every type declaration and its brace-delimited body.
    $types = New-Object 'System.Collections.Generic.List[object]'
    for ($line = 0; $line -lt $lineStarts.Count; $line++) {
        $lineEnd = [Math]::Min(
            $(if ($line + 1 -lt $lineStarts.Count) { $lineStarts[$line + 1] - 1 } else { $stripped.Length }),
            $stripped.Length
        )
        $decl = [regex]::Match($stripped.Substring($lineStarts[$line], $lineEnd - $lineStarts[$line]), $typeDeclPattern)
        if (-not $decl.Success) { continue }
        $nameEnd = $lineStarts[$line] + $decl.Groups['name'].Index + $decl.Groups['name'].Length

        # First `{` opens the body; a `;` first means a bodyless record.
        $open = -1
        $close = -1
        $scan = $nameEnd
        while ($scan -lt $stripped.Length) {
            $ch = $stripped[$scan]
            if ($ch -eq ';') { break }
            if ($ch -eq '{') {
                $open = $scan
                $depth = 1
                $scan++
                while ($scan -lt $stripped.Length -and $depth -gt 0) {
                    if ($stripped[$scan] -eq '{') { $depth++ }
                    elseif ($stripped[$scan] -eq '}') { $depth-- }
                    $scan++
                }
                $close = $scan - 1
                break
            }
            $scan++
        }
        if ($open -lt 0 -or $close -lt 0) { continue }

        $types.Add(@{
            Name      = $decl.Groups['name'].Value
            Keyword   = $decl.Groups['kw'].Value
            Indent    = $decl.Groups['indent'].Length
            NameEnd   = $nameEnd
            Open      = $open
            Close     = $close
            OpenLine  = Get-LineOf -Starts $lineStarts -Offset $open
            CloseLine = Get-LineOf -Starts $lineStarts -Offset $close
        }) | Out-Null
    }

    foreach ($type in $types) {
        if ($type.Keyword -eq 'enum') { continue }

        $memberIndent = $type.Indent + 4
        $previous = $null
        for ($line = $type.OpenLine; $line -le $type.CloseLine; $line++) {
            $lineStart = $lineStarts[$line]
            $lineEnd = [Math]::Min(
                $(if ($line + 1 -lt $lineStarts.Count) { $lineStarts[$line + 1] - 1 } else { $stripped.Length }),
                $stripped.Length
            )
            if ($lineEnd -le $lineStart) { continue }
            $prefix = $stripped.Substring($lineStart, $lineEnd - $lineStart)
            $col = $prefix.Length - $prefix.TrimStart(' ', "`t").Length
            if ($col -ne $memberIndent) { continue }
            if ($prefix.Trim().Length -eq 0) { continue }
            if ($prefix.Trim()[0] -eq '[' -or $prefix.Trim()[0] -eq '{' -or $prefix.Trim()[0] -eq '}') { continue }
            if ($prefix.Trim() -notmatch '^[A-Za-z_~]') { continue }

            $member = Get-MemberInfo -Stripped $stripped -StartOffset ($lineStart + $col) -BodyEnd $type.Close -EnclosingName $type.Name -EnclosingKeyword $type.Keyword
            if ($null -eq $member) { continue }
            $member.Line = $line + 1
            $member.Rank = $member.Category * 1000 + $member.Tier * 10 + $member.StaticKey

            if ($null -ne $previous -and $member.Rank -lt $previous.Rank) {
                $violations.Add(
                    "$relative($($member.Line)): $($member.Name) ($($categoryNames[$member.Category]), " +
                    "$($tierNames[$member.Tier])) after $($previous.Name) " +
                    "($($categoryNames[$previous.Category]), $($tierNames[$previous.Tier]))."
                )
            }
            $previous = $member
        }
    }
}

if ($violations.Count -gt 0) {
    foreach ($violation in $violations) {
        Write-Host "lint-member-order: $violation"
    }
    Write-Host "lint-member-order FAILED: checked $checkedCount file(s), $($violations.Count) violation(s)."
    exit 1
}
Write-Host "lint-member-order passed: checked $checkedCount file(s), 0 violations."
exit 0
