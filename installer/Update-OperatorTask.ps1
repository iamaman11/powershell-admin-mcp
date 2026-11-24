# Register an on-demand, short-lived Scheduler operator. The task is owned by
# the interactive administrator, not by the bounded PowerShell job or SCM.
[CmdletBinding()]
param([ValidateSet('Start','Status')][string]$Action='Status',
      [ValidatePattern('^[a-f0-9]{32}$')][string]$OperationId,
      [ValidateSet('update','accept','forget','prune')][string]$Operation='update',
      [ValidatePattern('^[a-f0-9]{40}$')][string]$ExpectedCommit,
      [ValidatePattern('^[a-f0-9]{64}$')][string]$ExpectedPlanSha256,
      [switch]$RemoteAcceptanceConfirmed)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$taskName='WindowsAdminMCP-OperatorUpdate'
$root=Join-Path $env:ProgramFiles 'WindowsAdminMCP'
$updater=Join-Path $root 'native\installer\Update-Native.ps1'
$receipt=Join-Path $root '.operator-update.json'
$pwsh=Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'
function Test-OperatorPrincipal([string]$userId,[string]$account,[string]$sid=''){
    if($userId -ieq $account -or ($sid -and $userId -ceq $sid)){return $true}
    if(-not $sid -or -not $userId){return $false}
    # Task Scheduler can read back DOMAIN\User as just User. Resolve the
    # actual principal rather than trusting textual aliases or substrings.
    try{
        $resolved=([Security.Principal.NTAccount]::new($userId)).Translate([Security.Principal.SecurityIdentifier])
        return $resolved.Value -ceq $sid
    }catch{return $false}
}
function New-OperatorArguments([string]$script,[string]$mode,[string]$id,[string]$commit='',[string]$plan=''){
    $verb=if($mode -ceq 'accept'){
        '-Action accept -RemoteAcceptanceConfirmed -ExpectedCommit '+$commit
    }elseif($mode -ceq 'forget'){
        '-Action forget -ExpectedCommit '+$commit
    }elseif($mode -ceq 'prune' -and $commit -cmatch '^[a-f0-9]{40}$' -and $plan -cmatch '^[a-f0-9]{64}$'){
        '-Action prune -ExpectedCommit '+$commit+' -ExpectedPlanSha256 '+$plan
    }elseif($mode -ceq 'update' -and -not $commit){
        '-Action update'
    }else{throw 'UPDATE_UNKNOWN_OPERATOR_MODE'}
    return '-NoProfile -NonInteractive -File "'+$script+'" '+$verb+' -ScheduledOperator -OperationId '+$id
}
function Test-OperatorDefinition($task,[string]$account,[string]$executable,[string]$script,[string]$sid=''){
    if($null -eq $task){return $false}
    if(-not(Test-OperatorPrincipal $task.Principal.UserId $account $sid) -or
        $task.Principal.LogonType.ToString() -ne 'Interactive' -or
        $task.Principal.RunLevel.ToString() -ne 'Highest' -or @($task.Actions).Count -ne 1){return $false}
    $a=$task.Actions[0]
    if($a.Execute -ine $executable){return $false}
    $pattern='^-NoProfile -NonInteractive -File "'+[regex]::Escape($script)+
        '" -Action (update|accept|forget|prune)(?: -RemoteAcceptanceConfirmed -ExpectedCommit ([a-f0-9]{40})| -ExpectedCommit ([a-f0-9]{40})| -ExpectedCommit ([a-f0-9]{40}) -ExpectedPlanSha256 ([a-f0-9]{64}))? -ScheduledOperator -OperationId ([a-f0-9]{32})$'
    $match=[regex]::Match($a.Arguments,$pattern,[Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if(-not $match.Success){return $false}
    $mode=$match.Groups[1].Value
    if(($mode -ceq 'accept') -ne $match.Groups[2].Success -or
        ($mode -ceq 'forget') -ne $match.Groups[3].Success -or
        ($mode -ceq 'prune') -ne $match.Groups[4].Success -or
        ($mode -ceq 'prune') -ne $match.Groups[5].Success){return $false}
    $commit=if($mode -ceq 'accept'){$match.Groups[2].Value}elseif($mode -ceq 'forget'){$match.Groups[3].Value}else{$match.Groups[4].Value}
    return $a.Arguments -ceq (New-OperatorArguments $script $mode $match.Groups[6].Value $commit $match.Groups[5].Value)
}
function Get-OperatorReceipt {
    if(-not(Test-Path -LiteralPath $receipt -PathType Leaf)){return $null}
    if((Get-Item -LiteralPath $receipt).Length -gt 8192){throw 'UPDATE_RECEIPT_UNTRUSTED'}
    $value=Get-Content -LiteralPath $receipt -Raw|ConvertFrom-Json -AsHashtable
    if($value.schema -ne 1 -or $value.operation_id -cnotmatch '^[a-f0-9]{32}$' -or
       $value.state -notin @('RUNNING','NO_UPDATE','COMPLETED','FAILED') -or
       $value.stage -cnotmatch '^[a-z_]{1,32}$' -or
       ($value.ContainsKey('operation_action') -and $value.operation_action -cnotin @('REQUEST','ACCEPT','FORGET_LEGACY','CLEANUP_BACKUPS'))){
        throw 'UPDATE_RECEIPT_UNTRUSTED'
    }
    if(-not $value.ContainsKey('operation_action')){$value.operation_action='REQUEST'}
    return $value
}
# Dot-source for deterministic contract tests; never register a task on import.
if($MyInvocation.InvocationName -eq '.'){return}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=[Security.Principal.WindowsPrincipal]::new($identity)
if(-not [Environment]::UserInteractive -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0 -or
   $identity.IsSystem -or -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    throw 'UPDATE_INTERACTIVE_ADMIN_REQUIRED'
}
$task=Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if($Action -eq 'Start'){
    $taskLock=[IO.File]::Open((Join-Path $root '.operator-task.lock'),[IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try{
    if(-not $OperationId){throw 'UPDATE_OPERATION_ID_REQUIRED'}
    if($Operation -eq 'accept'){
        if(-not $RemoteAcceptanceConfirmed -or -not $ExpectedCommit){
            throw 'UPDATE_ACCEPT_ARGUMENTS_REQUIRED'
        }
    }elseif($Operation -eq 'forget'){
        if($RemoteAcceptanceConfirmed -or -not $ExpectedCommit){
            throw 'UPDATE_LEGACY_ARGUMENTS_REQUIRED'
        }
    }elseif($Operation -eq 'prune'){
        if($RemoteAcceptanceConfirmed -or -not $ExpectedCommit -or -not $ExpectedPlanSha256){
            throw 'UPDATE_BACKUP_CLEANUP_ARGUMENTS_REQUIRED'
        }
    }elseif($RemoteAcceptanceConfirmed -or $ExpectedCommit -or $ExpectedPlanSha256){
        throw 'UPDATE_UNEXPECTED_ACCEPT_ARGUMENTS'
    }
    if(-not(Test-Path -LiteralPath $updater -PathType Leaf) -or
       -not(Test-Path -LiteralPath $pwsh -PathType Leaf)){throw 'UPDATE_INSTALLED_HELPER_MISSING'}
    if($task -and $task.State.ToString() -eq 'Running'){throw 'UPDATE_OPERATOR_BUSY'}
    $previous=Get-OperatorReceipt
    if($previous -and $previous.state -eq 'RUNNING'){throw 'UPDATE_PREVIOUS_OUTCOME_UNKNOWN'}
    if($task -and -not $previous){throw 'UPDATE_PREVIOUS_OUTCOME_UNKNOWN'}
    if($task){
        if(-not(Test-OperatorDefinition $task $identity.Name $pwsh $updater $identity.User.Value)){
            throw 'UPDATE_OPERATOR_TASK_IDENTITY_MISMATCH'
        }
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
    $args=New-OperatorArguments $updater $Operation $OperationId $ExpectedCommit $ExpectedPlanSha256
    # PowerShell variable names are case-insensitive: $action would overwrite
    # the validated -Action Start|Status parameter and fail before registration.
    $taskAction=New-ScheduledTaskAction -Execute $pwsh -Argument $args
    $user=New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType Interactive -RunLevel Highest
    $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::FromMinutes(15)) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $taskName -Action $taskAction -Principal $user -Settings $settings|Out-Null
    $task=Get-ScheduledTask -TaskName $taskName
    if(-not(Test-OperatorDefinition $task $identity.Name $pwsh $updater $identity.User.Value)){throw 'UPDATE_OPERATOR_READBACK_FAILED'}
    # The receipt is created only by the independently scheduled updater.
    Start-ScheduledTask -TaskName $taskName
    [pscustomobject]@{status='REQUESTED';operation_id=$OperationId;task=$taskName;
        operation_action=if($Operation -eq 'accept'){'ACCEPT'}elseif($Operation -eq 'forget'){'FORGET_LEGACY'}elseif($Operation -eq 'prune'){'CLEANUP_BACKUPS'}else{'REQUEST'}}|ConvertTo-Json -Compress
    return
    }finally{$taskLock.Dispose()}
}
$state=if($task){$task.State.ToString()}else{'Absent'}
$info=if($task){Get-ScheduledTaskInfo -TaskName $taskName}else{$null}
$record=Get-OperatorReceipt
$taskId=$null
if($task){
    if(-not(Test-OperatorDefinition $task $identity.Name $pwsh $updater $identity.User.Value)){
        throw 'UPDATE_OPERATOR_TASK_IDENTITY_MISMATCH'
    }
    $taskId=([regex]::Match($task.Actions[0].Arguments,'-OperationId\s+([a-f0-9]{32})$')).Groups[1].Value
    if($record -and $record.operation_id -cne $taskId){throw 'UPDATE_STATUS_TASK_MISMATCH'}
    $taskOperation=if($task.Actions[0].Arguments.Contains(' -Action accept ')){'ACCEPT'}elseif($task.Actions[0].Arguments.Contains(' -Action forget ')){'FORGET_LEGACY'}elseif($task.Actions[0].Arguments.Contains(' -Action prune ')){'CLEANUP_BACKUPS'}else{'REQUEST'}
    if($record -and $record.operation_action -cne $taskOperation){throw 'UPDATE_STATUS_TASK_MISMATCH'}
}
[pscustomobject]@{status=if($record){$record.state}elseif($task){'REQUESTED'}else{'NOT_REQUESTED'};
    operation_id=if($record){$record.operation_id}else{$taskId};
    stage=if($record){$record.stage}else{$null};
    commit=if($record){$record.commit}else{$null};
    reason=if($record){$record.reason}else{$null};
    operation_action=if($record){$record.operation_action}elseif($task){$taskOperation}else{$null};
    rollback_pending=if($record){$record.rollback_pending}else{$null};
    plan_sha256=if($record -and $record.ContainsKey('plan_sha256')){$record.plan_sha256}else{$null};
    candidate_count=if($record -and $record.ContainsKey('candidate_count')){$record.candidate_count}else{$null};
    candidate_bytes=if($record -and $record.ContainsKey('candidate_bytes')){$record.candidate_bytes}else{$null};
    retained_count=if($record -and $record.ContainsKey('retained_count')){$record.retained_count}else{$null};
    reclaimed_bytes=if($record -and $record.ContainsKey('reclaimed_bytes')){$record.reclaimed_bytes}else{$null};
    deleted_count=if($record -and $record.ContainsKey('deleted_count')){$record.deleted_count}else{$null};
    remaining_count=if($record -and $record.ContainsKey('remaining_count')){$record.remaining_count}else{$null};
    task_state=$state;last_task_result=if($info){$info.LastTaskResult}else{$null}}|ConvertTo-Json -Compress
