param([Parameter(Mandatory)][string]$Output)
# Copies launcher/stop/diagnostics/service scripts next to a published Aegis.Agent.exe.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Force -Path (Join-Path $Output 'scripts') | Out-Null
foreach($f in 'aegis-common.ps1','start-aegis.ps1','stop-aegis.ps1','diagnose-aegis.ps1','install-service.ps1','uninstall-service.ps1','migrate-service-data.ps1'){
 Copy-Item (Join-Path $root "scripts\$f") (Join-Path $Output "scripts\$f") -Force
}
foreach($f in 'Start AEGIS.cmd','Stop AEGIS.cmd','Diagnose AEGIS.cmd','install-service.bat','uninstall-service.bat','migrate-service-data.bat'){
 Copy-Item -LiteralPath (Join-Path $root $f) -Destination (Join-Path $Output $f) -Force
}
Write-Output "Packaged scripts into $Output"