# Recovers from an interrupted schema import without deleting anything:
# the incomplete database is renamed to aether_demo_failed_<time>, then start-database recreates it.
. (Join-Path $PSScriptRoot 'common.ps1')
try{
 Assert-Running
 Set-DatabasePassword
 $marker=Join-Path $stateDir 'restored.json'
 $exists=Get-PsqlScalar 'postgres' "SELECT 1 FROM pg_database WHERE datname='aether_demo';" 'repair-exists'
 if($exists -eq '1' -and (Test-Path $marker)){Write-Output 'База aether_demo в порядке, ремонт не нужен.';exit 0}
 if($exists -eq '1'){
  $name='aether_demo_failed_'+(Get-Date).ToString('yyyyMMdd_HHmmss')
  $null=Run-Tool (Join-Path $pgBin 'psql.exe') @('-h','127.0.0.1','-p',"$dbPort",'-U','demo','-w','-d','postgres','-v','ON_ERROR_STOP=1','-c',('"ALTER DATABASE aether_demo RENAME TO '+$name+';"')) 'repair-rename'
  Write-Output "Неполная база сохранена как $name."
 }
 $env:PGPASSWORD=$null
 & (Join-Path $PSScriptRoot 'start-database.ps1') -NoConsole
}catch{Write-Output "ОШИБКА: $($_.Exception.Message)";exit 1}finally{$env:PGPASSWORD=$null}