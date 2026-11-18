$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../installer/Update-Native.ps1')
function Check($ok,$name){if(-not $ok){throw "FAIL $name"};Write-Host "PASS $name"}
function Reject([scriptblock]$body,[string]$prefix){try{& $body;return $false}catch{return $_.Exception.Message.StartsWith($prefix,[StringComparison]::Ordinal)}}
$sha='a'*40
$base=[pscustomobject]@{id=12;head_sha=$sha;head_branch='main';event='push';status='completed';conclusion='success'}
$runs=@($base,[pscustomobject]@{id=13;head_sha=$sha;head_branch='main';event='pull_request';status='completed';conclusion='success'},
    [pscustomobject]@{id=14;head_sha=$sha;head_branch='main';event='push';status='completed';conclusion='failure'},
    [pscustomobject]@{id=15;head_sha=('b'*40);head_branch='main';event='push';status='completed';conclusion='success'})
Check ((Select-UpdateRun $runs $sha) -eq 12) 'h5.update_requires_exact_successful_main_push'
Check (Reject {Select-UpdateRun @($runs[1],$runs[2],$runs[3]) $sha} 'UPDATE_MAIN_CI_NOT_ACCEPTED') 'h5.no_unaccepted_fallback'
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
    Check (Reject {Assert-IdleStore $report} 'UPDATE_ACTIVE_JOBS') "h5.block_$field"
    $report.store.$field=0
}
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
