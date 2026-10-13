$ErrorActionPreference='Stop'
# Sandbox test kit only: this bootstrap trusts a test CA and opens LAN ports.
if($env:USERNAME -ne 'WDAGUtilityAccount'){throw 'Windows Sandbox only'}
$local=Join-Path $env:USERPROFILE 'AEGIS-kit'
$results=Join-Path $env:USERPROFILE 'Desktop\AEGIS-results'
New-Item -ItemType Directory -Force -Path $local,$results | Out-Null
Start-Transcript (Join-Path $results 'startup.log') -Force | Out-Null
try{
 foreach($folder in @('AEGIS','AETHER-LAN')){Copy-Item (Join-Path $PSScriptRoot $folder) (Join-Path $local $folder) -Recurse -Force}
 $shell=New-Object -ComObject WScript.Shell
 foreach($item in @(@('Start AEGIS','AEGIS\Start AEGIS.cmd'),@('Stop AEGIS','AEGIS\Stop AEGIS.cmd'),@('Diagnose AEGIS','AEGIS\Diagnose AEGIS.cmd'),@('Start AETHER LAN','AETHER-LAN\Start AETHER LAN.cmd'),@('Stop AETHER LAN','AETHER-LAN\Stop AETHER LAN.cmd'))){
  $shortcut=$shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) ($item[0]+'.lnk')))
  $shortcut.TargetPath=Join-Path $local $item[1];$shortcut.WorkingDirectory=Split-Path -Parent $shortcut.TargetPath;$shortcut.Save()
 }
 # Monitoring first: it must work even when the relay/TLS/database fails (F1).
 & (Join-Path $local 'AEGIS\scripts\start-aegis.ps1')
 $relayScript=Join-Path $local 'AETHER-LAN\scripts\start-relay.ps1'
 $relayJob=Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$relayScript+'"'),'-Lan','-NoBrowser') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $results 'relay-start.log') -RedirectStandardError (Join-Path $results 'relay-start-error.log')
 $handle=$relayJob.Handle
 if(-not $relayJob.WaitForExit(180000)){throw 'Relay startup timed out'}
 if($relayJob.ExitCode -ne 0){throw 'Мониторинг AEGIS работает, но чат-сервер AETHER не запустился. Подробности: Desktop\AEGIS-results\relay-start.log и %LOCALAPPDATA%\AETHER-LAN\logs.'}
 Copy-Item (Join-Path $env:LOCALAPPDATA 'AETHER-LAN\connection.json') (Join-Path $results 'connection.json') -Force
 Copy-Item (Join-Path $env:LOCALAPPDATA 'AETHER-LAN\tls\root.cer') (Join-Path $results 'AETHER-Test-CA.cer') -Force
}catch{$_ | Out-String | Set-Content (Join-Path $results 'startup-error.txt');Add-Type -AssemblyName PresentationFramework;[Windows.MessageBox]::Show($_.Exception.Message,'AEGIS Sandbox') | Out-Null}
finally{Stop-Transcript -ErrorAction SilentlyContinue | Out-Null}
