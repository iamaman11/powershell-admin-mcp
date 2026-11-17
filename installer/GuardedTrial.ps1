# Shared one-shot upgrade trial. No permanent supervisor or alternate tunnel.
function Invoke-GuardedTrial {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][scriptblock]$StartCandidate,
        [Parameter(Mandatory)][scriptblock]$CheckCommit,
        [Parameter(Mandatory)][scriptblock]$Rollback,
        [Parameter(Mandatory)][int]$TimeoutSeconds
    )
    if($TimeoutSeconds -lt 1 -or $TimeoutSeconds -gt 600){throw 'INVALID_TRIAL_TIMEOUT'}
    $committed=$false
    $failure='TRIAL_TIMEOUT'
    try {
        & $StartCandidate
        $clock=[Diagnostics.Stopwatch]::StartNew()
        while($clock.Elapsed.TotalSeconds -lt $TimeoutSeconds){
            if(& $CheckCommit){$committed=$true;break}
            Start-Sleep -Milliseconds 200
        }
    }catch{
        # Do not serialize exception messages, which may contain credentials.
        $failure='TRIAL_FAILED'
    }finally{
        if(-not $committed){& $Rollback $failure}
    }
    return $committed
}
