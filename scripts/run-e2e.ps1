<#
.SYNOPSIS
    Runs the live-server conformance suite (PLAN.md M3.6): boots the real
    Signal Fish server (Docker), then drives the seven client-author
    checklist scenarios with SignalFishPollingClient over WebSocketTransport.

.DESCRIPTION
    With Docker available and no -ServerUrl given, the script starts a
    throwaway server container on -Port with open-mode development settings
    (no app allowlist) and short liveness timers so the heartbeat and
    silent-client scenarios finish in seconds. It waits for the port, runs
    tests/SignalFish.Client.E2E with SIGNALFISH_E2E_URL set, and always
    removes the container.

    Pass -ServerUrl to skip the boot and test an already-running server
    (the e2e workflow does this against its service container).

.EXAMPLE
    pwsh -NoProfile -File scripts/run-e2e.ps1

.EXAMPLE
    pwsh -NoProfile -File scripts/run-e2e.ps1 -ServerUrl ws://localhost:3536
#>
[CmdletBinding()]
param(
    # WebSocket base URL of an already-running server (e.g. ws://localhost:3536).
    # When empty, the script boots its own Docker container.
    [string]$ServerUrl = '',

    # Server image used when booting a container.
    [string]$Image = 'ghcr.io/ambiguous-interactive/signal-fish-server:latest',

    # Host port for the booted container.
    [int]$Port = 3536,

    # Keep the booted container after the run (diagnostics).
    [switch]$KeepServer,

    # Additional dotnet test arguments (e.g. --filter).
    [string[]]$DotNetTestArgs = @()
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$containerName = 'signalfish-e2e'
$bootedHere = $false

function Wait-ForServer {
    # Ready means: the HTTP listener answers (any status), not merely that
    # the port accepts — the WebSocket upgrade needs a warmed-up server.
    param([string]$BaseUrl, [int]$TimeoutSeconds)

    $handler = New-Object System.Net.Http.HttpClientHandler
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(2)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    try {
        while ((Get-Date) -lt $deadline) {
            try {
                $null = $client.GetAsync("$BaseUrl/v2/client-config").GetAwaiter().GetResult()
                return $true
            } catch [System.Net.Http.HttpRequestException] {
                Start-Sleep -Milliseconds 500
            }
        }
    } finally {
        $client.Dispose()
        $handler.Dispose()
    }

    return $false
}

try {
    if ([string]::IsNullOrWhiteSpace($ServerUrl)) {
        if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
            throw (
                'Docker is required to boot the conformance server. ' +
                'Install Docker, or pass -ServerUrl for an already-running server.'
            )
        }

        $existing = docker ps --filter "name=$containerName" --format '{{.Names}}'
        if ($existing -eq $containerName) {
            throw "Container '$containerName' is already running; stop it or pass -ServerUrl."
        }

        Write-Host "Booting $Image on port $Port..."
        docker run -d --name $containerName -p "${Port}:3536" `
            -e SIGNAL_FISH__SECURITY__ENFORCE_APP_ID_ALLOWLIST='false' `
            -e SIGNAL_FISH__SECURITY__REQUIRE_METRICS_AUTH='false' `
            -e SIGNAL_FISH__SECURITY__CORS_ORIGINS='*' `
            -e SIGNAL_FISH__RATE_LIMIT__MAX_ROOM_CREATIONS='100' `
            -e SIGNAL_FISH__RATE_LIMIT__MAX_JOIN_ATTEMPTS='100' `
            -e SIGNAL_FISH__SERVER__PING_TIMEOUT='3' `
            -e SIGNAL_FISH__WEBSOCKET__IDLE_TIMEOUT_SECS='3' `
            -e SIGNAL_FISH__WEBSOCKET__SERVER_PING_INTERVAL_SECS='0' `
            $Image | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "docker run failed (exit $LASTEXITCODE)."
        }

        $bootedHere = $true
        $ServerUrl = "ws://localhost:$Port"

        $probeBase = "http://localhost:$Port"
        if (-not (Wait-ForServer -BaseUrl $probeBase -TimeoutSeconds 60)) {
            docker logs $containerName 2>$null | Select-Object -Last 40 | Write-Host
            throw "Server did not answer HTTP on port $Port within 60s."
        }

        Write-Host "Server is up at $ServerUrl."
    }

    # Service containers (and manually started servers) may still be booting.
    $target = [Uri]$ServerUrl
    $probeBase = "http://$($target.Host):$($target.Port)"
    if (-not (Wait-ForServer -BaseUrl $probeBase -TimeoutSeconds 60)) {
        throw "No server answering HTTP on $($target.Host):$($target.Port)."
    }

    $env:SIGNALFISH_E2E_URL = $ServerUrl
    Write-Host "Running the conformance suite against $ServerUrl..."
    dotnet test (Join-Path $repoRoot 'tests/SignalFish.Client.E2E') `
        -c Release --nologo --logger trx `
        --results-directory (Join-Path $repoRoot 'tests/SignalFish.Client.E2E/TestResults') `
        @DotNetTestArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Conformance suite failed (exit $LASTEXITCODE)."
    }

    Write-Host 'Conformance suite: green.'
} finally {
    if ($bootedHere -and -not $KeepServer) {
        Write-Host 'Stopping the conformance server...'
        docker rm -f $containerName 2>$null | Out-Null
    }
}
