param([switch]$NoBrowser)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$baseUrl = "http://127.0.0.1:8765"
$healthUrl = "$baseUrl/api/v1/version"

function Test-AegisRunning {
    try {
        $response = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 2
        return $response.service -eq "AEGIS Agent"
    }
    catch { return $false }
}

try {
    if (Test-AegisRunning) {
        Write-Host "[AEGIS] Agent already running. Opening dashboard..."
        if (-not $NoBrowser) { Start-Process $baseUrl }
        exit 0
    }

    $exe = Join-Path $root "Aegis.Agent.exe"
    if (-not (Test-Path $exe)) {
        $exe = Join-Path $root "dist\AEGIS\Aegis.Agent.exe"
    }

    if (Test-Path $exe) {
        Write-Host "[AEGIS] Starting packaged agent..."
        $child = Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -WindowStyle Minimized -PassThru
    }
    else {
        $project = Join-Path $root "backend\Aegis.Agent\Aegis.Agent.csproj"
        if (-not (Test-Path $project)) {
            throw "No packaged agent found. Download the Windows release or clone the full source repository."
        }
        $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
        if (-not $dotnet) {
            throw ".NET 8 SDK is missing. Use the packaged Windows build or install: winget install Microsoft.DotNet.SDK.8"
        }
        Write-Host "[AEGIS] Starting agent from source..."
        $child = Start-Process -FilePath $dotnet.Source -ArgumentList @("run", "--project", ('"' + $project + '"')) -WorkingDirectory $root -WindowStyle Minimized -PassThru
    }

    $ready = $false
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        Start-Sleep -Milliseconds 750
        if (Test-AegisRunning) { $ready = $true; break }
        if ($child.HasExited) {
            throw "AEGIS Agent exited unexpectedly (exit code $($child.ExitCode))."
        }
    }

    if (-not $ready) {
        throw "AEGIS Agent did not start within 60 seconds. Check the Windows Event Log or run Aegis.Agent.exe in a terminal."
    }

    Write-Host "[AEGIS] Ready: $baseUrl"
    if (-not $NoBrowser) { Start-Process $baseUrl }
    exit 0
}
catch {
    Write-Host "[AEGIS] Startup failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
