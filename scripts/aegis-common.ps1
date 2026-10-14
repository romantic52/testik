# Shared helpers for AEGIS launcher, stop and diagnostics scripts (PowerShell 5.1+).
$AegisBaseUrl='http://127.0.0.1:8765'
$AegisPort=8765
$AegisStateRoot=Join-Path $env:LOCALAPPDATA 'AEGIS'
$AegisLogDir=Join-Path $AegisStateRoot 'logs'
$AegisProcessFile=Join-Path $AegisStateRoot 'agent-process.json'
New-Item -ItemType Directory -Force -Path $AegisLogDir | Out-Null

function Get-AegisBundleExe {
 $root=Split-Path -Parent $PSScriptRoot
 foreach($candidate in @((Join-Path $root 'Aegis.Agent.exe'),(Join-Path $root 'dist\AEGIS\Aegis.Agent.exe'))){
  if(Test-Path -LiteralPath $candidate){return (Resolve-Path -LiteralPath $candidate).Path}
 }
 return $null
}
function Get-AegisExeVersion([string]$exe){
 $v=[Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
 if(-not $v){return 'unknown'}
 $parts=@($v.Split('.'))
 while($parts.Count -lt 4){$parts+='0'}
 return ($parts[0..3] -join '.')
}
function Invoke-AegisJson([string]$path,[int]$timeout=2){
 try{
  $r=Invoke-WebRequest ($AegisBaseUrl+$path) -UseBasicParsing -TimeoutSec $timeout
  return @{Code=[int]$r.StatusCode;Body=($r.Content | ConvertFrom-Json)}
 }catch [Net.WebException]{
  $resp=$_.Exception.Response
  if($resp){
   $code=[int]$resp.StatusCode;$body=$null
   try{$body=(New-Object IO.StreamReader($resp.GetResponseStream())).ReadToEnd() | ConvertFrom-Json}catch{}
   return @{Code=$code;Body=$body}
  }
  return $null
 }catch{return $null}
}
# Kind: free | aegis | foreign. Never trusts an arbitrary JSON answer as AEGIS.
function Get-AegisPortState {
 $listener=@(Get-NetTCPConnection -State Listen -LocalPort $AegisPort -ErrorAction SilentlyContinue | Select-Object -First 1)
 $owner=$null
 if($listener.Count){$owner=Get-CimInstance Win32_Process -Filter ("ProcessId="+$listener[0].OwningProcess) -ErrorAction SilentlyContinue}
 $version=Invoke-AegisJson '/api/v1/version'
 if(-not $listener.Count -and -not $version){return [pscustomobject]@{Kind='free'}}
 $isAegis=$version -and $version.Code -eq 200 -and $version.Body.service -eq 'AEGIS Agent'
 if(-not $isAegis){
  return [pscustomobject]@{Kind='foreign';Pid=$(if($owner){$owner.ProcessId}else{$listener[0].OwningProcess});ProcessName=$(if($owner){$owner.Name}else{'неизвестно'})}
 }
 $ready=Invoke-AegisJson '/api/v1/health/ready'
 $diag=Invoke-AegisJson '/api/v1/diagnostics'
 $agentPid=$(if($diag -and $diag.Body.agent.processId){[int]$diag.Body.agent.processId}elseif($owner){$owner.ProcessId}else{$null})
 $proc=$null;if($agentPid){$proc=Get-CimInstance Win32_Process -Filter ("ProcessId="+$agentPid) -ErrorAction SilentlyContinue}
 return [pscustomobject]@{
  Kind='aegis';Version=[string]$version.Body.version;Pid=$agentPid
  Path=$(if($proc){$proc.ExecutablePath}else{$null})
  Ready=($ready -and $ready.Code -eq 200 -and $ready.Body.status -eq 'ready')
  Status=$(if($ready -and $ready.Body){$ready.Body.status}else{'no answer'})
  IsService=($diag -and $diag.Body.agent.runningAsWindowsService -eq $true)
  DataDirectory=$(if($diag){$diag.Body.agent.dataDirectory}else{$null})
 }
}
function Test-AegisSameBuild($state,[string]$exe,[string]$expected){
 if($state.Kind -ne 'aegis'){return $false}
 if($state.Version -ne $expected){return $false}
 # Path may be unreadable for a process of another user; then version must suffice.
 if($state.Path -and $exe -and ($state.Path -ne $exe)){return $false}
 return $true
}
function Wait-AegisReady([int]$seconds,$child){
 $deadline=(Get-Date).AddSeconds($seconds)
 while((Get-Date) -lt $deadline){
  $r=Invoke-AegisJson '/api/v1/health/ready'
  if($r -and $r.Code -eq 200 -and $r.Body.status -eq 'ready'){return $true}
  if($child -and $child.HasExited){return $false}
  Start-Sleep -Milliseconds 500
 }
 return $false
}
# Stops an AEGIS process only after confirming it really is Aegis.Agent.exe.
function Stop-AegisProcess([int]$processId){
 $p=Get-Process -Id $processId -ErrorAction SilentlyContinue
 if(-not $p){return}
 if($p.ProcessName -ne 'Aegis.Agent'){throw "PID $processId — не AEGIS ($($p.ProcessName)); остановка отменена."}
 $null=$p.Handle
 Stop-Process -Id $processId
 if(-not $p.WaitForExit(15000)){throw "AEGIS (PID $processId) не завершился за 15 секунд."}
 for($i=0;$i -lt 20 -and (Get-NetTCPConnection -State Listen -LocalPort $AegisPort -ErrorAction SilentlyContinue);$i++){Start-Sleep -Milliseconds 250}
}

# Console-mode data of AEGIS. UAC may elevate as a *different* (administrator) account whose
# %LOCALAPPDATA% has no AEGIS data, so also look through all local profiles; newest database wins.
function Find-AegisConsoleData([string]$preferred){
 if($preferred -and (Test-Path -LiteralPath (Join-Path $preferred 'aegis.db'))){return $preferred}
 $found=Get-ChildItem -Path (Join-Path $env:SystemDrive 'Users') -Directory -ErrorAction SilentlyContinue |
  ForEach-Object { Join-Path $_.FullName 'AppData\Local\AEGIS\aegis.db' } |
  Where-Object { Test-Path -LiteralPath $_ } |
  ForEach-Object { Get-Item -LiteralPath $_ } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
 if($found){return $found.DirectoryName}
 return $null
}
