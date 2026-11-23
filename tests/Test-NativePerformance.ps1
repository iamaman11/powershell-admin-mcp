# Local raw SDK/SCM benchmark, not a live ChatGPT/Codex acceptance test.
# Default mode is read-only; -ExerciseJobs is restricted to isolated Windows CI.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Exe,
      [ValidateRange(5,86400)][int]$DurationSeconds=30,
      [ValidateRange(3,10)][int]$ClientCount=3,
      [switch]$ExerciseJobs)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($ExerciseJobs -and ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows')){throw 'JOB_EXERCISE_CI_ONLY'}
$Exe=[IO.Path]::GetFullPath($Exe)
$clients=[Collections.Generic.List[Diagnostics.Process]]::new()
$statusSamples=[Collections.Generic.List[double]]::new()
$admission=[Collections.Generic.List[double]]::new()
$cancel=[Collections.Generic.List[double]]::new()
$logs=[Collections.Generic.List[double]]::new()
$execIdle=[Collections.Generic.List[double]]::new()
$execQueued=[Collections.Generic.List[double]]::new()
$cold=[Collections.Generic.List[double]]::new()
$cancelPhases=[Collections.Generic.List[object]]::new()
$meta=@{'io.modelcontextprotocol/protocolVersion'='2026-07-28';'io.modelcontextprotocol/clientCapabilities'=@{};'io.modelcontextprotocol/clientInfo'=@{name='local-performance';version='1'}}
$svc=Get-CimInstance Win32_Service -Filter "Name='WindowsAdminMCP'"
if(-not $svc -or $svc.State -ne 'Running' -or $svc.StartName -ne 'LocalSystem' -or $svc.ProcessId -eq 0 -or $svc.PathName -ine ('"'+$Exe+'" service')){throw 'SERVICE_IDENTITY_MISMATCH'}
$servicePid=[int]$svc.ProcessId
$serviceProcess=Get-Process -Id $servicePid
$serviceStart=$serviceProcess.StartTime
$sequence=0
function New-Client {
    $si=[Diagnostics.ProcessStartInfo]::new($Exe)
    $si.ArgumentList.Add('stdio');$si.UseShellExecute=$false
    $si.RedirectStandardInput=$true;$si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true
    $p=[Diagnostics.Process]::Start($si)
    $clients.Add($p);$null=$p.StandardError.ReadToEndAsync()
    return $p
}
function Send($p,$id,$name,$arguments){
    if($name -eq 'powershell_exec' -or ($name -eq 'powershell_job' -and $arguments.action -eq 'submit')){$arguments.execution_context='SYSTEM'}
    $msg=@{jsonrpc='2.0';id=$id;method='tools/call';params=@{name=$name;arguments=$arguments;_meta=$meta}}
    $p.StandardInput.WriteLine(($msg|ConvertTo-Json -Compress -Depth 12));$p.StandardInput.Flush()
}
function Receive($p,$id){
    $deadline=[Diagnostics.Stopwatch]::StartNew()
    do{
        $remaining=[Math]::Max(1,10000-$deadline.ElapsedMilliseconds)
        $line=$p.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromMilliseconds($remaining)).GetAwaiter().GetResult()
        if($null -eq $line){throw 'PERF_EOF'}
        $m=$line|ConvertFrom-Json -AsHashtable
        if($m.Contains('id') -and $m.id -eq $id){
            if($m.Contains('error') -or ($m.result.Contains('isError') -and $m.result.isError)){throw 'PERF_RPC_ERROR'}
            return $m.result.structuredContent
        }
    }while($deadline.ElapsedMilliseconds -lt 10000)
    throw 'PERF_RPC_TIMEOUT'
}
function Call($p,$id,$name,$arguments){
    Send $p $id $name $arguments
    Receive $p $id
}
function Require-Job($reply,[string]$phase){
    if($reply -isnot [Collections.IDictionary] -or -not $reply.Contains('status')){
        throw "PERF_INVALID_STRUCTURED_REPLY phase=$phase"
    }
    if($reply.status -ne 'OK' -or -not $reply.Contains('job') -or $null -eq $reply.job){
        # Report only status and transport timings; never script or output.
        $diagnostic=[ordered]@{phase=$phase;status=$reply.status;correlation_id=$reply['correlation_id'];native_error=$reply['native_error'];timing=$reply['timing']}
        throw ('PERF_JOB_REPLY '+($diagnostic|ConvertTo-Json -Depth 6 -Compress))
    }
    return $reply.job
}
function Confirm-Status($s){
    if($s.status -ne 'OK' -or $s.service_pid -ne $servicePid -or $s.execution_sid -ne 'S-1-5-18'){throw 'PERF_SERVICE_CHANGED'}
}
function Status-Round([int]$id){
    # All three requests sent before reading. Elapsed is a conservative client
    # observed upper bound (includes sequential response collection).
    $clocks=@(foreach($p in $clients){
        $watch=[Diagnostics.Stopwatch]::StartNew()
        Send $p $id 'powershell_status' @{}
        $watch
    })
    for($i=0;$i -lt $clients.Count;$i++){
        Confirm-Status (Receive $clients[$i] $id)
        $statusSamples.Add($clocks[$i].Elapsed.TotalMilliseconds)
    }
}
function Distribution($values){
    if($values.Count -eq 0){return $null}
    $sorted=[double[]]$values.ToArray();[Array]::Sort($sorted)
    [pscustomobject]@{count=$sorted.Length;min_ms=[Math]::Round($sorted[0],3);
        p50_ms=[Math]::Round($sorted[[int][Math]::Ceiling($sorted.Length*0.50)-1],3);
        p95_ms=[Math]::Round($sorted[[int][Math]::Ceiling($sorted.Length*0.95)-1],3);
        p99_ms=[Math]::Round($sorted[[int][Math]::Ceiling($sorted.Length*0.99)-1],3);
        max_ms=[Math]::Round($sorted[-1],3)}
}
function Resources {
    $p=Get-Process -Id $servicePid
    try{
        if($p.StartTime -ne $serviceStart -or $p.Path -ine $Exe){throw 'PERF_PROCESS_REPLACED'}
        [pscustomobject]@{working_set_bytes=$p.WorkingSet64;private_bytes=$p.PrivateMemorySize64;handles=$p.HandleCount}
    }finally{$p.Dispose()}
}
try{
    for($i=0;$i -lt $ClientCount;$i++){
        $watch=[Diagnostics.Stopwatch]::StartNew()
        $p=New-Client
        $sequence++
        Confirm-Status (Call $p $sequence 'powershell_status' @{})
        $cold.Add($watch.Elapsed.TotalMilliseconds)
    }
    # Warm up SDK, pipe and DB paths, do not count warmup in measured p95.
    for($i=0;$i -lt 3;$i++){$sequence++;Status-Round $sequence}
    $statusSamples.Clear()
    $first=Resources
    $peakWorking=$first.working_set_bytes;$peakPrivate=$first.private_bytes;$peakHandles=$first.handles
    if($ExerciseJobs){
        $prefix='perf-'+[guid]::NewGuid().ToString('N')
        $sequence++
        $hold=Call $clients[0] $sequence 'powershell_job' @{action='submit';operation_key=$prefix+'-hold';resource_key=$prefix;command="Start-Sleep -Seconds 35; Write-Output 'perf-fixture'";timeout_seconds=55}
        $null=Require-Job $hold 'hold-admission'
        $watch=[Diagnostics.Stopwatch]::StartNew()
        do{
            $sequence++;$s=Call $clients[1] $sequence 'powershell_job' @{action='status';job_id=$hold.job.job_id}
            $null=Require-Job $s 'hold-status'
            if($s.job.state -eq 'RUNNING'){break}
            if($s.job.state -ne 'QUEUED'){throw 'PERF_HOLD_EARLY_END'}
            Start-Sleep -Milliseconds 50
        }while($watch.Elapsed.TotalSeconds -lt 10)
        if($s.job.state -ne 'RUNNING'){throw 'PERF_HOLD_NOT_RUNNING'}
        # Synchronous short client wait while the same named resource is occupied.
        $sequence++;$watch=[Diagnostics.Stopwatch]::StartNew()
        $pending=Call $clients[0] $sequence 'powershell_exec' @{operation_key=$prefix+'-exec-queued';resource_key=$prefix;command="Write-Output 'must not execute'";timeout_seconds=1}
        $execQueued.Add($watch.Elapsed.TotalMilliseconds)
        if($pending.status -ne 'PENDING' -or -not $pending.job_id){throw 'PERF_PENDING_EXPECTED'}
        $sequence++;$cancelled=Call $clients[1] $sequence 'powershell_job' @{action='cancel';job_id=$pending.job_id}
        $null=Require-Job $cancelled 'pending-cancel'
        if($cancelled.job.state -ne 'CANCELLED'){throw 'PERF_PENDING_CANCEL_FAILED'}
        for($i=0;$i -lt 12;$i++){
            $sequence++;Status-Round $sequence
            $sequence++;$watch=[Diagnostics.Stopwatch]::StartNew()
            $logged=Call $clients[2] $sequence 'powershell_job' @{action='logs';job_id=$hold.job.job_id;step_index=1}
            $logs.Add($watch.Elapsed.TotalMilliseconds)
            $null=Require-Job $logged 'hold-logs'
            $sequence++;$watch=[Diagnostics.Stopwatch]::StartNew()
            $j=Call $clients[$i%3] $sequence 'powershell_job' @{action='submit';operation_key=$prefix+'-'+$i;resource_key=$prefix;command="Write-Output 'must remain queued'";timeout_seconds=10}
            $admission.Add($watch.Elapsed.TotalMilliseconds)
            $null=Require-Job $j 'queued-admission'
            if($j.job.state -ne 'QUEUED'){throw 'PERF_ADMISSION_FAILED'}
            $sequence++;$watch=[Diagnostics.Stopwatch]::StartNew()
            $clientIndex=($i+1)%3
            $c=Call $clients[$clientIndex] $sequence 'powershell_job' @{action='cancel';job_id=$j.job.job_id}
            $elapsed=$watch.Elapsed.TotalMilliseconds
            $cancel.Add($elapsed)
            $null=Require-Job $c 'queued-cancel'
            $cancelPhases.Add([pscustomobject]@{index=$i;client=$clientIndex;correlation_id=$c.correlation_id;client_ms=[Math]::Round($elapsed,3);timing=$c.timing})
            if($c.job.state -ne 'CANCELLED'){throw 'PERF_CANCEL_NOT_QUEUED'}
        }
        $watch=[Diagnostics.Stopwatch]::StartNew()
        do{
            $sequence++
            $s=Call $clients[2] $sequence 'powershell_job' @{action='logs';job_id=$hold.job.job_id}
            $null=Require-Job $s 'hold-readback'
            if($s.job.state -eq 'COMPLETED'){break}
            if($s.job.state -notin @('RUNNING','QUEUED')){throw 'PERF_HOLD_FAILED'}
            Start-Sleep -Milliseconds 250
        }while($watch.Elapsed.TotalSeconds -lt 50)
        if($s.job.state -ne 'COMPLETED' -or $s.job.execution.stdout.Trim() -cne 'perf-fixture'){throw 'PERF_HOLD_RESULT'}
        for($i=0;$i -lt 3;$i++){
            $sequence++;$watch=[Diagnostics.Stopwatch]::StartNew()
            $x=Call $clients[$i] $sequence 'powershell_exec' @{operation_key=$prefix+'-exec-idle-'+$i;command="Write-Output 'perf-idle'";timeout_seconds=10}
            $execIdle.Add($watch.Elapsed.TotalMilliseconds)
            if($x.status -ne 'COMPLETED' -or $x.stdout.Trim() -cne 'perf-idle'){throw 'PERF_IDLE_EXEC_FAILED'}
        }
    }
    $soakStatusStart=$statusSamples.Count
    $soak=[Diagnostics.Stopwatch]::StartNew()
    do{
        $sequence++;Status-Round $sequence
        $now=Resources
        $peakWorking=[Math]::Max($peakWorking,$now.working_set_bytes)
        $peakPrivate=[Math]::Max($peakPrivate,$now.private_bytes)
        $peakHandles=[Math]::Max($peakHandles,$now.handles)
        $remaining=($DurationSeconds*1000)-$soak.ElapsedMilliseconds
        if($remaining -gt 0){Start-Sleep -Milliseconds ([int][Math]::Min(1000,$remaining))}
    }while($soak.Elapsed.TotalSeconds -lt $DurationSeconds)
    $last=Resources
    $status=Distribution $statusSamples;$admissionStats=Distribution $admission;$cancelStats=Distribution $cancel
    $soakStatusCount=$statusSamples.Count-$soakStatusStart
    [pscustomobject]@{status='PASS';scope='LOCAL_RAW_SDK_NOT_REMOTE';clients=$ClientCount;service_pid=$servicePid;
        soak_seconds=[Math]::Round($soak.Elapsed.TotalSeconds,3);job_exercise=[bool]$ExerciseJobs;
        status_rounds=($statusSamples.Count/$ClientCount);
        soak_status_count=$soakStatusCount;
        soak_status_throughput_per_sec=[Math]::Round($soakStatusCount/$soak.Elapsed.TotalSeconds,3);
        cold_client_latency=(Distribution $cold);status_latency=$status;admission_latency=$admissionStats;cancel_latency=$cancelStats;
        logs_latency=(Distribution $logs);exec_idle_latency=(Distribution $execIdle);exec_queued_wait=(Distribution $execQueued);
        cancel_phase_samples=$cancelPhases.ToArray();
        baseline_goals=@{status_p95_100ms=($status.p95_ms -le 100);
            admission_p95_100ms=$(if($admissionStats){$admissionStats.p95_ms -le 100});
            cancel_p95_500ms=$(if($cancelStats){$cancelStats.p95_ms -le 500})};
        service_resources=@{first=$first;last=$last;peak_working_set_bytes=$peakWorking;peak_private_bytes=$peakPrivate;peak_handles=$peakHandles};
        physical_24h_tested=($soak.Elapsed.TotalSeconds -ge 86400)}|ConvertTo-Json -Depth 8 -Compress
}finally{
    foreach($p in $clients){
        if(-not $p.HasExited){$p.StandardInput.Close();if(-not $p.WaitForExit(3000)){$p.Kill($true);$p.WaitForExit()}}
        $p.Dispose()
    }
    $serviceProcess.Dispose()
}
