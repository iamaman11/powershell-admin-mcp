# Read-only / inert physical smoke check of the staged native application.
# Run in the authorized elevated interactive Windows session. No network,
# install, service stop, reboot, credential access, or image persistence.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Exe)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$Exe=(Resolve-Path -LiteralPath $Exe).Path
$svc=Get-CimInstance Win32_Service -Filter "Name='WindowsAdminMCP'"
if($null -eq $svc -or $svc.State -ne 'Running' -or $svc.PathName -cne ('"'+$Exe+'" service')){
    throw 'CANDIDATE_SERVICE_IDENTITY_MISMATCH'
}
$expectedPid=[int]$svc.ProcessId
$expectedExe=(Get-Process -Id $expectedPid -ErrorAction Stop).Path
if($expectedExe -ine $Exe){throw 'CANDIDATE_PROCESS_PATH_MISMATCH'}
$clients=[Collections.Generic.List[Diagnostics.Process]]::new()
$meta=@{'io.modelcontextprotocol/protocolVersion'='2026-07-28';'io.modelcontextprotocol/clientCapabilities'=@{};'io.modelcontextprotocol/clientInfo'=@{name='installed-native-smoke';version='1'}}
function Assert($ok,[string]$label){if(-not $ok){throw "FAIL $label"};Write-Host "PASS $label"}
function Start-Frontend {
    $si=[Diagnostics.ProcessStartInfo]::new($Exe)
    $si.ArgumentList.Add('stdio')
    $si.UseShellExecute=$false;$si.RedirectStandardInput=$true;$si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true
    $si.StandardInputEncoding=[Text.UTF8Encoding]::new($false)
    $si.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
    $p=[Diagnostics.Process]::Start($si);$clients.Add($p)
    $null=$p.StandardError.ReadToEndAsync()
    return $p
}
function Send($p,$message){
    $p.StandardInput.WriteLine(($message|ConvertTo-Json -Depth 16 -Compress));$p.StandardInput.Flush()
}
function Call($p,[int]$id,[string]$method,$params){
    Send $p @{jsonrpc='2.0';id=$id;method=$method;params=$params}
    $clock=[Diagnostics.Stopwatch]::StartNew()
    do {
        $line=$p.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(15)).GetAwaiter().GetResult()
        if($null -eq $line){throw "RPC_EOF $method"}
        $msg=$line|ConvertFrom-Json -AsHashtable
        if($msg.Contains('id') -and $msg.id -eq $id){
            if($msg.Contains('error')){throw "RPC_ERROR $method code=$($msg.error.code)"}
            return $msg.result
        }
    }while($clock.Elapsed.TotalSeconds -lt 15)
    throw "RPC_TIMEOUT $method"
}
function Tool($p,$id,$name,$arguments,$modern=$true) {
    $params=@{name=$name;arguments=$arguments}
    if($modern){$params._meta=$meta}
    return Call $p $id 'tools/call' $params
}
try {
    $a=Start-Frontend;$b=Start-Frontend;$c=Start-Frontend
    $i=Call $a 1 'initialize' @{protocolVersion='2025-06-18';capabilities=@{};clientInfo=@{name='installed-native-smoke';version='1'}}
    Assert ($i.protocolVersion -eq '2025-06-18') 'installed.legacy'
    Send $a @{jsonrpc='2.0';method='notifications/initialized'}
    foreach($p in @($b,$c)){
        $d=Call $p 1 'server/discover' @{_meta=$meta}
        Assert ('2026-07-28' -in $d.supportedVersions) 'installed.modern'
    }
    foreach($entry in @(@{p=$a;modern=$false},@{p=$b;modern=$true},@{p=$c;modern=$true})){
        $params=@{};if($entry.modern){$params._meta=$meta}
        $list=Call $entry.p 2 'tools/list' $params
        Assert (@($list.tools).Count -eq 4 -and @($list.tools.name|Where-Object {$_ -in @('powershell_status','powershell_exec','powershell_job','powershell_screenshot')}).Count -eq 4) 'installed.four_tools'
        $status=(Tool $entry.p 3 'powershell_status' @{} $entry.modern).structuredContent
        Assert ($status.status -eq 'OK' -and $status.service_pid -eq $expectedPid -and $status.execution_sid -eq 'S-1-5-18') 'installed.shared_service'
    }
    $key='physical-'+[guid]::NewGuid().ToString('N')
    $exec=(Tool $a 4 'powershell_exec' @{command="Write-Output 'native Здравствуй'; [Security.Principal.WindowsIdentity]::GetCurrent().User.Value";timeout_seconds=10;operation_key=$key} $false).structuredContent
    Assert ($exec.status -eq 'COMPLETED' -and $exec.stdout.Contains('native Здравствуй') -and $exec.stdout.Contains('S-1-5-18')) 'installed.real_exec'
    $jobKey=$key+'-job'
    $jobArgs=@{action='submit';operation_key=$jobKey;command="Start-Sleep -Seconds 2; Write-Output 'native-job'";timeout_seconds=10}
    $job=(Tool $b 4 'powershell_job' $jobArgs).structuredContent
    Assert ($job.status -eq 'OK' -and $job.job.job_id) 'installed.durable_submit'
    $dup=(Tool $c 4 'powershell_job' $jobArgs).structuredContent
    Assert ($dup.status -eq 'OK' -and $dup.job.job_id -eq $job.job.job_id) 'installed.shared_idempotency'
    $a.Kill($true);$a.WaitForExit()
    $clock=[Diagnostics.Stopwatch]::StartNew();$id=100
    do{
        $result=(Tool $c $id 'powershell_job' @{action='logs';job_id=$job.job.job_id}).structuredContent
        $id++
        if($result.job.state -eq 'COMPLETED'){break}
        Start-Sleep -Milliseconds 150
    }while($clock.Elapsed.TotalSeconds -lt 10)
    Assert ($result.job.state -eq 'COMPLETED' -and $result.job.execution.stdout.Trim() -ceq 'native-job') 'installed.durable_result'
    # Deliberately tiny region. Never print or save actual screen pixels.
    $shot=Tool $b 5 'powershell_screenshot' @{monitor=0;x=0;y=0;width=16;height=16;max_width=256}
    $descriptor=$shot.structuredContent
    if($descriptor.status -eq 'INTERACTIVE_DESKTOP_UNAVAILABLE'){
        Write-Host 'NOT_TESTED installed.real_png (interactive desktop unavailable)'
    }else{
        Assert ($descriptor.status -eq 'COMPLETED') 'installed.capture_status'
        $images=@($shot.content|Where-Object {$_.type -eq 'image'})
        Assert ($images.Count -eq 1 -and $images[0].mimeType -eq 'image/png') 'installed.png_block'
        $png=[Convert]::FromBase64String($images[0].data)
        Assert ($png.Length -eq $descriptor.bytes -and $png.Length -gt 24 -and [Convert]::ToHexString($png[0..7]) -eq '89504E470D0A1A0A' -and $descriptor.width -eq 16 -and $descriptor.height -eq 16) 'installed.real_png'
        [Array]::Clear($png,0,$png.Length)
        $shot=$null;$images=$null
    }
    Write-Host 'LOCAL_CANDIDATE_SMOKE_FINISHED (raw SDK clients, not ChatGPT/Codex tunnel)'
}finally{
    foreach($p in $clients){
        if(-not $p.HasExited){$p.StandardInput.Close();if(-not $p.WaitForExit(3000)){$p.Kill($true);$p.WaitForExit()}}
        $p.Dispose()
    }
}
