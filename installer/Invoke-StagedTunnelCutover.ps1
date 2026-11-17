# Guarded one-shot transition from the accepted legacy installation.
# Run as the existing elevated interactive user, via an independent local task.
# Preflight never stops or starts a tunnel. Trial requires a remote confirmation.
[CmdletBinding()]
param(
    [ValidateSet('Preflight','Trial')][string]$Mode='Preflight',
    [Parameter(Mandatory)][string]$CandidateExe,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$CandidateSha256,
    [Parameter(Mandatory)][string]$TunnelExe,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$TunnelSha256,
    [Parameter(Mandatory)][int]$ExpectedOldPid,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$AttemptId,
    [ValidateRange(30,600)][int]$TrialSeconds=180
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'GuardedTrial.ps1')
$root=Join-Path $env:ProgramFiles 'WindowsAdminMCP'
$stage=Join-Path $env:ProgramFiles 'WindowsAdminMCP-candidate'
$oldExe=Join-Path $root 'client\tunnel-client-runtime.exe'
$oldServer=Join-Path $root 'windows-admin-mcp.ps1'
$oldOperator=Join-Path $root 'WindowsAdminMCP.ps1'
$pwsh=Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'
$credential=Join-Path $env:ProgramData 'WindowsAdminMCP\tunnel-credential.dpapi'
$statusFile=Join-Path $stage ("cutover-$AttemptId.json")
$commitFile=Join-Path $stage ("commit-$AttemptId.txt")
$session=[Diagnostics.Process]::GetCurrentProcess().SessionId
$admin=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $admin.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -or
    -not [Environment]::UserInteractive -or $session -eq 0){throw 'INTERACTIVE_ADMIN_REQUIRED'}

function Assert-Hash([string]$path,[string]$hash){
    if(-not(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $hash){throw 'INTEGRITY_FAILED'}
}
function Assert-StagePath([string]$path){
    if(-not [IO.Path]::GetFullPath($path).StartsWith($stage+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'NOT_STAGED_PATH'}
    $file=Get-Item -LiteralPath $path
    if(($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'REPARSE_POINT'}
}
function Get-Ready([int]$expected){
    try{
        $listen=@(Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort 8080 -State Listen -ErrorAction Stop)
        if($listen.Count -ne 1 -or $listen[0].OwningProcess -ne $expected){return $false}
        return (Invoke-WebRequest 'http://127.0.0.1:8080/readyz' -TimeoutSec 2).StatusCode -eq 200
    }catch{return $false}
}
function Write-State([string]$state,[int]$pidValue=0,[string]$reason=''){
    $data=@{attempt=$AttemptId;state=$state;pid=$pidValue;reason=$reason;utc=[DateTimeOffset]::UtcNow.ToString('O')}
    $temp=$statusFile+'.tmp'
    [IO.File]::WriteAllText($temp,($data|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temp,$statusFile,$true)
}
# Use the official versioned loopback component schema; never log command arguments,
# request bodies, credentials, or raw protocol frames. Keep only three bounded snapshots.
function Save-HealthEvidence([string]$phase,[int]$expected){
    try{
        if($phase -notin @('initial','latest','final')){return}
        $listen=@(Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort 8080 -State Listen -ErrorAction Stop)
        if($listen.Count -ne 1 -or $listen[0].OwningProcess -ne $expected){return}
        $response=Invoke-WebRequest 'http://127.0.0.1:8080/health?details=true' -TimeoutSec 3
        if($response.Content.Length -gt 262144){return}
        $snapshot=$response.Content|ConvertFrom-Json -AsHashtable -Depth 30
        if($snapshot.schema_version -ne 1){return}
        $evidence=[ordered]@{attempt=$AttemptId;pid=$expected;phase=$phase;utc=[DateTimeOffset]::UtcNow.ToString('O');schema_version=1;ready=$snapshot.ready;runtime=$snapshot.runtime;components=@{}}
        foreach($name in @('mcp','control-plane','response-delivery','queue','dispatcher','oauth')){
            if($snapshot.components.Contains($name)){$evidence.components[$name]=$snapshot.components[$name]}
        }
        $path=Join-Path $stage ("health-$AttemptId-$phase.json")
        $tmp=$path+'.tmp'
        [IO.File]::WriteAllText($tmp,($evidence|ConvertTo-Json -Depth 32 -Compress),[Text.UTF8Encoding]::new($false))
        [IO.File]::Move($tmp,$path,$true)
    }catch{
        # Diagnostic failure cannot prevent rollback.
    }
}
function Wait-Ready($proc,[int]$seconds){
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while($clock.Elapsed.TotalSeconds -lt $seconds){
        if($proc.HasExited){return $false}
        if(Get-Ready $proc.Id){return $true}
        Start-Sleep -Milliseconds 250
    }
    return $false
}
Assert-StagePath $CandidateExe
Assert-StagePath $TunnelExe
Assert-Hash $CandidateExe $CandidateSha256
Assert-Hash $TunnelExe $TunnelSha256
Assert-Hash $oldExe 'CB3B2AB34A77096750561B8D46319AF282096A169A90B9C8E36567DD4FE06837'
Assert-Hash $oldServer '8889FBF509E2364808070397B0A39FC54E91E201A4384A9AB2F0A9EA47ED47B5'
if(-not(Test-Path -LiteralPath $oldOperator -PathType Leaf)){throw 'ROLLBACK_OPERATOR_MISSING'}
$old=Get-Process -Id $ExpectedOldPid -ErrorAction Stop
if($old.Path -ine $oldExe -or $old.SessionId -ne $session -or -not(Get-Ready $old.Id)){throw 'OLD_TUNNEL_IDENTITY_MISMATCH'}
$other=@(Get-CimInstance Win32_Process -Filter "Name='tunnel-client-runtime.exe'")
if($other.Count -ne 1 -or $other[0].ProcessId -ne $old.Id){throw 'UNEXPECTED_TUNNEL_INSTANCE'}
$svc=Get-CimInstance Win32_Service -Filter "Name='WindowsAdminMCP'"
if($null -eq $svc -or $svc.State -ne 'Running' -or $svc.StartName -ne 'LocalSystem' -or
    (Get-Process -Id $svc.ProcessId).Path -ine $CandidateExe){throw 'CANDIDATE_SERVICE_MISMATCH'}
if(Test-Path -LiteralPath $commitFile){throw 'ATTEMPT_ALREADY_CONFIRMED'}
$encrypted=[IO.File]::ReadAllBytes($credential)
if($encrypted.Length -gt 65536){throw 'CONFIG_TOO_LARGE'}
$plain=[Security.Cryptography.ProtectedData]::Unprotect($encrypted,$null,[Security.Cryptography.DataProtectionScope]::LocalMachine)
try{$config=[Text.Encoding]::UTF8.GetString($plain)|ConvertFrom-Json}
finally{[Security.Cryptography.CryptographicOperations]::ZeroMemory($plain)}
if($config.version -ne 1 -or [string]$config.tunnel_id -cnotmatch '^tunnel_[0-9a-f]{32}$' -or
    [string]::IsNullOrWhiteSpace($config.runtime_key)){throw 'INVALID_TUNNEL_CONFIG'}
# Confirm that the rollback binding represents the running tunnel, not merely a valid local credential.
$oldLine=[string]$other[0].CommandLine
$oldServerBinding=$oldServer.Replace('\','/')
if(-not $oldLine.Contains([string]$config.tunnel_id) -or
    -not $oldLine.Contains('env:CONTROL_PLANE_API_KEY') -or
    -not $oldLine.Contains('pwsh -NoLogo -NoProfile -NonInteractive -File') -or
    -not $oldLine.Contains($oldServerBinding) -or
    -not $oldLine.Contains('127.0.0.1:8080') -or
    $oldLine.Contains([string]$config.runtime_key)){throw 'ROLLBACK_BINDING_MISMATCH'}
$oldLine=$null
if($Mode -eq 'Preflight'){
    $config=$null
    Write-Output 'CUTOVER_PREFLIGHT=PASS (no tunnel stopped)'
    return
}
$script:new=$null
$script:fallback=$null
$script:lastSnapshot=[DateTimeOffset]::MinValue
function Start-Vendor([string]$exe,[string]$binding){
    $info=[Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute=$false
    $info.CreateNoWindow=$true
    $info.WorkingDirectory=Split-Path -Parent $exe
    # credential remains in process environment; never command line, output, or state file.
    $info.Environment['CONTROL_PLANE_API_KEY']=[string]$config.runtime_key
    $info.Environment['MCP_STDIO_SEND_INITIALIZED_NOTIFICATION']='true'
    if($exe -ieq $oldExe){$info.Environment['WINDOWS_ADMIN_MCP_EXEC']='1'}
    else{[void]$info.Environment.Remove('WINDOWS_ADMIN_MCP_EXEC')}
    foreach($arg in @('run','--control-plane.tunnel-id',[string]$config.tunnel_id,'--control-plane.api-key',
        'env:CONTROL_PLANE_API_KEY','--mcp.command',$binding,'--health.listen-addr','127.0.0.1:8080')){
        $info.ArgumentList.Add($arg)
    }
    return [Diagnostics.Process]::Start($info)
}
$newBinding='command="'+$CandidateExe.Replace('\','/')+'" stdio,channel=main'
$oldBinding='command=pwsh -NoLogo -NoProfile -NonInteractive -File "'+$oldServer.Replace('\','/')+'",channel=main'
try{
    Write-State 'TRIAL_STARTING'
    $accepted=Invoke-GuardedTrial -TimeoutSeconds $TrialSeconds -StartCandidate {
        if($old.HasExited -or -not(Get-Ready $old.Id)){throw 'OLD_TUNNEL_CHANGED'}
        $old.Kill();if(-not $old.WaitForExit(10000)){throw 'OLD_TUNNEL_NOT_STOPPED'}
        $script:new=Start-Vendor $TunnelExe $newBinding
        if(-not(Wait-Ready $script:new 45)){throw 'CANDIDATE_NOT_READY'}
        Save-HealthEvidence 'initial' $script:new.Id
        $script:lastSnapshot=[DateTimeOffset]::UtcNow
        Write-State 'AWAITING_REMOTE_CONFIRMATION' $script:new.Id
    } -CheckCommit {
        if($script:new.HasExited){throw 'CANDIDATE_EXITED'}
        if(([DateTimeOffset]::UtcNow-$script:lastSnapshot).TotalSeconds -ge 10){
            Save-HealthEvidence 'latest' $script:new.Id
            $script:lastSnapshot=[DateTimeOffset]::UtcNow
        }
        if(-not(Test-Path -LiteralPath $commitFile -PathType Leaf)){return $false}
        if((Get-Item -LiteralPath $commitFile).Length -gt 128){return $false}
        return ([IO.File]::ReadAllText($commitFile).Trim() -ceq "$AttemptId/$($script:new.Id)" -and (Get-Ready $script:new.Id))
    } -Rollback {
        param($why)
        if($null -ne $script:new -and -not $script:new.HasExited){Save-HealthEvidence 'final' $script:new.Id}
        Write-State 'ROLLING_BACK' 0 $why
        if($null -ne $script:new -and -not $script:new.HasExited){
            $script:new.Kill();if(-not $script:new.WaitForExit(10000)){throw 'CANDIDATE_NOT_STOPPED'}
        }
        if(-not $old.HasExited -and (Get-Ready $old.Id)){Write-State 'ROLLED_BACK' $old.Id $why;return}
        $script:fallback=Start-Vendor $oldExe $oldBinding
        if(-not(Wait-Ready $script:fallback 45)){Write-State 'ROLLBACK_FAILED' $script:fallback.Id $why;throw 'ROLLBACK_NOT_READY'}
        Write-State 'ROLLED_BACK' $script:fallback.Id $why
    }
    if($accepted){Save-HealthEvidence 'final' $script:new.Id;Write-State 'COMMITTED' $script:new.Id}
    # Do not end the independent task while its vendor child is running.
    $running=if($accepted){$script:new}else{$script:fallback}
    $config=$null
    if($null -ne $running){$running.WaitForExit()}
}finally{
    $config=$null
    $old.Dispose()
    if($null -ne $script:new){$script:new.Dispose()}
    if($null -ne $script:fallback){$script:fallback.Dispose()}
}
