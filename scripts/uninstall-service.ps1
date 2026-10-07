param(
    [switch]$PurgeData
)

$ErrorActionPreference = "Stop"
$serviceName = "AEGISAgent"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell session (Run as administrator)."
    }
}

Assert-Administrator

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne "Stopped") {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(20))
    }
    & sc.exe delete $serviceName | Out-Null
    Write-Host "AEGIS service removed."
}
else {
    Write-Host "AEGIS service is not installed."
}

$dataDirectory = Join-Path $env:ProgramData "AEGIS"
if ($PurgeData) {
    if (Test-Path -LiteralPath $dataDirectory) {
        Remove-Item -LiteralPath $dataDirectory -Recurse -Force
        Write-Host "Removed data directory: $dataDirectory"
    }
}
else {
    Write-Host "AEGIS service data was preserved: $dataDirectory"
    Write-Host "Use -PurgeData only if you explicitly want to delete it."
}
