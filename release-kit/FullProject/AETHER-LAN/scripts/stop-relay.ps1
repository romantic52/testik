$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot;$stateDir=Join-Path $env:LOCALAPPDATA 'AETHER-LAN'
$file=Join-Path $stateDir 'processes.json'
function Get-RelayRecords($value){
 foreach($item in $value){if($null -ne $item.pid){$item}elseif($null -ne $item.value){Get-RelayRecords $item.value}}
}
if(Test-Path $file){foreach($record in @(Get-RelayRecords (Get-Content $file -Raw | ConvertFrom-Json))){$p=Get-Process -Id $record.pid -ErrorAction SilentlyContinue;if($p -and $p.Path -eq $record.executable -and $p.StartTime.ToUniversalTime().ToString('o') -eq $record.started){$handle=$p.Handle;Stop-Process -Id $p.Id -Force;$null=$p.WaitForExit(10000)}}}
$data=Join-Path $stateDir 'pgdata'
if(Test-Path (Join-Path $data 'PG_VERSION')){
 $fso=New-Object -ComObject Scripting.FileSystemObject;$bin=Join-Path $root 'postgresql\bin';if($bin -match '[^\x00-\x7F]'){try{$bin=$fso.GetFolder($bin).ShortPath}catch{};if($bin -match '[^\x00-\x7F]'){$bin=Join-Path $env:ProgramData 'AEGIS\postgresql-16-relay\bin'}}
 $p=Start-Process (Join-Path $bin 'pg_ctl.exe') -ArgumentList @('-D',('"'+$data+'"'),'-m','fast','-w','stop') -WindowStyle Hidden -PassThru;$handle=$p.Handle
 if(-not $p.WaitForExit(60000)){throw 'PostgreSQL не остановился за 60 секунд.'}
 # pg_ctl returns 1 when the server was already stopped; check the port, not just the code.
 for($i=0;$i -lt 20 -and (Get-NetTCPConnection -State Listen -LocalPort 54329 -ErrorAction SilentlyContinue);$i++){Start-Sleep -Milliseconds 250}
 if(Get-NetTCPConnection -State Listen -LocalPort 54329 -ErrorAction SilentlyContinue){throw "PostgreSQL relay всё ещё слушает 54329 (pg_ctl exit $($p.ExitCode)). Логи: $stateDir\logs"}
}
foreach($port in 8766,8443){if(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue){Write-Output "Внимание: порт $port всё ещё занят процессом, который этот скрипт не запускал."}}
Write-Output 'AETHER LAN остановлен; база и ключи сохранены.'
