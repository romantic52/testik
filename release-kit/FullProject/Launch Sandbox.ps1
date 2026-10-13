$ErrorActionPreference='Stop'
$kitRoot=$PSScriptRoot
$runData=Join-Path $kitRoot 'RunData'
New-Item -ItemType Directory -Force -Path $runData | Out-Null
$escaped=[Security.SecurityElement]::Escape($kitRoot)
$escapedData=[Security.SecurityElement]::Escape($runData)
$config=@"
<Configuration>
 <VGpu>Enable</VGpu><Networking>Enable</Networking><MemoryInMB>8192</MemoryInMB>
 <ClipboardRedirection>Disable</ClipboardRedirection><PrinterRedirection>Disable</PrinterRedirection><AudioInput>Disable</AudioInput><VideoInput>Disable</VideoInput>
 <MappedFolders>
  <MappedFolder><HostFolder>$escaped</HostFolder><SandboxFolder>C:\Users\WDAGUtilityAccount\Desktop\AEGIS-kit</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
  <MappedFolder><HostFolder>$escapedData</HostFolder><SandboxFolder>C:\Users\WDAGUtilityAccount\Desktop\AEGIS-results</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
 </MappedFolders>
 <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File C:\Users\WDAGUtilityAccount\Desktop\AEGIS-kit\bootstrap-kit.ps1</Command></LogonCommand>
</Configuration>
"@
$configPath=Join-Path $env:TEMP ('AEGIS-'+[guid]::NewGuid().ToString('N')+'.wsb')
[IO.File]::WriteAllText($configPath,$config,[Text.UTF8Encoding]::new($false))
Start-Process (Join-Path $env:WINDIR 'System32\WindowsSandbox.exe') -ArgumentList ('"'+$configPath+'"') | Out-Null
