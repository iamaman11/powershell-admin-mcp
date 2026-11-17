# One native service and one interactive logon task; no second tunnel or network owner.
# Called by the final installer only after physical acceptance. Plan does not modify Windows.
[CmdletBinding()]
param([ValidateSet('Plan','Register','Status','Start','Stop')][string]$Action='Plan',
      [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedNativeSha256,
      [switch]$Accepted)
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
function New-Definition {
    $a=New-ScheduledTaskAction -Execute $native -Argument 'tunnel' -WorkingDirectory $root
    $p=New-ScheduledTaskPrincipal -UserId $user.Name -LogonType Interactive -RunLevel Highest
    $trigger=New-ScheduledTaskTrigger -AtLogOn -User $user.Name
    $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    New-ScheduledTask -Action $a -Principal $p -Trigger $trigger -Settings $settings
}
function Get-State {
    $svc=Get-CimInstance Win32_Service -Filter "Name='WindowsAdminMCP'"
    $task=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    $tunnels=@(Get-CimInstance Win32_Process -Filter "Name='tunnel-client-runtime.exe'"|Select-Object ProcessId,SessionId,ExecutablePath)
    [pscustomobject]@{service=$(if($svc){$svc.State});service_start=$(if($svc){$svc.StartMode});
        task=$(if($task){$task.State.ToString()});tunnel_instances=$tunnels;startup_owner='SCM + Task Scheduler'}
}
function Assert-Installed {
    if(-not $ExpectedNativeSha256){throw 'EXPECTED_NATIVE_DIGEST_REQUIRED'}
    if((Get-FileHash -LiteralPath $native -Algorithm SHA256).Hash -ne $ExpectedNativeSha256 -or
        (Get-FileHash -LiteralPath $vendor -Algorithm SHA256).Hash -ne 'DB590A52993C830A4C806108C3F25345EA986C6248210B86DF640E5081FF6A0B'){throw 'INSTALLATION_HASH_MISMATCH'}
    $svc=Get-CimInstance Win32_Service -Filter "Name='WindowsAdminMCP'"
    if(-not $svc -or $svc.PathName -ine ('"'+$native+'" service') -or $svc.StartName -ne 'LocalSystem'){throw 'SCM_IDENTITY_MISMATCH'}
    return $svc
}
function Assert-Task($task){
    if(-not $task -or @($task.Actions).Count -ne 1 -or @($task.Triggers).Count -ne 1 -or
        $task.Actions[0].Execute -ine $native -or $task.Actions[0].Arguments -cne 'tunnel' -or
        $task.Principal.LogonType.ToString() -ne 'Interactive' -or
        $task.Principal.RunLevel.ToString() -ne 'Highest' -or
        $task.Settings.MultipleInstances.ToString() -ne 'IgnoreNew' -or
        $task.Triggers[0].CimClass.CimClassName -ne 'MSFT_TaskLogonTrigger'){throw 'TASK_IDENTITY_MISMATCH'}
    $actual=$task.Principal.UserId
    $sid=if($actual -match '^S-1-'){$actual}else{([Security.Principal.NTAccount]::new($actual)).Translate([Security.Principal.SecurityIdentifier]).Value}
    if($sid -ne $user.User.Value){throw 'TASK_OWNER_MISMATCH'}
}
switch($Action){
    'Plan'{
        $d=New-Definition
        [pscustomobject]@{service=$svcName;service_start='Automatic';task=$name;trigger=$d.Triggers[0].CimClass.CimClassName;user=$d.Principal.UserId;logon=$d.Principal.LogonType.ToString();run_level=$d.Principal.RunLevel.ToString();execute=$d.Actions[0].Execute;arguments=$d.Actions[0].Arguments;multiple_instances=$d.Settings.MultipleInstances.ToString();restart_count=$d.Settings.RestartCount;mutated=$false}|ConvertTo-Json -Compress
    }
    'Status'{Get-State|ConvertTo-Json -Depth 4 -Compress}
    'Register'{
        if(-not $Accepted){throw 'PHYSICAL_ACCEPTANCE_REQUIRED'}
        $svc=Assert-Installed
        $task=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        if($task){Assert-Task $task}
        else{
            if(@(Get-CimInstance Win32_Process -Filter "Name='tunnel-client-runtime.exe'").Count -ne 0){throw 'EXISTING_TUNNEL_MUST_BE_STOPPED_BY_INSTALLER'}
            if(@(Get-ScheduledTask|Where-Object {$_.TaskName -like 'WindowsAdminMCP-R6-*' -and $_.State.ToString() -eq 'Running'}).Count){throw 'ACTIVE_TRIAL_TASK'}
        }
        $created=$false;$oldMode=$svc.StartMode
        try{
            if(-not $task){Register-ScheduledTask -TaskName $name -InputObject (New-Definition)|Out-Null;$created=$true}
            Assert-Task (Get-ScheduledTask -TaskName $name)
            Set-Service -Name $svcName -StartupType Automatic
            if((Get-CimInstance Win32_Service -Filter "Name='WindowsAdminMCP'").StartMode -ne 'Auto'){throw 'SCM_STARTUP_READBACK_FAILED'}
        }catch{
            if($created){Unregister-ScheduledTask -TaskName $name -Confirm:$false}
            if($oldMode -eq 'Manual'){Set-Service -Name $svcName -StartupType Manual}
            elseif($oldMode -eq 'Disabled'){Set-Service -Name $svcName -StartupType Disabled}
            elseif($oldMode -eq 'Auto'){Set-Service -Name $svcName -StartupType Automatic}
            throw
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
