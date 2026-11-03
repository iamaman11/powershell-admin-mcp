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
$expectedServer = 'C2E730FD51526758D9659BD1E5F92051AB313304BDFB0ADA4D873D49E6FB82C7'
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
Write-Warning 'This will permit ChatGPT MCP to execute arbitrary administrator PowerShell commands on this computer.'
$consent = Read-Host 'Type ENABLE to authorize this session'
if ($consent -cne 'ENABLE') { throw 'ADMIN_EXEC_NOT_APPROVED' }
$env:WINDOWS_ADMIN_MCP_EXEC = '1'
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
}
