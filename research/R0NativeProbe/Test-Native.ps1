param([Parameter(Mandatory)][string]$Exe,[Parameter(Mandatory)][string]$SdkDll)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$Exe=[IO.Path]::GetFullPath($Exe)
if (-not $env:GITHUB_ACTIONS -or $env:RUNNER_OS -ne 'Windows') {throw 'ISOLATED_WINDOWS_CI_ONLY'}
$name='WindowsAdminMcpR0Probe'
if(Get-Service -Name $name -ErrorAction SilentlyContinue){throw 'PROBE_SERVICE_ALREADY_EXISTS'}
$root=Join-Path $env:RUNNER_TEMP ('admin-mcp-r0-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$db=Join-Path $root 'state.db'
function Assert([bool]$condition,[string]$name){if(-not $condition){throw "FAIL $name"};Write-Host "PASS $name"}
function Read-State {
    $raw=& $Exe inspect $db
    if($LASTEXITCODE -ne 0){throw 'INSPECT_FAILED'}
    return ($raw|ConvertFrom-Json)
}
function Wait-State([string]$expected,[int]$starts){
    $deadline=[Diagnostics.Stopwatch]::StartNew()
    while($deadline.Elapsed.TotalSeconds -lt 20){
        try {$v=Read-State;if($v.state -eq $expected -and $v.starts -eq $starts){return $v}}catch{}
        Start-Sleep -Milliseconds 200
    }
    throw "STATE_TIMEOUT $expected $starts"
}
& $Exe job-object
if($LASTEXITCODE -ne 0){throw 'JOB_OBJECT_FAILED'}
try {
    $binary='"'+$Exe+'" service "'+$db+'"'
    New-Service -Name $name -BinaryPathName $binary -StartupType Automatic -DisplayName 'Isolated R0 probe' | Out-Null
    $svc=Get-CimInstance Win32_Service -Filter "Name='$name'"
    Assert ($svc.StartMode -eq 'Auto' -and $svc.StartName -eq 'LocalSystem') 'scm.auto.localsystem'
    Start-Service $name
    $a=Wait-State 'RUNNING' 1
    Assert ($a.checkpoint -eq 1 -and $a.identity -eq 'S-1-5-18') 'scm.actual_system_and_checkpoint_a'
    # Each request is a separate native process/connection, sharing the service.
    $pipeName='WindowsAdminMcpR0_'+[IO.Path]::GetFileName($root)
    function Start-PipeCall($request){
        $si=[Diagnostics.ProcessStartInfo]::new($Exe)
        foreach($v in @('pipe-client',$pipeName,($request|ConvertTo-Json -Compress))){$si.ArgumentList.Add($v)}
        $si.UseShellExecute=$false;$si.CreateNoWindow=$true;$si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true
        return [Diagnostics.Process]::Start($si)
    }
    function Finish-PipeCall($p){
        try{
            $output=$p.StandardOutput.ReadToEndAsync();$err=$p.StandardError.ReadToEndAsync()
            if(-not $p.WaitForExit(8000)){throw 'PIPE_CLIENT_TIMEOUT'}
            if($p.ExitCode -ne 0){throw "PIPE_CLIENT_FAILED $($err.GetAwaiter().GetResult())"}
            return ($output.GetAwaiter().GetResult()|ConvertFrom-Json)
        }finally{if(-not $p.HasExited){$p.Kill($true)};$p.Dispose()}
    }
    function PipeCall($r){return (Finish-PipeCall (Start-PipeCall $r))}
    $first=PipeCall @{action='submit';key='first';delayMs=8000}
    Assert ($first.status -eq 'OK') 'ipc.first_admitted'
    $running=$false
    for($i=0;$i -lt 15;$i++){
        $f=PipeCall @{action='status';key='first'}
        if($f.state -eq 'RUNNING'){$running=$true;break}
    }
    Assert $running 'ipc.first_running'
    $clock=[Diagnostics.Stopwatch]::StartNew()
    $p1=Start-PipeCall @{action='submit';key='second';delayMs=20}
    $p2=Start-PipeCall @{action='submit';key='second';delayMs=20}
    $p3=Start-PipeCall @{action='status';key='first'}
    $second=Finish-PipeCall $p1
    $repeat=Finish-PipeCall $p2
    $status=Finish-PipeCall $p3
    $clock.Stop()
    Assert ($second.status -eq 'OK' -and $repeat.id -eq $second.id) 'ipc.concurrent_duplicate_single_job'
    Assert ($status.id -eq $first.id -and $status.state -eq 'RUNNING' -and $clock.ElapsedMilliseconds -lt 4000) 'ipc.status_independent_of_long_job'
    Write-Host "IPC_CONCURRENT_PROCESS_ROUNDTRIP_MS=$($clock.ElapsedMilliseconds)"
    $conflict=PipeCall @{action='submit';key='second';delayMs=21}
    Assert ($conflict.status -eq 'CONFLICT') 'ipc.idempotency_payload_conflict'
    $third=PipeCall @{action='submit';key='third';delayMs=20}
    $fourth=PipeCall @{action='submit';key='fourth';delayMs=20}
    $overflow=PipeCall @{action='submit';key='overflow';delayMs=20}
    Assert ($third.status -eq 'OK' -and $fourth.status -eq 'OK' -and $overflow.status -eq 'BUSY') 'ipc.bounded_admission'
    $cancel=PipeCall @{action='cancel';key='third'}
    Assert ($cancel.state -eq 'CANCELLED') 'ipc.queued_cancel_during_long_job'
    $final=$null
    $deadline=[Diagnostics.Stopwatch]::StartNew()
    while($deadline.Elapsed.TotalSeconds -lt 15){
        $final=PipeCall @{action='status';key='fourth'}
        if($final.state -eq 'COMPLETED'){break}
        Start-Sleep -Milliseconds 100
    }
    $firstDone=PipeCall @{action='status';key='first'}
    $secondDone=PipeCall @{action='status';key='second'}
    $thirdDone=PipeCall @{action='status';key='third'}
    Assert ($firstDone.result -eq 'fixture:first' -and $secondDone.result -eq 'fixture:second' -and $thirdDone.state -eq 'CANCELLED' -and $final.result -eq 'fixture:fourth') 'ipc.reconnect_distinct_results_queue_drained'
    & (Join-Path $PSScriptRoot 'Test-McpShared.ps1') -Dll $SdkDll -Pipe $pipeName
    Stop-Service $name
    $s=Wait-State 'STOPPED' 1
    Assert ($s.checkpoint -eq 1) 'scm.graceful_stop'
    Start-Service $name
    $b=Wait-State 'RUNNING' 2
    Assert ($b.checkpoint -eq 1) 'sqlite.committed_checkpoint_not_repeated'
    $persisted=PipeCall @{action='submit';key='second';delayMs=20}
    Assert ($persisted.id -eq $second.id -and $persisted.state -eq 'COMPLETED') 'ipc.idempotency_survives_service_restart'
    $p=[Diagnostics.ProcessStartInfo]::new($Exe)
    $p.ArgumentList.Add('crash-write');$p.ArgumentList.Add($db);$p.UseShellExecute=$false;$p.CreateNoWindow=$true
    $writer=[Diagnostics.Process]::Start($p)
    try{if(-not $writer.WaitForExit(10000)){throw 'CRASH_WRITER_TIMEOUT'};Assert ($writer.ExitCode -ne 0) 'sqlite.writer_abrupt_exit'}finally{if(-not $writer.HasExited){$writer.Kill()};$writer.Dispose()}
    $after=Read-State
    Assert ($after.checkpoint -eq 1) 'sqlite.uncommitted_checkpoint_not_visible'
    $svc=Get-CimInstance Win32_Service -Filter "Name='$name'"
    Assert ($svc.ProcessId -gt 0) 'scm.live_process'
    Stop-Process -Id $svc.ProcessId -Force
    (Get-Service $name).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(20))
    Start-Service $name
    $c=Wait-State 'NEEDS_RECONCILIATION' 3
    Assert ($c.checkpoint -eq 1) 'scm.crash_restart_unknown_effect_not_replayed'
    Stop-Service $name
    $d=Read-State
    Assert ($d.state -eq 'NEEDS_RECONCILIATION') 'unknown_effect_survives_graceful_stop'
    Write-Host 'R0_NATIVE_MECHANISMS=PASS (no actual reboot or power-cut)'
} finally {
    $svc=Get-Service -Name $name -ErrorAction SilentlyContinue
    if($svc){if($svc.Status -ne 'Stopped'){Stop-Service $name -Force -ErrorAction SilentlyContinue};sc.exe delete $name|Out-Host}
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
