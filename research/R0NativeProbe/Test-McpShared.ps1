param([Parameter(Mandatory)][string]$Dll,[Parameter(Mandatory)][string]$Pipe)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$meta=@{'io.modelcontextprotocol/protocolVersion'='2026-07-28';'io.modelcontextprotocol/clientCapabilities'=@{};'io.modelcontextprotocol/clientInfo'=@{name='r0-shared-fixture';version='1'}}
$clients=[Collections.Generic.List[Diagnostics.Process]]::new()
function Assert($ok,[string]$name){if(-not $ok){throw "FAIL $name"};Write-Host "PASS $name"}
function Connect {
    $si=[Diagnostics.ProcessStartInfo]::new('dotnet')
    $si.ArgumentList.Add([IO.Path]::GetFullPath($Dll))
    $si.Environment['R0_PIPE']=$Pipe
    $si.UseShellExecute=$false;$si.RedirectStandardInput=$true;$si.RedirectStandardOutput=$true
    $si.RedirectStandardError=$true
    $p=[Diagnostics.Process]::Start($si);$clients.Add($p)
    $null=$p.StandardError.ReadToEndAsync()
    return $p
}
function Send($p,$id,$action,$key,$delay=0){
    $m=@{jsonrpc='2.0';id=$id;method='tools/call';params=@{name='probe_job';arguments=@{action=$action;key=$key;delayMs=$delay};_meta=$meta}}
    $p.StandardInput.WriteLine(($m|ConvertTo-Json -Depth 12 -Compress));$p.StandardInput.Flush()
}
function Receive($p,$id){
    while($true){
        $line=$p.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
        if($null -eq $line){throw "EOF $id"}
        $m=$line|ConvertFrom-Json -AsHashtable
        if($m.Contains('id') -and $m.id -eq $id){
            if($m.Contains('error') -or ($m.result.Contains('isError') -and $m.result.isError)){throw "RPC_FAILURE $line"}
            return $m.result.structuredContent
        }
    }
}
function Call($p,$id,$action,$key,$delay=0){Send $p $id $action $key $delay;Receive $p $id}
function Wait-JobState($p,$key,$state){
    $clock=[Diagnostics.Stopwatch]::StartNew();$id=100
    do{
        $v=Call $p $id 'status' $key;$id++
        if($v.state -eq $state){return $v}
        Start-Sleep -Milliseconds 50
    }while($clock.Elapsed.TotalSeconds -lt 12)
    throw "TIMEOUT $key $state"
}
try{
    $a=Connect;$b=Connect;$c=Connect
    foreach($p in @($a,$b,$c)){$v=Call $p 1 'status' 'mcp-not-present';Assert ($v.status -eq 'NOT_FOUND') 'mcp.independent_frontend'}
    $first=Call $a 2 'submit' 'mcp-first' 5000
    $running=Wait-JobState $b 'mcp-first' 'RUNNING'
    Assert ($first.id -eq $running.id) 'mcp.shared_job_identity'
    $clock=[Diagnostics.Stopwatch]::StartNew()
    Send $b 3 'submit' 'mcp-first' 5000
    Send $c 3 'submit' 'mcp-next' 20
    $repeat=Receive $b 3;$next=Receive $c 3
    Assert ($repeat.id -eq $first.id -and $next.state -eq 'QUEUED') 'mcp.concurrent_duplicate_and_queue'
    Assert ($clock.ElapsedMilliseconds -lt 2000) "mcp.control_responsive_ms=$($clock.ElapsedMilliseconds)"
    $cancelled=Call $b 4 'submit' 'mcp-cancel' 20
    $cancel=Call $c 4 'cancel' 'mcp-cancel'
    Assert ($cancel.id -eq $cancelled.id -and $cancel.state -eq 'CANCELLED') 'mcp.queued_cancel_from_other_client'
    $a.Kill($true);$a.WaitForExit()
    $done=Wait-JobState $b 'mcp-first' 'COMPLETED'
    Assert ($done.id -eq $first.id -and $done.result -eq 'fixture:mcp-first') 'mcp.disconnect_does_not_cancel'
    $done=Wait-JobState $c 'mcp-next' 'COMPLETED'
    Assert ($done.id -eq $next.id -and $done.result -eq 'fixture:mcp-next') 'mcp.next_job_and_distinct_result'
    $d=Connect;$done=Call $d 5 'status' 'mcp-first'
    Assert ($done.id -eq $first.id -and $done.state -eq 'COMPLETED') 'mcp.reconnect_result'
    Write-Host 'R0_MCP_SHARED=PASS (three independent SDK stdio frontends, synthetic jobs; no live tunnel)'
}finally{
    foreach($p in $clients){
        if(-not $p.HasExited){$p.StandardInput.Close();if(-not $p.WaitForExit(3000)){$p.Kill($true);$p.WaitForExit()}}
        $p.Dispose()
    }
}
