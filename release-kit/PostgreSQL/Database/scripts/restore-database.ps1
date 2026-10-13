param([string]$File)
# Restores a backup into a NEW database aether_demo_restore_<time>; aether_demo is never overwritten.
. (Join-Path $PSScriptRoot 'common.ps1')
try{
 Assert-Running
 if(-not $File){
  $folder=Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'AEGIS-PostgreSQL-backups'
  $File=@(Get-ChildItem $folder -Filter '*.dump' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1).FullName
 }
 if(-not $File -or -not (Test-Path -LiteralPath $File)){throw 'Файл резервной копии не найден. Сначала Backup PostgreSQL.cmd.'}
 $name='aether_demo_restore_'+(Get-Date).ToString('yyyyMMdd_HHmmss')
 Set-DatabasePassword
 $null=Run-Tool (Join-Path $pgBin 'createdb.exe') @('-h','127.0.0.1','-p',"$dbPort",'-U','demo','-w','-O','demo',$name) 'restore-create'
 $null=Run-Tool (Join-Path $pgBin 'pg_restore.exe') @('-h','127.0.0.1','-p',"$dbPort",'-U','demo','-w','-d',$name,'--no-owner','--exit-on-error',(Q $File)) 'restore-run'
 $tables=Get-PsqlScalar $name "SELECT count(*) FROM pg_tables WHERE schemaname NOT IN ('pg_catalog','information_schema');" 'restore-tables'
 Write-Output "Восстановлено в базу $name из $File; таблиц: $tables. Подключение: psql -h 127.0.0.1 -p $dbPort -U demo -d $name"
}catch{Write-Output "ОШИБКА: $($_.Exception.Message)";exit 1}finally{$env:PGPASSWORD=$null}