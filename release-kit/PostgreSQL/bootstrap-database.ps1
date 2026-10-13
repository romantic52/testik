$ErrorActionPreference='Stop'
if($env:USERNAME -ne 'WDAGUtilityAccount'){throw 'Windows Sandbox only.'}
$root=Join-Path $env:USERPROFILE 'AEGIS-PostgreSQL-kit'
$results=Join-Path $env:USERPROFILE 'Desktop\Database-results'
try {
 New-Item -ItemType Directory -Path $root -Force | Out-Null
 foreach($name in @('Database','data','sql')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $root -Recurse -Force}
 $shell=New-Object -ComObject WScript.Shell
 foreach($name in @('Start PostgreSQL','Stop PostgreSQL')){
  $link=$shell.CreateShortcut((Join-Path $env:USERPROFILE ('Desktop\'+$name+'.lnk')))
  $link.TargetPath=Join-Path $root ('Database\'+$name+'.cmd');$link.WorkingDirectory=Join-Path $root 'Database';$link.Save()
 }
 & (Join-Path $root 'Database\scripts\start-database.ps1') -NoConsole
 if(Test-Path $results){Copy-Item -LiteralPath (Join-Path $env:LOCALAPPDATA 'AEGIS-PostgreSQL\connection.json') -Destination $results -Force}
 Start-Process (Join-Path $root 'Database\Start PostgreSQL.cmd') | Out-Null
} catch {
 if(Test-Path $results){$_ | Out-String | Set-Content (Join-Path $results 'startup-error.txt') -Encoding UTF8}
 Add-Type -AssemblyName PresentationFramework
 [Windows.MessageBox]::Show($_.Exception.Message,'PostgreSQL') | Out-Null
 exit 1
}
