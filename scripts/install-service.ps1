param(
    [string]$Executable = "",
    [string]$SourceData = "",
    [switch]$NoStart
)
# Installs AEGIS as the Windows service "AEGIS Agent" (LocalSystem, automatic start).
# Running as a service gives AEGIS access to the Security log (sign-ins), per-connection
# traffic counters and hardware sensors that need elevation. Data: %ProgramData%\AEGIS.
# The console agent is stopped first; its data is copied once if the service has none.
$ErrorActionPreference = "Stop"
$serviceName = "AEGISAgent"
$displayName = "AEGIS Agent"
. (Join-Path $PSScriptRoot 'aegis-common.ps1')

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Нужны права администратора: запусти install-service.bat (он сам запросит повышение прав)."
}

if (-not $Executable) { $Executable = Get-AegisBundleExe }
if (-not $Executable -or -not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
    throw "Не найден Aegis.Agent.exe готового пакета."
}
$Executable = [IO.Path]::GetFullPath($Executable)
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    throw "Служба $serviceName уже установлена. Для переустановки сначала uninstall-service.bat."
}

# 1. Free port 8765 from a console AEGIS (never touches other programs).
$state = Get-AegisPortState
if ($state.Kind -eq 'foreign') { throw "Порт 8765 занят другой программой: $($state.ProcessName) (PID $($state.Pid))." }
if ($state.Kind -eq 'aegis') {
    Write-Host "Останавливаю консольный AEGIS $($state.Version) (PID $($state.Pid))…"
    Stop-AegisProcess $state.Pid
}

# 2. Data directory and one-time migration of the console data.
$dataDirectory = Join-Path $env:ProgramData "AEGIS"
New-Item -ItemType Directory -Force -Path $dataDirectory | Out-Null
if (-not $SourceData) { $SourceData = Join-Path $env:LOCALAPPDATA "AEGIS" }
$SourceData = Find-AegisConsoleData $SourceData
$targetDb = Join-Path $dataDirectory "aegis.db"
if (-not $SourceData) { Write-Host "Данных консольного AEGIS не найдено — служба начнёт с пустой базой." }
elseif (-not (Test-Path -LiteralPath $targetDb)) {
    Get-ChildItem -LiteralPath $SourceData -File | Where-Object { $_.Name -like 'aegis.db*' } |
        Copy-Item -Destination $dataDirectory
    Write-Host "Данные перенесены: $SourceData -> $dataDirectory (оригинал оставлен как резервная копия)."
}

# 3. Service with automatic restart on failure.
$binaryPath = '"' + $Executable + '" --Agent:DataDirectory="' + $dataDirectory + '"'
New-Service -Name $serviceName -BinaryPathName $binaryPath -DisplayName $displayName `
    -Description "AEGIS: локальный мониторинг и SOC-агент (127.0.0.1:8765)" -StartupType Automatic | Out-Null
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
& sc.exe failureflag $serviceName 1 | Out-Null

if (-not $NoStart) {
    Start-Service -Name $serviceName
    if (-not (Wait-AegisReady 60 $null)) { throw "Служба запущена, но AEGIS не ответил за 60 секунд. Логи: $dataDirectory\logs" }
}

$service = Get-Service -Name $serviceName
Write-Host "Служба AEGIS установлена: $($service.Status)"
Write-Host "Программа: $Executable"
Write-Host "Данные:    $dataDirectory"
Write-Host "Окно интерфейса: Start AEGIS.cmd (подключится к службе)."
