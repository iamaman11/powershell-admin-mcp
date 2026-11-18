# Local operator orchestration only. The existing installer owns verification,
# protected file swaps and rollback. SCM/Task Scheduler own process lifetime.
[CmdletBinding()]
param([ValidateSet('update','rollback','accept')][string]$Action='update',
      [switch]$RemoteAcceptanceConfirmed)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Select-UpdateRun($runs,[string]$commit){
    $matching=@($runs|Where-Object {$_ -and $_.head_sha -ceq $commit -and $_.head_branch -ceq 'main' -and
        $_.event -ceq 'push' -and $_.status -ceq 'completed' -and $_.conclusion -ceq 'success'}|Sort-Object id -Descending)
    if($matching.Count -eq 0){throw 'UPDATE_MAIN_CI_NOT_ACCEPTED'}
    return $matching[0].id
}
function Read-UpdateReceipt([string]$path,[string]$commit,[string]$runId){
    if((Get-Item -LiteralPath $path).Length -gt 4096){throw 'UPDATE_RECEIPT_SIZE'}
    $r=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json -AsHashtable
    if($r['schema'] -ne 1 -or $r['commit'] -cne $commit -or [string]$r['run_id'] -cne $runId -or
        [string]$r['sha256'] -cnotmatch '^[A-Fa-f0-9]{64}$'){throw 'UPDATE_RECEIPT_MISMATCH'}
    return [string]$r.sha256
}
function Assert-IdleStore($report){
    if($null -eq $report.service -or $report.service.status -ne 'OK' -or
        $null -eq $report.store -or $report.store.schemaVersion -ne 4){throw 'UPDATE_STORE_UNCONFIRMED'}
    if($report.store.running -ne 0 -or $report.store.queued -ne 0 -or $report.store.cancelRequested -ne 0){
        throw 'UPDATE_ACTIVE_JOBS: finish or cancel active and queued jobs first'
    }
}
function Assert-InstalledUpdateSource([string]$scriptDirectory,[string]$installRoot,$manifest){
    $expected=[IO.Path]::GetFullPath((Join-Path $installRoot 'native\installer')).TrimEnd([char]'\')
    $actual=[IO.Path]::GetFullPath($scriptDirectory).TrimEnd([char]'\')
    if($actual -ine $expected){return $false}
    # The script is already in the protected installation. Confirm it is part
    # of the verified package before relaxing the source-checkout requirement.
    foreach($relative in @('native/installer/Install-Native.ps1',
                           'native/installer/Update-Native.ps1',
                           'native/installer/NativeFileSwap.ps1')){
        $entry=$manifest.files.PSObject.Properties[$relative]
        if($null -eq $entry -or [string]$entry.Value -cnotmatch '^[A-Fa-f0-9]{64}$'){
            throw 'UPDATE_INSTALLED_HELPER_NOT_IN_MANIFEST'
        }
        $file=Join-Path $installRoot ($relative.Replace('/','\'))
        if(-not(Test-Path -LiteralPath $file -PathType Leaf) -or
           (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine [string]$entry.Value){
            throw 'UPDATE_INSTALLED_HELPER_HASH_MISMATCH'
        }
    }
    return $true
}
# Dot-sourcing exposes only the pure checks for isolated CI, not a way around
# the public local-console guard.
if($MyInvocation.InvocationName -eq '.'){return}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if([Console]::IsInputRedirected -or -not [Environment]::UserInteractive -or
    [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0 -or $identity.IsSystem -or
    -not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    throw 'UPDATE_LOCAL_INTERACTIVE_ADMIN_REQUIRED'
}
$root=Join-Path $env:ProgramFiles 'WindowsAdminMCP'
$native=Join-Path $root 'native\WindowsAdminMCP.exe'
$startup=Join-Path $root 'native\NativeStartup.ps1'
$installer=Join-Path $PSScriptRoot 'Install-Native.ps1'
$repo='iamaman11/powershell-admin-mcp'
$checkout=Split-Path -Parent $PSScriptRoot
$stage='preflight'
$download=$null
$stopping=$false
$maintenance=$false
function Read-InstalledDoctor {
    # doctor deliberately returns nonzero for NOT_TESTED remote evidence.
    $PSNativeCommandUseErrorActionPreference=$false
    $raw=& $native doctor
    $d=$raw|ConvertFrom-Json
    if($d.schemaVersion -ne 1 -or $d.package.status -ne 'VERIFIED'){throw 'UPDATE_INSTALLED_PACKAGE_UNVERIFIED'}
    return $d
}
function Assert-ClosedClients([int[]]$allowedParents){
    $others=@(Get-CimInstance Win32_Process -Filter "Name='WindowsAdminMCP.exe'"|Where-Object {
        $_.ExecutablePath -ieq $native -and $_.CommandLine -match '\sstdio\s*$' -and $_.ParentProcessId -notin $allowedParents
    })
    if($others.Count){throw 'UPDATE_LOCAL_CLIENTS_ACTIVE: close Codex and other local stdio connections'}
}
function Read-Manifest {Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw|ConvertFrom-Json}
function Invoke-GhJson([string]$endpoint){
    $PSNativeCommandUseErrorActionPreference=$false
    $raw=& gh api --hostname github.com $endpoint
    if($LASTEXITCODE -ne 0){throw 'UPDATE_GITHUB_READ_FAILED'}
    return ($raw|ConvertFrom-Json)
}
try{
    $installed=Read-Manifest
    if($Action -eq 'accept'){
        if(-not $RemoteAcceptanceConfirmed){throw 'REMOTE_ACCEPTANCE_REQUIRED'}
        & $installer -Action Accept -ExpectedCommit $installed.commit -RemoteAcceptanceConfirmed
        return
    }
    $d=Read-InstalledDoctor
    Assert-IdleStore $d
    if($d.package.commit -cne $installed.commit -or -not $d.service.versions.informational_version.EndsWith('+'+$installed.commit)){throw 'UPDATE_INSTALLED_IDENTITY_MISMATCH'}
    $before=& $startup -Action Status|ConvertFrom-Json
    if(-not $before.definition_valid -or -not $before.service_binding_valid){throw 'UPDATE_STARTUP_UNCONFIRMED'}
    Assert-ClosedClients @($d.tunnelPid|Where-Object {$null -ne $_})
    $pending=Test-Path -LiteralPath (Join-Path $root '.rollback\journal.json')
    if($Action -eq 'update'){
        $stage='acquire'
        $main=(Invoke-GhJson "repos/$repo/commits/main").sha
        if($main -cnotmatch '^[a-f0-9]{40}$'){throw 'UPDATE_MAIN_IDENTITY'}
        if($main -ceq $installed.commit){
            [pscustomobject]@{status='NO_UPDATE';commit=$main;startup_ready=$before.startup_ready}|ConvertTo-Json -Compress
            return
        }
        if(-not(Assert-InstalledUpdateSource $PSScriptRoot $root $installed)){
            # Backward-compatible route for older installations whose package
            # does not contain the updater. Kept only for their first upgrade.
            $source=(& git -C $checkout rev-parse HEAD).Trim()
            if($LASTEXITCODE -ne 0 -or $source -cne $main){throw 'UPDATE_CHECKOUT_NOT_CURRENT_MAIN'}
            $dirty=@(& git -C $checkout status --porcelain --untracked-files=no)
            if($LASTEXITCODE -ne 0 -or $dirty.Count){throw 'UPDATE_CHECKOUT_NOT_CLEAN'}
        }
        $runs=(Invoke-GhJson "repos/$repo/actions/workflows/windows-acceptance.yml/runs?branch=main&event=push&head_sha=$main&per_page=20").workflow_runs
        $runId=[string](Select-UpdateRun $runs $main)
        $download=Join-Path $root ('update-'+[guid]::NewGuid().ToString('N'))
        [void][IO.Directory]::CreateDirectory($download)
        & gh run download $runId --repo $repo --name WindowsAdminMCP-offline-package --dir $download
        if($LASTEXITCODE -ne 0){throw 'UPDATE_ARTIFACT_DOWNLOAD_FAILED'}
        $packages=@(Get-ChildItem -LiteralPath $download -File -Recurse -Filter 'WindowsAdminMCP-win-x64.zip')
        $receipts=@(Get-ChildItem -LiteralPath $download -File -Recurse -Filter 'WindowsAdminMCP-update.json')
        if($packages.Count -ne 1 -or $receipts.Count -ne 1){throw 'UPDATE_ARTIFACT_CONTENT_INVALID'}
        $digest=Read-UpdateReceipt $receipts[0].FullName $main $runId
        $verified=& $installer -Action Verify -PackagePath $packages[0].FullName -ExpectedSha256 $digest|ConvertFrom-Json
        if($verified.status -ne 'VERIFIED' -or $verified.commit -cne $main){throw 'UPDATE_PACKAGE_IDENTITY'}
        # A moving main is not a license to install an unintended revision.
        if((Invoke-GhJson "repos/$repo/commits/main").sha -cne $main){throw 'UPDATE_MAIN_MOVED'}
    }else{
        if(-not $pending){throw 'UPDATE_PENDING_ROLLBACK_REQUIRED'}
        $priorPath=Join-Path $root '.rollback\manifest.json'
        if(-not(Test-Path -LiteralPath $priorPath -PathType Leaf)){throw 'UPDATE_ROLLBACK_TARGET_UNPROVEN'}
        $prior=Get-Content -LiteralPath $priorPath -Raw|ConvertFrom-Json -AsHashtable
        if($prior['store_schema'] -ne 4 -or $prior['commit'] -cnotmatch '^[a-f0-9]{40}$'){
            throw 'UPDATE_ROLLBACK_TARGET_UNPROVEN'
        }
    }
    $stage='drain'
    $d=Read-InstalledDoctor
    Assert-IdleStore $d
    Assert-ClosedClients @($d.tunnelPid|Where-Object {$null -ne $_})
    # The operator must suspend other clients during maintenance. Refuse busy
    # queues, then close the remote ingress and check the store again.
    $stopping=$true
    $null=& $startup -Action Stop -ExpectedNativeSha256 $installed.files.'native/WindowsAdminMCP.exe'
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while(@(Get-Process -Name 'tunnel-client-runtime' -ErrorAction SilentlyContinue).Count -and $clock.Elapsed.TotalSeconds -lt 15){Start-Sleep -Milliseconds 200}
    if(@(Get-Process -Name 'tunnel-client-runtime' -ErrorAction SilentlyContinue).Count){throw 'UPDATE_TUNNEL_NOT_QUIESCENT'}
    Assert-ClosedClients @()
    Assert-IdleStore (Read-InstalledDoctor)
    $stage='maintenance'
    Stop-Service WindowsAdminMCP -ErrorAction Stop
    $receipt=& $installer -Action Maintenance -ExpectedCommit $installed.commit|ConvertFrom-Json
    if($receipt.status -ne 'MAINTENANCE_READY'){throw 'UPDATE_MAINTENANCE_UNCONFIRMED'}
    $maintenance=$true
    $stage='swap'
    if($Action -eq 'update'){
        $swapped=& $installer -Action Install -PackagePath $packages[0].FullName -ExpectedSha256 $digest -ReplaceUnaccepted:$pending|ConvertFrom-Json
        if($swapped.status -ne 'INSTALLED_OFFLINE' -or $swapped.commit -cne $main){throw 'UPDATE_INSTALL_UNCONFIRMED'}
    }else{
        $rolled=& $installer -Action Rollback|ConvertFrom-Json
        if($rolled.status -ne 'ROLLED_BACK_OFFLINE'){throw 'UPDATE_ROLLBACK_UNCONFIRMED'}
    }
    $stage='prepare'
    $current=Read-Manifest
    $prepared=& $installer -Action Prepare -ExpectedCommit $current.commit|ConvertFrom-Json
    if($prepared.status -ne 'TRIAL_PREPARED'){throw 'UPDATE_PREPARE_UNCONFIRMED'}
    $stage='start'
    $null=& $startup -Action Start -ExpectedNativeSha256 $current.files.'native/WindowsAdminMCP.exe'
    $stage='readiness'
    $clock=[Diagnostics.Stopwatch]::StartNew()
    do{
        $after=Read-InstalledDoctor
        if($null -ne $after.service -and $after.service.status -eq 'OK' -and
            $after.service.versions.informational_version.EndsWith('+'+$current.commit)){break}
        Start-Sleep -Milliseconds 500
    }while($clock.Elapsed.TotalSeconds -lt 20)
    if($null -eq $after.service -or $after.service.status -ne 'OK' -or
        -not $after.service.versions.informational_version.EndsWith('+'+$current.commit)){throw 'UPDATE_SERVICE_READBACK_FAILED'}
    $ready=$false
    if($after.tun -eq 'READY'){
        $clock=[Diagnostics.Stopwatch]::StartNew()
        do{
            try{
                $state=& $startup -Action Status|ConvertFrom-Json
                $v=@($state.tunnel_instances)
                if($state.task -eq 'Running' -and $v.Count -eq 1 -and
                    $v[0].ExecutablePath -ieq (Join-Path $root 'client\tunnel-client-runtime.exe')){
                    $listener=@(Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort 8080 -State Listen -ErrorAction SilentlyContinue)
                    if($listener.Count -eq 1 -and $listener[0].OwningProcess -eq $v[0].ProcessId){
                        $health=Invoke-WebRequest -Uri 'http://127.0.0.1:8080/readyz' -NoProxy -TimeoutSec 3
                        if($health.StatusCode -eq 200 -and $health.Content.Trim() -ceq 'ready'){$ready=$true;break}
                    }
                }
            }catch{ # A readiness failure is never acceptance.
            }
            Start-Sleep -Milliseconds 500
        }while($clock.Elapsed.TotalSeconds -lt 30)
        if(-not $ready){$after=Read-InstalledDoctor}
    }
    $trialStatus=if($ready){'TRIAL_LOCAL_READY'}elseif($after.tun -ne 'READY'){'TRIAL_WAITING_FOR_TUN'}else{'TRIAL_NOT_READY'}
    [pscustomobject]@{status=$trialStatus;commit=$current.commit;tun=$after.tun;local_ready=$ready;remote_accepted=$false;
        next_action='verify eight MCP tools externally, then use -Action accept -RemoteAcceptanceConfirmed'}|ConvertTo-Json -Compress
}catch{
    # Restore transport only when no maintenance/swap was attempted. Once
    # offline maintenance begins the installer journals are the source of truth;
    # do not auto-accept, erase history or guess a rollback after interruption.
    if($stopping -and -not $maintenance -and $stage -eq 'drain' -and $before.task -eq 'Running'){
        try{$null=& $startup -Action Start -ExpectedNativeSha256 $installed.files.'native/WindowsAdminMCP.exe'}catch{Write-Warning 'UPDATE_PRE_SWAP_RESTART_FAILED'}
    }
    Write-Error ("UPDATE_FAILED stage="+$stage+" reason="+$_.Exception.Message) -ErrorAction Continue
    throw
}finally{
    if($download -and (Test-Path -LiteralPath $download)){Remove-Item -LiteralPath $download -Recurse -Force}
}
