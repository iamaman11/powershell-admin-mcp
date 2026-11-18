# Former v0.7 launcher. Forward only to the native operator, never launch old MCP.
$ErrorActionPreference='Stop'
& (Join-Path $env:ProgramFiles 'WindowsAdminMCP\native\WindowsAdminMCP.ps1') -Action start
