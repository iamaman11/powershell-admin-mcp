param([Parameter(Mandatory)][string]$Package,[Parameter(Mandatory)][string]$Sha256)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows'){throw 'CI_ONLY'}
. (Join-Path $PSScriptRoot '../installer/NativeFileSwap.ps1')
$installer=Join-Path $PSScriptRoot '../installer/Install-Native.ps1'
$root=Join-Path $env:ProgramFiles 'WindowsAdminMCP'
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
        $doctor=Read-Doctor
        Check ($doctor.package.status -eq 'FILE_SET_MISMATCH' -and $null -eq $doctor.startup) 'r6.doctor_unexpected_file_blocks_script'
    }finally{Remove-Item -LiteralPath $unexpected -Force}
    & $startup -Action RemoveTrial -ExpectedNativeSha256 (Get-FileHash -LiteralPath $native).Hash|Out-Null
    Check (-not(Get-ScheduledTask -TaskName 'WindowsAdminMCP-Tunnel' -ErrorAction SilentlyContinue)) 'r6.remove_quiescent_trial'
    $rollback=& $installer -Action Rollback|ConvertFrom-Json
    Check ($rollback.status -eq 'ROLLED_BACK_OFFLINE' -and -not(Test-Path (Join-Path $root 'native')) -and -not(Test-Path (Join-Path $root 'client')) -and -not(Test-Path (Join-Path $root '.rollback'))) 'r6.explicit_clean_install_rollback'
}finally{
    $task=Get-ScheduledTask -TaskName 'WindowsAdminMCP-Tunnel' -ErrorAction SilentlyContinue
    if($task -and $task.State.ToString() -ne 'Running'){Unregister-ScheduledTask -TaskName 'WindowsAdminMCP-Tunnel' -Confirm:$false}
    if(Get-Service WindowsAdminMCP -ErrorAction SilentlyContinue){& sc.exe delete WindowsAdminMCP|Out-Host}
    if(Test-Path -LiteralPath $root){Remove-Item -LiteralPath $root -Recurse -Force}
}
