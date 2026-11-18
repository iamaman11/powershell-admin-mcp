# Read-only / inert physical smoke check of the staged native application.
# Run in the authorized elevated interactive Windows session. Optional resource
# checks make read-only requests using existing CLI authorization, never print
# credentials. No install, service stop, reboot, or image persistence.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Exe,[switch]$CheckUserResources)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$Exe=(Resolve-Path -LiteralPath $Exe).Path
$svc=Get-CimInstance Win32_Service -Filter "Name='WindowsAdminMCP'"
if($null -eq $svc -or $svc.State -ne 'Running' -or $svc.PathName -cne ('"'+$Exe+'" service')){
    throw 'CANDIDATE_SERVICE_IDENTITY_MISMATCH'
}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$ownerSid=$identity.User.Value
$ownerSession=[Diagnostics.Process]::GetCurrentProcess().SessionId
if($identity.IsSystem -or $ownerSession -eq 0 -or -not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    throw 'AUTHORIZED_INTERACTIVE_ADMIN_REQUIRED'
}
$expectedPid=[int]$svc.ProcessId
$expectedExe=(Get-Process -Id $expectedPid -ErrorAction Stop).Path
if($expectedExe -ine $Exe){throw 'CANDIDATE_PROCESS_PATH_MISMATCH'}
$clients=[Collections.Generic.List[Diagnostics.Process]]::new()
$meta=@{'io.modelcontextprotocol/protocolVersion'='2026-07-28';'io.modelcontextprotocol/clientCapabilities'=@{};'io.modelcontextprotocol/clientInfo'=@{name='installed-native-smoke';version='1'}}
function Assert($ok,[string]$label){if(-not $ok){throw "FAIL $label"};Write-Host "PASS $label"}
function Start-Frontend {
    $si=[Diagnostics.ProcessStartInfo]::new($Exe)
    $si.ArgumentList.Add('stdio')
    $si.UseShellExecute=$false;$si.RedirectStandardInput=$true;$si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true
    $si.StandardInputEncoding=[Text.UTF8Encoding]::new($false)
    $si.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
    $p=[Diagnostics.Process]::Start($si);$clients.Add($p)
    $null=$p.StandardError.ReadToEndAsync()
    return $p
}
function Send($p,$message){
    $p.StandardInput.WriteLine(($message|ConvertTo-Json -Depth 16 -Compress));$p.StandardInput.Flush()
}
function Call($p,[int]$id,[string]$method,$params){
    Send $p @{jsonrpc='2.0';id=$id;method=$method;params=$params}
    $clock=[Diagnostics.Stopwatch]::StartNew()
    do {
        $line=$p.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(15)).GetAwaiter().GetResult()
        if($null -eq $line){throw "RPC_EOF $method"}
        $msg=$line|ConvertFrom-Json -AsHashtable
        if($msg.Contains('id') -and $msg.id -eq $id){
            if($msg.Contains('error')){throw "RPC_ERROR $method code=$($msg.error.code)"}
            return $msg.result
        }
    }while($clock.Elapsed.TotalSeconds -lt 15)
    throw "RPC_TIMEOUT $method"
}
function Tool($p,$id,$name,$arguments,$modern=$true) {
    $params=@{name=$name;arguments=$arguments}
    if($modern){$params._meta=$meta}
    return Call $p $id 'tools/call' $params
}
try {
    $a=Start-Frontend;$b=Start-Frontend;$c=Start-Frontend
    $i=Call $a 1 'initialize' @{protocolVersion='2025-06-18';capabilities=@{};clientInfo=@{name='installed-native-smoke';version='1'}}
    Assert ($i.protocolVersion -eq '2025-06-18') 'installed.legacy'
    Send $a @{jsonrpc='2.0';method='notifications/initialized'}
    foreach($p in @($b,$c)){
        $d=Call $p 1 'server/discover' @{_meta=$meta}
        Assert ('2026-07-28' -in $d.supportedVersions) 'installed.modern'
    }
    foreach($entry in @(@{p=$a;modern=$false},@{p=$b;modern=$true},@{p=$c;modern=$true})){
        $params=@{};if($entry.modern){$params._meta=$meta}
        $list=Call $entry.p 2 'tools/list' $params
        Assert (@($list.tools).Count -eq 5 -and @($list.tools.name|Where-Object {$_ -in @('powershell_status','powershell_exec','powershell_job','powershell_screenshot','windows.list_windows')}).Count -eq 5) 'installed.window_catalog_tools'
        $status=(Tool $entry.p 3 'powershell_status' @{} $entry.modern).structuredContent
        Assert ($status.status -eq 'OK' -and $status.service_pid -eq $expectedPid -and $status.execution_sid -eq 'S-1-5-18') 'installed.shared_service'
    }
    $key='physical-'+[guid]::NewGuid().ToString('N')
    $identityCommand=@'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
[pscustomobject]@{sid=$identity.User.Value;session=[Diagnostics.Process]::GetCurrentProcess().SessionId;profile=$env:USERPROFILE;hkcu=(Test-Path 'HKCU:\Software');elevated=([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)}|ConvertTo-Json -Compress
'@
    $presence=[Diagnostics.Stopwatch]::StartNew();$presenceId=800
    do{
        $userStatus=(Tool $b $presenceId 'powershell_status' @{}).structuredContent;$presenceId++
        if($userStatus.user_context_available -and $userStatus.command_context -eq 'USER'){break}
        Start-Sleep -Milliseconds 250
    }while($presence.Elapsed.TotalSeconds -lt 15)
    Assert ($userStatus.command_context -eq 'USER' -and $userStatus.user_context_available -and
        $userStatus.frontend.sid -eq $ownerSid -and $userStatus.frontend.session_id -eq $ownerSession) 'installed.actual_user_runner_available'
    $user=(Tool $b 31 'powershell_exec' @{operation_key=$key+'-user';command=$identityCommand;timeout_seconds=10}).structuredContent
    Assert ($user.status -eq 'COMPLETED' -and $user.audit_persisted -and $user.execution_context -eq 'USER' -and $user.execution_sid -eq $ownerSid) 'installed.default_user_execution'
    $actual=$user.stdout|ConvertFrom-Json
    Assert ($actual.sid -eq $ownerSid -and $actual.session -eq $ownerSession -and $actual.profile -eq $env:USERPROFILE -and $actual.hkcu -and $actual.elevated) 'installed.real_user_profile_hkcu_elevation'
    if($CheckUserResources){
        $probe=@'
$PSNativeCommandUseErrorActionPreference=$false
$result=[ordered]@{gh_auth_exit=$null;gh_api_exit=$null;wsl_list_exit=$null;docker_context_exit=$null}
$g=Get-Command gh.exe -ErrorAction SilentlyContinue
if($g){& $g.Source auth status --hostname github.com *> $null;$result.gh_auth_exit=$LASTEXITCODE;if($LASTEXITCODE -eq 0){& $g.Source api user --silent *> $null;$result.gh_api_exit=$LASTEXITCODE}}
$w=Get-Command wsl.exe -ErrorAction SilentlyContinue
if($w){& $w.Source --list --quiet *> $null;$result.wsl_list_exit=$LASTEXITCODE}
$d=Get-Command docker.exe -ErrorAction SilentlyContinue
if($d){& $d.Source context show *> $null;$result.docker_context_exit=$LASTEXITCODE}
$result|ConvertTo-Json -Compress
exit 0
'@
        $resourceJob=(Tool $b 32 'powershell_job' @{action='submit';operation_key=$key+'-resources';command=$probe;timeout_seconds=60;execution_context='USER'}).structuredContent
        Assert ($resourceJob.status -eq 'OK' -and $resourceJob.job.job_id) 'installed.user_resource_admission'
        $watch=[Diagnostics.Stopwatch]::StartNew();$id=400
        do{
            $resourceResult=(Tool $c $id 'powershell_job' @{action='logs';job_id=$resourceJob.job.job_id}).structuredContent;$id++
            if($resourceResult.job.state -eq 'COMPLETED'){break}
            if($resourceResult.job.state -notin @('RUNNING','QUEUED')){throw 'USER_RESOURCE_JOB_FAILED'}
            Start-Sleep -Milliseconds 250
        }while($watch.Elapsed.TotalSeconds -lt 70)
        Assert ($resourceResult.job.state -eq 'COMPLETED' -and $resourceResult.job.execution.audit_persisted) 'installed.user_resource_result'
        $observed=$resourceResult.job.execution.stdout|ConvertFrom-Json
        Write-Host ('USER_RESOURCE_READBACK '+($observed|ConvertTo-Json -Compress))
        Assert ($null -ne $observed.gh_auth_exit -and $observed.gh_auth_exit -eq 0 -and $null -ne $observed.gh_api_exit -and $observed.gh_api_exit -eq 0) 'installed.existing_github_cli_authorization'
        Assert ($null -ne $observed.wsl_list_exit -and $observed.wsl_list_exit -eq 0) 'installed.user_wsl_list'
        Assert ($null -ne $observed.docker_context_exit -and $observed.docker_context_exit -eq 0) 'installed.user_docker_context'
    }
    # Two independent USER trees run at once; a SYSTEM command must finish
    # before either 20-second job, proving no global execution gate.
    $parallel=@()
    try{
        for($n=0;$n -lt 2;$n++){
            $client=@($b,$c)[$n]
            $held=(Tool $client (40+$n) 'powershell_job' @{action='submit';operation_key=$key+'-overlap-'+$n;command="Start-Sleep -Seconds 20; Write-Output 'USER-OVERLAP-$n'";timeout_seconds=35;execution_context='USER'}).structuredContent
            Assert ($held.status -eq 'OK' -and $held.job.job_id) ('installed.user_parallel_admission.'+$n)
            $parallel+=@{id=$held.job.job_id;marker=('USER-OVERLAP-'+$n)}
        }
        $watch=[Diagnostics.Stopwatch]::StartNew();$id=500
        do{
            $states=@(foreach($job in $parallel){
                $v=(Tool $c $id 'powershell_job' @{action='status';job_id=$job.id}).structuredContent;$id++
                $v.job.state
            })
            if(@($states|Where-Object {$_ -eq 'RUNNING'}).Count -eq 2){break}
            Start-Sleep -Milliseconds 150
        }while($watch.Elapsed.TotalSeconds -lt 10)
        Assert (@($states|Where-Object {$_ -eq 'RUNNING'}).Count -eq 2) 'installed.two_user_jobs_running'
        $fast=(Tool $a 42 'powershell_exec' @{operation_key=$key+'-parallel-system';command="Write-Output 'SYSTEM-OVERLAP'";timeout_seconds=8;execution_context='SYSTEM'} $false).structuredContent
        Assert ($fast.status -eq 'COMPLETED' -and $fast.stdout.Trim() -ceq 'SYSTEM-OVERLAP' -and $fast.execution_context -eq 'SYSTEM') 'installed.independent_system_exec'
        foreach($job in $parallel){
            $v=(Tool $b $id 'powershell_job' @{action='status';job_id=$job.id}).structuredContent;$id++
            Assert ($v.job.state -eq 'RUNNING') 'installed.short_finished_while_user_job_running'
        }
        foreach($job in $parallel){
            $watch=[Diagnostics.Stopwatch]::StartNew()
            do{
                $v=(Tool $c $id 'powershell_job' @{action='logs';job_id=$job.id}).structuredContent;$id++
                if($v.job.state -eq 'COMPLETED'){break}
                if($v.job.state -notin @('RUNNING','QUEUED')){throw 'USER_OVERLAP_FAILED'}
                Start-Sleep -Milliseconds 250
            }while($watch.Elapsed.TotalSeconds -lt 40)
            Assert ($v.job.state -eq 'COMPLETED' -and $v.job.execution.audit_persisted -and $v.job.execution.execution_sid -eq $ownerSid -and $v.job.execution.stdout.Trim() -ceq $job.marker) 'installed.separate_user_results'
        }
    }finally{
        # Only the fixture's own IDs; never cancel any unrelated job.
        $cleanupId=700
        foreach($job in $parallel){
            try{
                $v=(Tool $b $cleanupId 'powershell_job' @{action='status';job_id=$job.id}).structuredContent;$cleanupId++
                if($v.job.state -in @('QUEUED','RUNNING')){$null=Tool $b $cleanupId 'powershell_job' @{action='cancel';job_id=$job.id};$cleanupId++}
            }catch{Write-Warning ('Fixture cleanup readback failed for job '+$job.id)}
        }
    }
    $exec=(Tool $a 4 'powershell_exec' @{command="Write-Output 'native Здравствуй'; [Security.Principal.WindowsIdentity]::GetCurrent().User.Value";timeout_seconds=10;operation_key=$key;execution_context='SYSTEM'} $false).structuredContent
    Assert ($exec.status -eq 'COMPLETED' -and $exec.stdout.Contains('native Здравствуй') -and $exec.stdout.Contains('S-1-5-18')) 'installed.real_exec'
    $jobKey=$key+'-job'
    $jobArgs=@{action='submit';operation_key=$jobKey;command="Start-Sleep -Seconds 2; Write-Output 'native-job'";timeout_seconds=10;execution_context='SYSTEM'}
    $job=(Tool $b 4 'powershell_job' $jobArgs).structuredContent
    Assert ($job.status -eq 'OK' -and $job.job.job_id) 'installed.durable_submit'
    $dup=(Tool $c 4 'powershell_job' $jobArgs).structuredContent
    Assert ($dup.status -eq 'OK' -and $dup.job.job_id -eq $job.job.job_id) 'installed.shared_idempotency'
    $a.Kill($true);$a.WaitForExit()
    $clock=[Diagnostics.Stopwatch]::StartNew();$id=100
    do{
        $result=(Tool $c $id 'powershell_job' @{action='logs';job_id=$job.job.job_id}).structuredContent
        $id++
        if($result.job.state -eq 'COMPLETED'){break}
        Start-Sleep -Milliseconds 150
    }while($clock.Elapsed.TotalSeconds -lt 10)
    Assert ($result.job.state -eq 'COMPLETED' -and $result.job.execution.stdout.Trim() -ceq 'native-job') 'installed.durable_result'
    # Deliberately tiny region. Never print or save actual screen pixels.
    $shot=Tool $b 5 'powershell_screenshot' @{monitor=0;x=0;y=0;width=16;height=16;max_width=256}
    $descriptor=$shot.structuredContent
    if($descriptor.status -in @('INTERACTIVE_DESKTOP_UNAVAILABLE','SESSION_ZERO','NON_INTERACTIVE_SESSION','NOT_PHYSICAL_CONSOLE','INPUT_DESKTOP_ACCESS_FAILED','INPUT_DESKTOP_NOT_DEFAULT','THREAD_DESKTOP_NOT_DEFAULT')){
        Write-Host 'NOT_TESTED installed.real_png (interactive desktop unavailable)'
    }else{
        Assert ($descriptor.status -eq 'COMPLETED') 'installed.capture_status'
        $images=@($shot.content|Where-Object {$_.type -eq 'image'})
        Assert ($images.Count -eq 1 -and $images[0].mimeType -eq 'image/png') 'installed.png_block'
        $png=[Convert]::FromBase64String($images[0].data)
        Assert ($png.Length -eq $descriptor.bytes -and $png.Length -gt 24 -and [Convert]::ToHexString($png[0..7]) -eq '89504E470D0A1A0A' -and $descriptor.width -eq 16 -and $descriptor.height -eq 16) 'installed.real_png'
        [Array]::Clear($png,0,$png.Length)
        $shot=$null;$images=$null
    }
    Write-Host 'LOCAL_CANDIDATE_SMOKE_FINISHED (raw SDK clients, not ChatGPT/Codex tunnel)'
}finally{
    foreach($p in $clients){
        if(-not $p.HasExited){$p.StandardInput.Close();if(-not $p.WaitForExit(3000)){$p.Kill($true);$p.WaitForExit()}}
        $p.Dispose()
    }
}
