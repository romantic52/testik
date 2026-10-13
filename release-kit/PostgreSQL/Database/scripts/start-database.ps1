param([switch]$NoConsole)
. (Join-Path $PSScriptRoot 'common.ps1')
$mutex=[Threading.Mutex]::new($false,'Local\AEGIS-PostgreSQL-'+[Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
$locked=$false
try {
 $locked=$mutex.WaitOne(90000)
 if(-not $locked){throw 'Another database launch is still running.'}
 New-Item -ItemType Directory -Path $stateDir,$logs -Force | Out-Null
 $newCluster=-not(Test-Path (Join-Path $dataDir 'PG_VERSION'))
 if($newCluster){
  if(Test-Path $dataDir){throw "Incomplete cluster directory. Existing files have been preserved: $dataDir"}
  if(-not(Test-Path $secretFile)){
   $bytes=New-Object byte[] 32;$rng=[Security.Cryptography.RandomNumberGenerator]::Create();$rng.GetBytes($bytes);$password=[Convert]::ToBase64String($bytes);$rng.Dispose()
   [Management.Automation.PSCredential]::new('demo',(ConvertTo-SecureString $password -AsPlainText -Force)) | Export-Clixml $secretFile
   $password=$null
  }
  Set-DatabasePassword
  $pw=Join-Path $stateDir 'initdb.tmp'
  try {
   [IO.File]::WriteAllText($pw,$env:PGPASSWORD)
   $null=Run-Tool (Join-Path $pgBin 'initdb.exe') @('-D',(Q $dataDir),'-U','demo','--auth=scram-sha-256','--pwfile',(Q $pw),'--encoding=UTF8','--locale=C') 'initdb'
  } finally {Remove-Item -LiteralPath $pw -Force -ErrorAction SilentlyContinue;$env:PGPASSWORD=$null}
  "`nlisten_addresses = '127.0.0.1'`nport = $dbPort`nmax_connections = 20`nshared_buffers = '32MB'" | Add-Content (Join-Path $dataDir 'postgresql.conf') -Encoding ASCII
 }
 $status=Run-Tool (Join-Path $pgBin 'pg_ctl.exe') @('-D',(Q $dataDir),'status') 'status' -AllowNonzero
 if($status -ne 0){$null=Run-Tool (Join-Path $pgBin 'pg_ctl.exe') @('-D',(Q $dataDir),'-l',(Q (Join-Path $logs 'postgresql.log')),'-w','start') 'start'}
 Set-DatabasePassword
 $connection=@('-h','127.0.0.1','-p',"$dbPort",'-U','demo','-w')
 $null=Run-Tool (Join-Path $pgBin 'psql.exe') ($connection+@('-d','postgres','-At','-c','"SELECT datname FROM pg_database WHERE datname=''aether_demo'';"')) 'database-exists'
 $exists=Get-Content (Join-Path $logs 'database-exists.log') -Raw
 if([string]::IsNullOrWhiteSpace($exists)){$exists=''}else{$exists=$exists.Trim()}
 $marker=Join-Path $stateDir 'restored.json'
 if($exists -ne 'aether_demo'){
  $null=Run-Tool (Join-Path $pgBin 'createdb.exe') ($connection+@('-O','demo','aether_demo')) 'create'
  $null=Run-Tool (Join-Path $pgBin 'psql.exe') ($connection+@('-d','aether_demo','-v','ON_ERROR_STOP=1','--single-transaction','-f',(Q (Join-Path $kitRoot 'data\aether.sql')))) 'restore'
  @{restored=(Get-Date).ToString('o');source='data/aether.sql';sample='Original AETHER schema without account or message data'} | ConvertTo-Json | Set-Content $marker -Encoding UTF8
 }elseif(-not(Test-Path $marker)){
  throw 'База aether_demo есть, но импорт схемы ранее не завершился (нет restored.json). Данные сохранены. Запусти Repair PostgreSQL.cmd: он переименует неполную базу и создаст новую.'
 }
 $null=Run-Tool (Join-Path $pgBin 'psql.exe') ($connection+@('-d','aether_demo','-v','ON_ERROR_STOP=1','-f',(Q (Join-Path $kitRoot 'sql\overview.sql')))) 'overview'
 @{database='aether_demo';user='demo';host='127.0.0.1';port=$dbPort;data=$dataDir;logs=$logs} | ConvertTo-Json | Set-Content (Join-Path $stateDir 'connection.json') -Encoding UTF8
 Write-Output "PostgreSQL ready: 127.0.0.1:$dbPort / aether_demo. AEGIS and AETHER relay are not started by this launcher."
} finally {$env:PGPASSWORD=$null;if($locked){$mutex.ReleaseMutex()};$mutex.Dispose()}
if(-not $NoConsole){& (Join-Path $PSScriptRoot 'open-postgresql.ps1') -AlreadyStarted}
