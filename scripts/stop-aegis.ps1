param([switch]$Quiet)
# Stops the AEGIS agent started by this launcher. Data in %LOCALAPPDATA%\AEGIS is kept.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'aegis-common.ps1')
try{
 $state=Get-AegisPortState
 if($state.Kind -eq 'free'){Write-Output 'AEGIS не запущен.';exit 0}
 if($state.Kind -eq 'foreign'){throw "Порт 8765 занят не AEGIS: $($state.ProcessName) (PID $($state.Pid)). Ничего не остановлено."}
 if($state.IsService){throw 'AEGIS работает как служба Windows. Останови её: services.msc → «AEGIS Agent», или uninstall-service.bat.'}
 Stop-AegisProcess $state.Pid
 Remove-Item -LiteralPath $AegisProcessFile -ErrorAction SilentlyContinue
 Write-Output "AEGIS $($state.Version) (PID $($state.Pid)) остановлен. Данные сохранены в $($state.DataDirectory)."
}catch{
 Write-Output "ОШИБКА: $($_.Exception.Message)"
 if(-not $Quiet){Add-Type -AssemblyName PresentationFramework;[Windows.MessageBox]::Show($_.Exception.Message,'AEGIS: остановка') | Out-Null}
 exit 1
}
