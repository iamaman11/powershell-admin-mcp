# Compatibility alias. The only operator entrypoint is WindowsAdminMCP.ps1.
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'WindowsAdminMCP.ps1') -Action start
