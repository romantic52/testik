. (Join-Path $PSScriptRoot 'common.ps1')
if(Test-Path (Join-Path $dataDir 'PG_VERSION')){
 $status=Run-Tool (Join-Path $pgBin 'pg_ctl.exe') @('-D',(Q $dataDir),'status') 'status-stop' -AllowNonzero
 if($status -eq 0){$null=Run-Tool (Join-Path $pgBin 'pg_ctl.exe') @('-D',(Q $dataDir),'-m','fast','-w','stop') 'stop'}
}
Write-Output 'Demonstration database stopped. Data has been saved.'
