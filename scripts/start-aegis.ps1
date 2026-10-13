param([switch]$NoBrowser,[switch]$ReplaceOther)
# AEGIS launcher. Starts the Aegis.Agent.exe that sits next to this script and
# opens its window. It never silently reuses a different AEGIS build: if another
# version/copy owns port 8765, the operator decides whether to replace it.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'aegis-common.ps1')

function Open-Window {
 $profile=Join-Path $AegisStateRoot 'EdgeProfile'
 foreach($edge in @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe","$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe")) {
  if(Test-Path -LiteralPath $edge){Start-Process $edge -ArgumentList @("--app=$AegisBaseUrl",('--user-data-dir="'+$profile+'"'),'--no-first-run','--no-default-browser-check','--window-size=1450,900') | Out-Null;return}
 }
 Start-Process $AegisBaseUrl | Out-Null
}
function Ask([string]$text){
 if($NoBrowser){return [bool]$ReplaceOther}
 Add-Type -AssemblyName PresentationFramework
 return [Windows.MessageBox]::Show($text,'AEGIS','YesNo','Warning') -eq 'Yes'
}

$mutex=[Threading.Mutex]::new($false,'Local\AEGIS-Launcher-'+[Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
$locked=$false
try {
 $locked=$mutex.WaitOne(60000)
 if(-not $locked){throw 'Другой запуск AEGIS ещё выполняется. Подожди минуту и попробуй снова.'}
 $exe=Get-AegisBundleExe
 if(-not $exe){throw 'Это папка с исходниками, а не готовая программа: рядом нет Aegis.Agent.exe. Открой «Start AEGIS.cmd» из готового пакета AEGIS-windows-x64 (в нём уже есть .NET, SDK не нужен).'}
 $expected=Get-AegisExeVersion $exe
 Write-Output "Пакет: $exe (версия $expected)"

 $state=Get-AegisPortState
 if($state.Kind -eq 'aegis' -and -not (Test-AegisSameBuild $state $exe $expected)) {
  $who="версия $($state.Version), PID $($state.Pid)"+$(if($state.Path){", файл $($state.Path)"}else{''})
  if($state.IsService){throw "Порт 8765 занят службой Windows «AEGIS Agent» ($who). Обнови или удали службу (uninstall-service.bat от её пакета), затем запусти снова."}
  if(-not (Ask "На этом компьютере уже работает другой AEGIS ($who).`n`nОстановить его и запустить версию $expected из этой папки?`nДанные (инциденты, аудит, история) не удаляются.")){throw "Запуск отменён: порт 8765 занят другой копией AEGIS ($who)."}
  Stop-AegisProcess $state.Pid
  $state=Get-AegisPortState
 }
 if($state.Kind -eq 'foreign'){throw "Порт 8765 занят другой программой: $($state.ProcessName) (PID $($state.Pid)). AEGIS её не останавливает. Закрой эту программу или освободи порт."}
 if($state.Kind -eq 'aegis' -and -not $state.Ready){
  # Our own build is starting up or stale: give it a moment.
  if(-not (Wait-AegisReady 30 $null)){throw "AEGIS (PID $($state.Pid)) отвечает, но не готов: $($state.Status). Запусти «Diagnose AEGIS.cmd»."}
 }
 if($state.Kind -eq 'free') {
  $stamp=(Get-Date).ToString('yyyyMMdd-HHmmss')
  $child=Start-Process $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $AegisLogDir "$stamp-agent.log") -RedirectStandardError (Join-Path $AegisLogDir "$stamp-agent-error.log")
  $null=$child.Handle
  if(-not (Wait-AegisReady 60 $child)){
   if($child.HasExited){throw "AEGIS завершился с кодом $($child.ExitCode). Лог: $AegisLogDir\$stamp-agent-error.log"}
   throw "AEGIS не стал готов за 60 секунд. Логи: $AegisLogDir"
  }
  [ordered]@{pid=$child.Id;started=$child.StartTime.ToUniversalTime().ToString('o');executable=$exe;version=$expected} | ConvertTo-Json -Compress | Set-Content $AegisProcessFile -Encoding UTF8
 }
 $final=Get-AegisPortState
 if(-not (Test-AegisSameBuild $final $exe $expected)){throw "На порту 8765 отвечает не та сборка AEGIS (версия $($final.Version)). Запусти «Diagnose AEGIS.cmd»."}
 Remove-Item -LiteralPath (Join-Path $AegisLogDir 'startup-error.txt') -ErrorAction SilentlyContinue
 if(-not $NoBrowser){Open-Window}
 Write-Output "AEGIS $expected готов: $AegisBaseUrl"
}catch{
 $message=$_.Exception.Message
 $message | Set-Content (Join-Path $AegisLogDir 'startup-error.txt') -Encoding UTF8
 Write-Output "ОШИБКА: $message"
 if(-not $NoBrowser){Add-Type -AssemblyName PresentationFramework;[Windows.MessageBox]::Show($message,'AEGIS: ошибка запуска') | Out-Null}
 exit 1
}finally{if($locked){$mutex.ReleaseMutex()};$mutex.Dispose()}
