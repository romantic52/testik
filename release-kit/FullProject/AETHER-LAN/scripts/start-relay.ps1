param([switch]$Lan,[switch]$NoBrowser)
$ErrorActionPreference='Stop'
# Local mode: 127.0.0.1:8766 only. LAN mode (-Lan): HTTPS on this PC's IPv4 with a
# self-made CA; the CA is trusted system-wide only inside the disposable Sandbox.
$sandbox=$env:USERNAME -eq 'WDAGUtilityAccount'
$root=Split-Path -Parent $PSScriptRoot
$stateDir=Join-Path $env:LOCALAPPDATA 'AETHER-LAN'
$logs=Join-Path $stateDir 'logs';New-Item -ItemType Directory -Force -Path $stateDir,$logs | Out-Null
$pgBin=Join-Path $root 'postgresql\bin';$python=Join-Path $root 'python\python.exe';$data=Join-Path $stateDir 'pgdata';$secretFile=Join-Path $stateDir 'db-password.xml';$adminFile=Join-Path $stateDir 'db-admin-password.xml'
function Q([string]$value){return '"'+$value+'"'}
# PostgreSQL on Windows cannot initdb from a non-ASCII install path (cp1251 bytes in
# the share path break UTF8 bootstrap). Use the 8.3 short path, or an ASCII runtime copy.
function Get-AsciiPath([string]$path,[string]$fallbackName){
 if($path -notmatch '[^\x00-\x7F]'){return $path}
 try{$short=(New-Object -ComObject Scripting.FileSystemObject).GetFolder($path).ShortPath;if($short -notmatch '[^\x00-\x7F]'){return $short}}catch{}
 if(-not $fallbackName){throw "Путь содержит не-ASCII символы и не имеет короткого имени: $path. Перенеси папку в путь из латинских букв."}
 $cache=Join-Path $env:ProgramData ('AEGIS-runtime\'+$fallbackName)
 if(-not(Test-Path (Join-Path $cache 'bin\initdb.exe'))){New-Item -ItemType Directory -Force -Path $cache | Out-Null;Copy-Item (Join-Path (Split-Path -Parent $path) '*') $cache -Recurse -Force}
 return (Join-Path $cache 'bin')
}
function Get-RelayRecords($value){
 foreach($item in $value){if($null -ne $item.pid){$item}elseif($null -ne $item.value){Get-RelayRecords $item.value}}
}
function Run-Tool([string]$exe,[string[]]$arguments,[string]$name,[switch]$AllowNonzero){
 $p=Start-Process $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logs ($name+'.log')) -RedirectStandardError (Join-Path $logs ($name+'-error.log'))
 $handle=$p.Handle;if(-not $p.WaitForExit(90000)){Stop-Process -Id $p.Id -Force;throw "$name timeout"}
 $code=$p.ExitCode;if($null -eq $code -or ($code -ne 0 -and -not $AllowNonzero)){throw "$name failed: $code. Logs: $logs"};return $code
}
try {
 $pgBin=Get-AsciiPath $pgBin 'postgresql-16-relay';$data=Join-Path (Get-AsciiPath $stateDir $null) 'pgdata'
 if(-not(Test-Path (Join-Path $data 'PG_VERSION'))){
  if(Test-Path $data){throw "Incomplete database directory: $data. Existing files have been preserved."}
  $bytes=New-Object byte[] 32;$rng=[Security.Cryptography.RandomNumberGenerator]::Create();$rng.GetBytes($bytes);$admin=[Convert]::ToBase64String($bytes);$rng.GetBytes($bytes);$password=[Convert]::ToBase64String($bytes);$rng.Dispose()
  # F3: persist both secrets (DPAPI) before touching the cluster so an interrupted init is recoverable.
  $password | ConvertTo-SecureString -AsPlainText -Force | Export-Clixml $secretFile
  $admin | ConvertTo-SecureString -AsPlainText -Force | Export-Clixml $adminFile
  $pw=Join-Path $stateDir 'initdb.tmp';[IO.File]::WriteAllText($pw,$admin)
  try{$null=Run-Tool (Join-Path $pgBin 'initdb.exe') @('-D',(Q $data),'-U','postgres','--auth=scram-sha-256','--pwfile',(Q $pw),'--encoding=UTF8','--locale=C') 'initdb'}finally{Remove-Item -LiteralPath $pw -Force -ErrorAction SilentlyContinue}
  "`nlisten_addresses = '127.0.0.1'`nport = 54329`nmax_connections = 40`nshared_buffers = '64MB'" | Add-Content (Join-Path $data 'postgresql.conf') -Encoding ASCII
  $null=Run-Tool (Join-Path $pgBin 'pg_ctl.exe') @('-D',(Q $data),'-l',(Q (Join-Path $logs 'postgresql.log')),'-w','start') 'pg-start'
  $env:PGPASSWORD=$admin
  $null=Run-Tool (Join-Path $pgBin 'psql.exe') @('-h','127.0.0.1','-p','54329','-U','postgres','-d','postgres','-v','ON_ERROR_STOP=1','-c',("`"CREATE ROLE aether_user LOGIN PASSWORD '$password';`"")) 'db-role'
  $null=Run-Tool (Join-Path $pgBin 'createdb.exe') @('-h','127.0.0.1','-p','54329','-U','postgres','-O','aether_user','aether_lan') 'db-create'
  $env:PGPASSWORD=$null
 }else{
  if(-not(Test-Path $secretFile)){throw 'Database password backup is missing; existing database was not changed.'}
  $status=Run-Tool (Join-Path $pgBin 'pg_ctl.exe') @('-D',(Q $data),'status') 'pg-status' -AllowNonzero
  if($status -ne 0){$null=Run-Tool (Join-Path $pgBin 'pg_ctl.exe') @('-D',(Q $data),'-l',(Q (Join-Path $logs 'postgresql.log')),'-w','start') 'pg-start'}
  # F3: finish a previously interrupted initialisation (role/database) without touching existing data.
  if(Test-Path $adminFile){
   $env:PGPASSWORD=[Net.NetworkCredential]::new('',(Import-Clixml $adminFile)).Password
   $null=Run-Tool (Join-Path $pgBin 'psql.exe') @('-h','127.0.0.1','-p','54329','-U','postgres','-d','postgres','-At','-c','"SELECT 1 FROM pg_roles WHERE rolname=''aether_user'';"') 'db-role-check'
   if(-not ((Get-Content (Join-Path $logs 'db-role-check.log') -Raw) -match '1')){
    $pwd=[Net.NetworkCredential]::new('',(Import-Clixml $secretFile)).Password
    $null=Run-Tool (Join-Path $pgBin 'psql.exe') @('-h','127.0.0.1','-p','54329','-U','postgres','-d','postgres','-v','ON_ERROR_STOP=1','-c',("""CREATE ROLE aether_user LOGIN PASSWORD '$pwd';""")) 'db-role';$pwd=$null
   }
   $null=Run-Tool (Join-Path $pgBin 'psql.exe') @('-h','127.0.0.1','-p','54329','-U','postgres','-d','postgres','-At','-c','"SELECT 1 FROM pg_database WHERE datname=''aether_lan'';"') 'db-check'
   if(-not ((Get-Content (Join-Path $logs 'db-check.log') -Raw) -match '1')){$null=Run-Tool (Join-Path $pgBin 'createdb.exe') @('-h','127.0.0.1','-p','54329','-U','postgres','-O','aether_user','aether_lan') 'db-create'}
   $env:PGPASSWORD=$null
  }
 }
 $secret=Import-Clixml $secretFile;$env:DB_PASS=[Net.NetworkCredential]::new('',$secret).Password
 $env:DB_NAME='aether_lan';$env:DB_USER='aether_user';$env:DB_HOST='127.0.0.1';$env:PGPORT='54329';$env:ALLOWED_ORIGINS='http://127.0.0.1:8765,http://127.0.0.1:8766'
 $pids=@()
 try{$running=Invoke-RestMethod 'http://127.0.0.1:8766/health' -TimeoutSec 2}catch{$running=$null}
 # Only the original AETHER health contract counts as a running relay (F6).
 if($running -and -not ($running.status -eq 'ok' -and $running.e2e)){throw 'Порт 8766 занят не AETHER relay. Освободи порт; чужой процесс не останавливается.'}
 if(-not $running){
  $p=Start-Process $python -ArgumentList '-m uvicorn server.main:app --host 127.0.0.1 --port 8766' -WorkingDirectory (Join-Path $root 'relay') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logs 'relay.log') -RedirectStandardError (Join-Path $logs 'relay-error.log')
  $pids+=@{pid=$p.Id;started=$p.StartTime.ToUniversalTime().ToString('o');executable=$python}
 }
 for($i=0;$i -lt 45;$i++){try{$health=Invoke-RestMethod 'http://127.0.0.1:8766/health' -TimeoutSec 2;break}catch{Start-Sleep -Seconds 1}}
 if(-not $health){throw "Relay did not start. Logs: $logs"}
 $url='http://127.0.0.1:8766'
 if($Lan){
  $ip=(Get-NetIPAddress -AddressFamily IPv4 | Where-Object {$_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*'} | Select-Object -First 1).IPAddress
  $tls=Join-Path $stateDir 'tls'
  # F5: re-issue the test certificate when the guest IP changed or it is about to expire.
  $pem=Join-Path $tls 'server.pem'
  if(Test-Path $pem){
   $valid=$false
   try{$cert=New-Object Security.Cryptography.X509Certificates.X509Certificate2 $pem;$san=($cert.Extensions | Where-Object {$_.Oid.Value -eq '2.5.29.17'}).Format($false);$valid=($san -match [regex]::Escape($ip)) -and $cert.NotAfter -gt (Get-Date).AddDays(3)}catch{}
   if(-not $valid){Move-Item $tls ($tls+'-old-'+(Get-Date).ToString('yyyyMMddHHmmss'));Write-Output "TLS-сертификат не подходит для $ip или истекает — выпускаю новый."}
  }
  if(-not(Test-Path (Join-Path $tls 'server.pem'))){$null=Run-Tool $python @((Q (Join-Path $PSScriptRoot 'make-tls.py')),(Q $tls),$ip) 'tls'
   # Only the disposable Sandbox trusts the test CA system-wide. On a real PC the
   # server does not need to trust its own CA: other computers import root.cer.
   if($sandbox){Import-Certificate (Join-Path $tls 'root.cer') -CertStoreLocation Cert:\LocalMachine\Root | Out-Null}}
  $url="https://${ip}:8443"
  # Verify HTTPS against our own CA file (no system trust change needed).
  function Test-Https{ $code="import ssl,urllib.request,sys;ctx=ssl.create_default_context(cafile=sys.argv[2]);print(urllib.request.urlopen(sys.argv[1]+'/health',context=ctx,timeout=3).read().decode())"; $r=& $python -c $code $url (Join-Path $tls 'root.pem') 2>$null; if($LASTEXITCODE -eq 0 -and $r -match 'server_never_sees_plaintext'){return $r}; return $null }
  $secure=Test-Https
  if(-not $secure){
   $p=Start-Process $python -ArgumentList @('-m','uvicorn','server.main:app','--host',$ip,'--port','8443','--ssl-keyfile',(Q (Join-Path $tls 'server.key')),'--ssl-certfile',(Q (Join-Path $tls 'server.pem'))) -WorkingDirectory (Join-Path $root 'relay') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logs 'relay-https.log') -RedirectStandardError (Join-Path $logs 'relay-https-error.log')
   $pids+=@{pid=$p.Id;started=$p.StartTime.ToUniversalTime().ToString('o');executable=$python}
  }
  for($i=0;$i -lt 45 -and -not $secure;$i++){Start-Sleep -Seconds 1;$secure=Test-Https}
  if(-not $secure){throw "HTTPS validation failed. Logs: $logs"}
 }
 $recordFile=Join-Path $stateDir 'processes.json'
 $old=@();if(Test-Path $recordFile){$old=@(Get-RelayRecords (Get-Content $recordFile -Raw | ConvertFrom-Json))}
 $all=@($old)+@($pids)
 ConvertTo-Json -InputObject $all -Depth 5 | Set-Content $recordFile -Encoding UTF8
 @{server=$url;local='http://127.0.0.1:8766';logs=$logs;certificate=(Join-Path $stateDir 'tls\root.cer')} | ConvertTo-Json | Set-Content (Join-Path $stateDir 'connection.json') -Encoding UTF8
 Write-Output "AETHER relay ready: $url"
 if(-not $NoBrowser){Start-Process 'http://127.0.0.1:8766' | Out-Null}
}catch{
 $message=$_.Exception.Message;$message | Set-Content (Join-Path $logs 'startup-error.txt');if(-not $NoBrowser){Add-Type -AssemblyName PresentationFramework;[Windows.MessageBox]::Show($message,'AETHER LAN') | Out-Null};exit 1
}finally{$env:DB_PASS=$null;$env:PGPASSWORD=$null}
