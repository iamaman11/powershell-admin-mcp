param([Parameter(Mandatory)][string]$Exe)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if(-not $env:GITHUB_ACTIONS -or $env:RUNNER_OS -ne 'Windows'){throw 'ISOLATED_WINDOWS_CI_ONLY'}
$Exe=[IO.Path]::GetFullPath($Exe)
$name='WindowsAdminMCP'
$data=Join-Path $env:ProgramData 'WindowsAdminMCP.Data'
if(Test-Path -LiteralPath $data){throw 'STATE_ALREADY_EXISTS'}
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
    & sc.exe failure $name reset= 0 actions= restart/1000 | Out-Host
    if($LASTEXITCODE -ne 0){throw 'SCM_FAILURE_POLICY_FAILED'}
    Start-Service $name
    $a=Connect;$b=Connect;$c=Connect
    $svc=Get-CimInstance Win32_Service -Filter "Name='$name'"
    foreach($p in @($a,$b,$c)){
        Send $p 1 'tools/list' @{_meta=$meta}
        $list=Receive $p 1
        Assert (@($list.tools).Count -eq 3 -and @($list.tools.name|Where-Object {$_ -in @('powershell_status','powershell_exec','powershell_job')}).Count -eq 3) 'r3.three_implemented_tools'
        Assert (@($list.tools|Where-Object {$_.name -eq 'powershell_status' -and $_.Contains('outputSchema')}).Count -eq 1) 'r1.typed_status_schema'
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
    function ToolCall($p,$id,$tool,$arguments){
        Send $p $id 'tools/call' @{name=$tool;arguments=$arguments;_meta=$meta}
        return (Receive $p $id).structuredContent
    }
    function Job($p,$id,$arguments){return (ToolCall $p $id 'powershell_job' $arguments)}
    function Wait-JobState($p,$jobId,$state){
        $clock=[Diagnostics.Stopwatch]::StartNew();$id=2000
        do{
            $v=Job $p $id @{action='logs';job_id=$jobId};$id++
            if($v.job.state -eq $state){return $v.job}
            Start-Sleep -Milliseconds 100
        }while($clock.Elapsed.TotalSeconds -lt 15)
        throw "JOB_STATE_TIMEOUT $state $($v.job.state)"
    }
    $x=ToolCall $a 10 'powershell_exec' @{command="Write-Output 'real-exec'; [Security.Principal.WindowsIdentity]::GetCurrent().User.Value";timeout_seconds=10;operation_key='short'}
    Assert ($x.status -eq 'COMPLETED' -and $x.stdout.Contains('real-exec') -and $x.stdout.Contains('S-1-5-18') -and $x.audit_persisted) 'r3.actual_exec_service_context'
    $prefix="Write-Output 'Проверка 🚀'"+[char]10+'#'
    $long=$prefix+('x'*(11982-$prefix.Length))
    $x=ToolCall $b 11 'powershell_exec' @{command=$long;timeout_seconds=10;operation_key='long'}
    Assert ($x.status -eq 'COMPLETED' -and $x.stdout.Trim() -ceq 'Проверка 🚀') 'r3.long_unicode_through_mcp'
    $first=Job $a 12 @{action='submit';operation_key='shared';command="Start-Sleep -Seconds 5; Write-Output 'first'";timeout_seconds=20}
    Assert ($first.status -eq 'OK' -and $first.job.state -eq 'QUEUED') 'r3.durable_admission'
    $null=Wait-JobState $b $first.job.job_id 'RUNNING'
    Send $b 13 'tools/call' @{name='powershell_job';arguments=@{action='submit';operation_key='next';command="Write-Output 'second'";timeout_seconds=10};_meta=$meta}
    Send $c 13 'tools/call' @{name='powershell_job';arguments=@{action='submit';operation_key='shared';command="Start-Sleep -Seconds 5; Write-Output 'first'";timeout_seconds=20};_meta=$meta}
    $next=(Receive $b 13).structuredContent;$dup=(Receive $c 13).structuredContent
    Assert ($next.job.state -eq 'QUEUED' -and $dup.job.job_id -eq $first.job.job_id) 'r3.concurrent_fifo_and_idempotency'
    $conflict=Job $c 14 @{action='submit';operation_key='shared';command="Write-Output 'different'"}
    Assert ($conflict.status -eq 'CONFLICT') 'r3.same_key_different_payload_conflict'
    $a.Kill($true);$a.WaitForExit()
    $one=Wait-JobState $b $first.job.job_id 'COMPLETED'
    $two=Wait-JobState $c $next.job.job_id 'COMPLETED'
    Assert ($one.execution.stdout.Trim() -eq 'first' -and $two.execution.stdout.Trim() -eq 'second') 'r3.disconnect_and_distinct_results'
    $kill=Job $b 15 @{action='submit';operation_key='cancel';command='Start-Sleep -Seconds 30';timeout_seconds=60}
    $null=Wait-JobState $c $kill.job.job_id 'RUNNING'
    Start-Sleep -Milliseconds 700
    $ack=Job $c 16 @{action='cancel';job_id=$kill.job.job_id}
    Assert ($ack.status -eq 'OK' -and $ack.job.state -eq 'CANCEL_REQUESTED') 'r3.running_cancel_ack'
    $unknown=Wait-JobState $b $kill.job.job_id 'NEEDS_RECONCILIATION'
    Assert ($unknown.execution.status -eq 'CANCELLED_UNKNOWN_EFFECT') 'r3.cancel_unknown_effect'
    Stop-Service $name
    Start-Service $name
    $null=Wait-Status $b 'OK'
    $saved=Job $c 17 @{action='logs';job_id=$first.job.job_id}
    Assert ($saved.job.state -eq 'COMPLETED' -and $saved.job.execution.stdout.Trim() -eq 'first') 'r3.persisted_real_output'
    $list=Job $c 18 @{action='list'}
    Assert (@($list.jobs|Where-Object {$_.job_id -eq $kill.job.job_id -and $_.state -eq 'NEEDS_RECONCILIATION'}).Count -eq 1) 'r3.list_recovery'
    Write-Host 'R3_NATIVE_JOBS=PASS (actual service and PowerShell, three SDK stdio clients; no live tunnel/reboot)'
    # Real non-idempotent local side effect, followed by a deliberate interruption.
    $marker=Join-Path $data 'recovery-marker.txt'
    $safePath=$marker.Replace("'","''")
    $scriptA="[IO.File]::AppendAllText('$safePath','A'); Start-Sleep -Seconds 90"
    $scriptB="[IO.File]::AppendAllText('$safePath','B'); Write-Output 'R4_OUTPUT_FIXTURE_7A98E1'"
    $active=Job $b 30 @{action='submit';operation_key='r4-crash-active';command=$scriptA;timeout_seconds=120}
    $queued=Job $c 31 @{action='submit';operation_key='r4-crash-queued';command=$scriptB;timeout_seconds=20}
    Assert ($active.status -eq 'OK' -and $queued.status -eq 'OK') 'r4.durable_pending_jobs'
    $deadline=[Diagnostics.Stopwatch]::StartNew()
    while($deadline.Elapsed.TotalSeconds -lt 10){
        if((Test-Path -LiteralPath $marker) -and [IO.File]::ReadAllText($marker) -eq 'A'){break}
        Start-Sleep -Milliseconds 100
    }
    Assert ((Test-Path -LiteralPath $marker) -and [IO.File]::ReadAllText($marker) -eq 'A') 'r4.external_effect_observed_before_failure'
    $pre=(Get-CimInstance Win32_Service -Filter "Name='$name'").ProcessId
    Stop-Process -Id $pre -Force
    # Do not start service manually: SCM recovery policy is the owner.
    $clock=[Diagnostics.Stopwatch]::StartNew()
    do{
        Start-Sleep -Milliseconds 200
        $restarted=Status $b 301
        if($restarted.status -eq 'OK' -and $restarted.service_pid -ne $pre){break}
    }while($clock.Elapsed.TotalSeconds -lt 20)
    Assert ($restarted.status -eq 'OK' -and $restarted.service_pid -ne $pre) 'r4.actual_scm_auto_restart'
    $unknown=Wait-JobState $b $active.job.job_id 'NEEDS_RECONCILIATION'
    Assert ($null -eq $unknown.execution -and $unknown.job_id -eq $active.job.job_id) 'r4.unknown_effect_not_replayed'
    $recovered=Wait-JobState $c $queued.job.job_id 'COMPLETED'
    Assert ($recovered.execution.stdout.Trim() -eq 'R4_OUTPUT_FIXTURE_7A98E1' -and [IO.File]::ReadAllText($marker) -ceq 'AB') 'r4.queued_runs_after_crash_effect_exactly_once_in_fixture'
    $again=Job $b 32 @{action='submit';operation_key='r4-crash-active';command=$scriptA;timeout_seconds=120}
    Assert ($again.job.job_id -eq $active.job.job_id -and $again.job.state -eq 'NEEDS_RECONCILIATION') 'r4.retry_cannot_replay_unknown_effect'
    Stop-Service $name
    # Read actual persisted database bytes only while SCM service is stopped.
    $files=Get-ChildItem -LiteralPath $data -File | Where-Object {$_.Name -like 'jobs.db*'}
    Assert (@($files).Count -gt 0) 'r4.database_exists'
    foreach($file in $files){
        $bytes=[IO.File]::ReadAllBytes($file.FullName)
        $text=[Text.Encoding]::UTF8.GetString($bytes)
        Assert (-not $text.Contains('R4_OUTPUT_FIXTURE_7A98E1') -and -not $text.Contains('AppendAllText')) 'r4.dpapi_no_fixture_plaintext_in_database'
    }
    Start-Service $name
    $null=Wait-Status $b 'OK'
    $read=Job $c 33 @{action='logs';job_id=$queued.job.job_id}
    Assert ($read.job.execution.stdout.Trim() -eq 'R4_OUTPUT_FIXTURE_7A98E1') 'r4.dpapi_decrypt_after_restart'
    Write-Host 'R4_ACTUAL_CRASH_RECOVERY=PASS (SCM restart, not physical OS reboot)'
}finally{
    foreach($p in $clients){
        if(-not $p.HasExited){$p.StandardInput.Close();if(-not $p.WaitForExit(3000)){$p.Kill($true);$p.WaitForExit()}}
        $p.Dispose()
    }
    if(Get-Service $name -ErrorAction SilentlyContinue){Stop-Service $name -ErrorAction SilentlyContinue;sc.exe delete $name|Out-Host}
    if(Test-Path -LiteralPath $data){Remove-Item -LiteralPath $data -Recurse -Force}
}
