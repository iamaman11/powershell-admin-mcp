param([Parameter(Mandatory)][string]$Exe)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if(-not $env:GITHUB_ACTIONS -or $env:RUNNER_OS -ne 'Windows'){throw 'ISOLATED_WINDOWS_CI_ONLY'}
$Exe=[IO.Path]::GetFullPath($Exe)
$name='WindowsAdminMCP'
if(Get-Service $name -ErrorAction SilentlyContinue){throw 'SERVICE_ALREADY_EXISTS'}
$clients=[Collections.Generic.List[Diagnostics.Process]]::new()
$meta=@{'io.modelcontextprotocol/protocolVersion'='2026-07-28';'io.modelcontextprotocol/clientCapabilities'=@{};'io.modelcontextprotocol/clientInfo'=@{name='r1-fixture';version='1'}}
function Assert($ok,[string]$label){if(-not $ok){throw "FAIL $label"};Write-Host "PASS $label"}
function Connect {
    $si=[Diagnostics.ProcessStartInfo]::new($Exe)
    $si.ArgumentList.Add('stdio')
    $si.Environment['R0_SERVICE_PID']='1'
    $si.Environment['R0_PIPE']='not-trusted'
    $si.UseShellExecute=$false;$si.RedirectStandardInput=$true;$si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true
    $p=[Diagnostics.Process]::Start($si);$clients.Add($p)
    $null=$p.StandardError.ReadToEndAsync()
    return $p
}
function Send($p,$id,$method,$params){
    $m=@{jsonrpc='2.0';id=$id;method=$method;params=$params}
    $p.StandardInput.WriteLine(($m|ConvertTo-Json -Depth 12 -Compress));$p.StandardInput.Flush()
}
function Receive($p,$id){
    while($true){
        $line=$p.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
        if($null -eq $line){throw 'UNEXPECTED_EOF'}
        $m=$line|ConvertFrom-Json -AsHashtable
        if($m.Contains('id') -and $m.id -eq $id){
            if($m.Contains('error') -or ($m.result.Contains('isError') -and $m.result.isError)){throw "RPC_FAILURE $line"}
            return $m.result
        }
    }
}
function Status($p,$id){
    Send $p $id 'tools/call' @{name='powershell_status';arguments=@{};_meta=$meta}
    return (Receive $p $id).structuredContent
}
function Wait-Status($p,[string]$expected){
    $clock=[Diagnostics.Stopwatch]::StartNew();$id=1000
    do{
        $v=Status $p $id;$id++
        if($v.status -eq $expected){return $v}
        Start-Sleep -Milliseconds 100
    }while($clock.Elapsed.TotalSeconds -lt 10)
    throw "STATUS_TIMEOUT $expected $($v.status)"
}
try{
    New-Service -Name $name -BinaryPathName ('"'+$Exe+'" service') -StartupType Automatic | Out-Null
    Start-Service $name
    $a=Connect;$b=Connect;$c=Connect
    $svc=Get-CimInstance Win32_Service -Filter "Name='$name'"
    foreach($p in @($a,$b,$c)){
        Send $p 1 'tools/list' @{_meta=$meta}
        $list=Receive $p 1
        Assert (@($list.tools).Count -eq 1 -and $list.tools[0].name -eq 'powershell_status') 'r1.actual_tool_only'
        Assert ($list.tools[0].Contains('outputSchema')) 'r1.typed_status_schema'
        $v=Wait-Status $p 'OK'
        Assert ($v.service_pid -eq $svc.ProcessId -and $v.execution_sid -eq 'S-1-5-18' -and $v.elevated) 'r1.scm_pid_and_real_execution_identity'
    }
    foreach($p in @($a,$b,$c)){Send $p 2 'tools/call' @{name='powershell_status';arguments=@{};_meta=$meta}}
    $results=@(foreach($p in @($a,$b,$c)){(Receive $p 2).structuredContent})
    Assert (@($results|Where-Object {$_.status -eq 'OK' -and $_.service_pid -eq $svc.ProcessId}).Count -eq 3) 'r1.one_service_three_stdio_clients'
    Assert (@($results.correlation_id|Select-Object -Unique).Count -eq 3) 'r1.unique_correlations'
    Stop-Service $name
    $offline=Wait-Status $a 'SERVICE_UNAVAILABLE'
    Assert ($offline.native_error -eq 1062) 'r1.typed_service_offline'
    Start-Service $name
    $now=Wait-Status $a 'OK'
    $newSvc=Get-CimInstance Win32_Service -Filter "Name='$name'"
    Assert ($now.service_pid -eq $newSvc.ProcessId -and $now.started_utc -ne $results[0].started_utc) 'r1.same_frontend_reconnects_new_service'
    Write-Host 'R1_NATIVE_STATUS=PASS (one candidate binary; no execution or tunnel yet)'
}finally{
    foreach($p in $clients){
        if(-not $p.HasExited){$p.StandardInput.Close();if(-not $p.WaitForExit(3000)){$p.Kill($true);$p.WaitForExit()}}
        $p.Dispose()
    }
    if(Get-Service $name -ErrorAction SilentlyContinue){Stop-Service $name -ErrorAction SilentlyContinue;sc.exe delete $name|Out-Host}
}
