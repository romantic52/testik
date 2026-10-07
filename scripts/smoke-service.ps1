param(
    [Parameter(Mandatory = $true)]
    [string]$Executable
)

$ErrorActionPreference = "Stop"
$baseUrl = "http://127.0.0.1:8765"
$install = Join-Path $PSScriptRoot "install-service.ps1"
$uninstall = Join-Path $PSScriptRoot "uninstall-service.ps1"
$installed = $false

try {
    & $install -Executable $Executable
    $installed = $true

    $deadline = (Get-Date).AddSeconds(45)
    $diagnostics = $null
    do {
        Start-Sleep -Milliseconds 750
        try {
            $diagnostics = Invoke-RestMethod "$baseUrl/api/v1/diagnostics" -TimeoutSec 3
        }
        catch {
        }
    } while (-not $diagnostics -and (Get-Date) -lt $deadline)

    if (-not $diagnostics) {
        throw "Installed AEGIS Windows Service did not become reachable within 45 seconds"
    }
    $agent = if ($diagnostics.agent) { $diagnostics.agent } else { $diagnostics }
    if ($agent.runningAsWindowsService -ne $true) {
        throw "AEGIS process is reachable but did not detect Windows Service hosting"
    }

    $health = Invoke-RestMethod "$baseUrl/api/v1/health" -TimeoutSec 5
    if ($health.status -notin @("ok", "warming_up")) {
        throw "Unexpected service health status: $($health.status)"
    }

    Write-Host "AEGIS Windows Service smoke passed. PID=$($agent.processId) Uptime=$($agent.uptimeSeconds)s"
}
finally {
    if ($installed -or (Get-Service -Name "AEGISAgent" -ErrorAction SilentlyContinue)) {
        & $uninstall
    }
}
