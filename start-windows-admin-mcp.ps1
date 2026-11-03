# Run from an intentionally elevated PowerShell 7 window.
# A full Windows administrator token is required for powershell_exec.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$exe = Join-Path $root 'client\tunnel-client-runtime.exe'
$server = Join-Path $root 'windows-admin-mcp.ps1'
foreach ($f in @($exe, $server)) {
    if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { throw "FILE_NOT_FOUND: $f" }
}
$expectedExe = 'CB3B2AB34A77096750561B8D46319AF282096A169A90B9C8E36567DD4FE06837'
$expectedServer = 'CB0907D77A598DDD703C0395A7A4EFA3C43EA4936A4A2B5360656472163873B8'
if ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -cne $expectedExe) {
    throw 'TUNNEL_BINARY_INTEGRITY_FAILED'
}
if ((Get-FileHash -LiteralPath $server -Algorithm SHA256).Hash -cne $expectedServer) {
    throw 'MCP_SERVER_INTEGRITY_FAILED'
}
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'NOT_ELEVATED: Launch PowerShell 7 with Run as administrator.'
}
Write-Warning 'SAFE disables remote PowerShell. APPROVAL requires local one-use approval. SUPERVISED is unrestricted.'
$consent = Read-Host 'Type ENABLE to authorize this session'
if ($consent -cne 'ENABLE') { throw 'ADMIN_EXEC_NOT_APPROVED' }
$mode=(Read-Host 'Execution mode SAFE / APPROVAL / SUPERVISED (default SAFE)').Trim().ToUpperInvariant()
if(-not $mode){$mode='SAFE'}
if($mode -cnotin @('SAFE','APPROVAL','SUPERVISED')){throw 'INVALID_EXEC_MODE'}
if($mode -ceq 'SUPERVISED'){
 Write-Warning 'SUPERVISED allows unrestricted administrator PowerShell over the active tunnel.'
 if((Read-Host 'Type UNSAFE to enable unrestricted remote commands') -cne 'UNSAFE'){throw 'UNRESTRICTED_MODE_NOT_APPROVED'}
}
$env:WINDOWS_ADMIN_MCP_EXEC_MODE=$mode.ToLowerInvariant()
$env:WINDOWS_ADMIN_MCP_EXEC=if($mode -eq 'SAFE'){'0'}else{'1'}
Write-Host "EXECUTION_MODE=$mode"
$sec = Read-Host 'Enter OpenAI tunnel runtime API key (not an admin API key)' -AsSecureString
$cred = [pscredential]::new('runtime', $sec)
$env:CONTROL_PLANE_API_KEY = $cred.GetNetworkCredential().Password
Remove-Variable sec, cred
if ([string]::IsNullOrWhiteSpace($env:CONTROL_PLANE_TUNNEL_ID)) {
    $env:CONTROL_PLANE_TUNNEL_ID = Read-Host 'Enter OpenAI tunnel ID (not a secret API key)'
}
if ([string]::IsNullOrWhiteSpace($env:CONTROL_PLANE_TUNNEL_ID)) { throw 'TUNNEL_ID_REQUIRED' }
$serverArg = '"' + $server.Replace('\','/') + '"'
$binding = 'command=pwsh -NoLogo -NoProfile -NonInteractive -File ' + $serverArg + ',channel=main'
Write-Host 'Starting Windows Admin MCP tunnel. Keep this window open. Health: http://127.0.0.1:8080/readyz'
try {
    & $exe run --control-plane.tunnel-id $env:CONTROL_PLANE_TUNNEL_ID --control-plane.api-key env:CONTROL_PLANE_API_KEY --mcp.command $binding --health.listen-addr 127.0.0.1:8080
} finally {
    Remove-Item Env:CONTROL_PLANE_API_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:WINDOWS_ADMIN_MCP_EXEC -ErrorAction SilentlyContinue
    Remove-Item Env:WINDOWS_ADMIN_MCP_EXEC_MODE -ErrorAction SilentlyContinue
}
