param([Parameter(Mandatory)][string]$Exe)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$Exe=[IO.Path]::GetFullPath($Exe)
if (-not $env:GITHUB_ACTIONS -or $env:RUNNER_OS -ne 'Windows') {throw 'ISOLATED_WINDOWS_CI_ONLY'}
$name='WindowsAdminMcpR0Probe'
if(Get-Service -Name $name -ErrorAction SilentlyContinue){throw 'PROBE_SERVICE_ALREADY_EXISTS'}
$root=Join-Path $env:RUNNER_TEMP ('admin-mcp-r0-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$db=Join-Path $root 'state.db'
function Assert([bool]$condition,[string]$name){if(-not $condition){throw "FAIL $name"};Write-Host "PASS $name"}
function Read-State {
    $raw=& $Exe inspect $db
    if($LASTEXITCODE -ne 0){throw 'INSPECT_FAILED'}
    return ($raw|ConvertFrom-Json)
}
function Wait-State([string]$expected,[int]$starts){
    $deadline=[Diagnostics.Stopwatch]::StartNew()
    while($deadline.Elapsed.TotalSeconds -lt 20){
        try {$v=Read-State;if($v.state -eq $expected -and $v.starts -eq $starts){return $v}}catch{}
        Start-Sleep -Milliseconds 200
    }
    throw "STATE_TIMEOUT $expected $starts"
}
& $Exe job-object
if($LASTEXITCODE -ne 0){throw 'JOB_OBJECT_FAILED'}
try {
    $binary='"'+$Exe+'" service "'+$db+'"'
    New-Service -Name $name -BinaryPathName $binary -StartupType Automatic -DisplayName 'Isolated R0 probe' | Out-Null
    $svc=Get-CimInstance Win32_Service -Filter "Name='$name'"
    Assert ($svc.StartMode -eq 'Auto' -and $svc.StartName -eq 'LocalSystem') 'scm.auto.localsystem'
    Start-Service $name
    $a=Wait-State 'RUNNING' 1
    Assert ($a.checkpoint -eq 1 -and $a.identity -eq 'S-1-5-18') 'scm.actual_system_and_checkpoint_a'
    Stop-Service $name
    $s=Wait-State 'STOPPED' 1
    Assert ($s.checkpoint -eq 1) 'scm.graceful_stop'
    Start-Service $name
    $b=Wait-State 'RUNNING' 2
    Assert ($b.checkpoint -eq 1) 'sqlite.committed_checkpoint_not_repeated'
    $p=[Diagnostics.ProcessStartInfo]::new($Exe)
    $p.ArgumentList.Add('crash-write');$p.ArgumentList.Add($db);$p.UseShellExecute=$false;$p.CreateNoWindow=$true
    $writer=[Diagnostics.Process]::Start($p)
    try{if(-not $writer.WaitForExit(10000)){throw 'CRASH_WRITER_TIMEOUT'};Assert ($writer.ExitCode -ne 0) 'sqlite.writer_abrupt_exit'}finally{if(-not $writer.HasExited){$writer.Kill()};$writer.Dispose()}
    $after=Read-State
    Assert ($after.checkpoint -eq 1) 'sqlite.uncommitted_checkpoint_not_visible'
    $svc=Get-CimInstance Win32_Service -Filter "Name='$name'"
    Assert ($svc.ProcessId -gt 0) 'scm.live_process'
    Stop-Process -Id $svc.ProcessId -Force
    (Get-Service $name).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(20))
    Start-Service $name
    $c=Wait-State 'NEEDS_RECONCILIATION' 3
    Assert ($c.checkpoint -eq 1) 'scm.crash_restart_unknown_effect_not_replayed'
    Stop-Service $name
    $d=Read-State
    Assert ($d.state -eq 'NEEDS_RECONCILIATION') 'unknown_effect_survives_graceful_stop'
    Write-Host 'R0_NATIVE_MECHANISMS=PASS (no actual reboot or power-cut)'
} finally {
    $svc=Get-Service -Name $name -ErrorAction SilentlyContinue
    if($svc){if($svc.Status -ne 'Stopped'){Stop-Service $name -Force -ErrorAction SilentlyContinue};sc.exe delete $name|Out-Host}
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
