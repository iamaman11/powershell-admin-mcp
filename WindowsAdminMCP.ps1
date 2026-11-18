# Thin native operator. The old PowerShell MCP server and launcher are retired.
[CmdletBinding()]
param(
 [ValidateSet('start','status','stop','doctor','test','install','configure')][string]$Action='status',
 [string]$PackagePath,
 [ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedSha256,
 [string]$EvidencePath,
 [ValidatePattern('^tunnel_[a-f0-9]{32}$')][string]$TunnelId
)
$ErrorActionPreference='Stop'
$root=Join-Path $env:ProgramFiles 'WindowsAdminMCP'
$native=Join-Path $root 'native\WindowsAdminMCP.exe'
$startup=Join-Path $root 'native\NativeStartup.ps1'
$exe=Join-Path $root 'client\tunnel-client-runtime.exe'
$secretRoot=Join-Path $env:ProgramData 'WindowsAdminMCP'
$credential=Join-Path $secretRoot 'tunnel-credential.dpapi'
function Assert-Administrator {
 $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
 if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'ADMINISTRATOR_REQUIRED'}
}
function Get-TunnelRuntime {
 return @(Get-CimInstance Win32_Process -Filter 'Name="tunnel-client-runtime.exe"'|Where-Object {$_.ExecutablePath -eq $exe})
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

Assert-Administrator
switch($Action){
 'status' { & $startup -Action Status;break }
 'start' {
  $expected=(Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw|ConvertFrom-Json).files.'native/WindowsAdminMCP.exe'
  & $startup -Action Start -ExpectedNativeSha256 $expected;break
 }
 'stop' {
  if([Console]::IsInputRedirected -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0){throw 'LOCAL_INTERACTIVE_CONSOLE_REQUIRED'}
  $expected=(Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw|ConvertFrom-Json).files.'native/WindowsAdminMCP.exe'
  & $startup -Action Stop -ExpectedNativeSha256 $expected;break
 }
 {$_ -in 'doctor','test'} {
  if($EvidencePath){& $native doctor --evidence $EvidencePath}else{& $native doctor}
  break
 }
 'install' {
  $script=Join-Path $PSScriptRoot 'Install-WindowsAdminMCP.ps1'
  if(-not(Test-Path -LiteralPath $script)){throw 'INSTALL_FROM_TRUSTED_CHECKOUT_WITH_CI_PACKAGE'}
  & $script -Action Install -PackagePath $PackagePath -ExpectedSha256 $ExpectedSha256;break
 }
 'configure' {
  Assert-Administrator
  if([Console]::IsInputRedirected){throw 'LOCAL_CONSOLE_REQUIRED_FOR_ONE_TIME_CONFIGURATION'}
  if([Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0){throw 'LOCAL_INTERACTIVE_CONSOLE_REQUIRED'}
  $tunnel=if($TunnelId){$TunnelId}else{Get-ExistingTunnelId}
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

}
