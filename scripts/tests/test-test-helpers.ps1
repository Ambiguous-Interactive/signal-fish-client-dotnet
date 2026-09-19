<#
.SYNOPSIS
    Self-tests for the shared test helpers in test-common.ps1.
#>
. (Join-Path $PSScriptRoot 'test-common.ps1')

$repo = New-TestRepo
try {
    Write-Host 'test-test-helpers'

    $arrayPath = Join-Path $repo 'array-content.md'
    Write-TestFile -Path $arrayPath -Content @('line one', '', 'line three')
    $arrayLines = @([System.IO.File]::ReadAllLines($arrayPath))
    Assert-Equal 3 $arrayLines.Count 'array content becomes one line per element'
    Assert-Equal 'line one' $arrayLines[0] 'array content preserves element order'
    Assert-Equal 'line three' $arrayLines[2] 'array content preserves blank separators'

    $singlePath = Join-Path $repo 'single-string.md'
    Write-TestFile -Path $singlePath -Content "# Title`n`nBody.`n"
    Assert-Equal 3 @([System.IO.File]::ReadAllLines($singlePath)).Count 'single-string content keeps embedded newlines'

    $crlfPath = Join-Path $repo 'crlf.md'
    Write-TestFile -Path $crlfPath -Content "a`r`nb"
    Assert-True (-not ([System.IO.File]::ReadAllText($crlfPath)).Contains("`r")) 'CR bytes are normalized to LF'
}
finally {
    Remove-TestRepo $repo
}
Get-ScriptExitSummary
