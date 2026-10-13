param([switch]$AlreadyStarted)
. (Join-Path $PSScriptRoot 'common.ps1')
if(-not $AlreadyStarted){& (Join-Path $PSScriptRoot 'start-database.ps1') -NoConsole}
Set-DatabasePassword
$env:PGCLIENTENCODING='UTF8'
try {
 $Host.UI.RawUI.WindowTitle='AEGIS Database Demo - PostgreSQL'
 $env:PAGER=$null;$env:PSQL_PAGER=$null
 & (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p $dbPort -U demo -w -d aether_demo -X -v ON_ERROR_STOP=1 -P pager=off -f (Join-Path $kitRoot 'sql\overview.sql')
 if($LASTEXITCODE -ne 0){throw 'Database overview failed.'}
 Write-Output 'Real PostgreSQL database. Examples: \dt   \d users   SELECT username FROM users;   \q'
 & (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p $dbPort -U demo -w -d aether_demo -X -P pager=off
} finally {$env:PGPASSWORD=$null;$env:PGCLIENTENCODING=$null}
