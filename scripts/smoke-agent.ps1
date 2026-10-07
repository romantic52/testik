param(
    [string]$Executable = ""
)

$ErrorActionPreference = "Stop"
$baseUrl = "http://127.0.0.1:8765"
$api = "$baseUrl/api/v1"
$process = $null

try {
    if ($Executable) {
        $process = Start-Process -FilePath $Executable -PassThru -WindowStyle Hidden
    }
    else {
        $project = Join-Path $PSScriptRoot "..\backend\Aegis.Agent\Aegis.Agent.csproj"
        $process = Start-Process -FilePath "dotnet" -ArgumentList @(
            "run",
            "--project",
            $project,
            "-c",
            "Release",
            "--no-build"
        ) -PassThru -WindowStyle Hidden
    }

    $deadline = (Get-Date).AddSeconds(35)
    $health = $null

    do {
        Start-Sleep -Milliseconds 750
        try {
            $health = Invoke-RestMethod "$api/health" -TimeoutSec 3
        }
        catch {
            if ($process.HasExited) {
                throw "AEGIS Agent exited before becoming healthy. ExitCode=$($process.ExitCode)"
            }
        }
    } while (-not $health -and (Get-Date) -lt $deadline)

    if (-not $health) {
        throw "AEGIS Agent did not become healthy within 35 seconds"
    }
    if ($health.status -notin @("ok", "warming_up", "degraded")) {
        throw "Unexpected health status: $($health.status)"
    }

    $readyDeadline = (Get-Date).AddSeconds(20)
    $ready = $null
    do {
        try {
            $ready = Invoke-RestMethod "$api/health/ready" -TimeoutSec 3
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    } while (-not $ready -and (Get-Date) -lt $readyDeadline)

    if (-not $ready) {
        throw "AEGIS Agent did not become ready within 20 seconds"
    }
    if ($ready.database.ready -ne $true) {
        throw "SQLite readiness probe did not report ready"
    }

    $null = Invoke-RestMethod "$api/health/live" -TimeoutSec 5
    $null = Invoke-RestMethod "$api/version" -TimeoutSec 5
    $null = Invoke-RestMethod "$api/incidents" -TimeoutSec 5

    $diagnostics = Invoke-RestMethod "$api/diagnostics" -TimeoutSec 5
    if ($diagnostics.persistence.provider -ne "sqlite") {
        throw "Unexpected persistence provider: $($diagnostics.persistence.provider)"
    }

    $null = Invoke-RestMethod "$api/settings/alerts" -TimeoutSec 5
    $null = Invoke-RestMethod "$api/history?seconds=10" -TimeoutSec 5
    $null = Invoke-RestMethod "$api/network/connections?limit=5" -TimeoutSec 5
    $null = Invoke-RestMethod "$api/windows/events?log=System&limit=5" -TimeoutSec 5
    $null = Invoke-RestMethod "$api/windows/services?limit=5" -TimeoutSec 5

    $bundlePath = Join-Path $env:TEMP (
        "aegis-smoke-bundle-" + [Guid]::NewGuid().ToString("N") + ".zip"
    )
    Invoke-WebRequest "$api/reports/bundle.zip" -OutFile $bundlePath -TimeoutSec 15
    if ((Get-Item -LiteralPath $bundlePath).Length -lt 100) {
        throw "AEGIS support bundle is unexpectedly empty"
    }
    Remove-Item -LiteralPath $bundlePath -Force

    Write-Host "AEGIS smoke test passed. Status=$($health.status) Machine=$($health.machine) Persistence=$($diagnostics.persistence.provider)"
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit()
    }
}
