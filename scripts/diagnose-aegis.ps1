param([string]$OutFile)
# Read-only diagnostics: which AEGIS answers on 8765, which build this folder holds,
# data directory, recent logs. Changes nothing. Passwords/tokens are not collected.
$ErrorActionPreference='Continue'
. (Join-Path $PSScriptRoot 'aegis-common.ps1')
$exe=Get-AegisBundleExe
$expected=$(if($exe){Get-AegisExeVersion $exe}else{$null})
$state=Get-AegisPortState
$lines=New-Object Collections.Generic.List[string]
$lines.Add("AEGIS диагностика — $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))")
$lines.Add("Пользователь: $env:USERNAME   Компьютер: $env:COMPUTERNAME   ОС: $((Get-CimInstance Win32_OperatingSystem).Caption)")
$lines.Add("Пакет в этой папке: $(if($exe){"$exe (версия $expected)"}else{'НЕТ Aegis.Agent.exe — это папка исходников'})")
switch($state.Kind){
 'free' {$lines.Add('Порт 8765: свободен — AEGIS не запущен.')}
 'foreign' {$lines.Add("Порт 8765: ЗАНЯТ ДРУГОЙ ПРОГРАММОЙ $($state.ProcessName) (PID $($state.Pid)).")}
 'aegis' {
  $lines.Add("Порт 8765: AEGIS $($state.Version), PID $($state.Pid), готовность: $($state.Status), служба: $($state.IsService)")
  $lines.Add("  Файл процесса: $($state.Path)")
  $lines.Add("  Данные: $($state.DataDirectory)")
  if($exe){$lines.Add("  Совпадает с этой папкой: $(Test-AegisSameBuild $state $exe $expected)")}
 }
}
$sys=Invoke-AegisJson '/api/v1/system'
if($sys -and $sys.Code -eq 200){
 $s=$sys.Body
 $lines.Add("Телеметрия: CPU $([math]::Round([double]$s.cpuLoadPercent,1))%  RAM $($s.memory.loadPercent)%  GPU: $(if($s.gpus.Count){$s.gpus[0].name}else{'нет датчика'})  Температур: $($s.temperatures.Count)  Дисков: $($s.disks.Count)  Сетевых адаптеров: $($s.network.adapters.Count)")
}elseif($state.Kind -eq 'aegis'){$lines.Add("Телеметрия: недоступна (HTTP $($sys.Code))")}
$err=Join-Path $AegisLogDir 'startup-error.txt'
if(Test-Path $err){$lines.Add("Последняя ошибка запуска: $((Get-Content $err -Raw).Trim())")}
$lines.Add("Логи: $AegisLogDir")
foreach($f in @(Get-ChildItem $AegisLogDir -Filter '*-agent-error.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1)){
 $tail=@(Get-Content $f.FullName -Tail 15 -ErrorAction SilentlyContinue)
 if($tail.Count){$lines.Add("--- $($f.Name) (последние строки) ---");$tail | ForEach-Object {$lines.Add($_)}}
}
$text=$lines -join [Environment]::NewLine
if(-not $OutFile){$OutFile=Join-Path $AegisLogDir ('diagnostics-'+(Get-Date).ToString('yyyyMMdd-HHmmss')+'.txt')}
$text | Set-Content $OutFile -Encoding UTF8
Write-Output $text
Write-Output ''
Write-Output "Сохранено: $OutFile"
