param([Parameter(Mandatory)][string]$Package,[Parameter(Mandatory)][string]$Sha256)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows'){throw 'CI_ONLY'}
. (Join-Path $PSScriptRoot '../installer/NativeFileSwap.ps1')
$installer=Join-Path $PSScriptRoot '../installer/Install-Native.ps1'
$root=Join-Path $env:ProgramFiles 'WindowsAdminMCP'
$data=Join-Path $env:ProgramData 'WindowsAdminMCP.Data'
if(Test-Path -LiteralPath $data){throw 'FIXTURE_DATA_EXISTS'}
if(Test-Path -LiteralPath $root){throw 'FIXTURE_ROOT_EXISTS'}
if(Get-Service WindowsAdminMCP -ErrorAction SilentlyContinue){throw 'FIXTURE_SERVICE_EXISTS'}
function Check($ok,$name){if(-not $ok){throw "FAIL $name"};Write-Host "PASS $name"}
$verified=& $installer -PackagePath $Package -ExpectedSha256 $Sha256|ConvertFrom-Json
Check ($verified.status -eq 'VERIFIED' -and -not(Test-Path -LiteralPath $root)) 'r6.package_verify_no_install'
$rejected=$false
try{& $installer -PackagePath $Package -ExpectedSha256 ('0'*64)|Out-Null}catch{if($_.Exception.Message -eq 'PACKAGE_SHA256_MISMATCH'){$rejected=$true}else{throw}}
Check $rejected 'r6.wrong_package_digest_rejected'
$fixture=Join-Path $env:RUNNER_TEMP ('swap-'+[guid]::NewGuid().ToString('N'))
$old=Join-Path $fixture 'root';$new=Join-Path $fixture 'incoming'
try{
    foreach($dir in @($old,$new)){
        foreach($name in @('native','client')){[void][IO.Directory]::CreateDirectory((Join-Path $dir $name))}
        [IO.File]::WriteAllText((Join-Path $dir 'manifest.json'),$dir)
    }
    [IO.File]::WriteAllText((Join-Path $old 'client\old.txt'),'old')
    [IO.File]::WriteAllText((Join-Path $new 'client\new.txt'),'new')
    [IO.File]::WriteAllText((Join-Path $old 'sentinel.txt'),'keep')
    $rolled=$false
    try{Invoke-NativeFileSwap -Root $old -Incoming $new -Validate {throw 'FIXTURE_VALIDATION_FAILED'}}catch{if($_.Exception.Message -eq 'FIXTURE_VALIDATION_FAILED'){$rolled=$true}else{throw}}
    Check ($rolled -and (Test-Path (Join-Path $old 'client\old.txt')) -and -not(Test-Path (Join-Path $old 'client\new.txt')) -and -not(Test-Path (Join-Path $old '.rollback')) -and (Get-Content (Join-Path $old 'sentinel.txt') -Raw) -eq 'keep') 'r6.actual_file_swap_rollback'

    # Genuine process exit after directory swaps: kill a separate PowerShell
    # in the validation callback before any exception rollback can execute.
    foreach($name in @('native','client')){[void][IO.Directory]::CreateDirectory((Join-Path $new $name))}
    [IO.File]::WriteAllText((Join-Path $new 'manifest.json'),'replacement')
    [IO.File]::WriteAllText((Join-Path $new 'client\new.txt'),'next')
    $marker=Join-Path $fixture 'entered'
    $childScript=Join-Path $fixture 'interrupted.ps1'
    $source=@'
param($Helper,$Root,$Incoming,$Marker)
$ErrorActionPreference='Stop'
. $Helper
Invoke-NativeFileSwap -Root $Root -Incoming $Incoming -ServiceExisted $true -Validate {
    [IO.File]::WriteAllText($Marker,'entered')
    Start-Sleep -Seconds 120
}
'@
    [IO.File]::WriteAllText($childScript,$source)
    $si=[Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
    $si.UseShellExecute=$false
    foreach($arg in @('-NoProfile','-NonInteractive','-File',$childScript,(Join-Path $PSScriptRoot '../installer/NativeFileSwap.ps1'),$old,$new,$marker)){$si.ArgumentList.Add($arg)}
    $p=[Diagnostics.Process]::Start($si)
    try{
        $clock=[Diagnostics.Stopwatch]::StartNew()
        while(-not(Test-Path -LiteralPath $marker) -and -not $p.HasExited -and $clock.Elapsed.TotalSeconds -lt 10){Start-Sleep -Milliseconds 50}
        Check (Test-Path -LiteralPath $marker) 'r6.interruption_after_swap_reached'
        $p.Kill();if(-not $p.WaitForExit(5000)){throw 'CHILD_NOT_TERMINATED'}
        $j=Get-NativeSwapJournal $old
        Check ($j.service_existed -and $j.original.Count -eq 3) 'r6.interrupted_journal_persists'
        Restore-NativeFileSwap -Root $old
        Check ((Test-Path (Join-Path $old 'client\old.txt')) -and -not(Test-Path (Join-Path $old 'client\new.txt')) -and -not(Test-Path (Join-Path $old '.rollback'))) 'r6.recover_after_process_kill'
    }finally{if(-not $p.HasExited){$p.Kill();$p.WaitForExit()};$p.Dispose()}
}finally{Remove-Item -LiteralPath $fixture -Recurse -Force}
try{
    $result=& $installer -Action Install -PackagePath $Package -ExpectedSha256 $Sha256|ConvertFrom-Json
    $svc=Get-CimInstance Win32_Service -Filter "Name='WindowsAdminMCP'"
    Check ($result.status -eq 'INSTALLED_OFFLINE' -and $svc.StartMode -eq 'Manual' -and $svc.State -eq 'Stopped' -and $svc.StartName -eq 'LocalSystem') 'r6.actual_offline_install'
    Check ((Test-Path (Join-Path $root 'client\tunnel-client-runtime.exe')) -and (Test-Path (Join-Path $root 'native\WindowsAdminMCP.exe')) -and (Test-Path (Join-Path $root '.rollback'))) 'r6.stable_install_paths_and_backup'
    Check (-not(Get-ScheduledTask -TaskName 'WindowsAdminMCP-Tunnel' -ErrorAction SilentlyContinue)) 'r6.no_unaccepted_autostart'
    $repeat=& $installer -Action Install -PackagePath $Package -ExpectedSha256 $Sha256|ConvertFrom-Json
    Check ($repeat.status -eq 'NOOP' -and $repeat.rollback_retained) 'r6.repeat_install_keeps_rollback'
    # An idle connected stdio frontend holds native DLLs on Windows. Refuse
    # before the first file rename; never kill a client's process in the installer.
    $frontendInfo=[Diagnostics.ProcessStartInfo]::new((Join-Path $root 'native\WindowsAdminMCP.exe'))
    $frontendInfo.ArgumentList.Add('stdio');$frontendInfo.UseShellExecute=$false
    $frontendInfo.RedirectStandardInput=$true;$frontendInfo.RedirectStandardOutput=$true;$frontendInfo.RedirectStandardError=$true
    $frontend=[Diagnostics.Process]::Start($frontendInfo)
    $null=$frontend.StandardError.ReadToEndAsync()
    try{
        $blocked=$false
        try{& $installer -Action Install -PackagePath $Package -ExpectedSha256 $Sha256|Out-Null}catch{if($_.Exception.Message -eq 'ACTIVE_FRONTEND_MAINTENANCE_REQUIRED'){$blocked=$true}else{throw}}
        Check ($blocked -and (Test-Path (Join-Path $root 'native\WindowsAdminMCP.exe')) -and (Get-Content (Join-Path $root 'manifest.json') -Raw|ConvertFrom-Json).commit -eq $result.commit) 'r6.idle_frontend_blocks_before_swap'
    }finally{$frontend.StandardInput.Close();if(-not $frontend.WaitForExit(3000)){$frontend.Kill($true);$frontend.WaitForExit()};$frontend.Dispose()}
    $before=@(Get-ChildItem -LiteralPath $env:ProgramFiles -Directory -Filter 'WindowsAdminMCP.incoming-*').Count
    $null=& $installer -PackagePath $Package -ExpectedSha256 $Sha256
    $after=@(Get-ChildItem -LiteralPath $env:ProgramFiles -Directory -Filter 'WindowsAdminMCP.incoming-*').Count
    Check ($before -eq $after) 'r6.verify_cleans_temporary_tree'
    $blocked=$false
    try{& $installer -Action Accept -ExpectedCommit $result.commit|Out-Null}catch{if($_.Exception.Message -eq 'REMOTE_ACCEPTANCE_REQUIRED'){$blocked=$true}else{throw}}
    Check $blocked 'r6.accept_requires_explicit_remote_confirmation'
    $prepare=& $installer -Action Prepare -ExpectedCommit $result.commit|ConvertFrom-Json
    $task=Get-ScheduledTask -TaskName 'WindowsAdminMCP-Tunnel'
    Check ($prepare.status -eq 'TRIAL_PREPARED' -and -not $prepare.logon_enabled -and -not $task.Triggers[0].Enabled -and $task.State.ToString() -ne 'Running' -and $prepare.service_start -eq 'Manual') 'r6.actual_task_demand_only'
    $repeatPrepare=& $installer -Action Prepare -ExpectedCommit $result.commit|ConvertFrom-Json
    Check ($repeatPrepare.status -eq 'TRIAL_PREPARED' -and -not $repeatPrepare.logon_enabled) 'r6.prepare_idempotent'
    $native=Join-Path $root 'native\WindowsAdminMCP.exe'
    $recovery=& $native maintenance recovery|ConvertFrom-Json
    Check ($LASTEXITCODE -eq 0 -and $recovery.ready -and $recovery.nonCrashFailures -and $recovery.resetSeconds -eq 86400 -and @($recovery.actions).Count -eq 4 -and $recovery.actions[3].type -eq 0) 'r6.scm_recovery_native_readback'
    # Forced crash is confined to this isolated Windows CI fixture, with no jobs.
    Start-Service WindowsAdminMCP
    $crashedPid=(Get-CimInstance Win32_Service -Filter "Name='WindowsAdminMCP'").ProcessId
    Stop-Process -Id $crashedPid -Force
    $watch=[Diagnostics.Stopwatch]::StartNew()
    do{
        $restarted=Get-CimInstance Win32_Service -Filter "Name='WindowsAdminMCP'"
        if($restarted.State -eq 'Running' -and $restarted.ProcessId -ne 0 -and $restarted.ProcessId -ne $crashedPid){break}
        Start-Sleep -Milliseconds 200
    }while($watch.Elapsed.TotalSeconds -lt 20)
    Check ($restarted.State -eq 'Running' -and $restarted.ProcessId -ne $crashedPid -and $restarted.ProcessId -ne 0) 'r6.scm_restarts_after_isolated_crash'
    Stop-Service WindowsAdminMCP
    $blocked=$false
    try{& $installer -Action Accept -ExpectedCommit $result.commit -RemoteAcceptanceConfirmed|Out-Null}catch{if($_.Exception.Message -eq 'TRIAL_TASK_NOT_RUNNING'){$blocked=$true}else{throw}}
    Check ($blocked -and (Test-Path (Join-Path $root '.rollback')) -and -not (Get-ScheduledTask -TaskName 'WindowsAdminMCP-Tunnel').Triggers[0].Enabled) 'r6.operator_attestation_alone_cannot_accept'
    $native=Join-Path $root 'native\WindowsAdminMCP.exe'
    $startup=Join-Path $root 'native\NativeStartup.ps1'
    $state=& $startup -Action Status|ConvertFrom-Json
    Check ($state.definition_valid -and $state.service_binding_valid -and -not $state.startup_ready -and -not $state.logon_enabled) 'r6.startup_readback_demand_only'
    $doctorInfo=[Diagnostics.ProcessStartInfo]::new($native)
    $doctorInfo.ArgumentList.Add('doctor');$doctorInfo.UseShellExecute=$false
    $doctorInfo.RedirectStandardOutput=$true;$doctorInfo.RedirectStandardError=$true
    function Read-Doctor {
        $process=[Diagnostics.Process]::Start($doctorInfo)
        try{
            $out=$process.StandardOutput.ReadToEndAsync();$err=$process.StandardError.ReadToEndAsync()
            if(-not $process.WaitForExit(25000)){throw 'DOCTOR_TIMEOUT'}
            return ($out.GetAwaiter().GetResult()|ConvertFrom-Json)
        }finally{if(-not $process.HasExited){$process.Kill($true);$process.WaitForExit()};$process.Dispose()}
    }
    $doctor=Read-Doctor
    Check ($doctor.package.status -eq 'VERIFIED' -and $doctor.package.commit -eq $result.commit -and $doctor.startup.definitionValid -and -not $doctor.startup.ready) 'r6.doctor_installed_manifest_and_startup'
    $unexpected=Join-Path $root 'native\unexpected-fixture.txt'
    try{
        [IO.File]::WriteAllText($unexpected,'fixture')
        $acl=Get-Acl -LiteralPath $unexpected
        $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
        Set-Acl -LiteralPath $unexpected -AclObject $acl
        $doctor=Read-Doctor
        Check ($doctor.package.status -eq 'FILE_SET_MISMATCH' -and $null -eq $doctor.startup) 'r6.doctor_unexpected_file_blocks_script'
    }finally{Remove-Item -LiteralPath $unexpected -Force}
    & $startup -Action RemoveTrial -ExpectedNativeSha256 (Get-FileHash -LiteralPath $native).Hash|Out-Null
    Check (-not(Get-ScheduledTask -TaskName 'WindowsAdminMCP-Tunnel' -ErrorAction SilentlyContinue)) 'r6.remove_quiescent_trial'
    $rollback=& $installer -Action Rollback|ConvertFrom-Json
    Check ($rollback.status -eq 'ROLLED_BACK_OFFLINE' -and -not(Test-Path (Join-Path $root 'native')) -and -not(Test-Path (Join-Path $root 'client')) -and -not(Test-Path (Join-Path $root '.rollback'))) 'r6.explicit_clean_install_rollback'

    # A separate actual installation/upgrade/rollback exercise. CI only:
    # retire the first install journal and enable stopped startup registrations
    # as a fixture for an accepted baseline, not as remote acceptance evidence.
    $baseline=& $installer -Action Install -PackagePath $Package -ExpectedSha256 $Sha256|ConvertFrom-Json
    $null=& $installer -Action Prepare -ExpectedCommit $baseline.commit
    $task=Get-ScheduledTask -TaskName 'WindowsAdminMCP-Tunnel'
    $trigger=$task.Triggers[0];$trigger.Enabled=$true
    Set-ScheduledTask -TaskName 'WindowsAdminMCP-Tunnel' -Trigger $trigger|Out-Null
    Set-Service WindowsAdminMCP -StartupType Automatic
    Move-NativePath (Join-Path $root '.rollback') (Join-Path $root ('.accepted-backup-'+$baseline.commit))
    Write-Host 'FIXTURE_ONLY accepted baseline registration; no remote acceptance'
    $meta=@{'io.modelcontextprotocol/protocolVersion'='2026-07-28';'io.modelcontextprotocol/clientCapabilities'=@{};'io.modelcontextprotocol/clientInfo'=@{name='package-upgrade-fixture';version='1'}}
    function With-Client([scriptblock]$Body){
        $si=[Diagnostics.ProcessStartInfo]::new((Join-Path $root 'native\WindowsAdminMCP.exe'))
        $si.ArgumentList.Add('stdio');$si.UseShellExecute=$false
        $si.RedirectStandardInput=$true;$si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true
        $p=[Diagnostics.Process]::Start($si)
        $err=$p.StandardError.ReadToEndAsync()
        try{& $Body $p}finally{$p.StandardInput.Close();if(-not $p.WaitForExit(3000)){$p.Kill($true);$p.WaitForExit()};$p.Dispose()}
    }
    function Call-Tool($p,$id,$name,$arguments){
        if($name -eq 'powershell_exec' -or ($name -eq 'powershell_job' -and $arguments.action -eq 'submit')){$arguments.execution_context='SYSTEM'}
        $msg=@{jsonrpc='2.0';id=$id;method='tools/call';params=@{name=$name;arguments=$arguments;_meta=$meta}}
        $p.StandardInput.WriteLine(($msg|ConvertTo-Json -Depth 10 -Compress));$p.StandardInput.Flush()
        do{
            $line=$p.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
            if($null -eq $line){throw 'RPC_EOF'}
            $m=$line|ConvertFrom-Json -AsHashtable
        }while(-not $m.Contains('id') -or $m.id -ne $id)
        if($m.Contains('error') -or ($m.result.Contains('isError') -and $m.result.isError)){throw 'RPC_FAILURE'}
        return $m.result.structuredContent
    }
    Start-Service WindowsAdminMCP
    $saved=With-Client {
        param($p)
        $clock=[Diagnostics.Stopwatch]::StartNew()
        do{
            $s=Call-Tool $p 1 'powershell_status' @{}
            if($s.status -eq 'OK'){break}
            Start-Sleep -Milliseconds 100
        }while($clock.Elapsed.TotalSeconds -lt 10)
        if($s.status -ne 'OK'){throw 'SERVICE_NOT_READY'}
        Call-Tool $p 2 'powershell_exec' @{command="Write-Output 'upgrade-preserved'";operation_key='package-upgrade-durable';timeout_seconds=10}
    }
    Check ($saved.status -eq 'COMPLETED' -and $saved.stdout.Trim() -ceq 'upgrade-preserved') 'r6.update_durable_job_created'
    $blocked=$false
    try{& $installer -Action Maintenance -ExpectedCommit $baseline.commit|Out-Null}catch{if($_.Exception.Message -eq 'SCM_MAINTENANCE_REQUIRED'){$blocked=$true}else{throw}}
    Check $blocked 'r6.maintenance_refuses_running_service'
    Stop-Service WindowsAdminMCP
    $maintenance=& $installer -Action Maintenance -ExpectedCommit $baseline.commit|ConvertFrom-Json
    Check ($maintenance.status -eq 'MAINTENANCE_READY' -and $maintenance.service_start -eq 'Manual' -and -not(Get-ScheduledTask -TaskName 'WindowsAdminMCP-Tunnel' -ErrorAction SilentlyContinue)) 'r6.accepted_registration_offline_maintenance'
    $again=& $installer -Action Maintenance -ExpectedCommit $baseline.commit|ConvertFrom-Json
    Check ($again.status -eq 'MAINTENANCE_READY') 'r6.maintenance_idempotent'
    $replacementRoot=Join-Path $env:RUNNER_TEMP ('upgrade-'+[guid]::NewGuid().ToString('N'))
    $replacementZip=$replacementRoot+'.zip'
    try{
        [IO.Compression.ZipFile]::ExtractToDirectory($Package,$replacementRoot)
        $manifestFile=Join-Path $replacementRoot 'manifest.json'
        $manifest=Get-Content -LiteralPath $manifestFile -Raw|ConvertFrom-Json -AsHashtable
        $manifest.commit='1111111111111111111111111111111111111111'
        $marker=Join-Path $replacementRoot 'native\upgrade-fixture.txt'
        [IO.File]::WriteAllText($marker,'second package fixture')
        $manifest.files['native/upgrade-fixture.txt']=(Get-FileHash -LiteralPath $marker).Hash
        [IO.File]::WriteAllText($manifestFile,($manifest|ConvertTo-Json -Depth 5))
        [IO.Compression.ZipFile]::CreateFromDirectory($replacementRoot,$replacementZip)
        $newDigest=(Get-FileHash -LiteralPath $replacementZip).Hash
        $updated=& $installer -Action Install -PackagePath $replacementZip -ExpectedSha256 $newDigest|ConvertFrom-Json
        Check ($updated.status -eq 'INSTALLED_OFFLINE' -and $updated.commit -eq $manifest.commit -and (Test-Path -LiteralPath (Join-Path $root 'native\upgrade-fixture.txt'))) 'r6.actual_second_package_installed'
        function Read-SavedJob {
            Start-Service WindowsAdminMCP
            try{
                With-Client {
                    param($p)
                    $clock=[Diagnostics.Stopwatch]::StartNew()
                    do{
                        $s=Call-Tool $p 1 'powershell_status' @{}
                        if($s.status -eq 'OK'){break}
                        Start-Sleep -Milliseconds 100
                    }while($clock.Elapsed.TotalSeconds -lt 10)
                    if($s.status -ne 'OK'){throw 'SERVICE_NOT_READY'}
                    Call-Tool $p 2 'powershell_job' @{action='logs';job_id=$saved.job_id}
                }
            }finally{Stop-Service WindowsAdminMCP}
        }
        $read=Read-SavedJob
        Check ($read.status -eq 'OK' -and $read.job.job_id -eq $saved.job_id -and $read.job.execution.stdout.Trim() -ceq 'upgrade-preserved') 'r6.job_preserved_after_package_update'
        # Negative metadata fixture: target claims schema 2, current SQLite
        # is really schema 4. No old executable or DB is actually restored.
        $priorManifest=Join-Path $root '.rollback\manifest.json'
        $original=[IO.File]::ReadAllText($priorManifest)
        try{
            $prior=$original|ConvertFrom-Json -AsHashtable
            $prior.store_schema=2
            [IO.File]::WriteAllText($priorManifest,($prior|ConvertTo-Json -Depth 5))
            $blocked=$false
            try{& $installer -Action Rollback|Out-Null}catch{if($_.Exception.Message -eq 'STORE_SCHEMA_DOWNGRADE_FORBIDDEN'){$blocked=$true}else{throw}}
            Check ($blocked -and (Test-Path (Join-Path $root '.rollback')) -and (Test-Path (Join-Path $root 'native\upgrade-fixture.txt'))) 'r6.actual_installer_blocks_schema_downgrade'
        }finally{[IO.File]::WriteAllText($priorManifest,$original)}
        $restored=& $installer -Action Rollback|ConvertFrom-Json
        Check ($restored.status -eq 'ROLLED_BACK_OFFLINE' -and -not(Test-Path (Join-Path $root 'native\upgrade-fixture.txt'))) 'r6.actual_compatible_package_rollback'
        $read=Read-SavedJob
        Check ($read.status -eq 'OK' -and $read.job.job_id -eq $saved.job_id -and $read.job.execution.stdout.Trim() -ceq 'upgrade-preserved') 'r6.job_preserved_after_rollback'
        $prepared=& $installer -Action Prepare -ExpectedCommit $baseline.commit|ConvertFrom-Json
        Check ($prepared.status -eq 'TRIAL_PREPARED' -and -not $prepared.logon_enabled -and -not $prepared.rollback_retained) 'r6.restored_baseline_reprepare'
        $null=& $installer -Action Maintenance -ExpectedCommit $baseline.commit
        $null=& $installer -Action Install -PackagePath $replacementZip -ExpectedSha256 $newDigest
        # A failed schema-4 trial can be repaired forward without falsely Accepting it.
        $manifest.commit='2222222222222222222222222222222222222222'
        [IO.File]::WriteAllText($manifestFile,($manifest|ConvertTo-Json -Depth 5))
        Remove-Item -LiteralPath $replacementZip
        [IO.Compression.ZipFile]::CreateFromDirectory($replacementRoot,$replacementZip)
        $repairDigest=(Get-FileHash -LiteralPath $replacementZip).Hash
        $blocked=$false
        try{& $installer -Action Install -PackagePath $replacementZip -ExpectedSha256 $repairDigest|Out-Null}catch{if($_.Exception.Message -eq 'UNACCEPTED_PREVIOUS_INSTALLATION'){$blocked=$true}else{throw}}
        Check $blocked 'r6.forward_trial_repair_requires_explicit_switch'
        $held=[IO.File]::Open((Join-Path $root 'native\WindowsAdminMCP.dll'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        try{
            $failed=$false
            try{& $installer -Action Install -PackagePath $replacementZip -ExpectedSha256 $repairDigest -ReplaceUnaccepted|Out-Null}catch{$failed=$true}
            Check ($failed -and (Test-Path (Join-Path $root '.rollback\journal.json')) -and -not(Test-Path (Join-Path $root '.trial-backup-1111111111111111111111111111111111111111')) -and (Get-Content (Join-Path $root 'manifest.json') -Raw|ConvertFrom-Json).commit -eq '1111111111111111111111111111111111111111') 'r6.failed_forward_repair_restores_prior_trial_journal'
        }finally{$held.Dispose()}
        $repair=& $installer -Action Install -PackagePath $replacementZip -ExpectedSha256 $repairDigest -ReplaceUnaccepted|ConvertFrom-Json
        Check ($repair.status -eq 'INSTALLED_OFFLINE' -and $repair.commit -eq $manifest.commit -and -not $repair.autostart -and (Test-Path (Join-Path $root '.trial-backup-1111111111111111111111111111111111111111\journal.json'))) 'r6.forward_trial_repair_retains_backup_without_acceptance'
        $read=Read-SavedJob
        Check ($read.job.job_id -eq $saved.job_id -and $read.job.execution.stdout.Trim() -ceq 'upgrade-preserved') 'r6.forward_trial_repair_preserves_jobs'
    }finally{
        if(Test-Path -LiteralPath $replacementZip){Remove-Item -LiteralPath $replacementZip -Force}
        if(Test-Path -LiteralPath $replacementRoot){Remove-Item -LiteralPath $replacementRoot -Recurse -Force}
    }

}finally{
    $task=Get-ScheduledTask -TaskName 'WindowsAdminMCP-Tunnel' -ErrorAction SilentlyContinue
    if($task -and $task.State.ToString() -ne 'Running'){Unregister-ScheduledTask -TaskName 'WindowsAdminMCP-Tunnel' -Confirm:$false}
    if(Get-Service WindowsAdminMCP -ErrorAction SilentlyContinue){Stop-Service WindowsAdminMCP -ErrorAction SilentlyContinue;& sc.exe delete WindowsAdminMCP|Out-Host}
    if(Test-Path -LiteralPath $root){Remove-Item -LiteralPath $root -Recurse -Force}
    if(Test-Path -LiteralPath $data){Remove-Item -LiteralPath $data -Recurse -Force}
}
