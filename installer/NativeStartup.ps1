# One native service and one interactive logon task, supervising independent tunnels.
# Local durability is independent of external trial acceptance. The service
# and existing interactive logon task recover normally while the verified
# candidate still retains its unaccepted rollback journal.
[CmdletBinding()]
param([ValidateSet('Plan','Prepare','Accept','RemoveTrial','Status','Profiles','Start','Stop','Maintenance')][string]$Action='Plan',
      [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedNativeSha256,
      [switch]$RemoteAcceptanceConfirmed)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
# Source layout: installer/*.ps1. Installed layout: native/NativeStartup.ps1.
$pinFile=Join-Path $PSScriptRoot 'VendorLock.Generated.ps1'
if(-not(Test-Path -LiteralPath $pinFile -PathType Leaf)){$pinFile=Join-Path $PSScriptRoot 'installer/VendorLock.Generated.ps1'}
. $pinFile
$root=Join-Path $env:ProgramFiles 'WindowsAdminMCP'
$native=Join-Path $root 'native\WindowsAdminMCP.exe'
$vendor=Join-Path $root 'client\tunnel-client-runtime.exe'
$name='WindowsAdminMCP-Tunnel'
$svcName='WindowsAdminMCP'
$configRoot=Join-Path $env:ProgramData 'WindowsAdminMCP'
$user=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=[Security.Principal.WindowsPrincipal]::new($user)
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'ADMINISTRATOR_REQUIRED'}
function Assert-ProfileAcl([string]$path){
    $item=Get-Item -LiteralPath $path -ErrorAction Stop
    if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'TUNNEL_CONFIG_REPARSE_POINT'}
    $acl=Get-Acl -LiteralPath $path
    foreach($rule in @($acl.Access)){
        if($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544')){
            throw 'TUNNEL_CONFIG_ACL_INVALID'
        }
    }
}
function Get-ExpectedTunnels {
    Assert-ProfileAcl $configRoot
    $files=@([pscustomobject]@{id='primary';path=(Join-Path $configRoot 'tunnel-credential.dpapi')})
    $extra=Join-Path $configRoot 'tunnel-profiles'
    if(Test-Path -LiteralPath $extra -PathType Container){
        Assert-ProfileAcl $extra
        foreach($file in @(Get-ChildItem -LiteralPath $extra -File -Filter '*.dpapi')){
            if($file.Name -cnotmatch '^([a-z0-9][a-z0-9-]{0,31})\.dpapi$' -or $Matches[1] -eq 'primary'){throw 'TUNNEL_PROFILE_NAME_INVALID'}
            $files+=[pscustomobject]@{id=$Matches[1];path=$file.FullName}
        }
    }
    $ids=@{};$tunnels=@{};$ports=@{};$results=@()
    foreach($entry in $files){
        Assert-ProfileAcl $entry.path
        if((Get-Item -LiteralPath $entry.path).Length -gt 65536){throw 'TUNNEL_CREDENTIAL_SIZE_INVALID'}
        $plain=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($entry.path),$null,[Security.Cryptography.DataProtectionScope]::LocalMachine)
        try{
            $cred=[Text.Encoding]::UTF8.GetString($plain)|ConvertFrom-Json
            # Legacy primary credentials contain only version/tunnel_id/runtime_key.
            # StrictMode throws on absent PSCustomObject properties, so distinguish
            # missing optional fields from explicit invalid values.
            $profileProperty=$cred.PSObject.Properties['profile_id']
            $portProperty=$cred.PSObject.Properties['health_port']
            $id=if($null -eq $profileProperty){'primary'}else{[string]$profileProperty.Value}
            $port=if($null -eq $portProperty){8080}else{[int]$portProperty.Value}
            if($cred.version -ne 1 -or $id -cne $entry.id -or $cred.tunnel_id -cnotmatch '^tunnel_[0-9a-f]{32}$' -or
                [string]::IsNullOrWhiteSpace([string]$cred.runtime_key) -or $port -lt 1024 -or $port -gt 65535 -or
                ($id -eq 'primary') -ne ($port -eq 8080) -or $ids.ContainsKey($id) -or $tunnels.ContainsKey($cred.tunnel_id) -or $ports.ContainsKey($port)){
                throw 'TUNNEL_PROFILE_CONFLICT'
            }
            $ids[$id]=$true;$tunnels[$cred.tunnel_id]=$true;$ports[$port]=$true
            $results+=[pscustomobject]@{profile_id=$id;health_port=$port}
        }finally{[Security.Cryptography.CryptographicOperations]::ZeroMemory($plain)}
    }
    return @($results|Sort-Object profile_id)
}
function New-Definition([bool]$EnableLogon=$true) {
    $a=New-ScheduledTaskAction -Execute $native -Argument 'tunnel' -WorkingDirectory $root
    $p=New-ScheduledTaskPrincipal -UserId $user.Name -LogonType Interactive -RunLevel Highest
    $trigger=New-ScheduledTaskTrigger -AtLogOn -User $user.Name
    $trigger.Enabled=$EnableLogon
    $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    New-ScheduledTask -Action $a -Principal $p -Trigger $trigger -Settings $settings
}
function Get-State {
    $svc=Get-CimInstance Win32_Service -Filter "Name='WindowsAdminMCP'"
    $task=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    $definitionValid=$false
    if($task){try{Assert-Task $task -ReadOnly;$definitionValid=$true}catch{}}
    $serviceValid=[bool]($svc -and $svc.PathName -ieq ('"'+$native+'" service') -and $svc.StartName -eq 'LocalSystem')
    $logon=[bool]($task -and @($task.Triggers).Count -eq 1 -and $task.Triggers[0].Enabled -and
        $task.State.ToString() -ne 'Disabled')
    $recovery=$null
    if($svc){try{$recovery=& $native maintenance recovery | ConvertFrom-Json;if($LASTEXITCODE -ne 0){$recovery=$null}}catch{}}
    $tunnels=@(Get-CimInstance Win32_Process -Filter "Name='tunnel-client-runtime.exe'"|Select-Object ProcessId,SessionId,ExecutablePath)
    $profileState=@();$profileError=$null
    try{
        foreach($profile in @(Get-ExpectedTunnels)){
            $listen=@(Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $profile.health_port -State Listen -ErrorAction SilentlyContinue)
            $ownerPid=if($listen.Count -eq 1 -and @($tunnels|Where-Object {$_.ProcessId -eq $listen[0].OwningProcess}).Count -eq 1){$listen[0].OwningProcess}else{$null}
            $profileState+=[pscustomobject]@{profile_id=$profile.profile_id;health_port=$profile.health_port;vendor_pid=$ownerPid;running=($null -ne $ownerPid)}
        }
    }catch{$profileError='TUNNEL_CONFIGURATION_INVALID'}
    [pscustomobject]@{service=$(if($svc){$svc.State});service_start=$(if($svc){$svc.StartMode});
        task=$(if($task){$task.State.ToString()});logon_enabled=$logon;tunnel_instances=$tunnels;tunnel_profiles=$profileState;tunnel_config_error=$profileError;startup_owner='SCM + Task Scheduler';definition_valid=$definitionValid;service_binding_valid=$serviceValid;
        recovery=$recovery;startup_ready=[bool]($definitionValid -and $serviceValid -and $svc.StartMode -eq 'Auto' -and $logon -and $recovery -and $recovery.ready)}
}
function Assert-Installed {
    if(-not $ExpectedNativeSha256){throw 'EXPECTED_NATIVE_DIGEST_REQUIRED'}
    if((Get-FileHash -LiteralPath $native -Algorithm SHA256).Hash -ne $ExpectedNativeSha256 -or
        (Get-FileHash -LiteralPath $vendor -Algorithm SHA256).Hash -cne $VendorLockExeSha256){throw 'INSTALLATION_HASH_MISMATCH'}
    $svc=Get-CimInstance Win32_Service -Filter "Name='WindowsAdminMCP'"
    if(-not $svc -or $svc.PathName -ine ('"'+$native+'" service') -or $svc.StartName -ne 'LocalSystem'){throw 'SCM_IDENTITY_MISMATCH'}
    return $svc
}
function Resolve-Sid([string]$account){
    if($account -match '^S-1-'){return $account}
    return ([Security.Principal.NTAccount]::new($account)).Translate([Security.Principal.SecurityIdentifier]).Value
}
function Assert-Task($task,[switch]$ReadOnly){
    if(-not $task -or @($task.Actions).Count -ne 1 -or @($task.Triggers).Count -ne 1 -or
        $task.Actions[0].Execute -ine $native -or $task.Actions[0].Arguments -cne 'tunnel' -or
        $task.Principal.LogonType.ToString() -ne 'Interactive' -or
        $task.Principal.RunLevel.ToString() -ne 'Highest' -or
        $task.Settings.MultipleInstances.ToString() -ne 'IgnoreNew' -or
        $task.Triggers[0].CimClass.CimClassName -ne 'MSFT_TaskLogonTrigger' -or
        $task.Actions[0].WorkingDirectory -ine $root -or $task.Settings.RestartCount -ne 3){throw 'TASK_IDENTITY_MISMATCH'}
    $actual=$task.Principal.UserId
    $actualSid=Resolve-Sid $actual
    if((Resolve-Sid $task.Triggers[0].UserId) -ne $actualSid -or (-not $ReadOnly -and $actualSid -ne $user.User.Value)){throw 'TASK_OWNER_MISMATCH'}
}
# Read only official bounded loopback diagnostics. Readiness alone is not acceptance.
function Assert-RemoteEvidence {
    $task=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    Assert-Task $task
    if($task.State.ToString() -ne 'Running'){throw 'TRIAL_TASK_NOT_RUNNING'}
    $recovery=& $native maintenance recovery | ConvertFrom-Json
    if($LASTEXITCODE -ne 0 -or -not $recovery.ready){throw 'SCM_RECOVERY_READBACK_FAILED'}
    $service=Assert-Installed
    if($service.State -ne 'Running' -or $service.ProcessId -eq 0){throw 'SERVICE_NOT_RUNNING'}
    if((Get-Process -Id $service.ProcessId).Path -ine $native){throw 'SERVICE_PROCESS_MISMATCH'}
    $profiles=@(Get-ExpectedTunnels)
    $vendors=@(Get-CimInstance Win32_Process -Filter "Name='tunnel-client-runtime.exe'")
    if($vendors.Count -ne $profiles.Count){throw 'TRIAL_VENDOR_COUNT_MISMATCH'}
    $parents=@($vendors|ForEach-Object {
        if($_.ExecutablePath -ine $vendor -or $_.SessionId -eq 0){throw 'TRIAL_VENDOR_MISMATCH'}
        $p=Get-CimInstance Win32_Process -Filter ("ProcessId="+$_.ParentProcessId)
        if(-not $p -or $p.ExecutablePath -ine $native -or $p.SessionId -ne $_.SessionId -or
            $p.CommandLine -cnotmatch '\s+tunnel\s*$'){throw 'TRIAL_PARENT_MISMATCH'}
        $p.ProcessId
    }|Select-Object -Unique)
    if($parents.Count -ne 1){throw 'TRIAL_SUPERVISOR_MISMATCH'}
    $client=[Net.Http.HttpClientHandler]::new()
    $client.UseProxy=$false;$client.AllowAutoRedirect=$false
    $http=[Net.Http.HttpClient]::new($client)
    $http.Timeout=[TimeSpan]::FromSeconds(3);$http.MaxResponseContentBufferSize=262144
    try{
        $results=@()
        foreach($profile in $profiles){
            $port=[int]$profile.health_port
            $listen=@(Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $port -State Listen)
            if($listen.Count -ne 1 -or @($vendors|Where-Object {$_.ProcessId -eq $listen[0].OwningProcess}).Count -ne 1){throw 'TRIAL_HEALTH_OWNER_MISMATCH'}
            $response=$http.GetAsync("http://127.0.0.1:$port/health?details=true").GetAwaiter().GetResult()
            try{
                if(-not $response.IsSuccessStatusCode){throw 'TRIAL_HEALTH_UNAVAILABLE'}
                $health=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult()|ConvertFrom-Json -AsHashtable
            }finally{$response.Dispose()}
            if($health.schema_version -ne 1 -or $health.ready -ne $true -or $health.runtime.version -notmatch ('^'+[regex]::Escape($VendorLockVersion)+'(?:\+|$)')){throw 'TRIAL_HEALTH_INVALID'}
            $m=$health.components['mcp'];$c=$health.components['control-plane'];$delivery=$health.components['response-delivery']
            if($m.details.child_state -ne 'running' -or $m.details.evidence -ne 'same_child' -or
                [string]::IsNullOrWhiteSpace([string]$m.details.child_generation)){throw 'TRIAL_MCP_CHILD_UNVERIFIED'}
            if($c.status -ne 'ok' -or $delivery.status -ne 'ok' -or $delivery.details.accepted -lt 1){throw 'TRIAL_RESPONSE_DELIVERY_UNVERIFIED'}
            $after=@(Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $port -State Listen)
            if($after.Count -ne 1 -or $after[0].OwningProcess -ne $listen[0].OwningProcess){throw 'TRIAL_VENDOR_CHANGED'}
            $results+=[pscustomobject]@{profile_id=$profile.profile_id;pid=$listen[0].OwningProcess;generation=$m.details.child_generation;accepted_responses=$delivery.details.accepted}
        }
        return $results
    }finally{$http.Dispose()}
}

switch($Action){
    'Plan'{
        $d=New-Definition
        [pscustomobject]@{service=$svcName;service_start='Automatic';task=$name;trigger=$d.Triggers[0].CimClass.CimClassName;user=$d.Principal.UserId;logon=$d.Principal.LogonType.ToString();run_level=$d.Principal.RunLevel.ToString();execute=$d.Actions[0].Execute;arguments=$d.Actions[0].Arguments;multiple_instances=$d.Settings.MultipleInstances.ToString();restart_count=$d.Settings.RestartCount;trial_logon_enabled=[bool]$d.Triggers[0].Enabled;mutated=$false}|ConvertTo-Json -Compress
    }
    'Status'{Get-State|ConvertTo-Json -Depth 4 -Compress}
    'Profiles'{Get-ExpectedTunnels|ConvertTo-Json -Compress}
    'Prepare'{
        $svc=Assert-Installed
        $recovery=if($svc.State -eq 'Stopped'){& $native maintenance configure-recovery | ConvertFrom-Json}else{& $native maintenance recovery | ConvertFrom-Json}
        if($LASTEXITCODE -ne 0 -or -not $recovery.ready){throw 'SCM_RECOVERY_READBACK_FAILED'}
        $task=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        if($task){Assert-Task $task}
        $oldMode=$svc.StartMode
        $oldEnabled=[bool]($task -and $task.Triggers[0].Enabled)
        $oldTaskDisabled=[bool]($task -and $task.State.ToString() -eq 'Disabled')
        $created=$false
        if(-not $task){
            if($svc.StartMode -ne 'Manual'){throw 'MANUAL_SERVICE_REQUIRED'}
            if(@(Get-Process -Name 'tunnel-client-runtime' -ErrorAction SilentlyContinue).Count){throw 'ACTIVE_TUNNEL_MAINTENANCE_REQUIRED'}
            if(@(Get-ScheduledTask|Where-Object {$_.TaskName -like 'WindowsAdminMCP-R6-*' -and $_.State.ToString() -eq 'Running'}).Count){throw 'ACTIVE_TRIAL_TASK'}
        }
        try{
            if(-not $task){
                Register-ScheduledTask -TaskName $name -InputObject (New-Definition)|Out-Null
                $created=$true
            }elseif(-not $oldEnabled){
                $trigger=New-ScheduledTaskTrigger -AtLogOn -User $user.Name
                $trigger.Enabled=$true
                Set-ScheduledTask -TaskName $name -Trigger $trigger|Out-Null
            }
            if($oldTaskDisabled){Enable-ScheduledTask -TaskName $name|Out-Null}
            Set-Service -Name $svcName -StartupType Automatic
            $ready=Get-State
            if(-not $ready.startup_ready){throw 'TRIAL_AUTOSTART_READBACK_FAILED'}
        }catch{
            if($oldMode -eq 'Manual'){
                try{Set-Service -Name $svcName -StartupType Manual}catch{Write-Warning 'TRIAL_SERVICE_MODE_RESTORE_FAILED'}
            }
            if($created){
                try{Unregister-ScheduledTask -TaskName $name -Confirm:$false}catch{Write-Warning 'TRIAL_TASK_REMOVE_FAILED'}
            }elseif($task -and -not $oldEnabled){
                try{
                    $trigger=New-ScheduledTaskTrigger -AtLogOn -User $user.Name
                    $trigger.Enabled=$false
                    Set-ScheduledTask -TaskName $name -Trigger $trigger|Out-Null
                }catch{Write-Warning 'TRIAL_TASK_TRIGGER_RESTORE_FAILED'}
            }
            if($oldTaskDisabled){
                try{Disable-ScheduledTask -TaskName $name|Out-Null}catch{Write-Warning 'TRIAL_TASK_STATE_RESTORE_FAILED'}
            }
            throw
        }
        Get-State|ConvertTo-Json -Depth 4 -Compress
    }
    'Accept'{
        # Compatibility repair for older demand-only trials is allowed, but
        # fresh verified candidates have already enabled durable startup.
        # External evidence, not these registration bits, authorizes Accept.
        if(-not $RemoteAcceptanceConfirmed){throw 'REMOTE_ACCEPTANCE_REQUIRED'}
        $receipt=@(Assert-RemoteEvidence|Sort-Object profile_id)
        $task=Get-ScheduledTask -TaskName $name
        $svc=Assert-Installed
        if($svc.StartMode -notin @('Manual','Auto')){throw 'SCM_STARTUP_UNEXPECTED'}
        $wasEnabled=[bool]$task.Triggers[0].Enabled
        $oldMode=$svc.StartMode
        try{
            if(-not $wasEnabled){
                $t=New-ScheduledTaskTrigger -AtLogOn -User $user.Name
                $t.Enabled=$true
                Set-ScheduledTask -TaskName $name -Trigger $t|Out-Null
            }
            $registered=Get-ScheduledTask -TaskName $name
            Assert-Task $registered
            if(-not $registered.Triggers[0].Enabled){throw 'LOGON_READBACK_FAILED'}
            Set-Service -Name $svcName -StartupType Automatic
            if((Get-CimInstance Win32_Service -Filter "Name='WindowsAdminMCP'").StartMode -ne 'Auto'){throw 'SCM_STARTUP_READBACK_FAILED'}
            $after=@(Assert-RemoteEvidence|Sort-Object profile_id)
            if($after.Count -ne $receipt.Count){throw 'TRIAL_CHANGED_DURING_ACCEPTANCE'}
            for($i=0;$i -lt $receipt.Count;$i++){
                if($after[$i].profile_id -cne $receipt[$i].profile_id -or
                    $after[$i].pid -ne $receipt[$i].pid -or
                    $after[$i].generation -cne $receipt[$i].generation){throw 'TRIAL_CHANGED_DURING_ACCEPTANCE'}
            }
        }catch{
            if(-not $wasEnabled){
                $t=New-ScheduledTaskTrigger -AtLogOn -User $user.Name
                $t.Enabled=$false
                Set-ScheduledTask -TaskName $name -Trigger $t|Out-Null
            }
            if($oldMode -eq 'Manual'){Set-Service -Name $svcName -StartupType Manual}
            throw
        }
        [pscustomobject]@{status='REMOTE_ACCEPTED';tunnels=$receipt;pid=$receipt[0].pid;generation=$receipt[0].generation;accepted_responses=$receipt[0].accepted_responses}|ConvertTo-Json -Depth 5 -Compress
    }
    'Maintenance'{
        # Explicit offline transition only. The operator must first finish jobs
        # and stop the service and transport using an independent local console.
        $svc=Assert-Installed
        if($svc.State -ne 'Stopped' -or $svc.ProcessId -ne 0){throw 'SCM_MAINTENANCE_REQUIRED'}
        if(@(Get-Process -Name 'tunnel-client-runtime' -ErrorAction SilentlyContinue).Count){throw 'ACTIVE_TUNNEL_MAINTENANCE_REQUIRED'}
        if(@(Get-ScheduledTask|Where-Object {$_.TaskName -like 'WindowsAdminMCP-R6-*' -and $_.State.ToString() -eq 'Running'}).Count){throw 'ACTIVE_TRIAL_TASK'}
        $task=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        if($task){
            Assert-Task $task
            if($task.State.ToString() -notin @('Ready','Disabled')){throw 'TASK_MAINTENANCE_REQUIRED'}
        }
        # Disable automatic launches before touching files. A partial transition
        # leaves the installation offline, never automatically activates it.
        Set-Service -Name $svcName -StartupType Manual
        if($task){
            Disable-ScheduledTask -TaskName $name|Out-Null
            $disabled=Get-ScheduledTask -TaskName $name
            Assert-Task $disabled
            if($disabled.State.ToString() -ne 'Disabled'){throw 'TASK_DISABLE_READBACK_FAILED'}
        }
        $svc=Assert-Installed
        if($svc.State -ne 'Stopped' -or $svc.ProcessId -ne 0 -or $svc.StartMode -ne 'Manual' -or
            @(Get-Process -Name 'tunnel-client-runtime' -ErrorAction SilentlyContinue).Count){throw 'MAINTENANCE_READBACK_FAILED'}
        if($task){Unregister-ScheduledTask -TaskName $name -Confirm:$false}
        if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){throw 'TASK_REMOVAL_READBACK_FAILED'}
        Get-State|ConvertTo-Json -Depth 4 -Compress
    }
    'RemoveTrial'{
        $task=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        if($task){
            Assert-Task $task
            if($task.Triggers[0].Enabled -or $task.State.ToString() -eq 'Running'){throw 'TRIAL_NOT_QUIESCENT'}
            Unregister-ScheduledTask -TaskName $name -Confirm:$false
        }
        Get-State|ConvertTo-Json -Depth 4 -Compress
    }
    'Start'{
        $null=Assert-Installed
        # Start is also the explicit self-repair entry point for a verified
        # installation left in the old Manual/disabled-trigger trial state.
        if(-not (Get-State).startup_ready){
            & $PSCommandPath -Action Prepare -ExpectedNativeSha256 $ExpectedNativeSha256|Out-Null
        }
        Assert-Task (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue)
        $svc=Get-CimInstance Win32_Service -Filter "Name='WindowsAdminMCP'"
        if($svc.State -ne 'Running'){Start-Service -Name $svcName}
        $task=Get-ScheduledTask -TaskName $name
        if($task.State.ToString() -ne 'Running'){Start-ScheduledTask -TaskName $name}
        Get-State|ConvertTo-Json -Depth 4 -Compress
    }
    'Stop'{
        $null=Assert-Installed
        Assert-Task (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue)
        # Do not stop the durable job service merely because the interactive tunnel is stopped.
        Stop-ScheduledTask -TaskName $name
        Get-State|ConvertTo-Json -Depth 4 -Compress
    }
}
