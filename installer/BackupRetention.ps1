# Installed package backup retention. No service, tunnel, store or profile
# operations. The plan is a read-only, content-bounded snapshot; Apply requires
# its exact fingerprint and rechecks the complete plan under the install lock.
[CmdletBinding()]
param([ValidateSet('Plan','Apply')][string]$Action='Plan',
      [ValidatePattern('^[a-f0-9]{40}$')][string]$ExpectedCommit,
      [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedPlanSha256)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest

function Assert-BackupPath([string]$path){
    $item=Get-Item -LiteralPath $path -Force -ErrorAction Stop
    if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'BACKUP_REPARSE_POINT'}
    $acl=Get-Acl -LiteralPath $path -ErrorAction Stop
    $owner=$acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if($owner -notin @('S-1-5-32-544','S-1-5-18')){throw 'BACKUP_OWNER_UNTRUSTED'}
    foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
        if($rule.AccessControlType -eq 'Allow' -and
           $rule.IdentityReference.Value -notin @('S-1-5-32-544','S-1-5-18')){
            throw 'BACKUP_ACL_UNTRUSTED'
        }
    }
}
function Read-BackupCommit([string]$directory){
    $file=Join-Path $directory 'manifest.json'
    Assert-BackupPath $directory
    Assert-BackupPath $file
    if((Get-Item -LiteralPath $file).Length -gt 131072){throw 'BACKUP_MANIFEST_TOO_LARGE'}
    $manifest=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json -AsHashtable
    if($manifest.schema -ne 1 -or $manifest.commit -cnotmatch '^[a-f0-9]{40}$'){
        throw 'BACKUP_MANIFEST_INVALID'
    }
    return [string]$manifest.commit
}
function Get-BackupPlan([string]$root,[string]$expected){
    Assert-BackupPath $root
    $active=Read-BackupCommit $root
    if($active -cne $expected){throw 'BACKUP_INSTALLED_COMMIT_CHANGED'}
    $rollback=Join-Path $root '.rollback'
    $rollbackCommit=$null
    if(Test-Path -LiteralPath $rollback -PathType Container){
        $rollbackCommit=Read-BackupCommit $rollback
        Assert-BackupPath (Join-Path $rollback 'journal.json')
    }
    $candidates=@(Get-ChildItem -LiteralPath $root -Directory -Force|Where-Object {
        $_.Name -cmatch '^\.(trial|accepted)-backup-[a-f0-9]{40}$'
    }|Sort-Object Name)
    if($candidates.Count -gt 512){throw 'BACKUP_COUNT_LIMIT'}
    $protected=@{}
    if($rollbackCommit){
        # Keep two historical trial generations so the immediate rollback is
        # not the only recovery point. Archive names identify the release
        # being replaced; the manifest records its predecessor.
        $firstTrialName='.trial-backup-'+$rollbackCommit
        $protected[$firstTrialName]=$true
        $firstTrial=Join-Path $root $firstTrialName
        if(Test-Path -LiteralPath $firstTrial -PathType Container){
            $firstManifest=Join-Path $firstTrial 'manifest.json'
            if(Test-Path -LiteralPath $firstManifest -PathType Leaf){
                $olderCommit=Read-BackupCommit $firstTrial
                $protected['.trial-backup-'+$olderCommit]=$true
            }
        }
    }
    $accepted=@($candidates|Where-Object Name -CLike '.accepted-backup-*'|
        Sort-Object LastWriteTimeUtc -Descending)
    if($accepted.Count -gt 0){$protected[$accepted[0].Name]=$true}
    $rows=@(foreach($candidate in $candidates){
        Assert-BackupPath $candidate.FullName
        $journal=Join-Path $candidate.FullName 'journal.json'
        Assert-BackupPath $journal
        # The oldest accepted archive predates package manifests. Preserve
        # such unprovable content for explicit operator review, never delete it.
        $verifiable=Test-Path -LiteralPath (Join-Path $candidate.FullName 'manifest.json') -PathType Leaf
        if($verifiable){
            # Archive names refer to the superseded release, not the contents.
            $null=Read-BackupCommit $candidate.FullName
        }
        $files=Get-ChildItem -LiteralPath $candidate.FullName -File -Recurse -Force
        $stat=$files|Measure-Object -Property Length -Sum
        [pscustomobject]@{name=$candidate.Name;bytes=[long]$stat.Sum;
            files=[int]$stat.Count;keep=($protected.ContainsKey($candidate.Name) -or -not $verifiable)}
    })
    $selected=@($rows|Where-Object {-not $_.keep})
    $retained=@($rows|Where-Object keep)
    [long]$selectedBytes=0;[long]$retainedBytes=0
    foreach($row in $selected){$selectedBytes+=[long]$row.bytes}
    foreach($row in $retained){$retainedBytes+=[long]$row.bytes}
    $canonical=($expected+'|'+$rollbackCommit+'|'+
        (($rows|ForEach-Object {"$($_.name):$($_.bytes):$($_.files):$($_.keep)"}) -join '|'))
    $digest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant()
    return [pscustomobject]@{status='READY';installed_commit=$active;plan_sha256=$digest;
        candidate_count=$selected.Count;candidate_bytes=$selectedBytes;
        retained_count=$retained.Count;retained_bytes=$retainedBytes;
        rollback_pending=($null -ne $rollbackCommit);entries=$selected}
}
function Invoke-BackupCleanup([string]$root,[string]$expected,[string]$digest){
    Assert-BackupPath $root
    $lockPath=Join-Path $root '.install.lock'
    if(Test-Path -LiteralPath $lockPath){Assert-BackupPath $lockPath}
    $lock=[IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try{
        $plan=Get-BackupPlan $root $expected
        if($plan.plan_sha256 -cne $digest){throw 'BACKUP_PLAN_CHANGED'}
        # Verify all objects before deleting the first archive. A symlink or
        # untrusted descendant in any candidate fails the entire operation.
        foreach($entry in $plan.entries){
            $path=Join-Path $root $entry.name
            foreach($child in Get-ChildItem -LiteralPath $path -Recurse -Force){
                Assert-BackupPath $child.FullName
            }
        }
        $count=0;[long]$bytes=0
        foreach($entry in $plan.entries){
            $path=Join-Path $root $entry.name
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
            if(Test-Path -LiteralPath $path){throw 'BACKUP_DELETE_UNCONFIRMED'}
            $count++;$bytes+=$entry.bytes
        }
        $remaining=Get-BackupPlan $root $expected
        return [pscustomobject]@{status='COMPLETED';installed_commit=$expected;
            plan_sha256=$digest;deleted_count=$count;reclaimed_bytes=$bytes;
            remaining_count=($remaining.candidate_count+$remaining.retained_count);
            rollback_pending=$remaining.rollback_pending}
    }finally{$lock.Dispose()}
}
if($MyInvocation.InvocationName -eq '.'){return}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=[Security.Principal.WindowsPrincipal]::new($identity)
if($identity.IsSystem -or -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    throw 'BACKUP_INTERACTIVE_ADMIN_REQUIRED'
}
if($Action -eq 'Apply' -and -not $ExpectedPlanSha256){throw 'BACKUP_PLAN_REQUIRED'}
if($Action -eq 'Plan' -and $ExpectedPlanSha256){throw 'BACKUP_UNEXPECTED_PLAN'}
$root=Join-Path $env:ProgramFiles 'WindowsAdminMCP'
$result=if($Action -eq 'Plan'){Get-BackupPlan $root $ExpectedCommit}else{
    Invoke-BackupCleanup $root $ExpectedCommit $ExpectedPlanSha256.ToLowerInvariant()
}
$output=$result|Select-Object * -ExcludeProperty entries
if($Action -eq 'Plan'){
    $output|Add-Member -NotePropertyName candidate_names -NotePropertyValue @($result.entries|ForEach-Object name)
}
$output|ConvertTo-Json -Compress -Depth 4
