param([string]$SourceData = "")
# Moves the console-mode AEGIS data into the installed service (for an install that started with an empty
# database). The service database is kept as a backup first; the console data is not modified.
$ErrorActionPreference = "Stop"
$serviceName = "AEGISAgent"
. (Join-Path $PSScriptRoot 'aegis-common.ps1')
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw "Нужны права администратора: запусти migrate-service-data.bat." }
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (-not $service) { throw "Служба AEGIS не установлена." }
$SourceData = Find-AegisConsoleData $SourceData
if (-not $SourceData) { throw "Не найдены данные консольного AEGIS (…\AppData\Local\AEGIS\aegis.db)." }
$dataDirectory = Join-Path $env:ProgramData "AEGIS"
Write-Host "Источник: $SourceData"
Write-Host "Служба:   $dataDirectory"
if ($service.Status -ne "Stopped") { Stop-Service -Name $serviceName -Force; $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(60)) }
$backup = Join-Path $dataDirectory ("service-db-backup-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Force -Path $backup | Out-Null
Get-ChildItem -LiteralPath $dataDirectory -File | Where-Object { $_.Name -like 'aegis.db*' } | Move-Item -Destination $backup
Get-ChildItem -LiteralPath $SourceData -File | Where-Object { $_.Name -like 'aegis.db*' } | Copy-Item -Destination $dataDirectory
Write-Host "Прежняя база службы сохранена: $backup"
Start-Service -Name $serviceName
if (-not (Wait-AegisReady 60 $null)) { throw "Служба не ответила за 60 секунд. Логи: $dataDirectory\logs" }
Write-Host "Готово: данные перенесены, служба запущена."
