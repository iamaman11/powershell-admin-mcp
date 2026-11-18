# One native service and one interactive logon task; no second tunnel or network owner.
# Prepare one demand-start task for explicit trials; enable logon only after acceptance.
[CmdletBinding()]
param([ValidateSet('Plan','Prepare','Accept','RemoveTrial','Status','Start','Stop','Maintenance')][string]$Action='Plan',
      [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedNativeSha256,
      [switch]$RemoteAcceptanceConfirmed)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=Join-Path $env:ProgramFiles 'WindowsAdminMCP'
$native=Join-Path $root 'native\WindowsAdminMCP.exe'
$vendor=Join-Path $root 'client\tunnel-client-runtime.exe'
$name='WindowsAdminMCP-Tunnel'
$svcName='WindowsAdminMCP'
$user=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=[Security.Principal.WindowsPrincipal]::new($user)
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'ADMINISTRATOR_REQUIRED'}
function New-Definition([bool]$EnableLogon=$false) {
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
    $logon=[bool]($task -and @($task.Triggers).Count -eq 1 -and $task.Triggers[0].Enabled)
    $recovery=$null
    if($svc){try{$recovery=& $native maintenance recovery | ConvertFrom-Json;if($LASTEXITCODE -ne 0){$recovery=$null}}catch{}}
    $tunnels=@(Get-CimInstance Win32_Process -Filter "Name='tunnel-client-runtime.exe'"|Select-Object ProcessId,SessionId,ExecutablePath)
    [pscustomobject]@{service=$(if($svc){$svc.State});service_start=$(if($svc){$svc.StartMode});
        task=$(if($task){$task.State.ToString()});logon_enabled=$logon;tunnel_instances=$tunnels;startup_owner='SCM + Task Scheduler';definition_valid=$definitionValid;service_binding_valid=$serviceValid;
        recovery=$recovery;startup_ready=[bool]($definitionValid -and $serviceValid -and $svc.StartMode -eq 'Auto' -and $logon -and $recovery -and $recovery.ready)}
}
function Assert-Installed {
    if(-not $ExpectedNativeSha256){throw 'EXPECTED_NATIVE_DIGEST_REQUIRED'}
    if((Get-FileHash -LiteralPath $native -Algorithm SHA256).Hash -ne $ExpectedNativeSha256 -or
        (Get-FileHash -LiteralPath $vendor -Algorithm SHA256).Hash -ne 'DB590A52993C830A4C806108C3F25345EA986C6248210B86DF640E5081FF6A0B'){throw 'INSTALLATION_HASH_MISMATCH'}
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
    $vendors=@(Get-CimInstance Win32_Process -Filter "Name='tunnel-client-runtime.exe'")
    if($vendors.Count -ne 1 -or $vendors[0].ExecutablePath -ine $vendor -or $vendors[0].SessionId -eq 0){throw 'TRIAL_VENDOR_MISMATCH'}
    $v=$vendors[0]
    $parent=Get-CimInstance Win32_Process -Filter ("ProcessId="+$v.ParentProcessId)
    if(-not $parent -or $parent.ExecutablePath -ine $native -or $parent.SessionId -ne $v.SessionId -or
        $parent.CommandLine -cnotmatch '\s+tunnel\s*$'){throw 'TRIAL_PARENT_MISMATCH'}
    $listen=@(Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort 8080 -State Listen)
    if($listen.Count -ne 1 -or $listen[0].OwningProcess -ne $v.ProcessId){throw 'TRIAL_HEALTH_OWNER_MISMATCH'}
    $client=[Net.Http.HttpClientHandler]::new()
    $client.UseProxy=$false;$client.AllowAutoRedirect=$false
    $http=[Net.Http.HttpClient]::new($client)
    $http.Timeout=[TimeSpan]::FromSeconds(3);$http.MaxResponseContentBufferSize=262144
    try{
        $response=$http.GetAsync('http://127.0.0.1:8080/health?details=true').GetAwaiter().GetResult()
        try{
            if(-not $response.IsSuccessStatusCode){throw 'TRIAL_HEALTH_UNAVAILABLE'}
            $health=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult()|ConvertFrom-Json -AsHashtable
        }finally{$response.Dispose()}
    }finally{$http.Dispose()}
    if($health.schema_version -ne 1 -or $health.ready -ne $true -or $health.runtime.version -notmatch '^0\.0\.21(?:\+|$)'){throw 'TRIAL_HEALTH_INVALID'}
    $m=$health.components['mcp']
    $c=$health.components['control-plane']
    $delivery=$health.components['response-delivery']
    $required=@('powershell_exec','powershell_job','powershell_screenshot','powershell_status',
                'windows.list_windows','windows.inspect_window','windows.capture_window','windows.invoke_element')
    if($m.state -ne 'discovered' -or $m.details.child_state -ne 'running' -or $m.details.evidence -ne 'same_child' -or
        $m.details.tools_list.complete -ne $true -or $m.details.tools_list.ok -ne $true -or
        @($m.details.tools_list.tool_names).Count -ne $required.Count -or
        @($required|Where-Object {$_ -cnotin $m.details.tools_list.tool_names}).Count -ne 0){throw 'TRIAL_MCP_DISCOVERY_UNVERIFIED'}
    if($c.status -ne 'ok' -or $delivery.status -ne 'ok' -or $delivery.details.accepted -lt 1){throw 'TRIAL_RESPONSE_DELIVERY_UNVERIFIED'}
    $after=@(Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort 8080 -State Listen)
    if($after.Count -ne 1 -or $after[0].OwningProcess -ne $v.ProcessId){throw 'TRIAL_VENDOR_CHANGED'}
    return [pscustomobject]@{pid=$v.ProcessId;generation=$m.details.child_generation;accepted_responses=$delivery.details.accepted}
}

switch($Action){
    'Plan'{
        $d=New-Definition
        [pscustomobject]@{service=$svcName;service_start='Automatic';task=$name;trigger=$d.Triggers[0].CimClass.CimClassName;user=$d.Principal.UserId;logon=$d.Principal.LogonType.ToString();run_level=$d.Principal.RunLevel.ToString();execute=$d.Actions[0].Execute;arguments=$d.Actions[0].Arguments;multiple_instances=$d.Settings.MultipleInstances.ToString();restart_count=$d.Settings.RestartCount;trial_logon_enabled=[bool]$d.Triggers[0].Enabled;mutated=$false}|ConvertTo-Json -Compress
    }
    'Status'{Get-State|ConvertTo-Json -Depth 4 -Compress}
    'Prepare'{
        $svc=Assert-Installed
        $recovery=if($svc.State -eq 'Stopped'){& $native maintenance configure-recovery | ConvertFrom-Json}else{& $native maintenance recovery | ConvertFrom-Json}
        if($LASTEXITCODE -ne 0 -or -not $recovery.ready){throw 'SCM_RECOVERY_READBACK_FAILED'}
        $task=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        if($task){
            Assert-Task $task
            if($task.Triggers[0].Enabled){throw 'ALREADY_ACCEPTED'}
        }else{
            if($svc.StartMode -ne 'Manual'){throw 'MANUAL_SERVICE_REQUIRED'}
            if(@(Get-Process -Name 'tunnel-client-runtime' -ErrorAction SilentlyContinue).Count){throw 'ACTIVE_TUNNEL_MAINTENANCE_REQUIRED'}
            if(@(Get-ScheduledTask|Where-Object {$_.TaskName -like 'WindowsAdminMCP-R6-*' -and $_.State.ToString() -eq 'Running'}).Count){throw 'ACTIVE_TRIAL_TASK'}
            Register-ScheduledTask -TaskName $name -InputObject (New-Definition)|Out-Null
            try{
                $task=Get-ScheduledTask -TaskName $name
                Assert-Task $task
                if($task.Triggers[0].Enabled -or $task.State.ToString() -eq 'Running'){throw 'TRIAL_TASK_NOT_DEMAND_ONLY'}
            }catch{
                Unregister-ScheduledTask -TaskName $name -Confirm:$false
                throw
            }
        }
        Get-State|ConvertTo-Json -Depth 4 -Compress
    }
    'Accept'{
        if(-not $RemoteAcceptanceConfirmed){throw 'REMOTE_ACCEPTANCE_REQUIRED'}
        $receipt=Assert-RemoteEvidence
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
            $after=Assert-RemoteEvidence
            if($after.pid -ne $receipt.pid -or $after.generation -ne $receipt.generation){throw 'TRIAL_CHANGED_DURING_ACCEPTANCE'}
        }catch{
            if(-not $wasEnabled){
                $t=New-ScheduledTaskTrigger -AtLogOn -User $user.Name
                $t.Enabled=$false
                Set-ScheduledTask -TaskName $name -Trigger $t|Out-Null
            }
            if($oldMode -eq 'Manual'){Set-Service -Name $svcName -StartupType Manual}
            throw
        }
        [pscustomobject]@{status='AUTOSTART_ACCEPTED';pid=$receipt.pid;generation=$receipt.generation;accepted_responses=$receipt.accepted_responses}|ConvertTo-Json -Compress
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
        Assert-Task (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue)
        Start-Service -Name $svcName
        Start-ScheduledTask -TaskName $name
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
