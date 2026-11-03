# Deploy WindowsAdminMCP v0.6.0 without embedding tunnel credentials.
# Run in an explicitly elevated PowerShell 7 shell on Windows x64.
# The OpenAI runtime is downloaded only from the official tagged release.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest

$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=[Security.Principal.WindowsPrincipal]::new($identity)
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'NOT_ELEVATED'}
if($PSVersionTable.PSVersion.Major -lt 7){throw 'POWERSHELL_7_REQUIRED'}
if([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64){throw 'WINDOWS_X64_REQUIRED'}

$installRoot=Join-Path $env:ProgramFiles 'WindowsAdminMCP'
$sourceRoot=$PSScriptRoot
$expectedServer='8A62AFA79D829D91D88C3F06D8F69004FEAE8C6286A6BE59F19EC7AF672AF0B9'
$expectedExe='CB3B2AB34A77096750561B8D46319AF282096A169A90B9C8E36567DD4FE06837'
$expectedZip='E8962891696BCCC05F27C47CF0F383FED127E955C1B0F4866471E3D0851F2FDD'
$runtimeUrl='https://github.com/openai/tunnel-client/releases/download/v0.0.19/tunnel-client-runtime-v0.0.19-windows-amd64.zip'
$srcServer=Join-Path $sourceRoot 'windows-admin-mcp.ps1'
$srcLauncher=Join-Path $sourceRoot 'start-windows-admin-mcp.ps1'
$srcTest=Join-Path $sourceRoot 'Test-WindowsAdminMCP.ps1'
$srcController=Join-Path $sourceRoot 'WindowsAdminMCP.ps1'
foreach($file in @($srcServer,$srcLauncher,$srcTest,$srcController)){
 if(-not(Test-Path -LiteralPath $file -PathType Leaf)){throw "REQUIRED_SOURCE_MISSING: $file"}
}
if((Get-FileHash -LiteralPath $srcServer -Algorithm SHA256).Hash -cne $expectedServer){throw 'SOURCE_MCP_INTEGRITY_FAILED'}

[void][IO.Directory]::CreateDirectory($installRoot)
# Harden the full tree before any executable or downloaded archive is installed.
$acl=Get-Acl -LiteralPath $installRoot
$acl.SetAccessRuleProtection($true,$false)
foreach($rule in @($acl.Access)){[void]$acl.RemoveAccessRuleSpecific($rule)}
$adminSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$acl.SetOwner($adminSid)
foreach($sidValue in @('S-1-5-18','S-1-5-32-544')){
 $sid=[Security.Principal.SecurityIdentifier]::new($sidValue)
 $allow=[Security.AccessControl.FileSystemAccessRule]::new($sid,
     [Security.AccessControl.FileSystemRights]::FullControl,
     [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
     [Security.AccessControl.PropagationFlags]::None,
     [Security.AccessControl.AccessControlType]::Allow)
 [void]$acl.AddAccessRule($allow)
}
Set-Acl -LiteralPath $installRoot -AclObject $acl
$clientDir=Join-Path $installRoot 'client'
[void][IO.Directory]::CreateDirectory($clientDir)
[void][IO.Directory]::CreateDirectory((Join-Path $installRoot 'logs'))
$exe=Join-Path $clientDir 'tunnel-client-runtime.exe'
if(Test-Path -LiteralPath $exe -PathType Leaf){
 if((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -cne $expectedExe){
     throw 'INSTALLED_EXE_INTEGRITY_FAILED_DO_NOT_OVERWRITE'
 }
 Write-Host 'OFFICIAL_RUNTIME_REUSED'
} else {
 $zipPath=Join-Path $installRoot 'runtime-download.tmp.zip'
 $stageExe=Join-Path $clientDir 'runtime-download.tmp.exe'
 try {
     Invoke-WebRequest -Uri $runtimeUrl -OutFile $zipPath -ErrorAction Stop
     if((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash -cne $expectedZip){throw 'OFFICIAL_ZIP_HASH_MISMATCH'}
     Add-Type -AssemblyName System.IO.Compression
     $zip=[IO.Compression.ZipFile]::OpenRead($zipPath)
     try {
         $matches=@($zip.Entries | Where-Object { $_.Name -ceq 'tunnel-client-runtime.exe' })
         if($matches.Count -ne 1){throw 'OFFICIAL_EXE_MISSING_OR_AMBIGUOUS'}
         $srcStream=$matches[0].Open()
         try {
             $dstStream=[IO.File]::Open($stageExe,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
             try {$srcStream.CopyTo($dstStream)}finally{$dstStream.Dispose()}
         } finally {$srcStream.Dispose()}
     } finally {$zip.Dispose()}
     if((Get-FileHash -LiteralPath $stageExe -Algorithm SHA256).Hash -cne $expectedExe){throw 'OFFICIAL_EXE_HASH_MISMATCH'}
     [IO.File]::Move($stageExe,$exe)
 } finally {
     Remove-Item -LiteralPath $zipPath,$stageExe -Force -ErrorAction SilentlyContinue
 }
}
# The single operator entrypoint pins official runtime and MCP server.
$control=[IO.File]::ReadAllText($srcController,[Text.Encoding]::UTF8)
if(-not $control.Contains($expectedExe) -or -not $control.Contains($expectedServer)){
 throw 'CONTROLLER_HASH_PIN_MISSING'
}
if($control -match 'tunnel_[a-f0-9]{32}' -or
   [IO.File]::ReadAllText($srcLauncher,[Text.Encoding]::UTF8) -match 'tunnel_[a-f0-9]{32}'){
 throw 'SOURCE_HAS_MACHINE_TUNNEL_ID'
}
foreach($pair in @(
  @($srcServer,(Join-Path $installRoot 'windows-admin-mcp.ps1')),
  @($srcLauncher,(Join-Path $installRoot 'start-windows-admin-mcp.ps1')),
  @($srcTest,(Join-Path $installRoot 'Test-WindowsAdminMCP.ps1')),
  @($srcController,(Join-Path $installRoot 'WindowsAdminMCP.ps1')))){
 Copy-Item -LiteralPath $pair[0] -Destination $pair[1] -Force
}
if((Get-FileHash (Join-Path $installRoot 'windows-admin-mcp.ps1') -Algorithm SHA256).Hash -cne $expectedServer){
 throw 'INSTALLED_SERVER_HASH_MISMATCH'
}
if((Get-FileHash $exe -Algorithm SHA256).Hash -cne $expectedExe){throw 'INSTALLED_RUNTIME_HASH_MISMATCH'}
Write-Host 'WINDOWS_ADMIN_MCP_INSTALLED=PASS'
Write-Host "INSTALL_ROOT=$installRoot"
Write-Host 'Execute Test-WindowsAdminMCP.ps1 in this directory before using the elevated launcher.'
