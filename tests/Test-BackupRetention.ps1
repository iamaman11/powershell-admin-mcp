$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../installer/BackupRetention.ps1')
function Check($ok,[string]$name){if(-not $ok){throw "FAIL $name"};Write-Host "PASS $name"}
function Reject([scriptblock]$body,[string]$reason){
    try{& $body;return $false}catch{return $_.Exception.Message -ceq $reason}
}
$root=Join-Path $env:RUNNER_TEMP ('wmcp-backups-'+[guid]::NewGuid().ToString('N'))
$admins=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$system=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')
function Set-TestProtection([string]$path){
    $acl=Get-Acl -LiteralPath $path
    $acl.SetOwner($admins)
    $acl.SetAccessRuleProtection($true,$false)
    $flags=if((Get-Item -LiteralPath $path).PSIsContainer){'ContainerInherit,ObjectInherit'}else{'None'}
    foreach($sid in @($admins,$system)){
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,
            'FullControl',$flags,'None','Allow'))
    }
    Set-Acl -LiteralPath $path -AclObject $acl
}
function New-TestBackup([string]$name,[string]$commit,[int]$dataLength){
    $path=Join-Path $root $name
    [void][IO.Directory]::CreateDirectory($path)
    [IO.File]::WriteAllText((Join-Path $path 'manifest.json'),
        (@{schema=1;commit=$commit}|ConvertTo-Json -Compress))
    [IO.File]::WriteAllText((Join-Path $path 'journal.json'),'{}')
    [IO.File]::WriteAllBytes((Join-Path $path 'payload.bin'),[byte[]]::new($dataLength))
    Set-TestProtection $path
    foreach($child in Get-ChildItem -LiteralPath $path -Force){Set-TestProtection $child.FullName}
    return $path
}
$current='a'*40;$previous='b'*40
try{
    [void][IO.Directory]::CreateDirectory($root)
    Set-TestProtection $root
    [IO.File]::WriteAllText((Join-Path $root 'manifest.json'),
        (@{schema=1;commit=$current}|ConvertTo-Json -Compress))
    Set-TestProtection (Join-Path $root 'manifest.json')
    [IO.File]::WriteAllBytes((Join-Path $root '.install.lock'),[byte[]]::new(0))
    Set-TestProtection (Join-Path $root '.install.lock')
    $null=New-TestBackup '.rollback' $previous 10
    $keepTrial=New-TestBackup ('.trial-backup-'+$previous) ('c'*40) 20
    $keepOlderTrial=New-TestBackup ('.trial-backup-'+('c'*40)) ('d'*40) 23
    $oldTrial=New-TestBackup ('.trial-backup-'+('d'*40)) ('e'*40) 30
    $oldAccepted=New-TestBackup ('.accepted-backup-'+('f'*40)) ('1'*40) 40
    $legacy=New-TestBackup ('.accepted-backup-'+('4'*40)) ('5'*40) 25
    Remove-Item -LiteralPath (Join-Path $legacy 'manifest.json') -Force
    Start-Sleep -Milliseconds 50
    $keepAccepted=New-TestBackup ('.accepted-backup-'+('2'*40)) ('3'*40) 50
    $plan=Get-BackupPlan $root $current
    Check ($plan.status -eq 'READY' -and $plan.candidate_count -eq 2 -and
        $plan.retained_count -eq 4 -and $plan.candidate_bytes -gt 70 -and
        $plan.plan_sha256 -cmatch '^[a-f0-9]{64}$') 'retention.plan_bounded_and_typed'
    $wrongCommit=Reject {Invoke-BackupCleanup $root ('9'*40) $plan.plan_sha256} 'BACKUP_INSTALLED_COMMIT_CHANGED'
    Check $wrongCommit 'retention.reject_changed_installation'
    [IO.File]::WriteAllBytes((Join-Path $oldTrial 'payload.bin'),[byte[]]::new(35))
    Set-TestProtection (Join-Path $oldTrial 'payload.bin')
    $changed=Reject {Invoke-BackupCleanup $root $current $plan.plan_sha256} 'BACKUP_PLAN_CHANGED'
    Check $changed 'retention.changed_size_denies_delete'
    Check ((Test-Path -LiteralPath $oldAccepted) -and (Test-Path -LiteralPath $oldTrial)) 'retention.failed_plan_preserves_all_archives'
    $fresh=Get-BackupPlan $root $current
    $result=Invoke-BackupCleanup $root $current $fresh.plan_sha256
    Check ($result.status -eq 'COMPLETED' -and $result.deleted_count -eq 2 -and
        $result.reclaimed_bytes -eq $fresh.candidate_bytes -and $result.remaining_count -eq 4) 'retention.apply_exact_plan_and_independent_readback'
    Check ((Test-Path -LiteralPath $keepTrial) -and (Test-Path -LiteralPath $keepOlderTrial) -and (Test-Path -LiteralPath $keepAccepted) -and
        (Test-Path -LiteralPath $legacy) -and
        (Test-Path -LiteralPath (Join-Path $root '.rollback')) -and
        -not(Test-Path -LiteralPath $oldTrial) -and -not(Test-Path -LiteralPath $oldAccepted)) 'retention.protected_rollback_and_last_accepted_survive'
    $stale=Reject {Invoke-BackupCleanup $root $current $fresh.plan_sha256} 'BACKUP_PLAN_CHANGED'
    Check $stale 'retention.stale_plan_cannot_replay'
}finally{
    # CI fixture only; never point this cleanup at the actual installation.
    if(Test-Path -LiteralPath $root){Remove-Item -LiteralPath $root -Recurse -Force}
}
