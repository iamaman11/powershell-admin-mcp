param([Parameter(Mandatory)][string]$Exe)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest

# This fixture is destructive only to its own fresh Windows CI SCM service and
# ProgramData store. It must never be run on an installed user's machine.
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows'){
    throw 'ISOLATED_WINDOWS_CI_ONLY'
}
$Exe=[IO.Path]::GetFullPath($Exe)
if(-not(Test-Path -LiteralPath $Exe -PathType Leaf)){throw 'NATIVE_EXECUTABLE_NOT_FOUND'}
$name='WindowsAdminMCP'
$data=Join-Path $env:ProgramData 'WindowsAdminMCP.Data'
if((Get-Service $name -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath $data)){
    throw 'ISOLATED_TEST_STATE_ALREADY_EXISTS'
}
$clients=[Collections.Generic.List[Diagnostics.Process]]::new()
$meta=@{'io.modelcontextprotocol/protocolVersion'='2026-07-28';'io.modelcontextprotocol/clientCapabilities'=@{};'io.modelcontextprotocol/clientInfo'=@{name='lost-ack-fixture';version='1'}}

function Assert($condition,[string]$name){
    if(-not $condition){throw "FAIL $name"}
    Write-Host "PASS $name"
}
function Connect {
    $start=[Diagnostics.ProcessStartInfo]::new($Exe)
    $start.ArgumentList.Add('stdio')
    $start.Environment['R0_SERVICE_PID']='1'
    $start.Environment['R0_PIPE']='not-trusted'
    $start.UseShellExecute=$false
    $start.RedirectStandardInput=$true
    $start.RedirectStandardOutput=$true
    $start.RedirectStandardError=$true
    $process=[Diagnostics.Process]::Start($start)
    $clients.Add($process)
    $null=$process.StandardError.ReadToEndAsync()
    return $process
}
function Send($process,[int]$id,[string]$name,[hashtable]$arguments){
    $request=@{jsonrpc='2.0';id=$id;method='tools/call';params=@{name=$name;arguments=$arguments;_meta=$meta}}
    $process.StandardInput.WriteLine(($request|ConvertTo-Json -Depth 15 -Compress))
    $process.StandardInput.Flush()
}
function Receive($process,[int]$expectedId,[int]$seconds=12){
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while($watch.Elapsed.TotalSeconds -lt $seconds){
        $remaining=[TimeSpan]::FromSeconds($seconds)-$watch.Elapsed
        $line=$process.StandardOutput.ReadLineAsync().WaitAsync($remaining).GetAwaiter().GetResult()
        if($null -eq $line){throw 'UNEXPECTED_FRONTEND_EOF'}
        $response=$line|ConvertFrom-Json -AsHashtable
        if($response.id -ne $expectedId){continue}
        if($response.Contains('error') -or
            ($response.result.Contains('isError') -and $response.result.isError)){
            throw "MCP_TOOL_ERROR $line"
        }
        return $response.result.structuredContent
    }
    throw "FRONTEND_RESPONSE_TIMEOUT $expectedId"
}
function Call($process,[int]$id,[string]$name,[hashtable]$arguments){
    Send $process $id $name $arguments
    return (Receive $process $id)
}
function Wait-Job($process,[string]$id,[string]$state){
    $watch=[Diagnostics.Stopwatch]::StartNew()
    do{
        $result=Call $process 5001 'powershell_job' @{action='logs';job_id=$id}
        if($result.job.state -eq $state){return $result.job}
        Start-Sleep -Milliseconds 100
    }while($watch.Elapsed.TotalSeconds -lt 20)
    throw "JOB_STATE_TIMEOUT $state"
}

try{
    New-Service -Name $name -BinaryPathName ('"'+$Exe+'" service') -StartupType Automatic|Out-Null
    Start-Service $name
    $sender=Connect
    $observer=Connect
    $watch=[Diagnostics.Stopwatch]::StartNew()
    do{
        $status=Call $observer 1001 'powershell_status' @{}
        if($status.status -eq 'OK'){break}
        Start-Sleep -Milliseconds 100
    }while($watch.Elapsed.TotalSeconds -lt 10)
    Assert ($status.status -eq 'OK') 'lost_ack.actual_service_ready'

    $marker=Join-Path $data 'lost-ack-side-effect.txt'
    $path=$marker.Replace("'","''")
    $command="Start-Sleep -Milliseconds 800; [IO.File]::AppendAllText('$path','X'); Write-Output 'ACK_LOST_EFFECT'"
    $key='lost-ack-'+[Guid]::NewGuid().ToString('N')
    $request=@{action='submit';operation_key=$key;command=$command;timeout_seconds=15;execution_context='SYSTEM'}

    # Discard the entire caller-side answer: do not consume this process's
    # stdout at any point. Independently prove the server did admit the job.
    Send $sender 2001 'powershell_job' $request
    $admitted=$null
    $watch=[Diagnostics.Stopwatch]::StartNew()
    do{
        $list=Call $observer 2002 'powershell_job' @{action='list';page_size=100}
        $admitted=@($list.jobs|Where-Object {$_.operation_key -ceq $key})|Select-Object -First 1
        if($null -ne $admitted){break}
        Start-Sleep -Milliseconds 100
    }while($watch.Elapsed.TotalSeconds -lt 10)
    Assert ($null -ne $admitted -and $admitted.job_id) 'lost_ack.admission_observed_from_independent_client'
    $sender.Kill($true)
    $sender.WaitForExit()
    Assert ($sender.HasExited) 'lost_ack.frontend_disconnected_before_reading_reply'

    $retry=Call $observer 2003 'powershell_job' $request
    Assert ($retry.status -eq 'OK' -and $retry.job.job_id -ceq $admitted.job_id) 'lost_ack.exact_same_key_returns_durable_job'
    $conflict=Call $observer 2004 'powershell_job' @{action='submit';operation_key=$key;command="Write-Output 'CONFLICT'";timeout_seconds=15;execution_context='SYSTEM'}
    Assert ($conflict.status -eq 'CONFLICT' -and $conflict.job.job_id -ceq $admitted.job_id) 'lost_ack.changed_payload_rejected'
    $done=Wait-Job $observer $admitted.job_id 'COMPLETED'
    Assert ($done.execution.stdout.Trim() -ceq 'ACK_LOST_EFFECT' -and
        (Test-Path -LiteralPath $marker -PathType Leaf) -and
        [IO.File]::ReadAllText($marker) -ceq 'X') 'lost_ack.effect_exactly_once_after_lost_reply'

    Stop-Service $name
    Start-Service $name
    $watch=[Diagnostics.Stopwatch]::StartNew()
    do{
        $status=Call $observer 2010 'powershell_status' @{}
        if($status.status -eq 'OK'){break}
        Start-Sleep -Milliseconds 100
    }while($watch.Elapsed.TotalSeconds -lt 10)
    Assert ($status.status -eq 'OK') 'lost_ack.actual_service_restart'
    $again=Call $observer 2011 'powershell_job' $request
    Assert ($again.status -eq 'OK' -and $again.job.job_id -ceq $admitted.job_id -and
        $again.job.state -eq 'COMPLETED' -and [IO.File]::ReadAllText($marker) -ceq 'X') 'lost_ack.no_replay_across_service_restart'
    Write-Host 'R4_LOST_ACK_FAIL_CLOSED=PASS (two independent MCP clients, real SCM, exactly-once fixture; no reboot)'
}finally{
    foreach($process in $clients){
        if(-not $process.HasExited){
            $process.StandardInput.Close()
            if(-not $process.WaitForExit(3000)){$process.Kill($true);$process.WaitForExit()}
        }
        $process.Dispose()
    }
    if(Get-Service $name -ErrorAction SilentlyContinue){
        Stop-Service $name -ErrorAction SilentlyContinue
        sc.exe delete $name|Out-Host
    }
    if(Test-Path -LiteralPath $data){Remove-Item -LiteralPath $data -Recurse -Force}
}
