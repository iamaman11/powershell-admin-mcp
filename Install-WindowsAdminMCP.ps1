# Canonical offline package installer. Legacy v0.7 PowerShell installation retired.
[CmdletBinding()]
param([ValidateSet('Verify','Install','Rollback','Prepare','Accept','Maintenance')][string]$Action='Verify',
      [string]$PackagePath,
      [ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedSha256,
      [ValidatePattern('^[a-f0-9]{40}$')][string]$ExpectedCommit,
      [switch]$RemoteAcceptanceConfirmed,
      [switch]$ReplaceUnaccepted)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'installer\Install-Native.ps1') @PSBoundParameters
