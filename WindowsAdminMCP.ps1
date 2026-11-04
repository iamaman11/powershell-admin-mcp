# WindowsAdminMCP operator entrypoint. One full-administrator session; no mode switches.
[CmdletBinding()]
param(
 [ValidateSet('start','status','stop','test','install','configure')][string]$Action='start'
)
$ErrorActionPreference='Stop'
$root=Join-Path $env:ProgramFiles 'WindowsAdminMCP'
$server=Join-Path $root 'windows-admin-mcp.ps1'
$exe=Join-Path $root 'client\tunnel-client-runtime.exe'
$secretRoot=Join-Path $env:ProgramData 'WindowsAdminMCP'
$credential=Join-Path $secretRoot 'tunnel-credential.dpapi'
$serverSha='8A62AFA79D829D91D88C3F06D8F69004FEAE8C6286A6BE59F19EC7AF672AF0B9'
$exeSha='CB3B2AB34A77096750561B8D46319AF282096A169A90B9C8E36567DD4FE06837'

function Assert-Administrator {
 $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
 if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'ADMINISTRATOR_REQUIRED'}
}
function Assert-Integrity {
 foreach($pair in @(@($server,$serverSha),@($exe,$exeSha))){
  if(-not(Test-Path -LiteralPath $pair[0] -PathType Leaf)){throw 'INSTALLATION_INCOMPLETE'}
  if((Get-FileHash -LiteralPath $pair[0] -Algorithm SHA256).Hash -cne $pair[1]){throw 'INSTALLATION_HASH_MISMATCH'}
 }
}
function Get-TunnelRuntime {
 $exeExpected=$exe
 return @(Get-CimInstance Win32_Process -Filter 'Name="tunnel-client-runtime.exe"' |
  Where-Object {$_.ExecutablePath -eq $exeExpected})
}
function Get-Health {
 try{return [int](Invoke-WebRequest 'http://127.0.0.1:8080/readyz' -UseBasicParsing -TimeoutSec 3).StatusCode}
 catch{return 0}
}
function Protect-ConfigRoot {
 [void][IO.Directory]::CreateDirectory($secretRoot)
 $acl=Get-Acl -LiteralPath $secretRoot
 $acl.SetAccessRuleProtection($true,$false)
 foreach($rule in @($acl.Access)){[void]$acl.RemoveAccessRuleSpecific($rule)}
 foreach($sid in @('S-1-5-18','S-1-5-32-544')){
  $rule=[Security.AccessControl.FileSystemAccessRule]::new(
   [Security.Principal.SecurityIdentifier]::new($sid),
   [Security.AccessControl.FileSystemRights]::FullControl,
   [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
   [Security.AccessControl.PropagationFlags]::None,
   [Security.AccessControl.AccessControlType]::Allow)
  [void]$acl.AddAccessRule($rule)
 }
 Set-Acl -LiteralPath $secretRoot -AclObject $acl
}
function Get-TunnelIdFromText([string]$text) {
 if([string]::IsNullOrEmpty($text)){return $null}
 # Accept both --control-plane.tunnel-id=... and --control-plane.tunnel-id <value>.
 $cmd=[regex]::Match($text,'(?i)--control-plane\.tunnel-id(?:\s+|=)["'']?(tunnel_[a-f0-9]{32})(?=$|[\s"'';])')
 if($cmd.Success){return $cmd.Groups[1].Value.ToLowerInvariant()}
 # Recover the local machine-specific ID from earlier privileged launchers.
 $assignment=[regex]::Match($text,'(?i)\$env:CONTROL_PLANE_TUNNEL_ID\s*=\s*["''](tunnel_[a-f0-9]{32})["'']')
 if($assignment.Success){return $assignment.Groups[1].Value.ToLowerInvariant()}
 return $null
}
function Get-ExistingTunnelId {
 if($env:CONTROL_PLANE_TUNNEL_ID -match '^tunnel_[a-f0-9]{32}$'){return $env:CONTROL_PLANE_TUNNEL_ID.ToLowerInvariant()}
 foreach($proc in @(Get-TunnelRuntime)){
  $id=Get-TunnelIdFromText ([string]$proc.CommandLine)
  if($id){return $id}
 }
 # v0.4/v0.5 backups were saved in this administrator-only ProgramData directory.
 # This is a migration fallback, not a new runtime dependency.
 if(Test-Path -LiteralPath $secretRoot -PathType Container){
  $dirAcl=Get-Acl -LiteralPath $secretRoot
  if(-not $dirAcl.AreAccessRulesProtected){throw 'CONFIG_ARCHIVE_ACL_UNPROTECTED'}
  $dirs=@(Get-ChildItem -LiteralPath $secretRoot -Directory -Filter 'rollback-*' -ErrorAction Stop |
   Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 20)
  foreach($dir in $dirs){
   $path=Join-Path $dir.FullName 'start-windows-admin-mcp.ps1'
   if(-not(Test-Path -LiteralPath $path -PathType Leaf)){continue}
   if((Get-Item -LiteralPath $path).Length -gt 16384){continue}
   $id=Get-TunnelIdFromText ([IO.File]::ReadAllText($path))
   if($id){return $id}
  }
 }
 return $null
}
switch($Action){
 'status' {
  $runtime=@(Get-TunnelRuntime)
  $sha=if(Test-Path -LiteralPath $server){(Get-FileHash -LiteralPath $server -Algorithm SHA256).Hash}else{$null}
  [pscustomobject]@{
   installed=[bool]$sha;integrity_ok=($sha -ceq $serverSha -and (Test-Path $exe) -and (Get-FileHash $exe -Algorithm SHA256).Hash -ceq $exeSha)
   runtime_running=($runtime.Count -gt 0);runtime_pid=@($runtime|ForEach-Object {$_.ProcessId})
   ready_http=(Get-Health);credential_configured=(Test-Path -LiteralPath $credential -PathType Leaf)
   administrator=( [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
   source_of_truth='iamaman11/powershell-admin-mcp'
  }|ConvertTo-Json -Depth 4 -Compress
  break
 }
 'install' {
  Assert-Administrator
  if($PSScriptRoot -eq $root){throw 'INSTALL_FROM_TRUSTED_CHECKOUT'}
  & (Join-Path $PSScriptRoot 'Install-WindowsAdminMCP.ps1')
  break
 }
 'test' { & (Join-Path $root 'Test-WindowsAdminMCP.ps1');break }
 'configure' {
  Assert-Administrator
  if([Console]::IsInputRedirected){throw 'LOCAL_CONSOLE_REQUIRED_FOR_ONE_TIME_CONFIGURATION'}
  $tunnel=Get-ExistingTunnelId
  if(-not $tunnel){throw 'TUNNEL_ID_AUTODISCOVERY_FAILED: no running tunnel or protected backup containing a usable ID'}
  if($tunnel -cnotmatch '^tunnel_[0-9a-f]{32}$'){throw 'INVALID_TUNNEL_ID'}
  $secure=Read-Host 'Runtime API key (only for one-time encrypted storage)' -AsSecureString
  $credentialObj=[pscredential]::new('runtime',$secure)
  $key=$credentialObj.GetNetworkCredential().Password
  if([string]::IsNullOrWhiteSpace($key)){throw 'EMPTY_RUNTIME_API_KEY'}
  $json=[pscustomobject]@{version=1;tunnel_id=$tunnel;runtime_key=$key}|ConvertTo-Json -Compress
  $bytes=[Text.Encoding]::UTF8.GetBytes($json)
  try{
   $protected=[Security.Cryptography.ProtectedData]::Protect($bytes,$null,[Security.Cryptography.DataProtectionScope]::LocalMachine)
   Protect-ConfigRoot
   $temp=Join-Path $secretRoot ('tunnel-credential.'+[guid]::NewGuid().ToString('N')+'.tmp')
   try{
    [IO.File]::WriteAllBytes($temp,$protected)
    Move-Item -LiteralPath $temp -Destination $credential -Force
   }finally{Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue}
  }finally{
   [Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes)
   $key=$null;$json=$null
  }
  Write-Host 'CREDENTIAL_CONFIGURED=PASS (encrypted locally; no further key prompts)'
  break
 }
 'start' {
  Assert-Administrator
  Assert-Integrity
  $running=@(Get-TunnelRuntime)
  if($running.Count -gt 0){
   if((Get-Health) -eq 200){Write-Host 'ALREADY_RUNNING=PASS (tunnel and MCP available)';break}
   throw 'TUNNEL_RUNNING_BUT_NOT_READY_CHECK_STATUS'
  }
  if(-not(Test-Path -LiteralPath $credential -PathType Leaf)){
   throw 'ONE_TIME_CONFIGURATION_REQUIRED: run WindowsAdminMCP.ps1 configure locally once'
  }
  if((Get-Item -LiteralPath $credential).Length -gt 65536){throw 'CONFIG_TOO_LARGE'}
  $encrypted=[IO.File]::ReadAllBytes($credential)
  $bytes=[Security.Cryptography.ProtectedData]::Unprotect($encrypted,$null,[Security.Cryptography.DataProtectionScope]::LocalMachine)
  try{$config=[Text.Encoding]::UTF8.GetString($bytes)|ConvertFrom-Json -ErrorAction Stop}
  finally{[Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes)}
  if($config.version -ne 1 -or [string]$config.tunnel_id -cnotmatch '^tunnel_[0-9a-f]{32}$' -or [string]::IsNullOrWhiteSpace($config.runtime_key)){
   throw 'BAD_TUNNEL_CREDENTIAL_CONFIGURATION'
  }
  $env:CONTROL_PLANE_API_KEY=[string]$config.runtime_key
  $env:WINDOWS_ADMIN_MCP_EXEC='1'
  $binding='command=pwsh -NoLogo -NoProfile -NonInteractive -File "' + $server.Replace('\','/') + '",channel=main'
  Write-Host 'Starting tunnel and WindowsAdminMCP (full administrator access).'
  try{
   & $exe run --control-plane.tunnel-id $config.tunnel_id --control-plane.api-key env:CONTROL_PLANE_API_KEY --mcp.command $binding --health.listen-addr 127.0.0.1:8080
  }finally{
   Remove-Item Env:CONTROL_PLANE_API_KEY -ErrorAction SilentlyContinue
   Remove-Item Env:WINDOWS_ADMIN_MCP_EXEC -ErrorAction SilentlyContinue
   $config=$null
  }
  break
 }
 'stop' {
  Assert-Administrator
  if([Console]::IsInputRedirected){throw 'LOCAL_INTERACTIVE_CONSOLE_REQUIRED'}
  $runtime=@(Get-TunnelRuntime)
  if(-not $runtime){Write-Host 'ALREADY_STOPPED';break}
  if((Read-Host 'Type STOP to disconnect ChatGPT MCP') -cne 'STOP'){throw 'STOP_NOT_CONFIRMED'}
  foreach($p in $runtime){Stop-Process -Id $p.ProcessId -Force}
  Write-Host 'STOP_REQUESTED'
 }
}
