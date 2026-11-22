# Native offline install. No credential access, network calls, live shutdown,
# TUN changes, or logon task activation. Existing active installs fail closed.
[CmdletBinding()]
param([ValidateSet('Verify','Install','Rollback','Prepare','Accept','Maintenance')][string]$Action='Verify',
      [string]$PackagePath,
      [ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedSha256,
      [ValidatePattern('^[a-f0-9]{40}$')][string]$ExpectedCommit,
      [switch]$RemoteAcceptanceConfirmed,
      [switch]$ReplaceUnaccepted)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($ReplaceUnaccepted -and $Action -ne 'Install'){throw 'REPLACE_UNACCEPTED_INSTALL_ONLY'}
. (Join-Path $PSScriptRoot 'NativeFileSwap.ps1')
. (Join-Path $PSScriptRoot 'VendorLock.Generated.ps1')
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'ADMIN_REQUIRED'}
if($PSVersionTable.PSVersion.Major -lt 7 -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64'){throw 'WINDOWS_X64_PWSH7_REQUIRED'}
$root=Join-Path $env:ProgramFiles 'WindowsAdminMCP'
$admin=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$system=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')
function Assert-Protected($path){
    $item=Get-Item -LiteralPath $path -Force
    if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'REPARSE_POINT'}
    $acl=Get-Acl -LiteralPath $path
    if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @($admin.Value,$system.Value)){throw 'UNTRUSTED_OWNER'}
    foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
        if($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin @($admin.Value,$system.Value)){throw 'UNTRUSTED_ACL'}
    }
}
function New-Protected($path){
    if(Test-Path -LiteralPath $path){throw 'DIRECTORY_ALREADY_EXISTS'}
    $acl=[Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true,$false);$acl.SetOwner($admin)
    foreach($sid in @($admin,$system)){
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    }
    [void][System.IO.FileSystemAclExtensions]::CreateDirectory($acl,$path)
    Assert-Protected $path
}
function Read-Manifest($dir){
    Assert-Protected $dir
    foreach($sub in @('native','client')){
        $path=Join-Path $dir $sub
        Assert-NativeSwapTree $path
        Assert-Protected $path
        foreach($item in Get-ChildItem -LiteralPath $path -Recurse -Force){Assert-Protected $item.FullName}
    }
    $p=Join-Path $dir 'manifest.json'
    Assert-Protected $p
    $m=Get-Content -LiteralPath $p -Raw|ConvertFrom-Json -AsHashtable
    if($m.schema -ne 1 -or $m.vendor_version -cne $VendorLockVersion -or $m.commit -cnotmatch '^[a-f0-9]{40}$' -or $m.files -isnot [Collections.IDictionary]){throw 'MANIFEST_INVALID'}
    # Existing rollback backups predate the lock; all new lock-bearing packages must include it.
    $hasPin=$m.files -is [Collections.IDictionary] -and $m.files.Contains('native/installer/VendorLock.Generated.ps1')
    if(($hasPin -and -not $m.Contains('vendor_zip_sha256')) -or
       ($m.Contains('vendor_zip_sha256') -and $m.vendor_zip_sha256 -cne $VendorLockZipSha256)){throw 'VENDOR_ZIP_LOCK_MISMATCH'}
    if($m.Contains('store_schema') -and $m.store_schema -notin @(2,3,4)){throw 'MANIFEST_STORE_SCHEMA_INVALID'}
    if($m.files.Count -lt 3 -or $m.files.Count -gt 2000){throw 'MANIFEST_SIZE_INVALID'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($name in $m.files.Keys){
        if($name -cnotmatch '^(native|client)/[A-Za-z0-9_./+-]+$' -or $name.Split('/') -contains '..' -or
            $name.Split('/') -contains '.' -or $name.Contains('//') -or @($name.Split('/')|Where-Object {$_.EndsWith('.') -or $_ -eq ''}).Count -or -not $seen.Add($name) -or $m.files[$name] -cnotmatch '^[A-F0-9]{64}$'){throw 'MANIFEST_ENTRY_INVALID'}
        $file=Join-Path $dir $name
        Assert-Protected $file
        if((Get-FileHash -LiteralPath $file).Hash -cne $m.files[$name]){throw 'PACKAGE_FILE_HASH_INVALID'}
    }
    foreach($required in @('native/WindowsAdminMCP.exe','native/NativeStartup.ps1','client/tunnel-client-runtime.exe')){
        if(-not $m.files.Contains($required)){throw 'PACKAGE_REQUIRED_FILE_MISSING'}
    }
    if($m.files['client/tunnel-client-runtime.exe'] -cne $VendorLockExeSha256){throw 'VENDOR_HASH_INVALID'}
    foreach($sub in @('native','client')){
        $path=Join-Path $dir $sub
        Assert-Protected $path
        $items=@(Get-ChildItem -LiteralPath $path -Recurse -Force)
        foreach($item in $items){
            Assert-Protected $item.FullName
            if(-not $item.PSIsContainer){
                $rel=[IO.Path]::GetRelativePath($dir,$item.FullName).Replace('\','/')
                if(-not $seen.Contains($rel)){throw 'UNEXPECTED_PACKAGE_FILE'}
            }
        }
    }
    return $m
}
$exe=Join-Path $root 'native\WindowsAdminMCP.exe'
$binding='"'+$exe+'" service'
function Assert-Quiescent {
    # Do not ever stop an active connection or replace a running service here.
    $exe=Join-Path $root 'native\WindowsAdminMCP.exe'
    if(@(Get-Process -Name 'tunnel-client-runtime' -ErrorAction SilentlyContinue).Count){throw 'ACTIVE_TUNNEL_MAINTENANCE_REQUIRED'}
    $frontends=@(Get-CimInstance Win32_Process -Filter "Name='WindowsAdminMCP.exe'" | Where-Object {
        $_.ExecutablePath -ieq $exe -and $_.CommandLine -match '\sstdio\s*$'
    })
    if($frontends.Count){throw 'ACTIVE_FRONTEND_MAINTENANCE_REQUIRED'}
    if(Get-ScheduledTask -TaskName 'WindowsAdminMCP-Tunnel' -ErrorAction SilentlyContinue){throw 'AUTOSTART_MAINTENANCE_REQUIRED'}
    if(@(Get-ScheduledTask|Where-Object {$_.TaskName -like 'WindowsAdminMCP-R6-*' -and $_.State.ToString() -eq 'Running'}).Count){throw 'ACTIVE_TRIAL_TASK'}
    $service=Get-CimInstance Win32_Service -Filter "Name='WindowsAdminMCP'"
    $exe=Join-Path $root 'native\WindowsAdminMCP.exe'
    $binding='"'+$exe+'" service'
    if($service -and ($service.State -ne 'Stopped' -or $service.PathName -ine $binding -or $service.StartName -ne 'LocalSystem' -or $service.StartMode -ne 'Manual')){throw 'SCM_MAINTENANCE_REQUIRED'}
    if(-not(Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'))){throw 'MACHINE_POWERSHELL_REQUIRED'}


    return $service
}
function Assert-StoreCompatible($reader,$target){
    # The fixed offline reader belongs to an already verified package. Never
    # restore an old database or use a file header while a WAL may be present.
    $si=[Diagnostics.ProcessStartInfo]::new($reader)
    $si.UseShellExecute=$false;$si.CreateNoWindow=$true
    $si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true
    $si.ArgumentList.Add('maintenance');$si.ArgumentList.Add('store-schema')
    $null=$si.Environment.Remove('CONTROL_PLANE_API_KEY')
    $null=$si.Environment.Remove('OPENAI_API_KEY')
    $p=[Diagnostics.Process]::Start($si)
    try{
        $out=$p.StandardOutput.ReadToEndAsync();$err=$p.StandardError.ReadToEndAsync()
        if(-not $p.WaitForExit(10000)){throw 'STORE_SCHEMA_READBACK_TIMEOUT'}
        if($p.ExitCode -ne 0){throw 'STORE_SCHEMA_READBACK_FAILED'}
        $receipt=$out.GetAwaiter().GetResult()|ConvertFrom-Json -AsHashtable
        if($receipt.status -eq 'ABSENT'){return}
        if($receipt.status -ne 'OBSERVED' -or $receipt.version -isnot [long] -and $receipt.version -isnot [int]){throw 'STORE_SCHEMA_READBACK_FAILED'}
        if($null -eq $target){throw 'ROLLBACK_TARGET_SCHEMA_UNPROVEN'}
        if($target -lt $receipt.version){throw 'STORE_SCHEMA_DOWNGRADE_FORBIDDEN'}
        if($receipt.version -lt 2){throw 'STORE_SCHEMA_UNSUPPORTED'}
    }finally{if(-not $p.HasExited){$p.Kill($true);$p.WaitForExit()};$p.Dispose()}
}
function Enter-Maintenance {
    $path=Join-Path $root '.install.lock'
    # The root is protected; never follow a preexisting reparse point.
    if(Test-Path -LiteralPath $path){
        $item=Get-Item -LiteralPath $path -Force
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'LOCK_REPARSE_POINT'}
    }
    # The protected parent prevents unprivileged creation or replacement.
    return [IO.File]::Open($path,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
}
function Restore-ServiceRegistration($journal){
    $actual=Get-CimInstance Win32_Service -Filter "Name='WindowsAdminMCP'"
    if($journal.service_existed){
        if(-not $actual){throw 'ORIGINAL_SERVICE_MISSING'}
    }elseif($actual){
        if($actual.PathName -ine $binding -or $actual.StartName -ne 'LocalSystem' -or $actual.State -ne 'Stopped' -or $actual.StartMode -ne 'Manual'){throw 'SCM_ROLLBACK_IDENTITY_MISMATCH'}
        & sc.exe delete WindowsAdminMCP|Out-Null
        if($LASTEXITCODE -ne 0){throw 'SCM_ROLLBACK_FAILED'}
    }
}
if($Action -in @('Prepare','Accept','Maintenance')){
    if(-not $ExpectedCommit){throw 'EXPECTED_COMMIT_REQUIRED'}
    if($Action -eq 'Accept' -and -not $RemoteAcceptanceConfirmed){throw 'REMOTE_ACCEPTANCE_REQUIRED'}
    Assert-Protected $root
    $lock=Enter-Maintenance
    try{
        $m=Read-Manifest $root
        if($m.commit -cne $ExpectedCommit){throw 'INSTALLED_COMMIT_MISMATCH'}
        $startup=Join-Path $root 'native\NativeStartup.ps1'
        $backup=Join-Path $root '.rollback'
        $previous=Join-Path $root ('.accepted-backup-'+$m.commit)
        $pending=Test-Path -LiteralPath $backup -PathType Container
        if($Action -eq 'Maintenance'){
            $result=& $startup -Action Maintenance -ExpectedNativeSha256 $m.files['native/WindowsAdminMCP.exe']|ConvertFrom-Json
            $null=Assert-Quiescent
            [pscustomobject]@{status='MAINTENANCE_READY';commit=$m.commit;service_start=$result.service_start;autostart=$false;rollback_retained=$pending}|ConvertTo-Json -Compress
            return
        }
        # A rollback from forward repair can restore an unaccepted candidate.
        # Reattach its retained journal only for that exact restored commit.
        if($Action -eq 'Prepare' -and -not $pending -and -not(Test-Path -LiteralPath $previous)){
            $archived=Join-Path $root ('.trial-backup-'+$m.commit)
            if(Test-Path -LiteralPath $archived -PathType Container){
                Assert-Protected $archived
                Assert-NativeSwapTree $archived
                foreach($item in Get-ChildItem -LiteralPath $archived -Recurse -Force){Assert-Protected $item.FullName}
                $null=Get-NativeSwapJournal -Root $root -Backup $archived
                Move-NativePath $archived $backup
                $pending=$true
            }
        }
        if($pending){
            Assert-Protected $backup
            Assert-NativeSwapTree $backup
            $null=Get-NativeSwapJournal $root
        }else{
            # Previously accepted package may be prepared again after offline
            # maintenance or rollback. Historical acceptance is not live proof.
            if(-not(Test-Path -LiteralPath $previous -PathType Container)){throw 'PENDING_INSTALL_REQUIRED'}
            Assert-Protected $previous
            Assert-NativeSwapTree $previous
        }
        if($Action -eq 'Prepare'){
            $result=& $startup -Action Prepare -ExpectedNativeSha256 $m.files['native/WindowsAdminMCP.exe']|ConvertFrom-Json
            [pscustomobject]@{status='TRIAL_PREPARED';commit=$m.commit;logon_enabled=$result.logon_enabled;service_start=$result.service_start;rollback_retained=$pending}|ConvertTo-Json -Compress
            return
        }
        if($pending -and (Test-Path -LiteralPath $previous)){throw 'ACCEPTANCE_BACKUP_EXISTS'}
        $result=& $startup -Action Accept -ExpectedNativeSha256 $m.files['native/WindowsAdminMCP.exe'] -RemoteAcceptanceConfirmed|ConvertFrom-Json
        if($result.status -ne 'REMOTE_ACCEPTED'){throw 'REMOTE_ACCEPTANCE_MISSING'}
        # Previous files are retained, but no longer represent an unfinished transaction.
        # The same-volume rename is the final local commit point.
        if($pending){Move-NativePath $backup $previous}
        [pscustomobject]@{status='ACCEPTED';commit=$m.commit;autostart=$true;previous_files_retained=$true;vendor_pid=$result.pid;generation=$result.generation;accepted_responses=$result.accepted_responses}|ConvertTo-Json -Compress
    }finally{$lock.Dispose()}
    return
}

if($Action -eq 'Rollback'){
    $null=Assert-Quiescent
    Assert-Protected $root
    $lock=Enter-Maintenance
    try{
        $null=Assert-Quiescent
        $backup=Join-Path $root '.rollback'
        Assert-Protected $backup
        Assert-NativeSwapTree $backup
        foreach($item in Get-ChildItem -LiteralPath $backup -Recurse -Force){Assert-Protected $item.FullName}
        foreach($name in @('native','client','manifest.json')){
            $existing=Join-Path $root $name
            if(Test-Path -LiteralPath $existing){Assert-Protected $existing}
        }
        # No DB at all is the only case where legacy/absent previous manifests
        # can be restored without a compatibility decision.
        $dbRoot=Join-Path $env:ProgramData 'WindowsAdminMCP.Data'
        if(Test-Path -LiteralPath $dbRoot){
            Assert-Protected $dbRoot
            $current=Read-Manifest $root
            if(-not $current.Contains('store_schema') -or $current.store_schema -notin @(3,4)){throw 'ROLLBACK_READER_UNPROVEN'}
            $targetSchema=$null
            if(Test-Path -LiteralPath (Join-Path $backup 'manifest.json')){
                $old=Read-Manifest $backup
                $targetSchema=if($old.Contains('store_schema')){[int]$old.store_schema}else{2}
            }
            Assert-StoreCompatible $exe $targetSchema
        }
        Restore-NativeFileSwap -Root $root -BeforeRestore {param($j) Restore-ServiceRegistration $j}
        [pscustomobject]@{status='ROLLED_BACK_OFFLINE';autostart=$false;remote_accepted=$false}|ConvertTo-Json -Compress
    }finally{$lock.Dispose()}
    return
}
if(-not $PackagePath -or -not $ExpectedSha256){throw 'PACKAGE_AND_DIGEST_REQUIRED'}

# Keep the archive open without write sharing from hashing to extraction.
$archivePath=(Resolve-Path -LiteralPath $PackagePath).Path
$stream=[IO.File]::Open($archivePath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
$incoming=$null
$swapAttempted=$false
$lock=$null
try{
    if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) -ne $ExpectedSha256){throw 'PACKAGE_SHA256_MISMATCH'}
    $stream.Position=0
    # Verification uses a protected temporary root outside the live installation.
    $incoming=Join-Path $env:ProgramFiles ('WindowsAdminMCP.incoming-'+[guid]::NewGuid().ToString('N'))
    New-Protected $incoming
    $archive=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Read,$true)
    try{
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        [long]$total=0
        if($archive.Entries.Count -gt 2200){throw 'PACKAGE_ENTRY_LIMIT'}
        foreach($entry in $archive.Entries){
            $name=$entry.FullName.Replace('\','/')
            if($name -ne 'manifest.json' -and $name -cnotmatch '^(native|client)/[A-Za-z0-9_./+-]*$'){throw 'PACKAGE_ENTRY_INVALID'}
            if($name.Split('/') -contains '..' -or $name.Split('/') -contains '.' -or $name.Contains('//') -or -not $seen.Add($name)){throw 'PACKAGE_PATH_INVALID'}
            $total+=$entry.Length
            if($total -gt 536870912){throw 'PACKAGE_SIZE_LIMIT'}
        }
        [IO.Compression.ZipFileExtensions]::ExtractToDirectory($archive,$incoming)
    }finally{$archive.Dispose()}
    # The extracted files may inherit the elevated user's default owner.
    # Normalize only the private incoming tree before checking its complete ACL.
    foreach($item in (Get-ChildItem -LiteralPath $incoming -Recurse -Force)){
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'PACKAGE_REPARSE_POINT'}
        $acl=Get-Acl -LiteralPath $item.FullName
        $acl.SetOwner($admin)
        Set-Acl -LiteralPath $item.FullName -AclObject $acl
    }
    $m=Read-Manifest $incoming
    if($Action -eq 'Verify'){
        [pscustomobject]@{status='VERIFIED';commit=$m.commit;files=$m.files.Count;installed=$false}|ConvertTo-Json -Compress
        return
    }
    $service=Assert-Quiescent
    if(-not(Test-Path -LiteralPath $root)){New-Protected $root}else{Assert-Protected $root}
    $lock=Enter-Maintenance
    $service=Assert-Quiescent
    foreach($name in @('native','client','manifest.json')){
        $p=Join-Path $root $name
        if(Test-Path -LiteralPath $p){Assert-Protected $p}
    }
    $old=$null
    if(Test-Path -LiteralPath (Join-Path $root 'manifest.json')){
        $old=Read-Manifest $root
        if($old.commit -eq $m.commit -and $old.files.Count -eq $m.files.Count -and $service){
            $same=$true
            foreach($name in $m.files.Keys){if(-not $old.files.Contains($name) -or $old.files[$name] -ne $m.files[$name]){$same=$false;break}}
            if($same){[pscustomobject]@{status='NOOP';commit=$m.commit;rollback_retained=(Test-Path -LiteralPath (Join-Path $root '.rollback'));autostart=$false}|ConvertTo-Json -Compress;return}
        }
    }
    $pendingBackup=Join-Path $root '.rollback'
    if((Test-Path -LiteralPath $pendingBackup) -and -not $ReplaceUnaccepted){throw 'UNACCEPTED_PREVIOUS_INSTALLATION'}
    if(-not $m.Contains('store_schema') -or $m.store_schema -ne 4){throw 'PACKAGE_STORE_SCHEMA_REQUIRED'}
    Assert-StoreCompatible (Join-Path $incoming 'native\WindowsAdminMCP.exe') ([int]$m.store_schema)
    # Forward repair of a failed trial must not require false acceptance or a
    # schema downgrade. Retain its older file backup; never touch the database.
    $archivedPending=$null
    if(Test-Path -LiteralPath $pendingBackup){
        if(-not $old -or -not $old.Contains('store_schema') -or $old.store_schema -ne 4){throw 'TRIAL_REPAIR_REQUIRES_SCHEMA4_CANDIDATE'}
        Assert-Protected $pendingBackup
        Assert-NativeSwapTree $pendingBackup
        $null=Get-NativeSwapJournal $root
        $archivedPending=Join-Path $root ('.trial-backup-'+$old.commit)
        if(Test-Path -LiteralPath $archivedPending){throw 'TRIAL_BACKUP_ALREADY_EXISTS'}
        Move-NativePath $pendingBackup $archivedPending
    }
    $swapAttempted=$true
    try{
      Invoke-NativeFileSwap -Root $root -Incoming $incoming -ServiceExisted ([bool]$service) -BeforeRestore {param($j) Restore-ServiceRegistration $j} -Validate {
        $null=Read-Manifest $root
        if(-not $service){
            New-Service -Name WindowsAdminMCP -BinaryPathName $binding -StartupType Manual|Out-Null
            $actual=Get-CimInstance Win32_Service -Filter "Name='WindowsAdminMCP'"
            if($actual.PathName -ine $binding -or $actual.StartName -ne 'LocalSystem' -or $actual.StartMode -ne 'Manual'){throw 'SCM_READBACK_FAILED'}
        }
      }
    }catch{
        # If the normal swap rollback succeeded, restore the prior trial journal.
        # If it failed, keep both backups untouched for explicit offline recovery.
        if($archivedPending -and -not(Test-Path -LiteralPath $pendingBackup)){Move-NativePath $archivedPending $pendingBackup}
        throw
    }
    [pscustomobject]@{status='INSTALLED_OFFLINE';commit=$m.commit;rollback_retained=$true;autostart=$false;remote_accepted=$false}|ConvertTo-Json -Compress
}finally{
    if($lock){$lock.Dispose()}
    $stream.Dispose()
    # If rollback itself failed, keep the incoming files for offline recovery.
    if($incoming -and (Test-Path -LiteralPath $incoming) -and (-not $swapAttempted -or -not(Test-Path -LiteralPath (Join-Path $root '.rollback')))){
        Remove-Item -LiteralPath $incoming -Recurse -Force
    }elseif($incoming -and (Test-Path -LiteralPath $incoming) -and @(Get-ChildItem -LiteralPath $incoming -Force).Count -eq 0){
        Remove-Item -LiteralPath $incoming
    }
}
