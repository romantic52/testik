$ErrorActionPreference='Stop'
if($env:USERNAME -ne 'WDAGUtilityAccount'){throw 'Guest only'}
$root=Split-Path -Parent $PSScriptRoot;$stateDir=Join-Path $env:LOCALAPPDATA 'AETHER-LAN'
$file=Join-Path $stateDir 'processes.json'
function Get-RelayRecords($value){
 foreach($item in $value){if($null -ne $item.pid){$item}elseif($null -ne $item.value){Get-RelayRecords $item.value}}
}
if(Test-Path $file){foreach($record in @(Get-RelayRecords (Get-Content $file -Raw | ConvertFrom-Json))){$p=Get-Process -Id $record.pid -ErrorAction SilentlyContinue;if($p -and $p.Path -eq $record.executable -and $p.StartTime.ToUniversalTime().ToString('o') -eq $record.started){$handle=$p.Handle;Stop-Process -Id $p.Id -Force;$null=$p.WaitForExit(10000)}}}
$data=Join-Path $stateDir 'pgdata'
if(Test-Path (Join-Path $data 'PG_VERSION')){$p=Start-Process (Join-Path $root 'postgresql\bin\pg_ctl.exe') -ArgumentList @('-D',('"'+$data+'"'),'-m','fast','-w','stop') -WindowStyle Hidden -PassThru;$handle=$p.Handle;if(-not $p.WaitForExit(60000)){throw 'PostgreSQL stop timeout'}}
Write-Output 'AETHER LAN stopped; database and keys have been preserved.'
