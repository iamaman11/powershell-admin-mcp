# Read-only MCP contract regression. No service installation or network access.
# Verifies the actual generated tools/list schema, not merely C# enum reflection.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Exe)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$Exe=(Resolve-Path -LiteralPath $Exe).Path
function Assert([bool]$Ok,[string]$Label) {
    if(-not $Ok){throw "FAIL $Label"}
    Write-Host "PASS $Label"
}
$start=[Diagnostics.ProcessStartInfo]::new($Exe)
$start.ArgumentList.Add('stdio')
$start.UseShellExecute=$false
$start.RedirectStandardInput=$true
$start.RedirectStandardOutput=$true
$start.RedirectStandardError=$true
$start.StandardInputEncoding=[Text.UTF8Encoding]::new($false)
$start.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
$process=[Diagnostics.Process]::Start($start)
$stderr=$process.StandardError.ReadToEndAsync()
function Rpc([int]$Id,[string]$Method,$Params) {
    $process.StandardInput.WriteLine((@{jsonrpc='2.0';id=$Id;method=$Method;params=$Params}|ConvertTo-Json -Depth 20 -Compress))
    $process.StandardInput.Flush()
    $reply=$null
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while($clock.Elapsed.TotalSeconds -lt 15){
        $line=$process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(15)).GetAwaiter().GetResult()
        if($null -eq $line){throw "RPC_EOF $Method"}
        $candidate=$line|ConvertFrom-Json -AsHashtable
        if($candidate.Contains('id') -and $candidate.id -eq $Id){$reply=$candidate;break}
    }
    if($null -eq $reply){throw "RPC_TIMEOUT $Method"}
    if($reply.Contains('error')){throw "RPC_ERROR $Method $($reply.error.code)"}
    return $reply.result
}
try {
    $initial=Rpc 1 'initialize' @{protocolVersion='2025-06-18';capabilities=@{};clientInfo=@{name='tcp-schema-acceptance';version='1'}}
    Assert ($initial.protocolVersion -eq '2025-06-18') 'tcp_schema.mcp_legacy_initialized'
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $process.StandardInput.Flush()
    $list=Rpc 2 'tools/list' @{}
    $system=@($list.tools|Where-Object {$_.name -eq 'windows.system_query'})
    $tcp=@($list.tools|Where-Object {$_.name -eq 'windows.tcp_connections'})
    Assert ($system.Count -eq 1 -and $tcp.Count -eq 1) 'tcp_schema.both_legacy_and_dedicated_entrypoints'
    $systemKind=$system[0].inputSchema.properties.kind|ConvertTo-Json -Depth 20 -Compress
    Assert ($systemKind.Contains('TCP_CONNECTIONS',[StringComparison]::Ordinal)) 'tcp_schema.discriminator_advertises_tcp_connections'
    $tcpProperties=$tcp[0].inputSchema.properties
    Assert ($tcpProperties.Contains('pid') -and $tcpProperties.Contains('page_size') -and
        $tcpProperties.Contains('offset') -and -not $tcpProperties.Contains('kind') -and
        $tcp[0].Contains('outputSchema')) 'tcp_schema.dedicated_typed_contract_without_enum_requirement'
    $bad=Rpc 3 'tools/call' @{name='windows.tcp_connections';arguments=@{page_size=0}}
    Assert ($bad.structuredContent.status -eq 'INVALID_ARGUMENT' -and
        $bad.structuredContent.kind -eq 'TCP_CONNECTIONS') 'tcp_schema.invalid_early_reject_typed_result'
} finally {
    try {$process.StandardInput.Close()} catch {}
    if(-not $process.HasExited){$process.Kill($true)}
    $process.WaitForExit(10000)|Out-Null
    $process.Dispose()
}
