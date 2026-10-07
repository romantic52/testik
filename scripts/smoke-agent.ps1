param(
    [string]$Executable = ""
)

$ErrorActionPreference = "Stop"
$baseUrl = "http://127.0.0.1:8765"
$process = $null

try {
    if ($Executable) {
        $process = Start-Process -FilePath $Executable -PassThru -WindowStyle Hidden
    }
    else {
        $project = Join-Path $PSScriptRoot "..\backend\Aegis.Agent\Aegis.Agent.csproj"
        $process = Start-Process -FilePath "dotnet" -ArgumentList @("run", "--project", $project, "-c", "Release", "--no-build") -PassThru -WindowStyle Hidden
    }

    $deadline = (Get-Date).AddSeconds(35)
    $health = $null

    do {
        Start-Sleep -Milliseconds 750
        try {
            $health = Invoke-RestMethod "$baseUrl/api/health" -TimeoutSec 3
        }
        catch {
            if ($process.HasExited) {
                throw "AEGIS Agent exited before becoming healthy. ExitCode=$($process.ExitCode)"
            }
        }
    } while (-not $health -and (Get-Date) -lt $deadline)

    if (-not $health) { throw "AEGIS Agent did not become healthy within 35 seconds" }
    if ($health.status -notin @("ok", "warming_up")) { throw "Unexpected health status: $($health.status)" }

    $null = Invoke-RestMethod "$baseUrl/api/incidents" -TimeoutSec 5
    $null = Invoke-RestMethod "$baseUrl/api/settings/alerts" -TimeoutSec 5
    $null = Invoke-RestMethod "$baseUrl/api/history?seconds=10" -TimeoutSec 5
    $null = Invoke-RestMethod "$baseUrl/api/network/connections?limit=5" -TimeoutSec 5
    $null = Invoke-RestMethod "$baseUrl/api/windows/events?log=System&limit=5" -TimeoutSec 5

    Write-Host "AEGIS smoke test passed. Status=$($health.status) Machine=$($health.machine)"
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit()
    }
}
