param([switch]$NoBrowser)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$baseUrl='http://127.0.0.1:8765'
$logDir=Join-Path $env:LOCALAPPDATA 'AEGIS\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
function Test-Ready {
 try {$version=Invoke-RestMethod "$baseUrl/api/v1/version" -TimeoutSec 2;$ready=Invoke-RestMethod "$baseUrl/api/v1/health/ready" -TimeoutSec 2;return $version.service -eq 'AEGIS Agent' -and $ready.status -eq 'ready'}catch{return $false}
}
function Open-Window {
 $profile=Join-Path $env:LOCALAPPDATA 'AEGIS\EdgeProfile'
 foreach($edge in @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe","$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe")) {
  if(Test-Path -LiteralPath $edge){Start-Process $edge -ArgumentList @("--app=$baseUrl",('--user-data-dir="'+$profile+'"'),'--no-first-run','--no-default-browser-check','--window-size=1450,900') | Out-Null;return}
 }
 Start-Process $baseUrl | Out-Null
}
$mutex=[Threading.Mutex]::new($false,'Local\AEGIS-Launcher-'+[Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
$locked=$false
try {
 $locked=$mutex.WaitOne(60000)
 if(-not $locked){throw 'Another AEGIS launch is still running.'}
 if(-not(Test-Ready)) {
  $exe=Join-Path $root 'Aegis.Agent.exe'
  if(-not(Test-Path -LiteralPath $exe)){$exe=Join-Path $root 'dist\AEGIS\Aegis.Agent.exe'}
  if(-not(Test-Path -LiteralPath $exe)){throw 'This is a source folder. Open Start AEGIS.cmd from the ready Windows bundle containing Aegis.Agent.exe. The Windows bundle includes .NET and needs no SDK.'}
  $stamp=(Get-Date).ToString('yyyyMMdd-HHmmss')
  $child=Start-Process $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logDir "$stamp-agent.log") -RedirectStandardError (Join-Path $logDir "$stamp-agent-error.log")
  $handle=$child.Handle
  $ready=$false
  for($i=0;$i -lt 80;$i++) {
   if(Test-Ready){$ready=$true;break}
   if($child.HasExited){throw "AEGIS exited with code $($child.ExitCode). Logs: $logDir"}
   Start-Sleep -Milliseconds 750
  }
  if(-not $ready){throw "AEGIS did not become ready. Logs: $logDir"}
  @{pid=$child.Id;started=$child.StartTime.ToUniversalTime().ToString('o');executable=$exe} | ConvertTo-Json | Set-Content (Join-Path $env:LOCALAPPDATA 'AEGIS\agent-process.json') -Encoding UTF8
 }
 if(-not $NoBrowser){Open-Window}
 Write-Output "AEGIS ready: $baseUrl"
}catch{
 $message=$_.Exception.Message
 $message | Set-Content (Join-Path $logDir 'startup-error.txt') -Encoding UTF8
 if(-not $NoBrowser){Add-Type -AssemblyName PresentationFramework;[Windows.MessageBox]::Show($message,'AEGIS startup error') | Out-Null}
 exit 1
}finally{if($locked){$mutex.ReleaseMutex()};$mutex.Dispose()}
