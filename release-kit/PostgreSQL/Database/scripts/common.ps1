$ErrorActionPreference='Stop'
# Loopback-only PostgreSQL (127.0.0.1:54331) in %LOCALAPPDATA%\AEGIS-PostgreSQL.
# Works for any Windows user; the outer Sandbox launcher is only a convenient isolated way to demo it.
$componentRoot=Split-Path -Parent $PSScriptRoot
$kitRoot=Split-Path -Parent $componentRoot
$stateDir=Join-Path $env:LOCALAPPDATA 'AEGIS-PostgreSQL'
$dataDir=Join-Path $stateDir 'pgdata'
$logs=Join-Path $stateDir 'logs'
$secretFile=Join-Path $stateDir 'credential.xml'
$pgBin=Join-Path $componentRoot 'postgresql\bin'
$dbPort=54331
function Q([string]$value){return '"'+$value+'"'}
function Run-Tool([string]$exe,[string[]]$arguments,[string]$name,[switch]$AllowNonzero){
 New-Item -ItemType Directory -Path $logs -Force | Out-Null
 $stdout=Join-Path $logs ($name+'.log');$stderr=Join-Path $logs ($name+'-error.log')
 $p=Start-Process $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
 $handle=$p.Handle
 if(-not $p.WaitForExit(90000)){Stop-Process -Id $p.Id -Force;throw "$name timed out. Logs: $logs"}
 $code=$p.ExitCode
 if($null -eq $code -or ($code -ne 0 -and -not $AllowNonzero)){
  throw "$name failed ($code). $(Get-Content $stderr -Raw -ErrorAction SilentlyContinue) Logs: $logs"
 }
 return $code
}
function Set-DatabasePassword {
 if(-not(Test-Path $secretFile)){throw "Database credential is missing. Existing data has been preserved: $stateDir"}
 $credential=Import-Clixml $secretFile
 $env:PGPASSWORD=$credential.GetNetworkCredential().Password
}

function Get-PsqlScalar([string]$db,[string]$sql,[string]$name){
 $null=Run-Tool (Join-Path $pgBin 'psql.exe') @('-h','127.0.0.1','-p',"$dbPort",'-U','demo','-w','-d',$db,'-At','-c',('"'+$sql+'"')) $name
 $v=Get-Content (Join-Path $logs ($name+'.log')) -Raw
 if([string]::IsNullOrWhiteSpace($v)){return ''};return $v.Trim()
}
function Assert-Running {
 $status=Run-Tool (Join-Path $pgBin 'pg_ctl.exe') @('-D',(Q $dataDir),'status') 'status-check' -AllowNonzero
 if($status -ne 0){throw 'PostgreSQL не запущен. Сначала Start PostgreSQL.cmd.'}
}
