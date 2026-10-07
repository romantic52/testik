param(
    [string]$Executable = "",
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"
$serviceName = "AEGISAgent"
$displayName = "AEGIS Agent"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell session (Run as administrator)."
    }
}

Assert-Administrator

if (-not $Executable) {
    $bundleCandidate = Join-Path $PSScriptRoot "..\Aegis.Agent.exe"
    $repoCandidate = Join-Path $PSScriptRoot "..\dist\AEGIS\Aegis.Agent.exe"

    if (Test-Path -LiteralPath $bundleCandidate -PathType Leaf) {
        $Executable = $bundleCandidate
    }
    else {
        $Executable = $repoCandidate
    }
}

$Executable = [IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
    throw "Published AEGIS executable was not found: $Executable. Run build-release.bat first."
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    throw "Service $serviceName already exists. Run uninstall-service.ps1 first."
}

$dataDirectory = Join-Path $env:ProgramData "AEGIS"
New-Item -ItemType Directory -Force -Path $dataDirectory | Out-Null

$binaryPath = '"' + $Executable + '" --Agent:DataDirectory="' + $dataDirectory + '"'
New-Service -Name $serviceName -BinaryPathName $binaryPath -DisplayName $displayName -Description "Local AEGIS Windows monitoring and SOC agent" -StartupType Automatic | Out-Null

& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
& sc.exe failureflag $serviceName 1 | Out-Null

if (-not $NoStart) {
    Start-Service -Name $serviceName
}

$service = Get-Service -Name $serviceName
Write-Host "AEGIS service installed."
Write-Host "Name:   $($service.Name)"
Write-Host "Status: $($service.Status)"
Write-Host "Binary: $Executable"
Write-Host "Data:   $dataDirectory"
