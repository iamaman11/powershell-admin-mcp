param([Parameter(Mandatory)][string]$Dll)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "FAIL $Name" }
    Write-Host "PASS $Name"
}
function Send($p, $message) {
    $p.StandardInput.WriteLine(($message | ConvertTo-Json -Depth 20 -Compress))
    $p.StandardInput.Flush()
}
function Receive($p, [int]$Id) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt 15) {
        $line = $p.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(15)).GetAwaiter().GetResult()
        if ($null -eq $line) { throw "EOF waiting for id=$Id" }
        $msg = $line | ConvertFrom-Json -AsHashtable
        if ($msg.Contains('id') -and $msg.id -eq $Id) { return $msg }
        # Notification or cancellation reply for an earlier in-flight request.
    }
    throw "TIMEOUT waiting for id=$Id"
}
function Call($p, [int]$Id, [string]$Method, $Params) {
    Send $p @{jsonrpc='2.0';id=$Id;method=$Method;params=$Params}
    $reply = Receive $p $Id
    if ($reply.Contains('error')) { throw "RPC_ERROR $Method $($reply.error | ConvertTo-Json -Compress)" }
    return $reply.result
}
function Tool($p, [int]$Id, [string]$Name, $Arguments, $Meta) {
    $params = @{name=$Name;arguments=$Arguments}
    if ($null -ne $Meta) { $params._meta=$Meta }
    return Call $p $Id 'tools/call' $params
}
function Probe([bool]$Modern) {
    $si = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $si.ArgumentList.Add([IO.Path]::GetFullPath($Dll))
    $si.UseShellExecute=$false
    $si.RedirectStandardInput=$true
    $si.RedirectStandardOutput=$true
    $si.RedirectStandardError=$true
    $si.StandardInputEncoding=[Text.UTF8Encoding]::new($false)
    $si.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
    $p = [Diagnostics.Process]::Start($si)
    $stderr=$p.StandardError.ReadToEndAsync()
    try {
        $meta=$null
        $listParams=@{}
        if ($Modern) {
            $d=Call $p 1 'server/discover' @{}
            Assert ('2026-07-28' -in $d.supportedVersions) 'modern.discovery'
            $meta=@{'io.modelcontextprotocol/protocolVersion'='2026-07-28';'io.modelcontextprotocol/clientCapabilities'=@{}}
            $listParams._meta=$meta
        } else {
            $i=Call $p 1 'initialize' @{protocolVersion='2025-06-18';capabilities=@{};clientInfo=@{name='raw-acceptance';version='1'}}
            Assert ($i.protocolVersion -eq '2025-06-18') 'legacy.negotiation'
            Send $p @{jsonrpc='2.0';method='notifications/initialized'}
        }
        $list=Call $p 2 'tools/list' $listParams
        Assert (@($list.tools).Count -eq 4) "tools.count modern=$Modern"
        $echoSchema=@($list.tools | Where-Object {$_.name -eq 'probe_echo'})[0]
        Assert ($echoSchema.inputSchema.properties.value.type -eq 'string') "generated.input.schema modern=$Modern"
        Assert ($echoSchema.Contains('outputSchema')) "generated.output.schema modern=$Modern"
        $value='Проверка Unicode 🚀'
        $echo=Tool $p 3 'probe_echo' @{value=$value} $meta
        Assert ($echo.structuredContent.value -ceq $value -and $echo.structuredContent.status -eq 'ok') "structured.unicode modern=$Modern"
        $bad= @{name='probe_echo';arguments=@{value=@{invalid=$true}}}
        if ($null -ne $meta) {$bad._meta=$meta}
        Send $p @{jsonrpc='2.0';id=4;method='tools/call';params=$bad}
        $invalid=Receive $p 4
        Assert ($invalid.Contains('error') -or ($invalid.Contains('result') -and $invalid.result.isError)) "negative.type modern=$Modern"
        $png=Tool $p 5 'probe_png' @{} $meta
        $image=@($png.content | Where-Object {$_.type -eq 'image'})[0]
        $decoded=[Convert]::FromBase64String($image.data)
        Assert ($image.mimeType -eq 'image/png' -and [Convert]::ToHexString($decoded[0..7]) -eq '89504E470D0A1A0A') "image.block modern=$Modern"
        if (-not $Modern) {
            Send $p @{jsonrpc='2.0';id=100;method='tools/call';params=@{name='probe_wait';arguments=@{}}}
            $started=$false
            for ($n=0;$n -lt 20;$n++) {
                $c=Tool $p (200+$n) 'probe_counters' @{} $null
                if ($c.structuredContent.started -eq 1) {$started=$true;break}
                Start-Sleep -Milliseconds 100
            }
            Assert $started 'cancellation.started'
            Send $p @{jsonrpc='2.0';method='notifications/cancelled';params=@{requestId=100;reason='acceptance'}}
            $cancelled=$false
            for ($n=0;$n -lt 20;$n++) {
                $c=Tool $p (300+$n) 'probe_counters' @{} $null
                if ($c.structuredContent.cancelled -eq 1) {$cancelled=$true;break}
                Start-Sleep -Milliseconds 100
            }
            Assert $cancelled 'cancellation.observed.by.handler'
        }
        $p.StandardInput.Close()
        Assert ($p.WaitForExit(10000) -and $p.ExitCode -eq 0) "clean.shutdown modern=$Modern"
    } finally {
        if (-not $p.HasExited) {$p.Kill($true); $p.WaitForExit()}
        if ($stderr.IsCompleted) { Write-Host ($stderr.GetAwaiter().GetResult()) }
        $p.Dispose()
    }
}
Probe $false
Probe $true
Write-Host 'R0_SDK_RAW_WIRE=PASS (fixed PNG, not desktop; no real ChatGPT/Codex)'
