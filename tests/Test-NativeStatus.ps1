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
    if($method -eq 'tools/call' -and ($params.name -eq 'powershell_exec' -or
        ($params.name -eq 'powershell_job' -and $params.arguments.action -eq 'submit')) -and
        -not $params.arguments.ContainsKey('execution_context')){$params.arguments.execution_context='SYSTEM'}
    $m=@{jsonrpc='2.0';id=$id;method=$method;params=$params}
    $p.StandardInput.WriteLine(($m|ConvertTo-Json -Depth 12 -Compress));$p.StandardInput.Flush()
}
function Receive($p,$id,[int]$timeout=10){
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while($true){
        $remaining=[TimeSpan]::FromSeconds($timeout)-$watch.Elapsed
        if($remaining.TotalMilliseconds -le 0){throw 'RECEIVE_TIMEOUT'}
        $line=$p.StandardOutput.ReadLineAsync().WaitAsync($remaining).GetAwaiter().GetResult()
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
        Assert (@($list.tools).Count -eq 6 -and @($list.tools.name|Where-Object {$_ -in @('powershell_status','powershell_exec','powershell_job','powershell_screenshot','windows.list_windows','windows.inspect_window')}).Count -eq 6) 'r5.window_tools_including_inspect'
        Assert (@($list.tools|Where-Object {$_.name -eq 'powershell_status' -and $_.Contains('outputSchema')}).Count -eq 1) 'r1.typed_status_schema'
        $v=Wait-Status $p 'OK'
        Assert ($v.service_pid -eq $svc.ProcessId -and $v.execution_sid -eq 'S-1-5-18' -and $v.elevated) 'r1.scm_pid_and_real_execution_identity'
        $caller=[Security.Principal.WindowsIdentity]::GetCurrent()
        Assert ($v.frontend.pid -eq $p.Id -and $v.frontend.sid -eq $caller.User.Value -and
            $v.frontend.session_id -eq $p.SessionId -and $v.command_context -in @('SYSTEM','UNCONFIRMED') -and
            $v.versions.mcp_version -eq '0.8.1-dev' -and $v.versions.store_schema -eq 4 -and $v.timing.ipc_ms -ge 0) 'hardening.separate_frontend_and_runner_identity'
    }
    Send $a 31 'tools/call' @{name='windows.list_windows';arguments=@{max_windows=0};_meta=$meta}
    $invalidWindowList=(Receive $a 31).structuredContent
    Assert ($invalidWindowList.status -eq 'INVALID_ARGUMENT' -and @($invalidWindowList.windows).Count -eq 0) 'windows.mcp_invalid_limit'
    Send $a 32 'tools/call' @{name='windows.list_windows';arguments=@{max_windows=1};_meta=$meta}
    $restrictedWindowList=(Receive $a 32).structuredContent
    Assert ($restrictedWindowList.status -eq 'USER_CONTEXT_UNAVAILABLE' -and @($restrictedWindowList.windows).Count -eq 0) 'windows.reject_unbound_ci_frontend'
    Send $a 33 'tools/call' @{name='windows.inspect_window';arguments=@{window_id='bad';max_depth=3};_meta=$meta}
    $badInspect=(Receive $a 33).structuredContent
    Assert ($badInspect.status -eq 'INVALID_ARGUMENT' -and @($badInspect.elements).Count -eq 0) 'windows.mcp_invalid_inspect_id'
    Send $a 34 'tools/call' @{name='windows.inspect_window';arguments=@{window_id='w1:7fffffffffffffff:1092:1dbaf8d88b62800:1';max_depth=3};_meta=$meta}
    $restrictedInspect=(Receive $a 34).structuredContent
    Assert ($restrictedInspect.status -eq 'USER_CONTEXT_UNAVAILABLE' -and @($restrictedInspect.elements).Count -eq 0) 'windows.inspect_reject_unbound_ci_frontend'
    $inspectorWorker=(& $Exe window-inspect-worker 'bad' 3 60 | ConvertFrom-Json)
    Assert ($inspectorWorker.status -eq 'INVALID_ARGUMENT' -and @($inspectorWorker.elements).Count -eq 0) 'windows.inspect_worker_wire'
    foreach($p in @($a,$b,$c)){Send $p 2 'tools/call' @{name='powershell_status';arguments=@{};_meta=$meta}}
    $results=@(foreach($p in @($a,$b,$c)){(Receive $p 2).structuredContent})
    Assert (@($results|Where-Object {$_.status -eq 'OK' -and $_.service_pid -eq $svc.ProcessId}).Count -eq 3) 'r1.one_service_three_stdio_clients'
    Assert (@($results.correlation_id|Select-Object -Unique).Count -eq 3) 'r1.unique_correlations'
    Stop-Service $name
    $offline=Wait-Status $a 'SERVICE_UNAVAILABLE'
    Assert ($offline.native_error -eq 1062) 'r1.typed_service_offline'
    Assert ($offline.command_context -eq 'UNCONFIRMED' -and $offline.frontend.pid -eq $a.Id) 'hardening.offline_context_not_falsely_confirmed'
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
    $unavailable=Job $a 119 @{action='submit';operation_key='no-user-worker';command="Write-Output 'MUST_NOT_RUN'";execution_context='USER'}
    Assert ($unavailable.status -eq 'USER_CONTEXT_UNAVAILABLE' -and (-not $unavailable.Contains('job') -or $null -eq $unavailable.job)) 'hardening.no_silent_system_fallback'
    $prefix="Write-Output 'Проверка 🚀'"+[char]10+'#'
    $long=$prefix+('x'*(11982-$prefix.Length))
    $shortLog=Job $b 101 @{action='logs';operation_key='short';step_index=1}
    Assert ($shortLog.job.execution.audit_persisted -and $shortLog.job.execution.stdout.Contains('real-exec') -and
        $shortLog.timing.store_ms -ge 0 -and $shortLog.job.execution.queue_wait_ms -ge 0) 'hardening.exec_and_logs_same_persisted_audit'
    $badIndex=Job $b 102 @{action='logs';operation_key='short';step_index=0}
    Assert ($badIndex.status -eq 'INVALID_ARGUMENT') 'hardening.zero_step_index_rejected'
    $x=ToolCall $b 11 'powershell_exec' @{command=$long;timeout_seconds=10;operation_key='long'}
    Assert ($x.status -eq 'COMPLETED' -and $x.stdout.Trim() -ceq 'Проверка 🚀') 'r3.long_unicode_through_mcp'
    # A 100-second benign runner command must return PENDING in the 90-second client budget
    # and continue to completion through the same durable job API.
    $boundaryClock=[Diagnostics.Stopwatch]::StartNew()
    Send $a 103 'tools/call' @{name='powershell_exec';arguments=@{command="Start-Sleep -Seconds 100; Write-Output 'BOUNDARY_OK'";timeout_seconds=120;operation_key='boundary-exec'};_meta=$meta}
    $boundary=(Receive $a 103 110).structuredContent
    Assert ($boundary.status -eq 'PENDING' -and $boundary.job_id -and
        -not $boundary.audit_persisted -and $boundaryClock.Elapsed.TotalSeconds -lt 110) 'hardening.exec_boundary_pending_before_tunnel_deadline'
    $boundaryId=$boundary.job_id
    $boundaryResult=Wait-JobState $b $boundaryId 'COMPLETED'
    Assert ($boundaryResult.execution.stdout.Trim() -ceq 'BOUNDARY_OK' -and $boundaryResult.execution.audit_persisted) 'hardening.boundary_result_saved'
    # Real workload overlap: six distinct live PowerShell trees, three SDK
    # frontends, then a seventh independent short command before releasing them.
    $parallelDir=Join-Path $data 'parallel-fixture'
    [IO.Directory]::CreateDirectory($parallelDir)|Out-Null
    $release=Join-Path $parallelDir 'release'
    $safeRelease=$release.Replace("'","''")
    $runs=@()
    for($i=0;$i -lt 6;$i++){
        $ready=Join-Path $parallelDir ('ready-'+$i)
        $safeReady=$ready.Replace("'","''")
        $command="[IO.File]::WriteAllText('$safeReady',[string]`$PID); while(-not [IO.File]::Exists('$safeRelease')){Start-Sleep -Milliseconds 100}; Write-Output 'parallel-$i'"
        $client=@($a,$b,$c)[$i%3]
        $admitted=Job $client (3000+$i) @{action='submit';operation_key=('parallel-many-'+$i);command=$command;timeout_seconds=60}
        Assert ($admitted.status -eq 'OK' -and $admitted.job.job_id) ('parallel.admitted.'+$i)
        $runs+=@{id=$admitted.job.job_id;ready=$ready;marker=('parallel-'+$i)}
    }
    $parallelClock=[Diagnostics.Stopwatch]::StartNew()
    try{
        do{
            $readyCount=@($runs|Where-Object {Test-Path -LiteralPath $_.ready}).Count
            if($readyCount -eq 6){break}
            Start-Sleep -Milliseconds 100
        }while($parallelClock.Elapsed.TotalSeconds -lt 25)
        if($readyCount -ne 6){
            $details=Job $b 3100 @{action='list';page_size=100}
            Write-Host ('PARALLEL_CAPACITY '+($details.store.budget|ConvertTo-Json -Compress))
            throw 'PARALLEL_SIX_PROCESSES_NOT_OBSERVED'
        }
        $processIds=@($runs|ForEach-Object {[int](Get-Content -LiteralPath $_.ready -Raw)})
        Assert (@($processIds|Select-Object -Unique).Count -eq 6 -and @($processIds|ForEach-Object {Get-Process -Id $_ -ErrorAction SilentlyContinue}).Count -eq 6) 'parallel.six_real_live_powershell_processes'
        $fast=ToolCall $c 3101 'powershell_exec' @{operation_key='parallel-seventh';command="Write-Output 'SEVENTH'";timeout_seconds=10}
        Assert ($fast.status -eq 'COMPLETED' -and $fast.stdout.Trim() -ceq 'SEVENTH') 'parallel.seventh_completes_while_six_active'
        $ack=Job $b 3102 @{action='cancel';job_id=$runs[0].id}
        Assert ($ack.status -eq 'OK' -and $ack.job.state -eq 'CANCEL_REQUESTED') 'parallel.cancel_one'
        $interrupted=Wait-JobState $c $runs[0].id 'NEEDS_RECONCILIATION'
        Assert ($interrupted.execution.status -eq 'CANCELLED_UNKNOWN_EFFECT') 'parallel.cancelled_tree_unknown_not_replayed'
        $remaining=Job $b 3103 @{action='status';job_id=$runs[1].id}
        Assert ($remaining.job.state -eq 'RUNNING') 'parallel.cancel_does_not_interrupt_other_job'
        Assert (-not (Get-Process -Id $processIds[0] -ErrorAction SilentlyContinue)) 'parallel.cancelled_pid_gone'
    }finally{
        [IO.File]::WriteAllText($release,'release')
    }
    for($i=1;$i -lt 6;$i++){
        $done=Wait-JobState $b $runs[$i].id 'COMPLETED'
        Assert ($done.execution.stdout.Trim() -ceq $runs[$i].marker -and $done.execution.audit_persisted) ('parallel.distinct_saved_result.'+$i)
    }
    $first=Job $a 12 @{action='submit';operation_key='shared';resource_key='fixture-fifo';command="Start-Sleep -Seconds 12; Write-Output 'first'";timeout_seconds=20}
    Assert ($first.status -eq 'OK' -and $first.job.state -eq 'QUEUED') 'r3.durable_admission'
    $null=Wait-JobState $b $first.job.job_id 'RUNNING'
    $independent=Job $c 120 @{action='submit';operation_key='parallel-short';command="Write-Output 'INDEPENDENT'";timeout_seconds=10}
    $fast=Wait-JobState $c $independent.job.job_id 'COMPLETED'
    $still=Job $b 121 @{action='status';job_id=$first.job.job_id}
    Assert ($fast.execution.stdout.Trim() -ceq 'INDEPENDENT' -and $still.job.state -eq 'RUNNING') 'parallel.short_finishes_while_long_active'
    Send $b 13 'tools/call' @{name='powershell_job';arguments=@{action='submit';operation_key='next';resource_key='fixture-fifo';command="Write-Output 'second'";timeout_seconds=10};_meta=$meta}
    Send $c 13 'tools/call' @{name='powershell_job';arguments=@{action='submit';operation_key='shared';resource_key='fixture-fifo';command="Start-Sleep -Seconds 12; Write-Output 'first'";timeout_seconds=20};_meta=$meta}
    $next=(Receive $b 13).structuredContent;$dup=(Receive $c 13).structuredContent
    Assert ($next.job.state -eq 'QUEUED' -and $dup.job.job_id -eq $first.job.job_id) 'parallel.explicit_resource_fifo_and_idempotency'
    $conflict=Job $c 14 @{action='submit';operation_key='shared';resource_key='fixture-fifo';command="Write-Output 'different'"}
    Assert ($conflict.status -eq 'CONFLICT') 'r3.same_key_different_payload_conflict'
    $a.Kill($true);$a.WaitForExit()
    $one=Wait-JobState $b $first.job.job_id 'COMPLETED'
    $two=Wait-JobState $c $next.job.job_id 'COMPLETED'
    Assert ($one.execution.stdout.Trim() -eq 'first' -and $two.execution.stdout.Trim() -eq 'second' -and $one.execution.audit_persisted -and $two.execution.audit_persisted) 'r3.disconnect_and_distinct_results'
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
    $unknownSource=Job $c 114 @{action='logs';job_id=$kill.job.job_id;step_index=1;include_interrupted_command=$true}
    Assert ($unknownSource.job.interrupted_source_available -and $unknownSource.job.interrupted_command -ceq 'Start-Sleep -Seconds 30') 'hardening.interrupted_source_retained'
    $decision=Job $c 115 @{action='reconcile';job_id=$kill.job.job_id;decision='UNKNOWN';evidence='Isolated cancellation fixture: effect deliberately unknown.'}
    Assert ($decision.status -eq 'OK' -and $decision.job.state -eq 'RECONCILED' -and $decision.job.reconciliation.decision -eq 'UNKNOWN') 'hardening.reconcile_unknown'
    $repeat=Job $b 116 @{action='reconcile';job_id=$kill.job.job_id;decision='UNKNOWN';evidence='Isolated cancellation fixture: effect deliberately unknown.'}
    Assert ($repeat.status -eq 'OK' -and $repeat.job.reconciliation.correlation_id -eq $decision.job.reconciliation.correlation_id) 'hardening.reconciliation_idempotent'
    $change=Job $b 117 @{action='reconcile';job_id=$kill.job.job_id;decision='EFFECT_CONFIRMED';evidence='Different decision'}
    Assert ($change.status -eq 'CONFLICT') 'hardening.reconciliation_immutable'
    $noreplay=Job $b 118 @{action='submit';operation_key='cancel';command='Start-Sleep -Seconds 30';timeout_seconds=60}
    Assert ($noreplay.job.state -eq 'RECONCILED' -and $noreplay.job.job_id -eq $kill.job.job_id) 'hardening.reconciled_key_no_replay'
    $saved=Job $c 17 @{action='logs';job_id=$first.job.job_id}
    Assert ($saved.job.state -eq 'COMPLETED' -and $saved.job.execution.stdout.Trim() -eq 'first') 'r3.persisted_real_output'
    $list=Job $c 18 @{action='list'}
    Assert (@($list.jobs|Where-Object {$_.job_id -eq $kill.job.job_id -and $_.state -eq 'RECONCILED'}).Count -eq 1) 'r3.list_recovery'
    Write-Host 'R3_NATIVE_JOBS=PASS (actual service and PowerShell, three SDK stdio clients; no live tunnel/reboot)'
    # Real non-idempotent local side effect, followed by a deliberate interruption.
    $marker=Join-Path $data 'recovery-marker.txt'
    $safePath=$marker.Replace("'","''")
    $scriptA="[IO.File]::AppendAllText('$safePath','A'); Start-Sleep -Seconds 90"
    $scriptB="[IO.File]::AppendAllText('$safePath','B'); Write-Output 'R4_OUTPUT_FIXTURE_7A98E1'"
    $active=Job $b 30 @{action='submit';operation_key='r4-crash-active';resource_key='fixture-crash';command=$scriptA;timeout_seconds=120}
    $queued=Job $c 31 @{action='submit';operation_key='r4-crash-queued';resource_key='fixture-crash';command=$scriptB;timeout_seconds=20}
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
    Assert ((-not $unknown.Contains('execution') -or $null -eq $unknown['execution']) -and $unknown.job_id -eq $active.job.job_id) 'r4.unknown_effect_not_replayed'
    $recovered=Wait-JobState $c $queued.job.job_id 'COMPLETED'
    Assert ($recovered.execution.stdout.Trim() -eq 'R4_OUTPUT_FIXTURE_7A98E1' -and [IO.File]::ReadAllText($marker) -ceq 'AB') 'r4.queued_runs_after_crash_effect_exactly_once_in_fixture'
    $again=Job $b 32 @{action='submit';operation_key='r4-crash-active';resource_key='fixture-crash';command=$scriptA;timeout_seconds=120}
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
    # A checkpoint is a committed step result, not a remembered in-memory index.
    $planMarker=Join-Path $data 'plan-marker.txt'
    $planPath=$planMarker.Replace("'","''")
    $steps=@(
        @{command="[IO.File]::AppendAllText('$planPath','A'); Write-Output 'step-A'";timeout_seconds=10;pause_after=$true},
        @{command="[IO.File]::AppendAllText('$planPath','B'); Write-Output 'step-B'";timeout_seconds=10}
    )
    $plan=Job $b 40 @{action='submit';operation_key='r4-plan';steps=$steps}
    Assert ($plan.status -eq 'OK' -and $plan.job.total_steps -eq 2) 'r4.plan_accepted'
    $paused=Wait-JobState $c $plan.job.job_id 'WAITING_FOR_RESUME'
    Assert ($paused.completed_steps -eq 1 -and $paused.execution.stdout.Trim() -ceq 'step-A' -and [IO.File]::ReadAllText($planMarker) -ceq 'A') 'r4.checkpoint_A_persisted'
    # Pending step must not run just because the process restarts.
    Stop-Service $name
    Start-Service $name
    $null=Wait-Status $b 'OK'
    $paused=Wait-JobState $b $plan.job.job_id 'WAITING_FOR_RESUME'
    Assert ($paused.completed_steps -eq 1 -and [IO.File]::ReadAllText($planMarker) -ceq 'A') 'r4.checkpoint_survives_restart'
    $samePlan=Job $c 41 @{action='submit';operation_key='r4-plan';steps=$steps}
    Assert ($samePlan.job.job_id -eq $plan.job.job_id -and $samePlan.job.state -eq 'WAITING_FOR_RESUME') 'r4.plan_idempotency'
    $deny=Job $b 42 @{action='resume';job_id=$plan.job.job_id;expected_completed_steps=2}
    Assert ($deny.status -eq 'NOT_RESUMABLE') 'r4.stale_checkpoint_rejected'
    $deny=Job $b 43 @{action='resume';job_id=$active.job.job_id;expected_completed_steps=1}
    Assert ($deny.status -eq 'NOT_RESUMABLE') 'r4.unknown_effect_resume_denied'
    $go=Job $c 44 @{action='resume';job_id=$plan.job.job_id;expected_completed_steps=1}
    Assert ($go.status -eq 'OK' -and $go.job.state -eq 'QUEUED') 'r4.resume_verified_checkpoint'
    $done=Wait-JobState $b $plan.job.job_id 'COMPLETED'
    Assert ($done.completed_steps -eq 2 -and $done.execution.stdout.Trim() -ceq 'step-B' -and [IO.File]::ReadAllText($planMarker) -ceq 'AB') 'r4.step_B_without_replaying_A'
    $log=Job $c 45 @{action='logs';job_id=$plan.job.job_id;step_index=1}
    Assert ($log.job.result_step -eq 1 -and $log.job.execution.stdout.Trim() -ceq 'step-A') 'r4.previous_step_output_persisted'
    # Invalid mixed single/plan payload cannot be admitted.
    $bad=Job $b 46 @{action='submit';operation_key='r4-invalid';command='Write-Output 1';steps=$steps}
    Assert ($bad.status -eq 'INVALID_ARGUMENT') 'r4.mixed_payload_rejected'
    Write-Host 'R4_CHECKPOINT_PLAN=PASS (explicit resume after SCM restart; OS reboot proof pending)'

    # External reboot boundary: no physical reboot in CI. A real SCM restart
    # must not release it; completed effects and outputs remain durable.
    $bootMarker=Join-Path $data 'boot-marker.txt'
    $bootPath=$bootMarker.Replace("'","''")
    $bootSteps=@(
        @{command="[IO.File]::AppendAllText('$bootPath','A'); Write-Output 'boot-step-A'";timeout_seconds=10;reboot_after=$true},
        @{command="if([IO.File]::ReadAllText('$bootPath') -cne 'A'){throw 'POSTCONDITION_FAILED'}; Write-Output 'verified'";timeout_seconds=10;verify_after_reboot=$true},
        @{command="[IO.File]::AppendAllText('$bootPath','B'); Write-Output 'boot-step-B'";timeout_seconds=10}
    )
    $missingCheck=@(@{command='Write-Output 1';reboot_after=$true},@{command='Write-Output 2'})
    $invalidBoot=Job $b 81 @{action='submit';operation_key='r4-boot-invalid';steps=$missingCheck}
    Assert ($invalidBoot.status -eq 'INVALID_ARGUMENT') 'r4.reboot_requires_declared_postcondition'
    $boot=Job $b 82 @{action='submit';operation_key='r4-boot-boundary';steps=$bootSteps}
    Assert ($boot.status -eq 'OK') 'r4.reboot_plan_admitted'
    $waiting=Wait-JobState $b $boot.job.job_id 'WAITING_FOR_REBOOT'
    Assert ($waiting.completed_steps -eq 1 -and $waiting.execution.stdout.Trim() -ceq 'boot-step-A' -and [IO.File]::ReadAllText($bootMarker) -ceq 'A') 'r4.reboot_checkpoint_committed'
    $deny=Job $c 83 @{action='resume';job_id=$boot.job.job_id;expected_completed_steps=1}
    Assert ($deny.status -eq 'NOT_RESUMABLE') 'r4.manual_resume_cannot_bypass_reboot'
    Stop-Service $name
    Start-Service $name
    $null=Wait-Status $b 'OK'
    $waiting=Wait-JobState $c $boot.job.job_id 'WAITING_FOR_REBOOT'
    Assert ($waiting.completed_steps -eq 1 -and $waiting.reboot_evidence -eq 'SAME_BOOT' -and [IO.File]::ReadAllText($bootMarker) -ceq 'A') 'r4.scm_restart_does_not_release_reboot'
    $same=Job $b 84 @{action='submit';operation_key='r4-boot-boundary';steps=$bootSteps}
    Assert ($same.job.job_id -eq $boot.job.job_id -and $same.job.state -eq 'WAITING_FOR_REBOOT') 'r4.reboot_checkpoint_idempotent'
    $cancelBoot=Job $c 85 @{action='cancel';job_id=$boot.job.job_id}
    Assert ($cancelBoot.status -eq 'OK' -and $cancelBoot.job.state -eq 'CANCELLED' -and [IO.File]::ReadAllText($bootMarker) -ceq 'A') 'r4.cancel_waiting_reboot'
    Write-Host 'R4_DURABLE_REBOOT_BOUNDARY=PASS (same kernel session; positive actual reboot still NOT_TESTED)'

    Send $b 70 'tools/call' @{name='powershell_screenshot';arguments=@{width=1};_meta=$meta}
    # This is a tool-level error, not a JSON-RPC failure.
    $line=$b.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
    $bad=$line|ConvertFrom-Json -AsHashtable
    Assert ($bad.id -eq 70 -and $bad.result.isError -and $bad.result.structuredContent.status -eq 'INVALID_ARGUMENT') 'r5.invalid_region_typed'
    $still=Status $c 71
    Assert ($still.status -eq 'OK') 'r5.status_after_screenshot_error'
    Write-Host 'R5_SCREENSHOT_CONTRACT=PASS (no physical desktop capture in CI)'

    # Native CLI diagnostics use the same service and complete store counts.
    $doctorStart=[Diagnostics.ProcessStartInfo]::new($Exe)
    $doctorStart.ArgumentList.Add('doctor')
    $doctorStart.UseShellExecute=$false
    $doctorStart.RedirectStandardOutput=$true
    $doctorStart.RedirectStandardError=$true
    $doctorProc=[Diagnostics.Process]::Start($doctorStart)
    try{
        $out=$doctorProc.StandardOutput.ReadToEndAsync()
        $err=$doctorProc.StandardError.ReadToEndAsync()
        if(-not $doctorProc.WaitForExit(15000)){throw 'DOCTOR_TIMEOUT'}
        $rawDoctor=$out.GetAwaiter().GetResult()
        $doctorExit=$doctorProc.ExitCode
    }finally{if(-not $doctorProc.HasExited){$doctorProc.Kill($true);$doctorProc.WaitForExit()};$doctorProc.Dispose()}
    $doctor=$rawDoctor|ConvertFrom-Json -AsHashtable
    Assert ($doctor.schemaVersion -eq 1 -and $doctor.status -eq 'DEGRADED' -and $doctorExit -eq 1) 'r6.doctor_reports_isolated_install_degraded'
    Assert ($doctor.service.service_pid -eq (Get-CimInstance Win32_Service -Filter "Name='$name'").ProcessId -and $doctor.store.schemaVersion -eq 4) 'r6.doctor_authoritative_service_store'
    Assert ($doctor.store.total -gt 0 -and $doctor.store.waitingForReboot -eq 0 -and $doctor.store.needsReconciliation -ge 1) 'r6.doctor_total_state_counts'
    Assert (@($doctor.checks|Where-Object {$_.component -eq 'Remote' -and $_.status -eq 'NOT_TESTED'}).Count -eq 1) 'r6.doctor_no_false_remote_acceptance'
    $performance=& (Join-Path $PSScriptRoot 'Test-NativePerformance.ps1') -Exe $Exe -DurationSeconds 12 -ExerciseJobs|ConvertFrom-Json
    Assert ($performance.status -eq 'PASS' -and $performance.status_latency.count -ge 12 -and
        $performance.admission_latency.count -eq 12 -and $performance.cancel_latency.count -eq 12) 'r7.bounded_performance_fixture'
    Write-Host ('PERFORMANCE_BASELINE='+($performance|ConvertTo-Json -Depth 8 -Compress))



}finally{
    foreach($p in $clients){
        if(-not $p.HasExited){$p.StandardInput.Close();if(-not $p.WaitForExit(3000)){$p.Kill($true);$p.WaitForExit()}}
        $p.Dispose()
    }
    if(Get-Service $name -ErrorAction SilentlyContinue){Stop-Service $name -ErrorAction SilentlyContinue;sc.exe delete $name|Out-Host}
    if(Test-Path -LiteralPath $data){Remove-Item -LiteralPath $data -Recurse -Force}
}
