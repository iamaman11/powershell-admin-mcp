# Isolated local probe. Never touches production tunnel, credentials or service.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'installer\GuardedTrial.ps1')
$test=[ordered]@{session=[Diagnostics.Process]::GetCurrentProcess().SessionId;interactive=[Environment]::UserInteractive;admin=([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator);checks=@()}
function Check([bool]$ok,[string]$name){
    if(-not $ok){throw "FAIL $name"}
    $test.checks+=@($name)
}
$script:child=$null
$script:rollback=$false
$script:reason=''
$script:commit=$false
$pwsh=Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'
function Start-Fixture {
    $si=[Diagnostics.ProcessStartInfo]::new($pwsh)
    $si.UseShellExecute=$false
    $si.CreateNoWindow=$true
    foreach($arg in @('-NoLogo','-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 60')){$si.ArgumentList.Add($arg)}
    $script:child=[Diagnostics.Process]::Start($si)
}
function Remove-Fixture {
    if($null -ne $script:child){
        if(-not $script:child.HasExited){$script:child.Kill($true);$script:child.WaitForExit()}
        $script:child.Dispose()
        $script:child=$null
    }
}
try {
    Check ($test.admin -and $test.interactive -and $test.session -gt 0) 'rollback.interactive_elevated'
    $result=Invoke-GuardedTrial -StartCandidate {Start-Fixture} -CheckCommit {$false} -Rollback {
        param($why)
        Remove-Fixture
        $script:rollback=$true;$script:reason=$why
    } -TimeoutSeconds 2
    Check (-not $result -and $script:rollback -and $script:reason -eq 'TRIAL_TIMEOUT' -and $null -eq $script:child) 'rollback.timeout_stops_candidate'
    $script:rollback=$false
    $result=Invoke-GuardedTrial -StartCandidate {Start-Fixture;throw 'FIXTURE_FAILURE'} -CheckCommit {$false} -Rollback {
        param($why)
        Remove-Fixture
        $script:rollback=$true;$script:reason=$why
    } -TimeoutSeconds 2
    Check (-not $result -and $script:rollback -and $script:reason -eq 'TRIAL_FAILED' -and $null -eq $script:child) 'rollback.exception_stops_candidate'
    $script:rollback=$false
    $result=Invoke-GuardedTrial -StartCandidate {Start-Fixture;throw 'CONTROL_PLANE_ACCESS_DENIED'} -CheckCommit {$false} -Rollback {
        param($why)
        Remove-Fixture
        $script:rollback=$true;$script:reason=$why
    } -TimeoutSeconds 2
    Check (-not $result -and $script:rollback -and $script:reason -eq 'CONTROL_PLANE_ACCESS_DENIED' -and $null -eq $script:child) 'rollback.access_denied_is_terminal'
    $script:rollback=$false
    $result=Invoke-GuardedTrial -StartCandidate {Start-Fixture} -CheckCommit {$true} -Rollback {$script:rollback=$true;Remove-Fixture} -TimeoutSeconds 2
    Check ($result -and -not $script:rollback -and -not $script:child.HasExited) 'rollback.explicit_commit_keeps_candidate'
    Remove-Fixture
    $test['status']='PASS'
} catch {
    $test['status']='FAIL'
    $test['failure_type']=$_.Exception.GetType().Name
} finally {
    Remove-Fixture
    [IO.File]::WriteAllText($Output,($test|ConvertTo-Json -Depth 4 -Compress),[Text.UTF8Encoding]::new($false))
}
if($test.status -ne 'PASS'){exit 1}
