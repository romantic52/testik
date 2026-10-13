$ErrorActionPreference='Stop'
if($env:USERNAME -ne 'WDAGUtilityAccount'){throw 'Open Start Database Sandbox.cmd from the outer folder. Database tools run inside Windows Sandbox.'}
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
