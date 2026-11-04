# WindowsAdminMCP v0.7.0 standalone acceptance. Does not restart production tunnel.
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$server=Join-Path $root 'windows-admin-mcp.ps1'
$ok=0;$fail=0
function Assert([string]$name,[bool]$condition){
 if($condition){$script:ok++; "PASS $name"} else {$script:fail++; "FAIL $name"}
}
function Request([int]$id,[string]$method,$params){
 $v=@{jsonrpc='2.0';id=$id;method=$method}
 if($null -ne $params){$v.params=$params}
 return ($v|ConvertTo-Json -Depth 12 -Compress)
}
function ExecRequest([int]$id,[string]$command,[int]$seconds=10){
 return Request $id 'tools/call' @{name='powershell_exec';arguments=@{command=$command;timeout_seconds=$seconds}}
}
function Session([string[]]$requests,[string]$enabled='0',[string]$path=$server){
 $si=[Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
 $si.UseShellExecute=$false;$si.CreateNoWindow=$true
 $si.RedirectStandardInput=$true;$si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true
 $si.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
 $si.StandardErrorEncoding=[Text.UTF8Encoding]::new($false)
 foreach($a in @('-NoLogo','-NoProfile','-NonInteractive','-File',$path)){[void]$si.ArgumentList.Add($a)}
 $si.Environment['WINDOWS_ADMIN_MCP_EXEC']=$enabled

 [void]$si.Environment.Remove('CONTROL_PLANE_API_KEY')
 [void]$si.Environment.Remove('OPENAI_API_KEY')
 $p=[Diagnostics.Process]::Start($si)
 try{
  $out=$p.StandardOutput.ReadToEndAsync()
  $err=$p.StandardError.ReadToEndAsync()
  foreach($q in $requests){$p.StandardInput.WriteLine($q)}
  $p.StandardInput.Close()
  if(-not $p.WaitForExit(90000)){$p.Kill($true);throw 'SESSION_TIMEOUT'}
  $s=$out.GetAwaiter().GetResult()
  $e=$err.GetAwaiter().GetResult()
  if($p.ExitCode -ne 0 -or $e.Length -ne 0){throw "SERVER_FAILED exit=$($p.ExitCode) stderr_length=$($e.Length) stderr=$($e.Substring(0,[Math]::Min(240,$e.Length)))"}
  $values=@()
  foreach($line in ($s -split [char]10|Where-Object{$_})){$values+=,(ConvertFrom-Json $line -AsHashtable)}
  return ,$values
 }finally{$p.Dispose()}
}
$t=$null;$errors=$null;$null=[Management.Automation.Language.Parser]::ParseFile($server,[ref]$t,[ref]$errors)
Assert 'source.syntax' (@($errors).Count -eq 0)
$init=Request 1 'initialize' @{protocolVersion='2025-06-18';capabilities=@{};clientInfo=@{name='acceptance';version='1'}}
$badUnknown=Request 2 'tools/call' @{name='not_existing';arguments=@{}}
$disabled=ExecRequest 3 'Write-Output NEVER' 5
$base=Session @($init,(Request 9 'ping' $null),(Request 11 'tools/list' $null),$badUnknown,$disabled,
 (Request 4 'no/such/method' $null),(Request 5 'tools/call' @{name='powershell_exec';arguments=@{command=''}}),
 '{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"powershell_exec","arguments":{"command":"Write-Output YES","timeout_seconds":1.7}}}',
 '{"id":7,"method":"ping"}','{"jsonrpc":"2.0","method":"notifications/initialized"}','not-json') '0'
Assert 'base.response.count' ($base.Count -eq 10)
Assert 'initialize.0.7.0' (($base|Where-Object id -eq 1).result.serverInfo.version -eq '0.7.0')
Assert 'ping.pass' (@($base|Where-Object id -eq 9).Count -eq 1)
Assert 'tools.list.three' (@(($base|Where-Object id -eq 11).result.tools).Count -eq 3)
Assert 'unknown.tool.protocol.error' ((($base|Where-Object id -eq 2).error.code -eq -32602) -and -not (($base|Where-Object id -eq 2).Contains('result')))
Assert 'disabled.exec.isError' ((($base|Where-Object id -eq 3).result.isError -eq $true) -and (($base|Where-Object id -eq 3).result.structuredContent.reason -eq 'ADMIN_EXEC_NOT_ENABLED_LOCALLY'))
Assert 'unknown.method.-32601' (($base|Where-Object id -eq 4).error.code -eq -32601)
Assert 'invalid.command.-32602' (($base|Where-Object id -eq 5).error.code -eq -32602)
Assert 'fractional.timeout.-32602' (($base|Where-Object id -eq 6).error.code -eq -32602)
Assert 'invalid.request.-32600' (($base|Where-Object id -eq 7).error.code -eq -32600)
Assert 'invalid.json.-32700' (@($base|Where-Object {$_.error.code -eq -32700}).Count -eq 1)
$unicode='$s=[string]::new([char[]]@(0x041F,0x0440,0x043E,0x0432,0x0435,0x0440,0x043A,0x0430)); Write-Output $s'
$standard=Session @((ExecRequest 20 $unicode 50),(ExecRequest 21 "throw 'TEST_THROW_ERROR'"),
 (ExecRequest 22 'Get-Item -LiteralPath C:\WINDOWS_ADMIN_MCP_NONEXISTENT_NEGATIVE -ErrorAction Continue'),
 (ExecRequest 23 '[Console]::Out.Write((''X''*2000000))' 50),
 (ExecRequest 24 '[Console]::Error.Write((''E''*100000))' 50),
 (ExecRequest 25 'exit 7'),
 (ExecRequest 26 'Start-Sleep -Seconds 4' 1)) '1'
function ById($arr,[int]$id){return ($arr|Where-Object id -eq $id)}
$uni=ById $standard 20
$expected=[string]::new([char[]]@(0x041F,0x0440,0x043E,0x0432,0x0435,0x0440,0x043A,0x0430))
Assert 'unicode.roundtrip' ($uni.result.structuredContent.stdout.Trim() -ceq $expected)
Assert 'unicode.codepoints' ([Convert]::ToHexString([Text.Encoding]::UTF8.GetBytes($uni.result.structuredContent.stdout.Trim())) -ceq 'D09FD180D0BED0B2D0B5D180D0BAD0B0')
$th=ById $standard 21
Assert 'throw.error.exit' (($th.result.isError -eq $true) -and ($th.result.structuredContent.exit_code -eq 1))
Assert 'throw.error.plain.stderr' (($th.result.structuredContent.stderr -match 'TEST_THROW_ERROR') -and ($th.result.structuredContent.stderr -notmatch 'CLIXML'))
$nt=ById $standard 22
Assert 'nonterminating.error.failure' (($nt.result.isError -eq $true) -and ($nt.result.structuredContent.exit_code -ne 0))
Assert 'nonterminating.error.no.clixml' ($nt.result.structuredContent.stderr -notmatch '#< CLIXML')
$big=ById $standard 23
Assert 'two.million.output.bounded' (($big.result.structuredContent.stdout.Length -eq 16000) -and ($big.result.structuredContent.output_truncated -eq $true))
$er=ById $standard 24
Assert 'stderr.only.diagnostic.success' (($er.result.isError -eq $false) -and ($er.result.structuredContent.stderr.Length -eq 16000) -and $er.result.structuredContent.output_truncated)
$nz=ById $standard 25
Assert 'exit7.nonzero' (($nz.result.isError -eq $true) -and ($nz.result.structuredContent.exit_code -eq 7))
$to=ById $standard 26
Assert 'timeout.unknown.effect' (($to.result.isError -eq $true) -and ($to.result.structuredContent.status -in @('TIMEOUT_UNKNOWN_EFFECT','TIMEOUT_BEFORE_EXECUTION')))
Assert 'audit.persisted' (@($standard|Where-Object {-not $_.result.structuredContent.audit_persisted}).Count -eq 0)
$parallel=Session @((ExecRequest 50 'Start-Sleep -Seconds 3' 10),(Request 51 'ping' $null),(Request 52 'tools/call' @{name='powershell_status';arguments=@{}})) '1'
Assert 'ping.before.sleep' (($parallel[0].id -eq 51) -and ($parallel[2].id -eq 50))
Assert 'status.before.sleep' (($parallel[1].id -eq 52) -and ($parallel[1].result.structuredContent.elevated -eq $true))
Assert 'sleep.completed' (($parallel[2].result.structuredContent.status -eq 'COMPLETED'))
$neg=Join-Path $root 'v03-negative-log'
[void][IO.Directory]::CreateDirectory($neg)
try {
 $dst=Join-Path $neg 'windows-admin-mcp.ps1'
 Copy-Item -LiteralPath $server -Destination $dst -Force
 [IO.File]::WriteAllText((Join-Path $neg 'logs'),'BLOCK_DIR')
 $mark=Join-Path $neg 'marker.txt'
 $result=Session @((ExecRequest 70 "Set-Content -LiteralPath '$mark' -Value UNSAFE")) '1' $dst
 Assert 'audit.fail.closed' ((($result[0].result.structuredContent.reason -eq 'AUDIT_LOG_UNAVAILABLE') -and ($result[0].result.isError)) -and -not (Test-Path $mark))
} finally {
 Remove-Item -LiteralPath $neg -Recurse -Force
}
$acl=Get-Acl $root
Assert 'root.acl.protected' $acl.AreAccessRulesProtected
Assert 'root.no.sandbox.ace' (-not @($acl.Access|Where-Object {$_.IdentityReference.Value -match 'CodexSandboxUsers'}).Count)
$log=Join-Path $root 'logs\commands.jsonl'
Assert 'audit.file' (Test-Path $log -PathType Leaf)
Assert 'audit.no.command.plaintext' (-not [IO.File]::ReadAllText($log).Contains('TEST_THROW_ERROR'))
# v0.4.0: meaningful process results, warnings, cancellation, queue deadline and progress.
$native=Session @(
 (ExecRequest 80 "cmd.exe /d /c exit 7; Write-Output 'AFTER_NATIVE'"),
 (ExecRequest 81 "try { throw 'RECOVERABLE_CASE' } catch { Write-Output 'RECOVERED' }"),
 (ExecRequest 82 "Write-Warning 'CAUTION'; Write-Output 'AFTER_WARNING'"),
 (ExecRequest 83 "[Console]::Error.WriteLine('INFO_STDERR'); Write-Output 'OK'"),
 (ExecRequest 84 'Start-Sleep -Seconds 3' 10),
 (ExecRequest 85 'Write-Output TOO_LATE' 1)
) '1'
$nat=ById $native 80
Assert 'native.exit7.after.output.failure' ($nat.result.isError -and $nat.result.structuredContent.exit_code -eq 7 -and $nat.result.structuredContent.status -eq 'COMMAND_ERROR')
$handled=ById $native 81
Assert 'handled.exception.is.success' (-not $handled.result.isError -and $handled.result.structuredContent.stdout -match 'RECOVERED')
$warn=ById $native 82
Assert 'warning.preserved.structured' (-not $warn.result.isError -and @($warn.result.structuredContent.warnings).Count -eq 1 -and $warn.result.structuredContent.warnings[0] -eq 'CAUTION')
$info=ById $native 83
Assert 'stderr.text.with.exit0.success' (-not $info.result.isError -and $info.result.structuredContent.stderr -match 'INFO_STDERR')
$queueTimeout=ById $native 85
Assert 'queued.timeout.before.start' ($queueTimeout.result.isError -and $queueTimeout.result.structuredContent.status -eq 'TIMEOUT_BEFORE_EXECUTION')

# Test cancellation of an active command and a command still in the queue.
$si=[Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
$si.UseShellExecute=$false;$si.CreateNoWindow=$true
$si.RedirectStandardInput=$true;$si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true
$si.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
$si.StandardErrorEncoding=[Text.UTF8Encoding]::new($false)
foreach($arg in @('-NoLogo','-NoProfile','-NonInteractive','-File',$server)){[void]$si.ArgumentList.Add($arg)}
$si.Environment['WINDOWS_ADMIN_MCP_EXEC']='1'

[void]$si.Environment.Remove('CONTROL_PLANE_API_KEY')
[void]$si.Environment.Remove('OPENAI_API_KEY')
$p=[Diagnostics.Process]::Start($si)
try {
    $outTask=$p.StandardOutput.ReadToEndAsync()
    $errTask=$p.StandardError.ReadToEndAsync()
    $active=@{jsonrpc='2.0';id=101;method='tools/call';params=@{name='powershell_exec';arguments=@{command='Start-Sleep -Seconds 12';timeout_seconds=20};_meta=@{progressToken='acceptance_progress'}}}
    $waiting=@{jsonrpc='2.0';id=102;method='tools/call';params=@{name='powershell_exec';arguments=@{command='Write-Output MUST_NOT_RUN';timeout_seconds=20}}}
    $p.StandardInput.WriteLine(($active|ConvertTo-Json -Compress -Depth 12))
    $p.StandardInput.WriteLine(($waiting|ConvertTo-Json -Compress -Depth 12))
    $p.StandardInput.WriteLine((Request 103 'ping' $null))
    $p.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":102,"reason":"offline queued cancellation"}}')
    Start-Sleep -Seconds 5  # Allow cold WindowsDesktop C# compilation and actual active process start before cancellation
    $p.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":101,"reason":"offline active cancellation"}}')
    $p.StandardInput.Close()
    if(-not $p.WaitForExit(30000)){$p.Kill($true);throw 'CANCEL_TEST_HUNG'}
    $stdErr=$errTask.GetAwaiter().GetResult()
    $responses=@($outTask.GetAwaiter().GetResult() -split [char]10|Where-Object {$_}|ForEach-Object {ConvertFrom-Json $_ -AsHashtable})
    Assert 'cancel.server.clean.exit' ($p.ExitCode -eq 0 -and $stdErr.Length -eq 0)
    $qCancel=ById $responses 102
    $aCancel=ById $responses 101
    Assert 'cancel.queued.before_execution' ($qCancel.result.structuredContent.status -eq 'CANCELLED_BEFORE_EXECUTION' -and $qCancel.result.isError)
    Assert 'cancel.active.kills_process' ($aCancel.result.structuredContent.status -eq 'CANCELLED_UNKNOWN_EFFECT' -and $aCancel.result.isError)
    Assert 'cancel.ping.independent' (@($responses|Where-Object id -eq 103).Count -eq 1)
    Assert 'progress.opt.in.notifications' (@($responses|Where-Object {$_.method -eq 'notifications/progress'}).Count -ge 2)
} finally {$p.Dispose()}


# v0.7.0: one full administrator session, no per-command approval or mode selection.
$commandOne="Write-Output 'ADMIN_RUN_ONE'"
$commandTwo="Write-Output 'ADMIN_RUN_TWO'"
$full=Session @((ExecRequest 201 $commandOne),(ExecRequest 202 $commandTwo),
  (Request 203 'tools/call' @{name='powershell_status';arguments=@{}})) '1'
$one=ById $full 201
$two=ById $full 202
$info=ById $full 203
Assert 'admin.first.command' (($one.result.structuredContent.status -eq 'COMPLETED') -and ($one.result.structuredContent.stdout -match 'ADMIN_RUN_ONE'))
Assert 'admin.second.no.approval' (($two.result.structuredContent.status -eq 'COMPLETED') -and ($two.result.structuredContent.stdout -match 'ADMIN_RUN_TWO'))
Assert 'admin.status.enabled' ($info.result.structuredContent.admin_exec_enabled -eq $true)
$disabledStatus=Session @((Request 204 'tools/call' @{name='powershell_status';arguments=@{}})) '0'
Assert 'admin.disabled.status' ($disabledStatus[0].result.structuredContent.admin_exec_enabled -eq $false)

# Multiple legacy client sessions, each with initialize + initialized notification.
# Offline tests cover the local server protocol only; the vendor tunnel shim is verified by CI configuration + physical probes.
$legacyInit=Request 301 'initialize' @{protocolVersion='2025-06-18';capabilities=@{};clientInfo=@{name='mcp-reconnect-test';version='1'}}
$legacyNotif='{"jsonrpc":"2.0","method":"notifications/initialized"}'
$legacyStatus=Request 302 'tools/call' @{name='powershell_status';arguments=@{}}
$legacyExec=ExecRequest 303 'Write-Output HANDSHAKE_OK'
$legacyFirst=Session @($legacyInit,$legacyNotif,$legacyStatus,$legacyExec) '1'
Assert 'legacy.session1.initialized.then.status' ((ById $legacyFirst 301).result.serverInfo.version -eq '0.7.0' -and (ById $legacyFirst 302).result.structuredContent.elevated)
Assert 'legacy.session1.initialized.then.exec' ((ById $legacyFirst 303).result.structuredContent.stdout -match 'HANDSHAKE_OK')
$legacyAgain=Session @($legacyInit,$legacyNotif,$legacyStatus,$legacyExec) '1'
Assert 'legacy.session2.reinitialized.status' ((ById $legacyAgain 302).result.structuredContent.elevated)
Assert 'legacy.session2.reinitialized.exec' ((ById $legacyAgain 303).result.structuredContent.stdout -match 'HANDSHAKE_OK')


# Dual-era MCP 2026-07-28: stateless metadata, discovery, image block and legacy compatibility.
$modernMeta=@{
 'io.modelcontextprotocol/protocolVersion'='2026-07-28'
 'io.modelcontextprotocol/clientCapabilities'=@{}
 'io.modelcontextprotocol/clientInfo'=@{name='offline-acceptance';version='1'}
}
function ModernRequest([int]$id,[string]$method,[hashtable]$values){
 $payload=@{};if($values){foreach($key in $values.Keys){$payload[$key]=$values[$key]}}
 $payload['_meta']=$modernMeta
 return Request $id $method $payload
}
$modern=Session @(
 (ModernRequest 401 'server/discover' @{}),
 (ModernRequest 402 'tools/list' @{}),
 (ModernRequest 403 'tools/call' @{name='powershell_status';arguments=@{}}),
 (ModernRequest 404 'tools/call' @{name='powershell_screenshot';arguments=@{monitor=0;x=0;y=0;width=320;height=180;max_width=320}}),
 (Request 405 'tools/call' @{name='powershell_status';arguments=@{};_meta=@{
     'io.modelcontextprotocol/protocolVersion'='1900-01-01'
     'io.modelcontextprotocol/clientCapabilities'=@{}
 }}),
 (Request 406 'tools/call' @{name='powershell_status';arguments=@{};_meta=@{
     'io.modelcontextprotocol/protocolVersion'='2026-07-28'
 }}),
 (ModernRequest 407 'tools/call' @{name='powershell_screenshot';arguments=@{monitor=99}}),
 (ModernRequest 408 'tools/call' @{name='powershell_screenshot';arguments=@{bad_argument=1}}),
 (Request 409 'initialize' @{protocolVersion='2025-06-18';capabilities=@{};clientInfo=@{name='legacy-after-modern';version='1'}}),
 (Request 410 'tools/list' $null)
) '1'
$mDisc=ById $modern 401
$mList=ById $modern 402
$mStatus=ById $modern 403
$mShot=ById $modern 404
$mBad=ById $modern 405
$mMissing=ById $modern 406
$mScreen=ById $modern 407
$mParam=ById $modern 408
$mLegacy=ById $modern 409
$mLegacyTools=ById $modern 410
Assert 'modern.discovery' ($mDisc.result.resultType -eq 'complete' -and $mDisc.result.supportedVersions -contains '2026-07-28')
Assert 'modern.tools.image.advertised' ($mList.result.resultType -eq 'complete' -and @($mList.result.tools|Where-Object name -eq 'powershell_screenshot').Count -eq 1)
Assert 'modern.status.result_type' ($mStatus.result.resultType -eq 'complete' -and $mStatus.result.structuredContent.elevated)
Assert 'modern.version.unsupported.32022' ($mBad.error.code -eq -32022 -and $mBad.error.data.requested -eq '1900-01-01')
Assert 'modern.client.capabilities.required' ($mMissing.error.code -eq -32602)
Assert 'screenshot.invalid.monitor.rejected' ($mScreen.result.isError -and $mScreen.result.structuredContent.reason -eq 'MONITOR_NOT_FOUND')
Assert 'screenshot.unknown.argument.rejected' ($mParam.error.code -eq -32602)
Assert 'legacy.initialize.after.modern' ($mLegacy.result.protocolVersion -eq '2025-06-18')
Assert 'legacy.tools.after.modern' ($null -eq $mLegacyTools.result.resultType -and @($mLegacyTools.result.tools).Count -eq 3)
if(-not $mShot.result.isError){
 $image=$mShot.result.content[0]
 $bytes=[Convert]::FromBase64String($image.data)
 Assert 'screenshot.image.content.png' ($image.type -eq 'image' -and $image.mimeType -eq 'image/png' -and $bytes.Length -ge 100 -and $bytes[0] -eq 137 -and $bytes[1] -eq 80)
 Assert 'screenshot.bounded.dimensions' ($mShot.result.structuredContent.width -eq 320 -and $mShot.result.structuredContent.height -eq 180)
}else{
 # CI virtual desktops may be noninteractive: return an exact actionable error, never fake a PNG.
 Assert 'screenshot.headless.fail.closed' ($mShot.result.structuredContent.reason -in @('INTERACTIVE_DESKTOP_UNAVAILABLE','SCREEN_CAPTURE_FAILED_OR_DESKTOP_LOCKED'))
}

"SUMMARY PASS=$ok FAIL=$fail"
if($fail){exit 1}
