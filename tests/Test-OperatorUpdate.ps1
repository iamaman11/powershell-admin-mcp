$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../installer/Update-Native.ps1')
. (Join-Path $PSScriptRoot '../installer/Update-OperatorTask.ps1')
function Check($ok,$name){if(-not $ok){throw "FAIL $name"};Write-Host "PASS $name"}
function Reject([scriptblock]$body,[string]$prefix){try{& $body;return $false}catch{return $_.Exception.Message.StartsWith($prefix,[StringComparison]::Ordinal)}}
$tokens=$null;$parseErrors=$null
$helperAst=[Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot '../installer/Update-OperatorTask.ps1'),[ref]$tokens,[ref]$parseErrors)
$shadowedAction=@($helperAst.FindAll({param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
    $node.Left.VariablePath.UserPath -ieq 'Action'
},$true))
Check ($parseErrors.Count -eq 0 -and $shadowedAction.Count -eq 0) 'operator.no_case_insensitive_validated_action_parameter_shadow'
$operatorExe='C:\Program Files\PowerShell\7\pwsh.exe'
$operatorScript='C:\Program Files\WindowsAdminMCP\native\installer\Update-Native.ps1'
$operatorId='a'*32
$action=[pscustomobject]@{Execute=$operatorExe;
    Arguments=('-NoProfile -NonInteractive -File "'+$operatorScript+'" -Action update -ScheduledOperator -OperationId '+$operatorId)}
$task=[pscustomobject]@{State='Running';Principal=[pscustomobject]@{UserId='ACME\Operator';LogonType='Interactive';RunLevel='Highest'};
    Actions=@($action)}
Check (Test-OperatorDefinition $task 'ACME\Operator' $operatorExe $operatorScript) 'operator.strict_interactive_scheduler_definition'
Check (Test-ScheduledUpdateIdentity $task 'ACME\Operator' $operatorScript $operatorId) 'operator.updater_checks_running_task_and_operation'
Check (-not(Test-ScheduledUpdateIdentity $task 'ACME\Operator' $operatorScript ('b'*32))) 'operator.rejects_foreign_operation'
$acceptCommit='b'*40
$acceptArguments=New-OperatorArguments $operatorScript 'accept' $operatorId $acceptCommit
$action.Arguments=$acceptArguments
Check ((Test-OperatorDefinition $task 'ACME\Operator' $operatorExe $operatorScript) -and
    (Test-ScheduledUpdateIdentity $task 'ACME\Operator' $operatorScript $operatorId '' '' 'accept' $acceptCommit)) 'operator.explicit_accept_definition_and_exact_commit'
Check (-not(Test-ScheduledUpdateIdentity $task 'ACME\Operator' $operatorScript $operatorId '' '' 'accept' ('c'*40)) -and
    -not(Test-ScheduledUpdateIdentity $task 'ACME\Operator' $operatorScript $operatorId)) 'operator.accept_rejects_other_commit_or_mode'
$action.Arguments=$acceptArguments.Replace(' -RemoteAcceptanceConfirmed','')
Check (-not(Test-OperatorDefinition $task 'ACME\Operator' $operatorExe $operatorScript) -and
    -not(Test-ScheduledUpdateIdentity $task 'ACME\Operator' $operatorScript $operatorId '' '' 'accept' $acceptCommit)) 'operator.accept_requires_confirmation'
$action.Arguments=$acceptArguments+' -Unexpected'
Check (-not(Test-OperatorDefinition $task 'ACME\Operator' $operatorExe $operatorScript)) 'operator.rejects_appended_task_arguments'
$action.Arguments=New-OperatorArguments $operatorScript 'update' $operatorId
$actual=[Security.Principal.WindowsIdentity]::GetCurrent()
$task.Principal.UserId=$actual.User.Translate([Security.Principal.NTAccount]).Value
Check (Test-OperatorDefinition $task 'ACME\Operator' $operatorExe $operatorScript $actual.User.Value) 'operator.scheduler_alias_resolves_exact_sid'
Check (Test-ScheduledUpdateIdentity $task 'ACME\Operator' $operatorScript $operatorId $actual.User.Value) 'operator.updater_scheduler_alias_resolves_exact_sid'
Check (-not(Test-OperatorDefinition $task 'ACME\Operator' $operatorExe $operatorScript 'S-1-5-18') -and
    -not(Test-ScheduledUpdateIdentity $task 'ACME\Operator' $operatorScript $operatorId 'S-1-5-18')) 'operator.rejects_foreign_principal_sid'
$task.Principal.UserId='ACME\Operator'
$task.Principal.LogonType='Password'
Check (-not(Test-OperatorDefinition $task 'ACME\Operator' $operatorExe $operatorScript) -and
    -not(Test-ScheduledUpdateIdentity $task 'ACME\Operator' $operatorScript $operatorId)) 'operator.rejects_background_password_task'
$sha='a'*40
$base=[pscustomobject]@{id=12;head_sha=$sha;head_branch='main';event='push';status='completed';conclusion='success'}
$runs=@($base,[pscustomobject]@{id=13;head_sha=$sha;head_branch='main';event='pull_request';status='completed';conclusion='success'},
    [pscustomobject]@{id=14;head_sha=$sha;head_branch='main';event='push';status='completed';conclusion='failure'},
    [pscustomobject]@{id=15;head_sha=('b'*40);head_branch='main';event='push';status='completed';conclusion='success'})
Check ((Select-UpdateRun $runs $sha) -eq 12) 'h5.update_requires_exact_successful_main_push'
Check (Reject {Select-UpdateRun @($runs[1],$runs[2],$runs[3]) $sha} 'UPDATE_MAIN_CI_NOT_ACCEPTED') 'h5.no_unaccepted_fallback'
$artifact=[pscustomobject]@{id=123;name='WindowsAdminMCP-offline-package';expired=$false;size_in_bytes=100000000;
    workflow_run=[pscustomobject]@{id=12;head_sha=$sha}}
Check ((Select-UpdateArtifact @($artifact) '12' $sha).id -eq 123) 'h5.artifact_bound_to_exact_main_run'
Check (Reject {Select-UpdateArtifact @($artifact) '13' $sha} 'UPDATE_ARTIFACT_IDENTITY_UNCONFIRMED') 'h5.no_foreign_run_artifact'
Check (Reject {Select-UpdateArtifact @($artifact) '12' ('b'*40)} 'UPDATE_ARTIFACT_IDENTITY_UNCONFIRMED') 'h5.no_foreign_commit_artifact'
Check (Reject {Select-UpdateArtifact @($artifact,$artifact) '12' $sha} 'UPDATE_ARTIFACT_IDENTITY_UNCONFIRMED') 'h5.no_ambiguous_artifact'
Check (Reject {Select-UpdateArtifact @([pscustomobject]@{id=123;name=$artifact.name;expired=$true;
    size_in_bytes=100000000;workflow_run=$artifact.workflow_run}) '12' $sha} 'UPDATE_ARTIFACT_IDENTITY_UNCONFIRMED') 'h5.no_expired_artifact'
$response=[Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::OK)
try{
    $response.Content=[Net.Http.ByteArrayContent]::new([byte[]](1,2,3,4,5,6,7,8))
    Check ((Confirm-UpdateDownloadResponse $response 0 0) -eq 8) 'h5.full_download_length_verified'
    Check (Reject {Confirm-UpdateDownloadResponse $response 4 8} 'UPDATE_DOWNLOAD_RESUME_UNCONFIRMED') 'h5.resume_refuses_unbounded_200_response'
}finally{$response.Dispose()}
$response=[Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::PartialContent)
try{
    $response.Content=[Net.Http.ByteArrayContent]::new([byte[]](5,6,7,8))
    $response.Content.Headers.ContentRange=[Net.Http.Headers.ContentRangeHeaderValue]::new(4,7,8)
    Check ((Confirm-UpdateDownloadResponse $response 4 8) -eq 8) 'h5.resume_verifies_exact_http_206_range'
    Check (Reject {Confirm-UpdateDownloadResponse $response 5 8} 'UPDATE_DOWNLOAD_RESUME_UNCONFIRMED') 'h5.resume_rejects_wrong_offset'
    $response.Content.Headers.ContentRange=[Net.Http.Headers.ContentRangeHeaderValue]::new(4,7,9)
    Check (Reject {Confirm-UpdateDownloadResponse $response 4 8} 'UPDATE_DOWNLOAD_RESUME_UNCONFIRMED') 'h5.resume_rejects_wrong_artifact_length'
}finally{$response.Dispose()}
$source=[IO.MemoryStream]::new([byte[]](1,2,3,4,5,6,7,8))
$destination=[IO.MemoryStream]::new()
try{
    Copy-UpdateDownloadStream $source $destination ([Threading.CancellationToken]::None) 8 1 8
    Check (([Convert]::ToHexString($destination.ToArray())) -ceq '0102030405060708') 'h5.stream_download_exact_bytes'
}finally{$source.Dispose();$destination.Dispose()}
$source=[IO.MemoryStream]::new([byte[]](5,6,7,8))
$destination=[IO.MemoryStream]::new()
try{
    $destination.Write([byte[]](1,2,3,4),0,4)
    Copy-UpdateDownloadStream $source $destination ([Threading.CancellationToken]::None) 8 1 8 4
    Check (([Convert]::ToHexString($destination.ToArray())) -ceq '0102030405060708') 'h5.resume_preserves_exact_prefix'
}finally{$source.Dispose();$destination.Dispose()}
$source=[IO.MemoryStream]::new([byte[]](1,2,3,4))
$destination=[IO.MemoryStream]::new()
try{
    Check (Reject {Copy-UpdateDownloadStream $source $destination ([Threading.CancellationToken]::None) 8 1 8} 'UPDATE_DOWNLOAD_INCOMPLETE') 'h5.truncated_stream_rejected'
}finally{$source.Dispose();$destination.Dispose()}
$source=[IO.MemoryStream]::new([byte[]](1,2,3,4))
$destination=[IO.MemoryStream]::new()
try{
    Check (Reject {Copy-UpdateDownloadStream $source $destination ([Threading.CancellationToken]::None) 0 1 3} 'UPDATE_DOWNLOAD_SIZE_LIMIT') 'h5.oversized_stream_rejected'
}finally{$source.Dispose();$destination.Dispose()}
$wrapperRoot=Join-Path ([IO.Path]::GetTempPath()) ('wmcp-artifact-'+[guid]::NewGuid().ToString('N'))
try{
    [void][IO.Directory]::CreateDirectory($wrapperRoot)
    [IO.File]::WriteAllText((Join-Path $wrapperRoot 'WindowsAdminMCP-update.json'),'{}')
    [IO.File]::WriteAllText((Join-Path $wrapperRoot 'WindowsAdminMCP-win-x64.zip'),'fake-package')
    $outer=Join-Path ([IO.Path]::GetTempPath()) ('wmcp-artifact-'+[guid]::NewGuid().ToString('N')+'.zip')
    try{
        [IO.Compression.ZipFile]::CreateFromDirectory($wrapperRoot,$outer)
        $expanded=Join-Path $wrapperRoot 'selected'
        [void][IO.Directory]::CreateDirectory($expanded)
        Expand-UpdateArtifact $outer $expanded
        Check ((Get-Content -LiteralPath (Join-Path $expanded 'WindowsAdminMCP-update.json') -Raw) -ceq '{}') 'h5.wrapper_extracts_only_named_files'
    }finally{if(Test-Path -LiteralPath $outer){Remove-Item -LiteralPath $outer -Force}}
}finally{Remove-Item -LiteralPath $wrapperRoot -Recurse -Force}
$path=Join-Path ([IO.Path]::GetTempPath()) ('update-receipt-'+[guid]::NewGuid().ToString('N')+'.json')
try{
    [IO.File]::WriteAllText($path,(@{schema=1;commit=$sha;run_id='12';sha256=('A'*64)}|ConvertTo-Json -Compress))
    Check ((Read-UpdateReceipt $path $sha '12') -ceq ('A'*64)) 'h5.receipt_exact_run'
    Check (Reject {Read-UpdateReceipt $path $sha '13'} 'UPDATE_RECEIPT_MISMATCH') 'h5.receipt_wrong_run_denied'
    Check (Reject {Read-UpdateReceipt $path ('b'*40) '12'} 'UPDATE_RECEIPT_MISMATCH') 'h5.receipt_wrong_commit_denied'
}finally{Remove-Item -LiteralPath $path -Force}
$report=[pscustomobject]@{service=[pscustomobject]@{status='OK'};store=[pscustomobject]@{schemaVersion=4;running=0;queued=0;cancelRequested=0}}
Assert-IdleStore $report
foreach($field in @('running','queued','cancelRequested')){
    $report.store.$field=1
    Assert-ValidStore $report
    Check (Reject {Assert-IdleStore $report} 'UPDATE_DRAIN_INCOMPLETE') "h5.wait_after_drain_$field"
    $report.store.$field=0
}
Check ((Assert-ValidStore $report) -eq $null) 'h5.preflight_allows_active_jobs_until_verified_artifact'
Check (Reject {Assert-IdleStore ([pscustomobject]@{service=$null;store=$null})} 'UPDATE_STORE_UNCONFIRMED') 'h5.fail_closed_store_unconfirmed'
$exe='C:\Program Files\WindowsAdminMCP\native\WindowsAdminMCP.exe'
$local=[pscustomobject]@{ExecutablePath=$exe;CommandLine='"'+$exe+'" stdio';ParentProcessId=42;SessionId=1}
$tunnel=[pscustomobject]@{ExecutablePath=$exe;CommandLine='"'+$exe+'" stdio';ParentProcessId=77;SessionId=1}
$otherBinary=[pscustomobject]@{ExecutablePath='C:\Other\WindowsAdminMCP.exe';CommandLine='"C:\Other\WindowsAdminMCP.exe" stdio';ParentProcessId=42;SessionId=1}
$service=[pscustomobject]@{ExecutablePath=$exe;CommandLine='"'+$exe+'" service';ParentProcessId=42;SessionId=0}
$samples=@($local,$tunnel,$otherBinary,$service)
Check (@(Select-UpdateClients $samples $exe @(77)).Count -eq 1) 'h5.only_canonical_stdio_leaf_selected'
Check (@(Select-UpdateClients $samples $exe @()).Count -eq 2) 'h5.tunnel_child_included_after_tunnel_stop'
Assert-UpdateClientScope $local 1 'S-1-5-21-fixture' 'S-1-5-21-fixture'
Check (Reject {Assert-UpdateClientScope $local 2 'S-1-5-21-fixture' 'S-1-5-21-fixture'} 'UPDATE_CLIENT_OTHER_SESSION') 'h5.never_close_foreign_session'
Check (Reject {Assert-UpdateClientScope $local 1 'S-1-5-21-fixture' 'S-1-5-21-other'} 'UPDATE_CLIENT_OTHER_OWNER') 'h5.never_close_foreign_owner'
$child=[pscustomobject]@{CreationDate=[datetime]'2026-11-18T20:00:05'}
$launcher=[pscustomobject]@{Name='codex.exe';ExecutablePath='C:\Tools\codex.exe';CreationDate=[datetime]'2026-11-18T20:00:00'}
Assert-BlockingLauncherIdentity $launcher @($child)
Check (Reject {Assert-BlockingLauncherIdentity ([pscustomobject]@{Name='services.exe';ExecutablePath='C:\Windows\System32\services.exe';CreationDate=$launcher.CreationDate}) @($child)} 'UPDATE_CLIENT_PARENT_PROTECTED') 'h5.never_kill_protected_parent'
Check (Reject {Assert-BlockingLauncherIdentity ([pscustomobject]@{Name='pwsh.exe';ExecutablePath='C:\Tools\pwsh.exe';CreationDate=[datetime]'2026-11-18T20:01:00'}) @($child)} 'UPDATE_CLIENT_PARENT_IDENTITY_CHANGED') 'h5.never_kill_reused_parent_pid'
Check (Reject {Assert-BlockingLauncherIdentity ([pscustomobject]@{Name='codex.exe';ExecutablePath=$null;CreationDate=$launcher.CreationDate}) @($child)} 'UPDATE_CLIENT_PARENT_PATH_UNCONFIRMED') 'h5.never_kill_unidentified_parent'
$fixtureRoot=Join-Path ([IO.Path]::GetTempPath()) ('wmcp-updater-'+[guid]::NewGuid().ToString('N'))
$helperDir=Join-Path $fixtureRoot 'native\installer'
try{
    [void][IO.Directory]::CreateDirectory($helperDir)
    $hashes=[ordered]@{}
    foreach($name in @('Install-Native.ps1','Update-Native.ps1','NativeFileSwap.ps1')){
        $file=Join-Path $helperDir $name
        [IO.File]::WriteAllText($file,"ci-fixture-$name")
        $hashes["native/installer/$name"]=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    }
    $installed=[pscustomobject]@{files=[pscustomobject]$hashes}
    Check (Assert-InstalledUpdateSource $helperDir $fixtureRoot $installed) 'h5.installed_updater_verified_without_checkout'
    Check (-not(Assert-InstalledUpdateSource $fixtureRoot $fixtureRoot $installed)) 'h5.unrelated_directory_requires_checkout'
    $old=[ordered]@{files=[ordered]@{'native/WindowsAdminMCP.exe'='A'*64}}
    Check (Reject {Assert-InstalledRollbackTarget $helperDir $fixtureRoot $installed $old} 'UPDATE_LEGACY_ROLLBACK_REQUIRES_CHECKOUT') 'h5.legacy_rollback_denied_before_stopping_service'
    Assert-InstalledRollbackTarget $fixtureRoot $fixtureRoot $installed $old
    $old.files['native/installer/Install-Native.ps1']='B'*64
    $old.files['native/installer/NativeFileSwap.ps1']='C'*64
    Assert-InstalledRollbackTarget $helperDir $fixtureRoot $installed $old
    Write-Host 'PASS h5.bundled_rollback_helpers_allowed'
    $installed.files.PSObject.Properties['native/installer/Update-Native.ps1'].Value='0'*64
    Check (Reject {Assert-InstalledUpdateSource $helperDir $fixtureRoot $installed} 'UPDATE_INSTALLED_HELPER_HASH_MISMATCH') 'h5.tampered_installed_updater_denied'
    $installed.files.PSObject.Properties.Remove('native/installer/Update-Native.ps1')
    Check (Reject {Assert-InstalledUpdateSource $helperDir $fixtureRoot $installed} 'UPDATE_INSTALLED_HELPER_NOT_IN_MANIFEST') 'h5.unlisted_installed_updater_denied'
}finally{Remove-Item -LiteralPath $fixtureRoot -Recurse -Force}
$packageRoot=Join-Path ([IO.Path]::GetTempPath()) ('wmcp-drain-package-'+[guid]::NewGuid().ToString('N'))
try{
    [void][IO.Directory]::CreateDirectory((Join-Path $packageRoot 'native'))
    $binary=Join-Path $packageRoot 'native\WindowsAdminMCP.exe'
    [IO.File]::WriteAllText($binary,'verified-candidate-fixture')
    $hash=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
    [IO.File]::WriteAllText((Join-Path $packageRoot 'manifest.json'),(@{files=@{'native/WindowsAdminMCP.exe'=$hash}}|ConvertTo-Json -Depth 3))
    $zip=Join-Path ([IO.Path]::GetTempPath()) ('wmcp-drain-'+[guid]::NewGuid().ToString('N')+'.zip')
    try{
        [IO.Compression.ZipFile]::CreateFromDirectory($packageRoot,$zip)
        $extracted=Join-Path $packageRoot 'selected\WindowsAdminMCP.exe'
        Check ((Extract-VerifiedDrainTool $zip (Join-Path $packageRoot 'selected')) -ceq $extracted -and
            (Get-FileHash -LiteralPath $extracted -Algorithm SHA256).Hash -ceq $hash) 'h5.verified_candidate_cli_extracted'
    }finally{if(Test-Path -LiteralPath $zip){Remove-Item -LiteralPath $zip -Force}}
}finally{Remove-Item -LiteralPath $packageRoot -Recurse -Force}
