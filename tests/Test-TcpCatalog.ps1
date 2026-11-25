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
    $tcpOutput=$tcp[0].outputSchema.properties
    $fields=@('status','kind','observed_utc','tcp_connections','next_offset','native_error','elapsed_ms')
    Assert ($tcpOutput.Count -eq $fields.Count -and
        @($tcpOutput.Keys|Where-Object {$_ -notin $fields}).Count -eq 0 -and
        @($fields|Where-Object {-not $tcpOutput.Contains($_)}).Count -eq 0) 'tcp_schema.output_has_exact_tcp_fields'
    $kindSchema=$tcp[0].outputSchema|ConvertTo-Json -Depth 30 -Compress
    Assert ($kindSchema.Contains('TCP_CONNECTIONS',[StringComparison]::Ordinal)) 'tcp_schema.output_discriminator_is_tcp_only'
    $chrome=@($list.tools|Where-Object {$_.name -eq 'windows.chrome_tab'})
    $invoke=@($list.tools|Where-Object {$_.name -eq 'windows.invoke_element'})
    Assert ($chrome.Count -eq 1 -and $invoke.Count -eq 1 -and
        $chrome[0].inputSchema.properties.Contains('scope') -and
        $chrome[0].inputSchema.properties.Contains('browser_window_id') -and
        $chrome[0].inputSchema.properties.Contains('page_size') -and
        $chrome[0].inputSchema.properties.Contains('offset') -and
        ($chrome[0].inputSchema.properties.scope|ConvertTo-Json -Compress).Contains('BROWSER')) `
        'browser_schema.publishes_exact_browser_scope'
    $invokeProps=$invoke[0].inputSchema.properties
    $invokeAction=$invokeProps.action|ConvertTo-Json -Depth 20 -Compress
    Assert ($invokeProps.Contains('target_element_id') -and
        $invokeProps.Contains('file_paths') -and
        $invokeAction.Contains('DRAG',[StringComparison]::Ordinal) -and
        $invokeAction.Contains('SET_FILES',[StringComparison]::Ordinal) -and
        $invoke[0].outputSchema.properties.Contains('selected_file_count')) `
        'browser_schema.publishes_background_drag_and_file_selection'
    $bad=Rpc 3 'tools/call' @{name='windows.tcp_connections';arguments=@{page_size=0}}
    Assert ($bad.structuredContent.status -eq 'INVALID_ARGUMENT' -and
        $bad.structuredContent.kind -eq 'TCP_CONNECTIONS' -and
        -not $bad.structuredContent.Contains('services') -and
        -not $bad.structuredContent.Contains('processes')) 'tcp_schema.invalid_early_reject_typed_result'
} finally {
    try {$process.StandardInput.Close()} catch {}
    if(-not $process.HasExited){$process.Kill($true)}
    $process.WaitForExit(10000)|Out-Null
    $process.Dispose()
}
