# Local approval for one exact remote PowerShell command. Do not call through MCP.
[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$CommandFile,
 [ValidateRange(1,10)][int]$Minutes=3
)
$ErrorActionPreference='Stop'
if([Console]::IsInputRedirected){throw 'LOCAL_INTERACTIVE_CONSOLE_REQUIRED'}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(-not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'NOT_ELEVATED'}
$command=[IO.File]::ReadAllText((Resolve-Path -LiteralPath $CommandFile),[Text.Encoding]::UTF8)
if([string]::IsNullOrWhiteSpace($command) -or $command.Length -gt 12000){throw 'INVALID_COMMAND'}
$sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($command))).ToLowerInvariant()
Write-Host ('Exact UTF-8 SHA256: '+$sha)
Write-Host 'REVIEW EXACT COMMAND BEFORE APPROVING:'
Write-Host $command
Write-Warning 'This approval permits ONE execution with full administrator rights. Existing effects cannot be rolled back automatically.'
$expected='APPROVE '+$sha.Substring(0,12)
if((Read-Host ('Type '+$expected)) -cne $expected){throw 'APPROVAL_DENIED'}
$dir=Join-Path $PSScriptRoot 'approvals'
[void][IO.Directory]::CreateDirectory($dir)
$p=Join-Path $dir ($sha+'.permit')
if(Test-Path -LiteralPath $p){throw 'APPROVAL_ALREADY_EXISTS'}
$expiry=[DateTime]::UtcNow.AddMinutes($Minutes).Ticks
$payload=$sha+[Environment]::NewLine+$expiry+[Environment]::NewLine
[IO.File]::WriteAllText($p,$payload,[Text.UTF8Encoding]::new($false))
Write-Host ('APPROVED_ONCE_SHA='+$sha)
Write-Host ('EXPIRES_UTC='+([DateTime]::new($expiry,[DateTimeKind]::Utc)).ToString('O'))
