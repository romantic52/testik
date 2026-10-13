$ErrorActionPreference='Stop'
$runData=Join-Path $PSScriptRoot 'RunData'
New-Item -ItemType Directory -Path $runData -Force | Out-Null
$escaped=[Security.SecurityElement]::Escape($PSScriptRoot)
$escapedData=[Security.SecurityElement]::Escape($runData)
$config=@"
<Configuration>
 <VGpu>Disable</VGpu><Networking>Disable</Networking><MemoryInMB>4096</MemoryInMB>
 <ClipboardRedirection>Disable</ClipboardRedirection><PrinterRedirection>Disable</PrinterRedirection><AudioInput>Disable</AudioInput><VideoInput>Disable</VideoInput>
 <MappedFolders>
  <MappedFolder><HostFolder>$escaped</HostFolder><SandboxFolder>C:\Users\WDAGUtilityAccount\Desktop\Database-Demo-kit</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
  <MappedFolder><HostFolder>$escapedData</HostFolder><SandboxFolder>C:\Users\WDAGUtilityAccount\Desktop\Database-results</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
 </MappedFolders>
 <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File C:\Users\WDAGUtilityAccount\Desktop\Database-Demo-kit\bootstrap-database.ps1</Command></LogonCommand>
</Configuration>
"@
$path=Join-Path $env:TEMP ('AEGIS-Database-'+[guid]::NewGuid().ToString('N')+'.wsb')
[IO.File]::WriteAllText($path,$config,[Text.UTF8Encoding]::new($false))
Start-Process (Join-Path $env:WINDIR 'System32\WindowsSandbox.exe') -ArgumentList ('"'+$path+'"') | Out-Null
