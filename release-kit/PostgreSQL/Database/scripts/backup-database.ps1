param([string]$Folder)
# Consistent pg_dump of aether_demo (custom format) into Documents\AEGIS-PostgreSQL-backups.
. (Join-Path $PSScriptRoot 'common.ps1')
try{
 Assert-Running
 if(-not $Folder){$Folder=Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'AEGIS-PostgreSQL-backups'}
 New-Item -ItemType Directory -Force -Path $Folder | Out-Null
 $file=Join-Path $Folder ('aether_demo-'+(Get-Date).ToString('yyyyMMdd-HHmmss')+'.dump')
 Set-DatabasePassword
 $null=Run-Tool (Join-Path $pgBin 'pg_dump.exe') @('-h','127.0.0.1','-p',"$dbPort",'-U','demo','-w','-d','aether_demo','-Fc','-f',(Q $file)) 'backup'
 $null=Run-Tool (Join-Path $pgBin 'pg_restore.exe') @('--list',(Q $file)) 'backup-verify'
 Write-Output "Резервная копия: $file ($([math]::Round((Get-Item $file).Length/1KB)) КБ), содержимое проверено pg_restore --list."
}catch{Write-Output "ОШИБКА: $($_.Exception.Message)";exit 1}finally{$env:PGPASSWORD=$null}