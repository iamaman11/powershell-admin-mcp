param([Parameter(Mandatory)][string]$Exe,[switch]$SmokeOnly,[switch]$LongBoundary,[switch]$Performance)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if(-not $env:GITHUB_ACTIONS -or $env:RUNNER_OS -ne 'Windows'){throw 'ISOLATED_WINDOWS_CI_ONLY'}
$Exe=[IO.Path]::GetFullPath($Exe)
$name='WindowsAdminMCP'
$data=Join-Path $env:ProgramData 'WindowsAdminMCP.Data'
if(Test-Path -LiteralPath $data){throw 'STATE_ALREADY_EXISTS'}
if(Get-Service $name -ErrorAction SilentlyContinue){throw 'SERVICE_ALREADY_EXISTS'}
$clients=[Collections.Generic.List[Diagnostics.Process]]::new()
$meta=@{'io.modelcontextprotocol/protocolVersion'='2026-07-28';'io.modelcontextprotocol/clientCapabilities'=@{};'io.modelcontextprotocol/clientInfo'=@{name='r1-fixture';version='1'}}
function Assert($ok,[string]$label){if(-not $ok){throw "FAIL $label"};Write-Host "PASS $label"}
function Connect {
    $si=[Diagnostics.ProcessStartInfo]::new($Exe)
    $si.ArgumentList.Add('stdio')
    $si.Environment['R0_SERVICE_PID']='1'
    $si.Environment['R0_PIPE']='not-trusted'
    $si.UseShellExecute=$false;$si.RedirectStandardInput=$true;$si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true
    $p=[Diagnostics.Process]::Start($si);$clients.Add($p)
    $null=$p.StandardError.ReadToEndAsync()
    return $p
}
function Send($p,$id,$method,$params){
    if($method -eq 'tools/call' -and ($params.name -eq 'powershell_exec' -or
        ($params.name -eq 'powershell_job' -and $params.arguments.action -eq 'submit')) -and
        -not $params.arguments.ContainsKey('execution_context')){$params.arguments.execution_context='SYSTEM'}
    $m=@{jsonrpc='2.0';id=$id;method=$method;params=$params}
    $p.StandardInput.WriteLine(($m|ConvertTo-Json -Depth 12 -Compress));$p.StandardInput.Flush()
}
function Receive($p,$id,[int]$timeout=10,[switch]$AllowToolError){
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while($true){
        $remaining=[TimeSpan]::FromSeconds($timeout)-$watch.Elapsed
        if($remaining.TotalMilliseconds -le 0){throw 'RECEIVE_TIMEOUT'}
        $line=$p.StandardOutput.ReadLineAsync().WaitAsync($remaining).GetAwaiter().GetResult()
        if($null -eq $line){throw 'UNEXPECTED_EOF'}
        $m=$line|ConvertFrom-Json -AsHashtable
        if($m.Contains('id') -and $m.id -eq $id){
            if($m.Contains('error') -or ($m.result.Contains('isError') -and $m.result.isError -and -not $AllowToolError)){throw "RPC_FAILURE $line"}
            return $m.result
        }
    }
}
function Status($p,$id){
    Send $p $id 'tools/call' @{name='powershell_status';arguments=@{};_meta=$meta}
    return (Receive $p $id).structuredContent
}
function Wait-Status($p,[string]$expected){
    $clock=[Diagnostics.Stopwatch]::StartNew();$id=1000
    do{
        $v=Status $p $id;$id++
        if($v.status -eq $expected){return $v}
        Start-Sleep -Milliseconds 100
    }while($clock.Elapsed.TotalSeconds -lt 10)
    throw "STATUS_TIMEOUT $expected $($v.status)"
}
try{
    New-Service -Name $name -BinaryPathName ('"'+$Exe+'" service') -StartupType Automatic | Out-Null
    & sc.exe failure $name reset= 0 actions= restart/1000 | Out-Host
    if($LASTEXITCODE -ne 0){throw 'SCM_FAILURE_POLICY_FAILED'}
    Start-Service $name
    $a=Connect;$b=Connect;$c=Connect
    $svc=Get-CimInstance Win32_Service -Filter "Name='$name'"
    foreach($p in @($a,$b,$c)){
        Send $p 1 'tools/list' @{_meta=$meta}
        $list=Receive $p 1
        $names=@($list.tools|ForEach-Object {$_.name})
        $required=@('powershell_status','powershell_exec','powershell_job','powershell_screenshot','windows.list_windows','windows.inspect_window','windows.capture_window','windows.invoke_element','windows.invoke_locator','windows.manage_window','windows.chrome_connection','powershell_update','windows.file_query','windows.file_read_chunk','windows.file_write','windows.file_manage')
        Assert (@($required|Where-Object {$_ -notin $names}).Count -eq 0 -and $names.Count -eq @($names|Select-Object -Unique).Count) 'r5.required_window_tools_without_duplicates'
        $inspectTool=@($list.tools|Where-Object name -eq 'windows.inspect_window')
        $actionTool=@($list.tools|Where-Object name -eq 'windows.invoke_element')
        $statusTool=@($list.tools|Where-Object name -eq 'powershell_status')
        $chromeTool=@($list.tools|Where-Object name -eq 'windows.chrome_connection')
        $fileQuery=@($list.tools|Where-Object name -eq 'windows.file_query')
        $fileRead=@($list.tools|Where-Object name -eq 'windows.file_read_chunk')
        $fileWrite=@($list.tools|Where-Object name -eq 'windows.file_write')
        $fileManageTool=@($list.tools|Where-Object name -eq 'windows.file_manage')
        $locator=@($list.tools|Where-Object name -eq 'windows.invoke_locator')
        $manage=@($list.tools|Where-Object name -eq 'windows.manage_window')
        Assert ($fileWrite.Count -eq 1 -and $locator.Count -eq 1 -and $manage.Count -eq 1 -and
            @(@('action','path','operation_id','expected_bytes','expected_sha256','offset','data_base64','chunk_sha256','execution_context')|Where-Object {-not $fileWrite[0].inputSchema.properties.Contains($_)}).Count -eq 0 -and
            @(@('status','operation_id','effect_state','received_bytes','safe_to_retry')|Where-Object {-not $fileWrite[0].outputSchema.properties.Contains($_)}).Count -eq 0 -and
            @(@('window_id','action','automation_id','name','role','confirm')|Where-Object {-not $locator[0].inputSchema.properties.Contains($_)}).Count -eq 0 -and
            @(@('window_id','action','x','y','width','height')|Where-Object {-not $manage[0].inputSchema.properties.Contains($_)}).Count -eq 0 -and
            $manage[0].outputSchema.properties.Contains('effect_state') -and
            $manage[0].outputSchema.properties.Contains('safe_to_retry') -and
            $actionTool[0].outputSchema.properties.Contains('safe_to_retry')) 'issue56.typed_filewrite_locator_and_window_manager'
        Assert ($fileManageTool.Count -eq 1 -and
            @(@('action','path','operation_id','destination','recursive','expected_version','expected_sha256','read_only','execution_context')|Where-Object {-not $fileManageTool[0].inputSchema.properties.Contains($_)}).Count -eq 0 -and
            @(@('status','operation_id','action','execution_context','effect_state','stage','safe_to_retry')|Where-Object {-not $fileManageTool[0].outputSchema.properties.Contains($_)}).Count -eq 0) 'issue56.typed_native_file_manage_catalog'
        Assert ($fileQuery.Count -eq 1 -and $fileRead.Count -eq 1 -and
            @(@('action','path','page_size','cursor','include_sha256','execution_context')|Where-Object {-not $fileQuery[0].inputSchema.properties.Contains($_)}).Count -eq 0 -and
            @(@('path','offset','length','expected_version','execution_context')|Where-Object {-not $fileRead[0].inputSchema.properties.Contains($_)}).Count -eq 0 -and
            $fileQuery[0].Contains('outputSchema') -and $fileRead[0].Contains('outputSchema')) 'files.two_typed_native_mcp_contracts'
        Assert ($chromeTool.Count -eq 1 -and
            @(@('action','window_id','expected_profile')|Where-Object {-not $chromeTool[0].inputSchema.properties.Contains($_)}).Count -eq 0 -and
            @(@('status','state','next_action','profile','profiles')|Where-Object {-not $chromeTool[0].outputSchema.properties.Contains($_)}).Count -eq 0) 'chrome.typed_multi_profile_and_consent_lifecycle'
        Assert ($inspectTool.Count -eq 1 -and $actionTool.Count -eq 1 -and
            @(@('window_id','max_depth','max_elements','start_path','start_element_id','offset','include_details','name_contains','role_filter','wait_ms','scope','tab_id','expected_profile','expected_origin','expected_url')|Where-Object {-not $inspectTool[0].inputSchema.properties.Contains($_)}).Count -eq 0 -and
            @(@('window_id','element_id','action','value','confirm','element_path','expected_name','expected_change','key','expected_kind','expected_value','expected_profile','expected_origin','expected_url','observation_timeout_ms')|Where-Object {-not $actionTool[0].inputSchema.properties.Contains($_)}).Count -eq 0) 'r5.actual_tools_list_exposes_typed_semantic_uia_contract'
        $updateTool=@($list.tools|Where-Object name -eq 'powershell_update')
        Assert ($updateTool.Count -eq 1 -and $updateTool[0].inputSchema.properties.Contains('action') -and
            $updateTool[0].inputSchema.properties.Contains('remote_acceptance_confirmed') -and
            $updateTool[0].inputSchema.properties.Contains('expected_commit') -and
            $updateTool[0].outputSchema.properties.Contains('operation_id') -and
            $updateTool[0].outputSchema.properties.Contains('installed_commit') -and
            $updateTool[0].outputSchema.properties.Contains('operation_action')) 'r5.typed_independent_operator_update_contract'
        Send $p 75 'tools/call' @{name='powershell_update';arguments=@{action='ACCEPT';expected_commit=('a'*40)};_meta=$meta}
        $invalidAccept=(Receive $p 75).structuredContent
        Assert ($invalidAccept.status -eq 'INVALID_ARGUMENT' -and
            $invalidAccept.detail -eq 'INVALID_ACTION_ARGUMENTS') 'r5.accept_without_explicit_confirmation_denied'
        Assert ($actionTool[0].outputSchema.properties.Contains('foreground_changed') -and
            $actionTool[0].outputSchema.properties.Contains('target_activated') -and
            $actionTool[0].outputSchema.properties.Contains('effect_state') -and
            $actionTool[0].outputSchema.properties.Contains('execution_method') -and
            $actionTool[0].outputSchema.properties.Contains('failure_stage') -and
            $actionTool[0].outputSchema.properties.Contains('hresult')) 'r5.typed_uia_effect_and_diagnostics_fields'
        Assert ($statusTool.Count -eq 1 -and $statusTool[0].inputSchema.properties.Contains('include_diagnostics') -and
            $statusTool[0].outputSchema.properties.Contains('diagnostics')) 'r5.optional_typed_doctor_without_new_tool'
        $schemaBytes=[Text.Encoding]::UTF8.GetByteCount(($list|ConvertTo-Json -Depth 30 -Compress))
        Write-Host "MCP_TOOLS_LIST_BYTES=$schemaBytes"
        # Typed Chrome profile discovery and consent states increase the catalog.
        Assert ($schemaBytes -le 65536) 'issue56.typed_tool_schema_under_64kib'
        Assert (@($list.tools|Where-Object {$_.name -eq 'powershell_status' -and $_.Contains('outputSchema')}).Count -eq 1) 'r1.typed_status_schema'
        $v=Wait-Status $p 'OK'
        Assert ($v.service_pid -eq $svc.ProcessId -and $v.execution_sid -eq 'S-1-5-18' -and $v.elevated) 'r1.scm_pid_and_real_execution_identity'
        $caller=[Security.Principal.WindowsIdentity]::GetCurrent()
        Assert ($v.frontend.pid -eq $p.Id -and $v.frontend.sid -eq $caller.User.Value -and
            $v.frontend.session_id -eq $p.SessionId -and $v.command_context -in @('SYSTEM','UNCONFIRMED') -and
            $v.versions.mcp_version -eq '0.8.1-dev' -and $v.versions.store_schema -eq 4 -and $v.timing.ipc_ms -ge 0) 'hardening.separate_frontend_and_runner_identity'
    }
    if($SmokeOnly){
        Write-Host 'R1_TYPED_MCP_SMOKE=PASS (three clients; SCM and schema; cleanup follows)'
        return
    }
    Send $a 31 'tools/call' @{name='windows.list_windows';arguments=@{max_windows=0};_meta=$meta}
    $invalidWindowList=(Receive $a 31).structuredContent
    Assert ($invalidWindowList.status -eq 'INVALID_ARGUMENT' -and @($invalidWindowList.windows).Count -eq 0) 'windows.mcp_invalid_limit'
    Send $a 201 'tools/call' @{name='windows.file_write';arguments=@{action='BEGIN';path='relative';operation_id='invalid'};_meta=$meta}
    $invalidWrite=(Receive $a 201).structuredContent
    Assert ($invalidWrite.status -eq 'INVALID_ARGUMENT') 'issue56.filewrite_rejects_invalid_before_io'
    Send $a 204 'tools/call' @{name='windows.file_manage';arguments=@{action='DELETE';path='relative';operation_id='invalid'};_meta=$meta}
    $invalidFileManage=(Receive $a 204).structuredContent
    Assert ($invalidFileManage.status -eq 'INVALID_ARGUMENT') 'issue56.filemanage_rejects_invalid_before_io'
    Send $a 202 'tools/call' @{name='windows.manage_window';arguments=@{window_id='bad';action='MOVE';x=1;y=2};_meta=$meta}
    $invalidManage=(Receive $a 202).structuredContent
    Assert ($invalidManage.status -eq 'INVALID_ARGUMENT') 'issue56.window_manage_rejects_stale_id'
    Send $a 203 'tools/call' @{name='windows.invoke_locator';arguments=@{window_id='bad';action='INVOKE';name='OK'};_meta=$meta}
    $invalidLocator=(Receive $a 203).structuredContent
    Assert ($invalidLocator.status -eq 'INVALID_ARGUMENT') 'issue56.locator_rejects_bad_window'
    Send $a 76 'tools/call' @{name='windows.chrome_connection';arguments=@{action='REQUEST';window_id='bad';expected_profile='Profile 5'};_meta=$meta}
    $badConnection=(Receive $a 76).structuredContent
    Assert ($badConnection.status -eq 'INVALID_ARGUMENT' -and $badConnection.state -eq 'NOT_REQUESTED') 'chrome.invalid_window_cannot_request_consent'
    Send $a 77 'tools/call' @{name='windows.chrome_connection';arguments=@{action='LIST'};_meta=$meta}
    $restrictedProfiles=(Receive $a 77).structuredContent
    Assert ($restrictedProfiles.status -eq 'USER_CONTEXT_UNAVAILABLE' -and $restrictedProfiles.state -eq 'NOT_REQUESTED') 'chrome.no_profile_enumeration_without_user_context'
    Send $a 32 'tools/call' @{name='windows.list_windows';arguments=@{max_windows=1};_meta=$meta}
    $restrictedWindowList=(Receive $a 32).structuredContent
    Assert ($restrictedWindowList.status -eq 'USER_CONTEXT_UNAVAILABLE' -and @($restrictedWindowList.windows).Count -eq 0) 'windows.reject_unbound_ci_frontend'
    Send $a 33 'tools/call' @{name='windows.inspect_window';arguments=@{window_id='bad';max_depth=3};_meta=$meta}
    $badInspect=(Receive $a 33).structuredContent
    Assert ($badInspect.status -eq 'INVALID_ARGUMENT' -and @($badInspect.elements).Count -eq 0) 'windows.mcp_invalid_inspect_id'
    Send $a 35 'tools/call' @{name='windows.inspect_window';arguments=@{window_id='bad';name_contains='button';role_filter='BUTTON';wait_ms=1500};_meta=$meta}
    $badFiltered=(Receive $a 35).structuredContent
    Assert ($badFiltered.status -eq 'INVALID_ARGUMENT') 'windows.mcp_typed_filtered_inspection'
    Send $a 34 'tools/call' @{name='windows.inspect_window';arguments=@{window_id='w1:7fffffffffffffff:1092:1dbaf8d88b62800:1';max_depth=3};_meta=$meta}
    $restrictedInspect=(Receive $a 34).structuredContent
    Assert ($restrictedInspect.status -eq 'USER_CONTEXT_UNAVAILABLE' -and @($restrictedInspect.elements).Count -eq 0) 'windows.inspect_reject_unbound_ci_frontend'
    Send $a 37 'tools/call' @{name='windows.capture_window';arguments=@{window_id='bad';max_width=1280};_meta=$meta}
    $badCapture=(Receive $a 37 -AllowToolError).structuredContent
    Assert ($badCapture.status -eq 'INVALID_ARGUMENT') 'windows.capture_reject_bad_window_id'
    Send $a 38 'tools/call' @{name='windows.capture_window';arguments=@{window_id='w1:7fffffffffffffff:1092:1dbaf8d88b62800:1';max_width=1280};_meta=$meta}
    $restrictedCapture=(Receive $a 38 -AllowToolError).structuredContent
    Assert ($restrictedCapture.status -eq 'USER_CONTEXT_UNAVAILABLE') 'windows.capture_reject_unbound_ci_frontend'
    $invalidCaptureWorker=(& $Exe window-capture-worker 'bad' 1280 | ConvertFrom-Json)
    Assert ($invalidCaptureWorker.result.status -eq 'INVALID_ARGUMENT') 'windows.invalid_capture_worker_args'
    Send $a 35 'tools/call' @{name='windows.invoke_element';arguments=@{window_id='bad';element_id='bad';action='INVOKE';confirm=$true};_meta=$meta}
    $badAction=(Receive $a 35).structuredContent
    Assert ($badAction.status -eq 'INVALID_ARGUMENT' -and -not $badAction.effect_unknown) 'windows.mcp_invalid_action_ref'
    Send $a 36 'tools/call' @{name='windows.invoke_element';arguments=@{window_id='w1:7fffffffffffffff:1092:1dbaf8d88b62800:1';element_id='e1:1:00000000000000000000000000000000';action='INVOKE';confirm=$false};_meta=$meta}
    $unconfirmedAction=(Receive $a 36).structuredContent
    Assert ($unconfirmedAction.status -eq 'STALE_ELEMENT') 'windows.expired_element_is_typed_before_confirmation'
    $freshRef='e1:'+([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString('x'))+':00000000000000000000000000000000'
    Send $a 90 'tools/call' @{name='windows.invoke_element';arguments=@{window_id='w1:7fffffffffffffff:1092:1dbaf8d88b62800:1';element_id=$freshRef;action='INVOKE';confirm=$false};_meta=$meta}
    $unconfirmedFresh=(Receive $a 90).structuredContent
    Assert ($unconfirmedFresh.status -eq 'CONFIRMATION_REQUIRED') 'windows.action_requires_confirmation'
    $invalidActionWorker=('{}' | & $Exe window-action-worker | ConvertFrom-Json)
    Assert ($invalidActionWorker.status -eq 'INVALID_ARGUMENT') 'windows.invalid_worker_payload'
    $inspectorWorker=(& $Exe window-inspect-worker 'bad' 3 60 | ConvertFrom-Json)
    Assert ($inspectorWorker.status -eq 'INVALID_ARGUMENT' -and @($inspectorWorker.elements).Count -eq 0) 'windows.inspect_worker_wire'
    foreach($p in @($a,$b,$c)){Send $p 2 'tools/call' @{name='powershell_status';arguments=@{};_meta=$meta}}
    $results=@(foreach($p in @($a,$b,$c)){(Receive $p 2).structuredContent})
    Assert (@($results|Where-Object {$_.status -eq 'OK' -and $_.service_pid -eq $svc.ProcessId}).Count -eq 3) 'r1.one_service_three_stdio_clients'
    Assert (@($results.correlation_id|Select-Object -Unique).Count -eq 3) 'r1.unique_correlations'
    Send $a 39 'tools/call' @{name='powershell_status';arguments=@{include_diagnostics=$true};_meta=$meta}
    $diagnostics=(Receive $a 39).structuredContent
    Assert ($diagnostics.status -eq 'OK' -and $diagnostics.diagnostics.status -in @('OK','DEGRADED','NOT_TESTED') -and
        @($diagnostics.diagnostics.checks).Count -eq 11 -and
        $diagnostics.diagnostics.checks[0].component -eq 'Native') 'r5.doctor_typed_on_non_ok_health_without_exec_error'
    Stop-Service $name
    $offline=Wait-Status $a 'SERVICE_UNAVAILABLE'
    Assert ($offline.native_error -eq 1062) 'r1.typed_service_offline'
    Assert ($offline.command_context -eq 'UNCONFIRMED' -and $offline.frontend.pid -eq $a.Id) 'hardening.offline_context_not_falsely_confirmed'
    Start-Service $name
    $now=Wait-Status $a 'OK'
    $newSvc=Get-CimInstance Win32_Service -Filter "Name='$name'"
    Assert ($now.service_pid -eq $newSvc.ProcessId -and $now.started_utc -ne $results[0].started_utc) 'r1.same_frontend_reconnects_new_service'
    function ToolCall($p,$id,$tool,$arguments){
        Send $p $id 'tools/call' @{name=$tool;arguments=$arguments;_meta=$meta}
        return (Receive $p $id).structuredContent
    }
    $fileFixture=Join-Path $env:ProgramData ('wmcp-native-read-'+[Guid]::NewGuid().ToString('N'))
    $null=New-Item -ItemType Directory -Path $fileFixture -Force
    try {
        $filePath=Join-Path $fileFixture 'binary.bin'
        $bytes=[byte[]]::new(262145)
        [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        [IO.File]::WriteAllBytes($filePath,$bytes)
        $stat=ToolCall $a 701 'windows.file_query' @{action='STAT';path=$filePath;execution_context='SYSTEM';include_sha256=$true}
        $expectedHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
        Assert ($stat.status -eq 'COMPLETED' -and $stat.execution_context -eq 'SYSTEM' -and
            $stat.info.size -eq $bytes.Length -and $stat.info.sha256 -eq $expectedHash -and
            -not [string]::IsNullOrWhiteSpace($stat.info.version)) 'files.system_native_stat_hash_and_version'
        $read=ToolCall $a 702 'windows.file_read_chunk' @{path=$filePath;offset=0;length=262144;expected_version=$stat.info.version;execution_context='SYSTEM'}
        $expectedChunkHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$bytes[0..262143]))
        Assert ($read.status -eq 'COMPLETED' -and $read.actual_length -eq 262144 -and
            $read.eof -eq $false -and $read.chunk_sha256 -ceq $expectedChunkHash -and
            [Convert]::FromBase64String($read.data_base64).Length -eq 262144) 'files.system_exact_max_binary_frame'
        $tail=ToolCall $a 703 'windows.file_read_chunk' @{path=$filePath;offset=262144;length=262144;expected_version=$stat.info.version;execution_context='SYSTEM'}
        Assert ($tail.status -eq 'COMPLETED' -and $tail.eof -eq $true -and
            $tail.actual_length -eq 1 -and [Convert]::FromBase64String($tail.data_base64)[0] -eq $bytes[262144]) 'files.system_tail_and_eof'
        $invalid=ToolCall $a 704 'windows.file_read_chunk' @{path=$filePath;offset=-1;execution_context='SYSTEM'}
        Assert ($invalid.status -eq 'INVALID_ARGUMENT') 'files.reject_negative_offset_without_side_effect'
        $realUser=ToolCall $a 705 'windows.file_query' @{action='STAT';path=$filePath;execution_context='USER'}
        Assert ($realUser.status -eq 'COMPLETED' -and $realUser.execution_context -eq 'USER' -and
            $realUser.info.size -eq $bytes.Length) 'files.real_elevated_user_without_worker_dependency'
        # Real SCM pipe + one SQLite receipt owner. Kill/restart the service
        # after APPEND, then STATUS before an explicit single COMMIT.
        $writePath=Join-Path $fileFixture 'atomic-system.bin'
        $writeId=[Guid]::NewGuid().ToString('N')
        $writeBytes=[Text.Encoding]::UTF8.GetBytes('native staged write and sqlite receipt 🌍')
        $writeHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($writeBytes))
        $startWrite=ToolCall $a 706 'windows.file_write' @{action='BEGIN';path=$writePath;
            operation_id=$writeId;expected_bytes=$writeBytes.Length;expected_sha256=$writeHash;
            execution_context='SYSTEM'}
        Assert ($startWrite.status -eq 'STAGED' -and -not (Test-Path -LiteralPath $writePath)) 'fileledger.system_begin_before_target_effect'
        $appendWrite=ToolCall $a 707 'windows.file_write' @{action='APPEND';path=$writePath;
            operation_id=$writeId;offset=0;data_base64=[Convert]::ToBase64String($writeBytes);
            chunk_sha256=$writeHash;execution_context='SYSTEM'}
        Assert ($appendWrite.status -eq 'STAGED' -and $appendWrite.received_bytes -eq $writeBytes.Length) 'fileledger.system_chunk_receipt_staged'
        Stop-Service $name
        Start-Service $name
        $restarted=Wait-Status $a 'OK'
        Assert ($restarted.status -eq 'OK') 'fileledger.scm_restarted_between_append_and_commit'
        $beforeCommit=ToolCall $a 708 'windows.file_write' @{action='STATUS';path=$writePath;
            operation_id=$writeId;execution_context='SYSTEM'}
        Assert ($beforeCommit.status -eq 'STAGED' -and $beforeCommit.received_bytes -eq $writeBytes.Length) 'fileledger.system_after_restart_exact_staged_readback'
        $committedWrite=ToolCall $a 709 'windows.file_write' @{action='COMMIT';path=$writePath;
            operation_id=$writeId;execution_context='SYSTEM'}
        Assert ($committedWrite.status -eq 'COMMITTED' -and $committedWrite.effect_state -eq 'EFFECT_VERIFIED' -and
            [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($writePath))) -ceq $writeHash) 'fileledger.system_atomic_commit_exact_bytes'
        $afterCommit=ToolCall $b 710 'windows.file_write' @{action='STATUS';path=$writePath;
            operation_id=$writeId;execution_context='SYSTEM'}
        Assert ($afterCommit.status -eq 'COMMITTED' -and -not $afterCommit.safe_to_retry) 'fileledger.system_cross_frontend_lost_ack_receipt'
        $userPath=Join-Path $fileFixture 'atomic-user.bin'
        $userWriteId=[Guid]::NewGuid().ToString('N')
        $userStart=ToolCall $b 711 'windows.file_write' @{action='BEGIN';path=$userPath;
            operation_id=$userWriteId;expected_bytes=0;
            expected_sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]@()));
            execution_context='USER'}
        $userCommit=ToolCall $c 712 'windows.file_write' @{action='COMMIT';path=$userPath;
            operation_id=$userWriteId;execution_context='USER'}
        Assert ($userStart.status -eq 'STAGED' -and $userCommit.status -eq 'COMMITTED' -and
            [IO.File]::Exists($userPath) -and ([IO.FileInfo]::new($userPath)).Length -eq 0) 'fileledger.user_elevated_atomic_zero_byte_cross_frontend'

        $managedDir=Join-Path $fileFixture 'native-managed'
        $mkdirId=[Guid]::NewGuid().ToString('N')
        $mkdir=ToolCall $a 713 'windows.file_manage' @{action='MKDIR';path=$managedDir;
            operation_id=$mkdirId;execution_context='SYSTEM'}
        Assert ($mkdir.status -eq 'COMPLETED' -and $mkdir.effect_state -eq 'EFFECT_VERIFIED' -and
            (Test-Path -LiteralPath $managedDir -PathType Container)) 'filemanage.system_mkdir_verified'
        Stop-Service $name
        Start-Service $name
        $null=Wait-Status $b 'OK'
        $mkdirStatus=ToolCall $b 714 'windows.file_manage' @{action='MKDIR';path=$managedDir;
            operation_id=$mkdirId;read_only=$true;execution_context='SYSTEM'}
        Assert ($mkdirStatus.status -eq 'COMPLETED' -and -not $mkdirStatus.safe_to_retry) 'filemanage.system_restart_durable_receipt_query'
        $managedCopy=Join-Path $managedDir 'copy.bin'
        $copyId=[Guid]::NewGuid().ToString('N')
        $copy=ToolCall $b 715 'windows.file_manage' @{action='COPY';path=$filePath;
            destination=$managedCopy;operation_id=$copyId;execution_context='SYSTEM';
            expected_version=$stat.info.version;expected_sha256=$stat.info.sha256}
        Assert ($copy.status -eq 'COMPLETED' -and
            [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($managedCopy))) -ceq $expectedHash) 'filemanage.system_atomic_copy_exact_cas'
        $copyAgain=ToolCall $c 716 'windows.file_manage' @{action='COPY';path=$filePath;
            destination=$managedCopy;operation_id=$copyId;execution_context='SYSTEM';
            expected_version=$stat.info.version;expected_sha256=$stat.info.sha256}
        Assert ($copyAgain.status -eq 'COMPLETED' -and -not $copyAgain.safe_to_retry) 'filemanage.cross_frontend_duplicate_not_executed'
        $managedMoved=Join-Path $managedDir 'moved.bin'
        $move=ToolCall $c 717 'windows.file_manage' @{action='MOVE';path=$managedCopy;
            destination=$managedMoved;operation_id=[Guid]::NewGuid().ToString('N');execution_context='SYSTEM'}
        Assert ($move.status -eq 'COMPLETED' -and (Test-Path -LiteralPath $managedMoved -PathType Leaf) -and
            -not (Test-Path -LiteralPath $managedCopy)) 'filemanage.system_atomic_move'
        $delete=ToolCall $a 718 'windows.file_manage' @{action='DELETE';path=$managedMoved;
            operation_id=[Guid]::NewGuid().ToString('N');execution_context='USER'}
        Assert ($delete.status -eq 'COMPLETED' -and $delete.execution_context -eq 'USER' -and
            -not (Test-Path -LiteralPath $managedMoved)) 'filemanage.real_elevated_user_delete'
        $removeDirectory=ToolCall $a 719 'windows.file_manage' @{action='DELETE';path=$managedDir;
            operation_id=[Guid]::NewGuid().ToString('N');execution_context='SYSTEM'}
        Assert ($removeDirectory.status -eq 'COMPLETED' -and
            -not (Test-Path -LiteralPath $managedDir)) 'filemanage.system_delete_empty_directory'
    } finally {Remove-Item -LiteralPath $fileFixture -Recurse -Force}
    function Job($p,$id,$arguments){return (ToolCall $p $id 'powershell_job' $arguments)}
    function Wait-JobState($p,$jobId,$state){
        $clock=[Diagnostics.Stopwatch]::StartNew();$id=2000
        do{
            $v=Job $p $id @{action='logs';job_id=$jobId};$id++
            if($v.job.state -eq $state){return $v.job}
            Start-Sleep -Milliseconds 100
        }while($clock.Elapsed.TotalSeconds -lt 15)
        throw "JOB_STATE_TIMEOUT $state $($v.job.state)"
    }
    $x=ToolCall $a 10 'powershell_exec' @{command="Write-Output 'real-exec'; [Security.Principal.WindowsIdentity]::GetCurrent().User.Value";timeout_seconds=10;operation_key='short'}
    Assert ($x.status -eq 'COMPLETED' -and $x.stdout.Contains('real-exec') -and $x.stdout.Contains('S-1-5-18') -and $x.audit_persisted) 'r3.actual_exec_service_context'
    $unavailable=Job $a 119 @{action='submit';operation_key='no-user-worker';command="Write-Output 'MUST_NOT_RUN'";execution_context='USER'}
    Assert ($unavailable.status -eq 'USER_CONTEXT_UNAVAILABLE' -and (-not $unavailable.Contains('job') -or $null -eq $unavailable.job)) 'hardening.no_silent_system_fallback'
    $prefix="Write-Output 'Проверка 🚀'"+[char]10+'#'
    $long=$prefix+('x'*(11982-$prefix.Length))
    $shortLog=Job $b 101 @{action='logs';operation_key='short';step_index=1}
    Assert ($shortLog.job.execution.audit_persisted -and $shortLog.job.execution.stdout.Contains('real-exec') -and
        $shortLog.timing.store_ms -ge 0 -and $shortLog.job.execution.queue_wait_ms -ge 0) 'hardening.exec_and_logs_same_persisted_audit'
    $badIndex=Job $b 102 @{action='logs';operation_key='short';step_index=0}
    Assert ($badIndex.status -eq 'INVALID_ARGUMENT') 'hardening.zero_step_index_rejected'
    $x=ToolCall $b 11 'powershell_exec' @{command=$long;timeout_seconds=10;operation_key='long'}
    Assert ($x.status -eq 'COMPLETED' -and $x.stdout.Trim() -ceq 'Проверка 🚀') 'r3.long_unicode_through_mcp'
    if($LongBoundary){
        # The long tunnel boundary runs weekly or on explicit dispatch.
        $boundaryClock=[Diagnostics.Stopwatch]::StartNew()
        Send $a 103 'tools/call' @{name='powershell_exec';arguments=@{command="Start-Sleep -Seconds 100; Write-Output 'BOUNDARY_OK'";timeout_seconds=120;operation_key='boundary-exec'};_meta=$meta}
        $boundary=(Receive $a 103 110).structuredContent
        Assert ($boundary.status -eq 'PENDING' -and $boundary.job_id -and
            -not $boundary.audit_persisted -and $boundaryClock.Elapsed.TotalSeconds -lt 110) 'hardening.exec_boundary_pending_before_tunnel_deadline'
        $boundaryResult=Wait-JobState $b $boundary.job_id 'COMPLETED'
        Assert ($boundaryResult.execution.stdout.Trim() -ceq 'BOUNDARY_OK' -and $boundaryResult.execution.audit_persisted) 'hardening.boundary_result_saved'
    }
    # Real workload overlap: six distinct live PowerShell trees, three SDK
    # frontends, then a seventh independent short command before releasing them.
    $parallelDir=Join-Path $data 'parallel-fixture'
    [IO.Directory]::CreateDirectory($parallelDir)|Out-Null
    $release=Join-Path $parallelDir 'release'
    $safeRelease=$release.Replace("'","''")
    $runs=@()
    for($i=0;$i -lt 6;$i++){
        $ready=Join-Path $parallelDir ('ready-'+$i)
        $safeReady=$ready.Replace("'","''")
        $command="[IO.File]::WriteAllText('$safeReady',[string]`$PID); while(-not [IO.File]::Exists('$safeRelease')){Start-Sleep -Milliseconds 100}; Write-Output 'parallel-$i'"
        $client=@($a,$b,$c)[$i%3]
        $admitted=Job $client (3000+$i) @{action='submit';operation_key=('parallel-many-'+$i);command=$command;timeout_seconds=60}
        Assert ($admitted.status -eq 'OK' -and $admitted.job.job_id) ('parallel.admitted.'+$i)
        $runs+=@{id=$admitted.job.job_id;ready=$ready;marker=('parallel-'+$i)}
    }
    $parallelClock=[Diagnostics.Stopwatch]::StartNew()
    try{
        do{
            $readyCount=@($runs|Where-Object {Test-Path -LiteralPath $_.ready}).Count
            if($readyCount -eq 6){break}
            Start-Sleep -Milliseconds 100
        }while($parallelClock.Elapsed.TotalSeconds -lt 25)
        if($readyCount -ne 6){
            $details=Job $b 3100 @{action='list';page_size=100}
            Write-Host ('PARALLEL_CAPACITY '+($details.store.budget|ConvertTo-Json -Compress))
            throw 'PARALLEL_SIX_PROCESSES_NOT_OBSERVED'
        }
        $processIds=@($runs|ForEach-Object {[int](Get-Content -LiteralPath $_.ready -Raw)})
        Assert (@($processIds|Select-Object -Unique).Count -eq 6 -and @($processIds|ForEach-Object {Get-Process -Id $_ -ErrorAction SilentlyContinue}).Count -eq 6) 'parallel.six_real_live_powershell_processes'
        $fast=ToolCall $c 3101 'powershell_exec' @{operation_key='parallel-seventh';command="Write-Output 'SEVENTH'";timeout_seconds=10}
        Assert ($fast.status -eq 'COMPLETED' -and $fast.stdout.Trim() -ceq 'SEVENTH') 'parallel.seventh_completes_while_six_active'
        $ack=Job $b 3102 @{action='cancel';job_id=$runs[0].id}
        Assert ($ack.status -eq 'OK' -and $ack.job.state -eq 'CANCEL_REQUESTED') 'parallel.cancel_one'
        $interrupted=Wait-JobState $c $runs[0].id 'NEEDS_RECONCILIATION'
        Assert ($interrupted.execution.status -eq 'CANCELLED_UNKNOWN_EFFECT') 'parallel.cancelled_tree_unknown_not_replayed'
        $remaining=Job $b 3103 @{action='status';job_id=$runs[1].id}
        Assert ($remaining.job.state -eq 'RUNNING') 'parallel.cancel_does_not_interrupt_other_job'
        Assert (-not (Get-Process -Id $processIds[0] -ErrorAction SilentlyContinue)) 'parallel.cancelled_pid_gone'
    }finally{
        [IO.File]::WriteAllText($release,'release')
    }
    for($i=1;$i -lt 6;$i++){
        $done=Wait-JobState $b $runs[$i].id 'COMPLETED'
        Assert ($done.execution.stdout.Trim() -ceq $runs[$i].marker -and $done.execution.audit_persisted) ('parallel.distinct_saved_result.'+$i)
    }
    $first=Job $a 12 @{action='submit';operation_key='shared';resource_key='fixture-fifo';command="Start-Sleep -Seconds 12; Write-Output 'first'";timeout_seconds=20}
    Assert ($first.status -eq 'OK' -and $first.job.state -eq 'QUEUED') 'r3.durable_admission'
    $null=Wait-JobState $b $first.job.job_id 'RUNNING'
    $independent=Job $c 120 @{action='submit';operation_key='parallel-short';command="Write-Output 'INDEPENDENT'";timeout_seconds=10}
    $fast=Wait-JobState $c $independent.job.job_id 'COMPLETED'
    $still=Job $b 121 @{action='status';job_id=$first.job.job_id}
    Assert ($fast.execution.stdout.Trim() -ceq 'INDEPENDENT' -and $still.job.state -eq 'RUNNING') 'parallel.short_finishes_while_long_active'
    Send $b 13 'tools/call' @{name='powershell_job';arguments=@{action='submit';operation_key='next';resource_key='fixture-fifo';command="Write-Output 'second'";timeout_seconds=10};_meta=$meta}
    Send $c 13 'tools/call' @{name='powershell_job';arguments=@{action='submit';operation_key='shared';resource_key='fixture-fifo';command="Start-Sleep -Seconds 12; Write-Output 'first'";timeout_seconds=20};_meta=$meta}
    $next=(Receive $b 13).structuredContent;$dup=(Receive $c 13).structuredContent
    Assert ($next.job.state -eq 'QUEUED' -and $dup.job.job_id -eq $first.job.job_id) 'parallel.explicit_resource_fifo_and_idempotency'
    $conflict=Job $c 14 @{action='submit';operation_key='shared';resource_key='fixture-fifo';command="Write-Output 'different'"}
    Assert ($conflict.status -eq 'CONFLICT') 'r3.same_key_different_payload_conflict'
    $a.Kill($true);$a.WaitForExit()
    $one=Wait-JobState $b $first.job.job_id 'COMPLETED'
    $two=Wait-JobState $c $next.job.job_id 'COMPLETED'
    Assert ($one.execution.stdout.Trim() -eq 'first' -and $two.execution.stdout.Trim() -eq 'second' -and $one.execution.audit_persisted -and $two.execution.audit_persisted) 'r3.disconnect_and_distinct_results'
    $kill=Job $b 15 @{action='submit';operation_key='cancel';command='Start-Sleep -Seconds 30';timeout_seconds=60}
    $null=Wait-JobState $c $kill.job.job_id 'RUNNING'
    Start-Sleep -Milliseconds 700
    $ack=Job $c 16 @{action='cancel';job_id=$kill.job.job_id}
    Assert ($ack.status -eq 'OK' -and $ack.job.state -eq 'CANCEL_REQUESTED') 'r3.running_cancel_ack'
    $unknown=Wait-JobState $b $kill.job.job_id 'NEEDS_RECONCILIATION'
    Assert ($unknown.execution.status -eq 'CANCELLED_UNKNOWN_EFFECT') 'r3.cancel_unknown_effect'
    Stop-Service $name
    Start-Service $name
    $null=Wait-Status $b 'OK'
    $unknownSource=Job $c 114 @{action='logs';job_id=$kill.job.job_id;step_index=1;include_interrupted_command=$true}
    Assert ($unknownSource.job.interrupted_source_available -and $unknownSource.job.interrupted_command -ceq 'Start-Sleep -Seconds 30') 'hardening.interrupted_source_retained'
    $decision=Job $c 115 @{action='reconcile';job_id=$kill.job.job_id;decision='UNKNOWN';evidence='Isolated cancellation fixture: effect deliberately unknown.'}
    Assert ($decision.status -eq 'OK' -and $decision.job.state -eq 'RECONCILED' -and $decision.job.reconciliation.decision -eq 'UNKNOWN') 'hardening.reconcile_unknown'
    $repeat=Job $b 116 @{action='reconcile';job_id=$kill.job.job_id;decision='UNKNOWN';evidence='Isolated cancellation fixture: effect deliberately unknown.'}
    Assert ($repeat.status -eq 'OK' -and $repeat.job.reconciliation.correlation_id -eq $decision.job.reconciliation.correlation_id) 'hardening.reconciliation_idempotent'
    $change=Job $b 117 @{action='reconcile';job_id=$kill.job.job_id;decision='EFFECT_CONFIRMED';evidence='Different decision'}
    Assert ($change.status -eq 'CONFLICT') 'hardening.reconciliation_immutable'
    $noreplay=Job $b 118 @{action='submit';operation_key='cancel';command='Start-Sleep -Seconds 30';timeout_seconds=60}
    Assert ($noreplay.job.state -eq 'RECONCILED' -and $noreplay.job.job_id -eq $kill.job.job_id) 'hardening.reconciled_key_no_replay'
    $saved=Job $c 17 @{action='logs';job_id=$first.job.job_id}
    Assert ($saved.job.state -eq 'COMPLETED' -and $saved.job.execution.stdout.Trim() -eq 'first') 'r3.persisted_real_output'
    $list=Job $c 18 @{action='list'}
    Assert (@($list.jobs|Where-Object {$_.job_id -eq $kill.job.job_id -and $_.state -eq 'RECONCILED'}).Count -eq 1) 'r3.list_recovery'
    Write-Host 'R3_NATIVE_JOBS=PASS (actual service and PowerShell, three SDK stdio clients; no live tunnel/reboot)'
    # Real non-idempotent local side effect, followed by a deliberate interruption.
    $marker=Join-Path $data 'recovery-marker.txt'
    $safePath=$marker.Replace("'","''")
    $scriptA="[IO.File]::AppendAllText('$safePath','A'); Start-Sleep -Seconds 90"
    $scriptB="[IO.File]::AppendAllText('$safePath','B'); Write-Output 'R4_OUTPUT_FIXTURE_7A98E1'"
    $active=Job $b 30 @{action='submit';operation_key='r4-crash-active';resource_key='fixture-crash';command=$scriptA;timeout_seconds=120}
    $queued=Job $c 31 @{action='submit';operation_key='r4-crash-queued';resource_key='fixture-crash';command=$scriptB;timeout_seconds=20}
    Assert ($active.status -eq 'OK' -and $queued.status -eq 'OK') 'r4.durable_pending_jobs'
    $deadline=[Diagnostics.Stopwatch]::StartNew()
    while($deadline.Elapsed.TotalSeconds -lt 10){
        if((Test-Path -LiteralPath $marker) -and [IO.File]::ReadAllText($marker) -eq 'A'){break}
        Start-Sleep -Milliseconds 100
    }
    Assert ((Test-Path -LiteralPath $marker) -and [IO.File]::ReadAllText($marker) -eq 'A') 'r4.external_effect_observed_before_failure'
    $pre=(Get-CimInstance Win32_Service -Filter "Name='$name'").ProcessId
    Stop-Process -Id $pre -Force
    # Do not start service manually: SCM recovery policy is the owner.
    $clock=[Diagnostics.Stopwatch]::StartNew()
    do{
        Start-Sleep -Milliseconds 200
        $restarted=Status $b 301
        if($restarted.status -eq 'OK' -and $restarted.service_pid -ne $pre){break}
    }while($clock.Elapsed.TotalSeconds -lt 20)
    Assert ($restarted.status -eq 'OK' -and $restarted.service_pid -ne $pre) 'r4.actual_scm_auto_restart'
    $unknown=Wait-JobState $b $active.job.job_id 'NEEDS_RECONCILIATION'
    Assert ((-not $unknown.Contains('execution') -or $null -eq $unknown['execution']) -and $unknown.job_id -eq $active.job.job_id) 'r4.unknown_effect_not_replayed'
    $recovered=Wait-JobState $c $queued.job.job_id 'COMPLETED'
    Assert ($recovered.execution.stdout.Trim() -eq 'R4_OUTPUT_FIXTURE_7A98E1' -and [IO.File]::ReadAllText($marker) -ceq 'AB') 'r4.queued_runs_after_crash_effect_exactly_once_in_fixture'
    $again=Job $b 32 @{action='submit';operation_key='r4-crash-active';resource_key='fixture-crash';command=$scriptA;timeout_seconds=120}
    Assert ($again.job.job_id -eq $active.job.job_id -and $again.job.state -eq 'NEEDS_RECONCILIATION') 'r4.retry_cannot_replay_unknown_effect'
    Stop-Service $name
    # Read actual persisted database bytes only while SCM service is stopped.
    $files=Get-ChildItem -LiteralPath $data -File | Where-Object {$_.Name -like 'jobs.db*'}
    Assert (@($files).Count -gt 0) 'r4.database_exists'
    foreach($file in $files){
        $bytes=[IO.File]::ReadAllBytes($file.FullName)
        $text=[Text.Encoding]::UTF8.GetString($bytes)
        Assert (-not $text.Contains('R4_OUTPUT_FIXTURE_7A98E1') -and -not $text.Contains('AppendAllText')) 'r4.dpapi_no_fixture_plaintext_in_database'
    }
    Start-Service $name
    $null=Wait-Status $b 'OK'
    $read=Job $c 33 @{action='logs';job_id=$queued.job.job_id}
    Assert ($read.job.execution.stdout.Trim() -eq 'R4_OUTPUT_FIXTURE_7A98E1') 'r4.dpapi_decrypt_after_restart'
    Write-Host 'R4_ACTUAL_CRASH_RECOVERY=PASS (SCM restart, not physical OS reboot)'
    # A checkpoint is a committed step result, not a remembered in-memory index.
    $planMarker=Join-Path $data 'plan-marker.txt'
    $planPath=$planMarker.Replace("'","''")
    $steps=@(
        @{command="[IO.File]::AppendAllText('$planPath','A'); Write-Output 'step-A'";timeout_seconds=10;pause_after=$true},
        @{command="[IO.File]::AppendAllText('$planPath','B'); Write-Output 'step-B'";timeout_seconds=10}
    )
    $plan=Job $b 40 @{action='submit';operation_key='r4-plan';steps=$steps}
    Assert ($plan.status -eq 'OK' -and $plan.job.total_steps -eq 2) 'r4.plan_accepted'
    $paused=Wait-JobState $c $plan.job.job_id 'WAITING_FOR_RESUME'
    Assert ($paused.completed_steps -eq 1 -and $paused.execution.stdout.Trim() -ceq 'step-A' -and [IO.File]::ReadAllText($planMarker) -ceq 'A') 'r4.checkpoint_A_persisted'
    # Pending step must not run just because the process restarts.
    Stop-Service $name
    Start-Service $name
    $null=Wait-Status $b 'OK'
    $paused=Wait-JobState $b $plan.job.job_id 'WAITING_FOR_RESUME'
    Assert ($paused.completed_steps -eq 1 -and [IO.File]::ReadAllText($planMarker) -ceq 'A') 'r4.checkpoint_survives_restart'
    $samePlan=Job $c 41 @{action='submit';operation_key='r4-plan';steps=$steps}
    Assert ($samePlan.job.job_id -eq $plan.job.job_id -and $samePlan.job.state -eq 'WAITING_FOR_RESUME') 'r4.plan_idempotency'
    $deny=Job $b 42 @{action='resume';job_id=$plan.job.job_id;expected_completed_steps=2}
    Assert ($deny.status -eq 'NOT_RESUMABLE') 'r4.stale_checkpoint_rejected'
    $deny=Job $b 43 @{action='resume';job_id=$active.job.job_id;expected_completed_steps=1}
    Assert ($deny.status -eq 'NOT_RESUMABLE') 'r4.unknown_effect_resume_denied'
    $go=Job $c 44 @{action='resume';job_id=$plan.job.job_id;expected_completed_steps=1}
    Assert ($go.status -eq 'OK' -and $go.job.state -eq 'QUEUED') 'r4.resume_verified_checkpoint'
    $done=Wait-JobState $b $plan.job.job_id 'COMPLETED'
    Assert ($done.completed_steps -eq 2 -and $done.execution.stdout.Trim() -ceq 'step-B' -and [IO.File]::ReadAllText($planMarker) -ceq 'AB') 'r4.step_B_without_replaying_A'
    $log=Job $c 45 @{action='logs';job_id=$plan.job.job_id;step_index=1}
    Assert ($log.job.result_step -eq 1 -and $log.job.execution.stdout.Trim() -ceq 'step-A') 'r4.previous_step_output_persisted'
    # Invalid mixed single/plan payload cannot be admitted.
    $bad=Job $b 46 @{action='submit';operation_key='r4-invalid';command='Write-Output 1';steps=$steps}
    Assert ($bad.status -eq 'INVALID_ARGUMENT') 'r4.mixed_payload_rejected'
    Write-Host 'R4_CHECKPOINT_PLAN=PASS (explicit resume after SCM restart; OS reboot proof pending)'

    # External reboot boundary: no physical reboot in CI. A real SCM restart
    # must not release it; completed effects and outputs remain durable.
    $bootMarker=Join-Path $data 'boot-marker.txt'
    $bootPath=$bootMarker.Replace("'","''")
    $bootSteps=@(
        @{command="[IO.File]::AppendAllText('$bootPath','A'); Write-Output 'boot-step-A'";timeout_seconds=10;reboot_after=$true},
        @{command="if([IO.File]::ReadAllText('$bootPath') -cne 'A'){throw 'POSTCONDITION_FAILED'}; Write-Output 'verified'";timeout_seconds=10;verify_after_reboot=$true},
        @{command="[IO.File]::AppendAllText('$bootPath','B'); Write-Output 'boot-step-B'";timeout_seconds=10}
    )
    $missingCheck=@(@{command='Write-Output 1';reboot_after=$true},@{command='Write-Output 2'})
    $invalidBoot=Job $b 81 @{action='submit';operation_key='r4-boot-invalid';steps=$missingCheck}
    Assert ($invalidBoot.status -eq 'INVALID_ARGUMENT') 'r4.reboot_requires_declared_postcondition'
    $boot=Job $b 82 @{action='submit';operation_key='r4-boot-boundary';steps=$bootSteps}
    Assert ($boot.status -eq 'OK') 'r4.reboot_plan_admitted'
    $waiting=Wait-JobState $b $boot.job.job_id 'WAITING_FOR_REBOOT'
    Assert ($waiting.completed_steps -eq 1 -and $waiting.execution.stdout.Trim() -ceq 'boot-step-A' -and [IO.File]::ReadAllText($bootMarker) -ceq 'A') 'r4.reboot_checkpoint_committed'
    $deny=Job $c 83 @{action='resume';job_id=$boot.job.job_id;expected_completed_steps=1}
    Assert ($deny.status -eq 'NOT_RESUMABLE') 'r4.manual_resume_cannot_bypass_reboot'
    Stop-Service $name
    Start-Service $name
    $null=Wait-Status $b 'OK'
    $waiting=Wait-JobState $c $boot.job.job_id 'WAITING_FOR_REBOOT'
    Assert ($waiting.completed_steps -eq 1 -and $waiting.reboot_evidence -eq 'SAME_BOOT' -and [IO.File]::ReadAllText($bootMarker) -ceq 'A') 'r4.scm_restart_does_not_release_reboot'
    $same=Job $b 84 @{action='submit';operation_key='r4-boot-boundary';steps=$bootSteps}
    Assert ($same.job.job_id -eq $boot.job.job_id -and $same.job.state -eq 'WAITING_FOR_REBOOT') 'r4.reboot_checkpoint_idempotent'
    $cancelBoot=Job $c 85 @{action='cancel';job_id=$boot.job.job_id}
    Assert ($cancelBoot.status -eq 'OK' -and $cancelBoot.job.state -eq 'CANCELLED' -and [IO.File]::ReadAllText($bootMarker) -ceq 'A') 'r4.cancel_waiting_reboot'
    Write-Host 'R4_DURABLE_REBOOT_BOUNDARY=PASS (same kernel session; positive actual reboot still NOT_TESTED)'

    Send $b 70 'tools/call' @{name='powershell_screenshot';arguments=@{width=1};_meta=$meta}
    # This is a tool-level error, not a JSON-RPC failure.
    $line=$b.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
    $bad=$line|ConvertFrom-Json -AsHashtable
    Assert ($bad.id -eq 70 -and $bad.result.isError -and $bad.result.structuredContent.status -eq 'INVALID_ARGUMENT') 'r5.invalid_region_typed'
    $still=Status $c 71
    Assert ($still.status -eq 'OK') 'r5.status_after_screenshot_error'
    Write-Host 'R5_SCREENSHOT_CONTRACT=PASS (no physical desktop capture in CI)'

    # Native CLI diagnostics use the same service and complete store counts.
    $doctorStart=[Diagnostics.ProcessStartInfo]::new($Exe)
    $doctorStart.ArgumentList.Add('doctor')
    $doctorStart.UseShellExecute=$false
    $doctorStart.RedirectStandardOutput=$true
    $doctorStart.RedirectStandardError=$true
    $doctorProc=[Diagnostics.Process]::Start($doctorStart)
    try{
        $out=$doctorProc.StandardOutput.ReadToEndAsync()
        $err=$doctorProc.StandardError.ReadToEndAsync()
        if(-not $doctorProc.WaitForExit(15000)){throw 'DOCTOR_TIMEOUT'}
        $rawDoctor=$out.GetAwaiter().GetResult()
        $doctorExit=$doctorProc.ExitCode
    }finally{if(-not $doctorProc.HasExited){$doctorProc.Kill($true);$doctorProc.WaitForExit()};$doctorProc.Dispose()}
    $doctor=$rawDoctor|ConvertFrom-Json -AsHashtable
    Assert ($doctor.schemaVersion -eq 1 -and $doctor.status -eq 'DEGRADED' -and $doctorExit -eq 1) 'r6.doctor_reports_isolated_install_degraded'
    Assert ($doctor.service.service_pid -eq (Get-CimInstance Win32_Service -Filter "Name='$name'").ProcessId -and $doctor.store.schemaVersion -eq 4) 'r6.doctor_authoritative_service_store'
    Assert ($doctor.store.total -gt 0 -and $doctor.store.waitingForReboot -eq 0 -and $doctor.store.needsReconciliation -ge 1) 'r6.doctor_total_state_counts'
    Assert ($doctor.localOperationalReady -and $doctor.reconciliationCount -ge 1 -and
        $doctor.externalAcceptanceStatus -eq 'NOT_TESTED') 'r6.doctor_operational_ready_despite_historical_reconciliation'
    Assert (@($doctor.checks|Where-Object {$_.component -eq 'Remote' -and $_.status -eq 'NOT_TESTED'}).Count -eq 1) 'r6.doctor_no_false_remote_acceptance'
    if($Performance){
        $performance=& (Join-Path $PSScriptRoot 'Test-NativePerformance.ps1') -Exe $Exe -DurationSeconds 12 -ClientCount 10 -ExerciseJobs|ConvertFrom-Json
        Assert ($performance.status -eq 'PASS' -and $performance.clients -eq 10 -and
            $performance.status_latency.count -ge 100 -and
            $performance.status_latency.p95_ms -le 500 -and
            $performance.admission_latency.count -eq 12 -and $performance.cancel_latency.count -eq 12) 'r7.bounded_performance_fixture'
        Write-Host ('PERFORMANCE_BASELINE='+($performance|ConvertTo-Json -Depth 8 -Compress))
    }



}finally{
    foreach($p in $clients){
        if(-not $p.HasExited){$p.StandardInput.Close();if(-not $p.WaitForExit(3000)){$p.Kill($true);$p.WaitForExit()}}
        $p.Dispose()
    }
    if(Get-Service $name -ErrorAction SilentlyContinue){Stop-Service $name -ErrorAction SilentlyContinue;sc.exe delete $name|Out-Host}
    if(Test-Path -LiteralPath $data){Remove-Item -LiteralPath $data -Recurse -Force}
}
