<#
.SYNOPSIS
    Verifies (default) or refreshes (-Sync) the golden protocol fixtures in
    tests/Golden/ against the signal-fish-server repo at a pinned commit.

.DESCRIPTION
    Golden fixtures are the red-green source of truth for the hand-rolled
    codec (PLAN.md M1). They are vendored byte-for-byte from the server
    repo's .llm/code-samples/protocol/ wire samples and are never hand-edited:
    resync via this script (-Sync) or fix upstream.

    Default mode downloads the upstream corpus at the pinned commit and
    compares it to tests/Golden/ byte-for-byte after CRLF normalization, so
    line-ending churn, BOM injection, and content edits all fail as drift.
    Missing or extra files fail as file-set drift. Failures include guidance,
    so unexplained drift is caught before codec tests consume stale samples.

    -Sync rewrites the fixtures from the pinned commit and regenerates
    tests/Golden/PROVENANCE.md. Use it only to intentionally bump the pin
    or re-vendor after reviewing the upstream diff.

    Network: one directory listing (authenticated `gh api` when available,
    else one anonymous api.github.com call) plus one
    raw.githubusercontent.com call per file.

.EXAMPLE
    pwsh -NoProfile -File scripts/sync-protocol-fixtures.ps1

.EXAMPLE
    pwsh -NoProfile -File scripts/sync-protocol-fixtures.ps1 -Sync -PinnedCommit <sha>
#>
[CmdletBinding()]
param(
    # Upstream commit the vendored corpus must match. Bump via -Sync after
    # reviewing the upstream diff between the old and new pins.
    [string]$PinnedCommit = 'eaae1ca3b16887f0f652bb11de135995929e8c23',

    # Repository root. Defaults to the parent of the scripts directory.
    [string]$RepoRoot,

    # Re-vendor fixtures + provenance from the pinned commit instead of only
    # verifying the vendored copy.
    [switch]$Sync
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$upstreamRepo = 'Ambiguous-Interactive/signal-fish-server'
$upstreamDir = '.llm/code-samples/protocol'
$provenanceFileName = 'PROVENANCE.md'

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}
$goldenDir = Join-Path (Join-Path $RepoRoot 'tests') 'Golden'
$provenancePath = Join-Path $goldenDir $provenanceFileName

$headers = @{ 'User-Agent' = 'signal-fish-client-dotnet fixture sync' }
$ordinal = [System.StringComparer]::Ordinal

function Get-UpstreamFileNames {
    # Listing: authenticated `gh api` when available (5000 req/hr), else the
    # anonymous REST API (60 req/hr per IP — flaky on shared CI egress).
    # File bytes always come from raw.githubusercontent.com (no API budget).
    $query = "repos/$upstreamRepo/contents/${upstreamDir}?ref=$PinnedCommit"
    $entries = $null
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        $json = gh api $query 2>$null
        if ($LASTEXITCODE -eq 0 -and $json) {
            $entries = ConvertFrom-Json -InputObject ($json -join "`n")
        }
    }
    if (-not $entries) {
        $uri = "https://api.github.com/$query"
        try {
            $entries = Invoke-RestMethod -Uri $uri -Headers $headers -Method Get
        } catch {
            throw "Failed to list ${upstreamRepo}:$upstreamDir at commit ${PinnedCommit} (gh unavailable/failed, anonymous API: $($_.Exception.Message))."
        }
    }
    $names = [string[]]@($entries | Where-Object { $_.type -eq 'file' } | ForEach-Object { $_.name })
    foreach ($name in $names) {
        if ($name -notmatch '^[A-Za-z0-9._-]+$') {
            throw "Upstream returned a file name outside the expected fixture pattern: '$name'."
        }
    }
    if ($names.Count -eq 0) {
        throw "Upstream directory ${upstreamRepo}:$upstreamDir is empty at commit $PinnedCommit."
    }
    [System.Array]::Sort($names, $ordinal)
    return $names
}

function Get-UpstreamFixtureBytes {
    param([Parameter(Mandatory)][string]$FileName)
    $uri = "https://raw.githubusercontent.com/$upstreamRepo/$PinnedCommit/$upstreamDir/$FileName"
    $tempFile = [System.IO.Path]::GetTempFileName()
    try {
        # Raw bytes, never Invoke-RestMethod stringification: a non-JSONL
        # upstream file served as application/json would otherwise be
        # auto-parsed into objects and re-serialized as garbage on both
        # sync and verify — self-consistently green.
        Invoke-WebRequest -Uri $uri -Headers $headers -Method Get -OutFile $tempFile
        # The comma prevents PowerShell from unrolling the byte array.
        return ,([System.IO.File]::ReadAllBytes($tempFile))
    } catch {
        throw "Failed to download $FileName at commit ${PinnedCommit}: $($_.Exception.Message)"
    } finally {
        Remove-Item -LiteralPath $tempFile -ErrorAction SilentlyContinue
    }
}

function ConvertTo-LfBytes {
    # Normalize CRLF to LF so Windows/Unix checkouts and upstream are
    # byte-comparable; .gitattributes enforces LF for these files. BOMs are
    # NOT stripped: they surface as drift, which is the point.
    param([Parameter(Mandatory)][AllowEmptyCollection()][byte[]]$Bytes)
    $result = [System.Collections.Generic.List[byte]]::new($Bytes.Length)
    for ($i = 0; $i -lt $Bytes.Length; $i++) {
        if ($Bytes[$i] -eq 13 -and $i + 1 -lt $Bytes.Length -and $Bytes[$i + 1] -eq 10) {
            continue
        }
        $result.Add($Bytes[$i])
    }
    return ,($result.ToArray())
}

function Get-LocalFixtureNames {
    if (-not (Test-Path -LiteralPath $goldenDir)) {
        return [string[]]@()
    }
    $names = [string[]]@(Get-ChildItem -LiteralPath $goldenDir -File -Filter '*.jsonl' |
        ForEach-Object { $_.Name })
    [System.Array]::Sort($names, $ordinal)
    return $names
}

function Assert-ExpectedFixtures {
    param(
        [string[]]$ExpectedNames,
        [string[]]$ActualNames,
        [string]$ProblemContext
    )
    $missing = @($ExpectedNames | Where-Object { $ActualNames -notcontains $_ })
    $extra = @($ActualNames | Where-Object { $ExpectedNames -notcontains $_ })
    if ($missing.Count -gt 0 -or $extra.Count -gt 0) {
        $details = @()
        if ($missing.Count -gt 0) { $details += "missing: $($missing -join ', ')" }
        if ($extra.Count -gt 0) { $details += "unexpected: $($extra -join ', ')" }
        throw "Fixture set drift ($ProblemContext) at commit ${PinnedCommit}: $($details -join '; '). Run with -Sync to re-vendor after reviewing the upstream diff."
    }
}

$upstreamNames = Get-UpstreamFileNames

if ($Sync) {
    New-Item -ItemType Directory -Force -Path $goldenDir | Out-Null
    $lineCounts = @{}
    foreach ($name in $upstreamNames) {
        $bytes = ConvertTo-LfBytes (Get-UpstreamFixtureBytes -FileName $name)
        [System.IO.File]::WriteAllBytes((Join-Path $goldenDir $name), $bytes)
        $text = [System.Text.Encoding]::UTF8.GetString($bytes)
        $lineCounts[$name] = $text.TrimEnd("`n").Split("`n").Count
    }
    $localNames = Get-LocalFixtureNames
    Assert-ExpectedFixtures -ExpectedNames $upstreamNames -ActualNames $localNames -ProblemContext 'after sync'

    $syncedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture)
    $fileTable = ($upstreamNames | ForEach-Object { "| ``$_`` | $($lineCounts[$_]) |" }) -join "`n"
    $provenance = @"
# Golden Protocol Fixtures

Vendored wire samples for the Signal Fish protocol; the red-green source of
truth for the hand-rolled codec (``PLAN.md`` M1). **Never hand-edit these
files** - resync via the sync script (``scripts/sync-protocol-fixtures.ps1``)
or fix upstream.

- Source: <https://github.com/$upstreamRepo>
- Upstream path: ``$upstreamDir/``
- Pinned commit: ``$PinnedCommit``
- Last synced: $syncedAt (UTC)

## Files

| File | Lines |
| --- | --- |
$fileTable

## Coverage gaps (at this pin)

The corpus is verbatim upstream and covers only what the server publishes.
The mandatory v2 floor also includes ``GameStarting``, ``RoomLeft``, and the
``*Failed`` family, which have no upstream wire samples yet. Request them
upstream; never hand-vendor replacements.
"@
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($provenancePath, "$provenance`n", $utf8NoBom)
    Write-Host "Synced $($upstreamNames.Count) fixture file(s) at pinned commit $PinnedCommit."
    Write-Host 'Review the diff, then commit. If the pin moved, summarize the upstream protocol changes in the commit message.'
    return
}

# Verify mode (default): vendored corpus must match the pinned commit exactly.
if (-not (Test-Path -LiteralPath $goldenDir)) {
    throw "tests/Golden/ does not exist. Run with -Sync to vendor the fixtures first."
}
$localNames = Get-LocalFixtureNames
Assert-ExpectedFixtures -ExpectedNames $upstreamNames -ActualNames $localNames -ProblemContext 'vendored corpus'

$mismatched = @()
foreach ($name in $upstreamNames) {
    $upstreamBytes = ConvertTo-LfBytes (Get-UpstreamFixtureBytes -FileName $name)
    $localBytes = ConvertTo-LfBytes ([System.IO.File]::ReadAllBytes((Join-Path $goldenDir $name)))
    if (-not [System.Linq.Enumerable]::SequenceEqual($upstreamBytes, $localBytes)) {
        $mismatched += $name
    }
}
if ($mismatched.Count -gt 0) {
    throw "Fixture drift at pinned commit ${PinnedCommit}: $($mismatched -join ', ') differ from upstream (content, line endings, or BOM). Fixtures are never hand-edited: if upstream moved intentionally, re-vendor with -Sync (bumping the pin if needed) and review the diff; otherwise restore the vendored bytes with git checkout."
}
Write-Host "Verified $($upstreamNames.Count) fixture file(s) byte-identical to ${upstreamRepo}@$PinnedCommit."
