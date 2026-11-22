# Thin native operator. The old PowerShell MCP server and launcher are retired.
[CmdletBinding()]
param(
 [ValidateSet('start','status','stop','doctor','test','install','configure','add-tunnel','configure-tunnel','list-tunnels','disable-tunnel','enable-tunnel','remove-tunnel','update','rollback','accept')][string]$Action='status',
 [string]$PackagePath,
 [ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedSha256,
 [string]$EvidencePath,
 [switch]$RemoteAcceptanceConfirmed,
 [ValidatePattern('^tunnel_[a-f0-9]{32}$')][string]$TunnelId,
 [ValidatePattern('^[a-z0-9][a-z0-9-]{0,31}$')][string]$ProfileId
)
$ErrorActionPreference='Stop'
$root=Join-Path $env:ProgramFiles 'WindowsAdminMCP'
$native=Join-Path $root 'native\WindowsAdminMCP.exe'
$startup=Join-Path $root 'native\NativeStartup.ps1'
$exe=Join-Path $root 'client\tunnel-client-runtime.exe'
$secretRoot=Join-Path $env:ProgramData 'WindowsAdminMCP'
$credential=Join-Path $secretRoot 'tunnel-credential.dpapi'
$profileRoot=Join-Path $secretRoot 'tunnel-profiles'
function Assert-Administrator {
 $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
 if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'ADMINISTRATOR_REQUIRED'}
}
function Get-TunnelRuntime {
 return @(Get-CimInstance Win32_Process -Filter 'Name="tunnel-client-runtime.exe"'|Where-Object {$_.ExecutablePath -eq $exe})
}
function Protect-ConfigRoot {
 if(Test-Path -LiteralPath $secretRoot){
  if((Get-Item -LiteralPath $secretRoot).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'CONFIG_ROOT_REPARSE_POINT'}
 }
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
function Assert-LocalConsole {
 if([Console]::IsInputRedirected -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0){throw 'LOCAL_INTERACTIVE_CONSOLE_REQUIRED'}
}
function Assert-ConfigPath([string]$path){
 $item=Get-Item -LiteralPath $path -ErrorAction Stop
 if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'CONFIG_REPARSE_POINT'}
 $acl=Get-Acl -LiteralPath $path
 foreach($rule in @($acl.Access)){
  if($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544')){throw 'CONFIG_ACL_INVALID'}
 }
}
function Read-TunnelProfile([string]$path,[string]$id){
 Assert-ConfigPath $path
 $item=Get-Item -LiteralPath $path -ErrorAction Stop
 if($item.Attributes -band [IO.FileAttributes]::ReparsePoint -or $item.Length -lt 1 -or $item.Length -gt 65536){throw 'INVALID_TUNNEL_PROFILE_FILE'}
 $bytes=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($path),$null,[Security.Cryptography.DataProtectionScope]::LocalMachine)
 try{
  $p=[Text.Encoding]::UTF8.GetString($bytes)|ConvertFrom-Json
  $profile=if($p.profile_id){[string]$p.profile_id}else{'primary'}
  $port=if($p.health_port){[int]$p.health_port}else{8080}
  if($p.version -ne 1 -or $p.tunnel_id -cnotmatch '^tunnel_[a-f0-9]{32}$' -or
      $profile -cne $id -or $port -lt 1024 -or $port -gt 65535){throw 'INVALID_TUNNEL_PROFILE'}
  return [pscustomobject]@{profile_id=$id;tunnel_id=[string]$p.tunnel_id;health_port=$port}
 }finally{[Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes)}
}
function Get-ConfiguredProfiles([switch]$IncludeDisabled){
 $items=@()
 if(Test-Path -LiteralPath $secretRoot -PathType Container){Assert-ConfigPath $secretRoot}
 if(Test-Path -LiteralPath $credential -PathType Leaf){$items+=Read-TunnelProfile $credential 'primary'}
 if(Test-Path -LiteralPath $profileRoot -PathType Container){
  Assert-ConfigPath $profileRoot
  foreach($file in @(Get-ChildItem -LiteralPath $profileRoot -File)){
   if($file.Name -match '^([a-z0-9][a-z0-9-]{0,31})\.dpapi$'){$items+=Read-TunnelProfile $file.FullName $Matches[1]}
   elseif($IncludeDisabled -and $file.Name -match '^([a-z0-9][a-z0-9-]{0,31})\.disabled$'){
    $item=Read-TunnelProfile $file.FullName $Matches[1]
    Add-Member -InputObject $item -NotePropertyName disabled -NotePropertyValue $true
    $items+=$item
   }
  }
 }
 return @($items)
}
function Save-TunnelProfile([string]$id,[string]$target,[string]$tunnel,[int]$port,[bool]$replace){
 Assert-LocalConsole
 $secure=Read-Host "Runtime API key for $id (encrypted locally)" -AsSecureString
 $cred=[pscredential]::new('runtime',$secure)
 $key=$cred.GetNetworkCredential().Password
 if([string]::IsNullOrWhiteSpace($key)){throw 'EMPTY_RUNTIME_API_KEY'}
 $json=[pscustomobject]@{version=1;tunnel_id=$tunnel;runtime_key=$key;profile_id=$id;health_port=$port}|ConvertTo-Json -Compress
 $bytes=[Text.Encoding]::UTF8.GetBytes($json)
 try{
  $protected=[Security.Cryptography.ProtectedData]::Protect($bytes,$null,[Security.Cryptography.DataProtectionScope]::LocalMachine)
  Protect-ConfigRoot
  if($id -ne 'primary') {
   if(Test-Path -LiteralPath $profileRoot){Assert-ConfigPath $profileRoot}
   [void][IO.Directory]::CreateDirectory($profileRoot)
   Assert-ConfigPath $profileRoot
  }
  $parent=Split-Path $target -Parent
  $temp=Join-Path $parent ('.tunnel-'+[guid]::NewGuid().ToString('N')+'.tmp')
  try{
   [IO.File]::WriteAllBytes($temp,$protected)
   [IO.File]::Move($temp,$target,$replace)
  }finally{Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue}
 }finally{
  [Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes)
  $key=$null;$json=$null
 }
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
 if(Test-Path -LiteralPath $credential -PathType Leaf){return (Read-TunnelProfile $credential 'primary').tunnel_id}
 if($env:CONTROL_PLANE_TUNNEL_ID -match '^tunnel_[a-f0-9]{32}$'){return $env:CONTROL_PLANE_TUNNEL_ID.ToLowerInvariant()}
 $running=@(Get-TunnelRuntime)
 if($running.Count -gt 1){throw 'MULTIPLE_TUNNELS_EXPLICIT_PRIMARY_ID_REQUIRED'}
 foreach($proc in $running){
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
 {$_ -in 'update','rollback','accept'} {
  $update=Join-Path $PSScriptRoot 'installer\Update-Native.ps1'
  if(-not(Test-Path -LiteralPath $update -PathType Leaf)){throw 'UPDATE_FROM_TRUSTED_MAIN_CHECKOUT'}
  & $update -Action $Action -RemoteAcceptanceConfirmed:$RemoteAcceptanceConfirmed
  break
 }
 'install' {
  $script=Join-Path $PSScriptRoot 'Install-WindowsAdminMCP.ps1'
  if(-not(Test-Path -LiteralPath $script)){throw 'INSTALL_FROM_TRUSTED_CHECKOUT_WITH_CI_PACKAGE'}
  & $script -Action Install -PackagePath $PackagePath -ExpectedSha256 $ExpectedSha256;break
 }
 'configure' {
  Assert-Administrator
  Assert-LocalConsole
  $tunnel=if($TunnelId){$TunnelId}else{Get-ExistingTunnelId}
  if(-not $tunnel){throw 'TUNNEL_ID_AUTODISCOVERY_FAILED: no running tunnel or protected backup containing a usable ID'}
  if($tunnel -cnotmatch '^tunnel_[0-9a-f]{32}$'){throw 'INVALID_TUNNEL_ID'}
  if(@(Get-ConfiguredProfiles -IncludeDisabled|Where-Object {$_.profile_id -ne 'primary' -and $_.tunnel_id -ceq $tunnel}).Count){throw 'DUPLICATE_TUNNEL_ID'}
  Save-TunnelProfile 'primary' $credential $tunnel 8080 $true
  Write-Host 'CREDENTIAL_CONFIGURED=PASS (encrypted locally; no further key prompts)'
  break
 }
 'add-tunnel' {
  Assert-LocalConsole
  if(-not $ProfileId -or $ProfileId -eq 'primary' -or -not $TunnelId){throw 'PROFILE_ID_AND_DISTINCT_TUNNEL_ID_REQUIRED'}
  if(-not(Test-Path -LiteralPath $credential -PathType Leaf)){throw 'CONFIGURE_PRIMARY_FIRST'}
  $existing=@(Get-ConfiguredProfiles -IncludeDisabled)
  if(@($existing|Where-Object {$_.profile_id -ceq $ProfileId -or $_.tunnel_id -ceq $TunnelId}).Count){throw 'DUPLICATE_TUNNEL_PROFILE_OR_ID'}
  $ports=@($existing|ForEach-Object {$_.health_port})
  $port=8081
  while($port -le 65535 -and ($ports -contains $port -or @(Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $port -State Listen -ErrorAction SilentlyContinue).Count)){ $port++ }
  if($port -gt 65535){throw 'NO_HEALTH_PORT_AVAILABLE'}
  $path=Join-Path $profileRoot ($ProfileId+'.dpapi')
  Save-TunnelProfile $ProfileId $path $TunnelId $port $false
  Write-Host "TUNNEL_ADDED profile=$ProfileId health_port=$port (supervisor detects change automatically)"
  break
 }
 'configure-tunnel' {
  Assert-LocalConsole
  if(-not $ProfileId -or $ProfileId -eq 'primary'){throw 'SECONDARY_PROFILE_ID_REQUIRED'}
  $path=Join-Path $profileRoot ($ProfileId+'.dpapi')
  if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw 'ENABLED_TUNNEL_PROFILE_NOT_FOUND'}
  $old=Read-TunnelProfile $path $ProfileId
  $tunnel=if($TunnelId){$TunnelId}else{$old.tunnel_id}
  if(@(Get-ConfiguredProfiles -IncludeDisabled|Where-Object {$_.profile_id -cne $ProfileId -and $_.tunnel_id -ceq $tunnel}).Count){throw 'DUPLICATE_TUNNEL_ID'}
  Save-TunnelProfile $ProfileId $path $tunnel $old.health_port $true
  Write-Host "TUNNEL_CREDENTIAL_ROTATED profile=$ProfileId (peer tunnels unchanged)"
  break
 }
 'list-tunnels' {
  @(Get-ConfiguredProfiles -IncludeDisabled|ForEach-Object {
   [pscustomobject]@{profile_id=$_.profile_id;health_port=$_.health_port;enabled=(-not $_.disabled)}
  })|ConvertTo-Json -Compress
  break
 }
 {$_ -in 'disable-tunnel','enable-tunnel','remove-tunnel'} {
  Assert-LocalConsole
  if(-not $ProfileId -or $ProfileId -eq 'primary'){throw 'SECONDARY_PROFILE_ID_REQUIRED'}
  $enabled=Join-Path $profileRoot ($ProfileId+'.dpapi')
  $disabled=Join-Path $profileRoot ($ProfileId+'.disabled')
  $from=if($Action -eq 'enable-tunnel'){$disabled}else{$enabled}
  $to=if($Action -eq 'disable-tunnel'){$disabled}else{$enabled}
  if(-not(Test-Path -LiteralPath $from -PathType Leaf)){throw 'TUNNEL_PROFILE_NOT_FOUND'}
  $current=Read-TunnelProfile $from $ProfileId
  if($Action -eq 'enable-tunnel' -and
      @(Get-ConfiguredProfiles|Where-Object {$_.profile_id -cne $ProfileId -and
          ($_.tunnel_id -ceq $current.tunnel_id -or $_.health_port -eq $current.health_port)}).Count){throw 'TUNNEL_PROFILE_CONFLICT'}
  if($Action -eq 'remove-tunnel'){
   $confirmation=Read-Host "Type $ProfileId to PERMANENTLY delete its encrypted credential"
   if($confirmation -cne $ProfileId){throw 'TUNNEL_REMOVAL_NOT_CONFIRMED'}
   Remove-Item -LiteralPath $from -Force
   Write-Host "TUNNEL_REMOVED profile=$ProfileId"
  }else{
   if(Test-Path -LiteralPath $to){throw 'TUNNEL_PROFILE_TARGET_EXISTS'}
   Move-Item -LiteralPath $from -Destination $to
   Write-Host "TUNNEL_$($Action.ToUpperInvariant()) profile=$ProfileId"
  }
  break
 }

}
