# Single operator entrypoint. Actions other than status/test require local console.
[CmdletBinding()]
param(
 [ValidateSet('status','start','stop','test','install','approve')][string]$Action='status',
 [string]$CommandFile
)
$ErrorActionPreference='Stop'
$installRoot=Join-Path $env:ProgramFiles 'WindowsAdminMCP'
switch($Action){
 'install' {
    if($PSScriptRoot -eq $installRoot){throw 'INSTALL_FROM_TRUSTED_SOURCE_CHECKOUT'}
    & (Join-Path $PSScriptRoot 'Install-WindowsAdminMCP.ps1'); break
 }
 'start' { & (Join-Path $installRoot 'start-windows-admin-mcp.ps1'); break }
 'test' { & (Join-Path $installRoot 'Test-WindowsAdminMCP.ps1'); break }
 'approve' {
    if([Console]::IsInputRedirected){throw 'LOCAL_INTERACTIVE_CONSOLE_REQUIRED'}
    if(-not $CommandFile){throw 'COMMAND_FILE_REQUIRED'}
    & (Join-Path $installRoot 'Approve-WindowsAdminMCP.ps1') -CommandFile $CommandFile
    break
 }
 'stop' {
    if([Console]::IsInputRedirected){throw 'LOCAL_INTERACTIVE_CONSOLE_REQUIRED'}
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    if(-not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'NOT_ELEVATED'}
    $bin=Join-Path $installRoot 'client\tunnel-client-runtime.exe'
    $runtime=@(Get-CimInstance Win32_Process -Filter 'Name="tunnel-client-runtime.exe"'|Where-Object {$_.ExecutablePath -eq $bin})
    if(-not $runtime){Write-Host 'ALREADY_STOPPED';break}
    if((Read-Host 'Type STOP to disconnect this computer') -cne 'STOP'){throw 'STOP_NOT_CONFIRMED'}
    foreach($p in $runtime){Stop-Process -Id $p.ProcessId -Force}
    Write-Host 'STOP_REQUESTED';break
 }
 'status' {
    $server=Join-Path $installRoot 'windows-admin-mcp.ps1'
    $launch=Join-Path $installRoot 'start-windows-admin-mcp.ps1'
    $bin=Join-Path $installRoot 'client\tunnel-client-runtime.exe'
    $runtime=@(Get-CimInstance Win32_Process -Filter 'Name="tunnel-client-runtime.exe"'|Where-Object {$_.ExecutablePath -eq $bin})
    $http=0
    try{$http=[int](Invoke-WebRequest 'http://127.0.0.1:8080/readyz' -UseBasicParsing -TimeoutSec 3).StatusCode}catch{}
    $sha=if(Test-Path $server){(Get-FileHash $server -Algorithm SHA256).Hash}else{$null}
    $pin=$false
    if($sha -and (Test-Path $launch)){$pin=[IO.File]::ReadAllText($launch).Contains($sha)}
    [pscustomobject]@{installed=(Test-Path $server);runtime_running=($runtime.Count -gt 0);
      runtime_pids=@($runtime|ForEach-Object {$_.ProcessId});ready_http=$http;
      server_sha256=$sha;launcher_pin_matches=$pin;
      source_of_truth='iamaman11/powershell-admin-mcp'}|ConvertTo-Json -Depth 4 -Compress
    break
 }
}
