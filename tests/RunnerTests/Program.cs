using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;

if (args is ["window-cdp-worker", var cdpParent] &&
    int.TryParse(cdpParent, out int cdpParentId) && cdpParentId > 0)
{
    // The production owner spawns its own executable. Give this CI-only
    // frontend the identical worker entry point for genuine pipe tests.
    await WindowChromeBroker.RunWorkerAsync(cdpParentId);
    return;
}

if (args is ["--chrome-pipe-live-action", var liveWindow, var liveProfile, var liveUrl,
        var managedProfileId])
{
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    ChromeBrokerRequest Status() => new(Connection: new(liveWindow, liveProfile,
        ChromeConnectionAction.STATUS, Mode: ChromeConnectionMode.MANAGED_BROWSER,
        ManagedProfileId: managedProfileId), TraceId: Guid.NewGuid().ToString("N"));
    var statusRequest = Status();
    var status = await ChromeBrokerPipe.ExchangeAsync(statusRequest, limit.Token);
    if (status?.Connection?.Status == ChromeConnectionCode.PROFILE_MISMATCH &&
        WindowCatalog.TryParseId(liveWindow, out var identity))
    {
        // The throwaway browser can update its native avatar label while
        // multiple independent clients are starting. This rejected STATUS
        // dispatched no browser action; re-read the same HWND and PID before
        // trying the read-only STATUS once with the current native selector.
        using var self = Process.GetCurrentProcess();
        var nativeAvatar = ChromeProfileCatalog.ReadWindowProfileStable(liveWindow,
            self.SessionId, out _);
        if (nativeAvatar is { } resolvedAvatar && resolvedAvatar.WindowId == liveWindow &&
            resolvedAvatar.ProcessId == identity.ProcessId)
        {
            liveProfile = resolvedAvatar.DisplayName;
            statusRequest = Status();
            status = await ChromeBrokerPipe.ExchangeAsync(statusRequest, limit.Token);
        }
    }
    var inspectRequest = new ChromeBrokerRequest(Inspect: new WindowInspectRequest(
        liveWindow, 4, 20, null, null, 0, false, "CI_CDP_BUTTON",
        WindowRoleFilter.BUTTON, 0, WindowInspectScope.TAB,
        ExpectedProfile: liveProfile, ExpectedUrl: liveUrl),
        TraceId: Guid.NewGuid().ToString("N"));
    var inspect = await ChromeBrokerPipe.ExchangeAsync(inspectRequest, limit.Token);
    var elements = inspect?.Inspect?.Elements.Where(e => e.ElementId.StartsWith("c1:",
        StringComparison.Ordinal)).ToArray() ?? [];
    ChromeBrokerReply? action = null;
    ChromeBrokerRequest? actionRequest = null;
    if (elements.Length == 1)
    {
        actionRequest = new(Action: new WindowActionRequest(liveWindow, elements[0].ElementId,
            WindowActionKind.CLICK, null, ExpectedProfile: liveProfile, ExpectedUrl: liveUrl,
            ObservationTimeoutMs: 1500), TraceId: Guid.NewGuid().ToString("N"));
        action = await ChromeBrokerPipe.ExchangeAsync(actionRequest, limit.Token);
    }
    bool valid = status?.OwnerPid is > 0 &&
        status.Connection?.State == ChromeConnectionState.CONNECTED &&
        status.TraceId == statusRequest.TraceId &&
        inspect?.OwnerPid == status.OwnerPid &&
        inspect?.TraceId == inspectRequest.TraceId &&
        inspect?.Inspect?.Status == WindowInspectCode.COMPLETED &&
        inspect.Inspect.SessionGeneration == status.Connection.SessionGeneration &&
        action?.OwnerPid == status.OwnerPid &&
        action?.TraceId == actionRequest?.TraceId &&
        action?.Action is { Status: WindowActionCode.COMPLETED, EffectUnknown: false,
            ExecutionMethod: WindowExecutionMethod.CHROME_CDP } &&
        action?.Action?.SessionGeneration == status?.Connection?.SessionGeneration;
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        owner_pid = status?.OwnerPid,
        session_generation = status?.Connection?.SessionGeneration,
        connection_code = status?.Connection?.Status.ToString(),
        connection_state = status?.Connection?.State.ToString(),
        connection_detail = status?.Connection?.Detail,
        inspect_code = inspect?.Inspect?.Status.ToString(),
        inspect_detail = inspect?.Inspect?.Detail,
        status = action?.Action?.Status.ToString(),
        action_detail = action?.Action?.Detail,
        verified = valid
    }, PipeWire.Json));
    Environment.ExitCode = valid ? 0 : 1;
    return;
}

if (args is ["--chrome-pipe-invalid-frame"])
{
    // A separate elevated, same-executable frontend sends one oversized frame
    // header. It keeps the pipe open until the owner rejects it, so the real
    // authorized-server path (not an early disconnected-client path) is tested.
    using var self = Process.GetCurrentProcess();
    await using var pipe = new NamedPipeClientStream(".", ChromeBrokerPipe.Name(self.SessionId),
        PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await pipe.ConnectAsync(8000, deadline.Token);
    var header = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
    await pipe.WriteAsync(header, deadline.Token);
    await pipe.FlushAsync(deadline.Token);
    try
    {
        int read = await pipe.ReadAsync(new byte[1].AsMemory(), deadline.Token);
        Environment.ExitCode = read == 0 ? 0 : 1;
    }
    catch (IOException) { Environment.ExitCode = 0; } // Server closed the rejected pipe.
    return;
}

if (args is ["--chrome-pipe-probe", var probedWindow])
{
    var request = new ChromeBrokerRequest(Connection: new(probedWindow,
        "CI_NO_CHROME", ChromeConnectionAction.STATUS),
        TraceId: Guid.NewGuid().ToString("N"));
    var result = await ChromeBrokerPipe.ExchangeAsync(request, CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        owner_pid = result?.OwnerPid,
        state = result?.Connection?.State.ToString(),
        trace_verified = result?.TraceId == request.TraceId
    }, PipeWire.Json));
    Environment.ExitCode = result?.OwnerPid is > 0 &&
        result.Connection?.State == ChromeConnectionState.NOT_REQUESTED &&
        result.TraceId == request.TraceId ? 0 : 1;
    return;
}

static void Check(bool ok, string label)
{
    if (!ok) throw new InvalidOperationException("FAIL " + label);
    Console.WriteLine("PASS " + label);
}

// The SDK's generated tool catalog and exact typed argument gates must stay
// discoverable even when a separately published client cached an older schema.
TcpSchemaAcceptance.Run(Check);
NetworkProbeAcceptance.Run();
await NetworkProbeAcceptance.RunLoopbackAsync();
await ExpertDiagnosticsAcceptance.RunAsync();
AdminActionFixture.Run();

// Exercise the owned, throwaway Chrome browser before the remaining native
// window tests, so their accessibility activity cannot disturb its toolbar.
await ChromeAcceptanceFixture.RunAsync();
{
    Check(SystemSnapshot.Valid(SystemQueryKind.PROCESSES,Environment.ProcessId,null,50,0) &&
        SystemSnapshot.Valid(SystemQueryKind.SERVICES,null,"WindowsAdminMCP",20,0) &&
        SystemSnapshot.Valid(SystemQueryKind.NETWORK_INTERFACES,null,null,10,0) &&
        !SystemSnapshot.Valid(SystemQueryKind.PROCESSES,-1,null,50,0) &&
        !SystemSnapshot.Valid(SystemQueryKind.SERVICES,null," ",50,0) &&
        !SystemSnapshot.Valid(SystemQueryKind.NETWORK_INTERFACES,12,null,50,0) &&
        !SystemSnapshot.Valid(SystemQueryKind.PROCESSES,null,null,51,0) &&
        !SystemSnapshot.Valid(SystemQueryKind.PROCESSES,null,null,50,-1),
        "issue72.system_query_discriminated_bounds");
    var own=SystemSnapshot.Observe(SystemQueryKind.PROCESSES,Environment.ProcessId,null,50,0,
        CancellationToken.None);
    var services=SystemSnapshot.Observe(SystemQueryKind.SERVICES,null,null,5,0,
        CancellationToken.None);
    var network=SystemSnapshot.Observe(SystemQueryKind.NETWORK_INTERFACES,null,null,5,0,
        CancellationToken.None);
    var missing=SystemSnapshot.Observe(SystemQueryKind.SERVICES,null,
        "WindowsAdminMCP-Never-Installed-Query-Fixture",50,0,CancellationToken.None);
    var self=own.Processes?.SingleOrDefault();
    Check(own.Status==SystemQueryCode.COMPLETED && self is { Pid: >0, Name: not "" } &&
        self.Pid==Environment.ProcessId &&
        self.ParentPid is >0 && self.ThreadCount is >0 &&
        services.Status==SystemQueryCode.COMPLETED && services.Services?.Length is >0 and <=5 &&
        network.Status==SystemQueryCode.COMPLETED && network.NetworkInterfaces?.Length is >0 and <=5 &&
        missing.Status==SystemQueryCode.NOT_FOUND && missing.Services is null,
        "issue72.native_process_service_network_query_read_only");
    var eventLogService=SystemSnapshot.Observe(SystemQueryKind.SERVICES,null,"EventLog",5,0,
        CancellationToken.None);
    Check(eventLogService.Status==SystemQueryCode.COMPLETED &&
        eventLogService.Services?.SingleOrDefault() is
            { StartMode: not null, DependsOn: not null, DetailsUnavailable:false } &&
        services.Services!.All(s=>s.DependsOn is null && s.StartMode is null),
        "issue72.exact_native_scm_config_without_expensive_list_expansion");
    string wire=System.Text.Json.JsonSerializer.Serialize(own,PipeWire.Json);
    Check(wire.Contains("\"kind\":\"PROCESSES\"",StringComparison.Ordinal) &&
        wire.Contains("\"pid\":",StringComparison.Ordinal) &&
        !wire.Contains("\"services\":",StringComparison.Ordinal) &&
        !wire.Contains("command",StringComparison.OrdinalIgnoreCase),
        "issue72.system_query_typed_variant_wire_without_command_payload");
}
{
    Check(SystemTcpQueries.NativeLayoutsValid &&
        SystemTcpQueries.NativePort(0x901f) == 8080 &&
        SystemTcpQueries.NativePort(0x5000) == 80 &&
        SystemTcpQueries.NativeScope(0) == 0 &&
        SystemSnapshot.Valid(SystemQueryKind.TCP_CONNECTIONS,null,null,50,0) &&
        SystemSnapshot.Valid(SystemQueryKind.TCP_CONNECTIONS,Environment.ProcessId,null,5,10) &&
        !SystemSnapshot.Valid(SystemQueryKind.TCP_CONNECTIONS,-1,null,5,0) &&
        !SystemSnapshot.Valid(SystemQueryKind.TCP_CONNECTIONS,null,"ignored",5,0) &&
        !SystemSnapshot.Valid(SystemQueryKind.TCP_CONNECTIONS,null,null,5,0,
            eventLog:SystemEventLogKind.APPLICATION),
        "issue72.tcp_native_layout_endianness_and_discriminated_validation");
    async Task<bool> ListenerVisible(IPAddress address,SystemTcpFamily family)
    {
        var fixture=new TcpListener(address,0);
        fixture.Start();
        try
        {
            int port=((IPEndPoint)fixture.LocalEndpoint).Port;
            bool found=false,validResponse=true;
            for(int attempt=0;attempt<10 && !found;attempt++)
            {
                int offset=0;
                do
                {
                    var result=SystemSnapshot.Observe(SystemQueryKind.TCP_CONNECTIONS,
                        Environment.ProcessId,null,50,offset,CancellationToken.None);
                    validResponse &= result.Status==SystemQueryCode.COMPLETED &&
                        result.TcpConnections is {Length:<=50} &&
                        result.Events is null && result.Processes is null && result.Services is null;
                    if(!validResponse)break;
                    found=result.TcpConnections!.Any(c=>c.Pid==(uint)Environment.ProcessId &&
                        c.Family==family && c.State==SystemTcpState.LISTEN &&
                        c.LocalAddress==address.ToString() && c.LocalPort==port &&
                        c.RemoteAddress is null && c.RemotePort is null);
                    if(found || result.NextOffset is null)break;
                    offset=result.NextOffset.Value;
                }while(offset<=1000);
                if(!found)await Task.Delay(50);
            }
            return validResponse && found;
        }
        finally {fixture.Stop();}
    }
    Check(await ListenerVisible(IPAddress.Loopback,SystemTcpFamily.IPV4),
        "issue72.native_tcp4_listener_pid_port_state_and_no_remote_leak");
    if(System.Net.Sockets.Socket.OSSupportsIPv6)
        Check(await ListenerVisible(IPAddress.IPv6Loopback,SystemTcpFamily.IPV6),
            "issue72.native_tcp6_listener_pid_port_state_and_no_remote_leak");
}
{
    Check(SystemSnapshot.Valid(SystemQueryKind.EVENT_LOG,null,null,5,0,
            eventLog:SystemEventLogKind.APPLICATION,eventId:1000) &&
        SystemSnapshot.Valid(SystemQueryKind.EVENT_LOG,null,null,5,0,
            eventLog:SystemEventLogKind.APPLICATION,
            eventProvider:SystemEventProviderFilter.WINDOWS_ERROR_REPORTING) &&
        SystemSnapshot.Valid(SystemQueryKind.REGISTRY,null,null,5,0,
            registryHive:SystemRegistryHive.LOCAL_MACHINE,
            registryPath:@"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            registryView:SystemRegistryView.REGISTRY64) &&
        SystemSnapshot.Valid(SystemQueryKind.SCHEDULED_TASKS,null,null,5,0,taskFolder:@"\") &&
        !SystemSnapshot.Valid(SystemQueryKind.EVENT_LOG,null,null,5,0) &&
        !SystemSnapshot.Valid(SystemQueryKind.EVENT_LOG,null,null,5,0,
            eventLog:SystemEventLogKind.APPLICATION,eventId:-1) &&
        !SystemSnapshot.Valid(SystemQueryKind.EVENT_LOG,null,null,5,0,
            eventLog:SystemEventLogKind.SYSTEM,registryHive:SystemRegistryHive.USERS) &&
        !SystemSnapshot.Valid(SystemQueryKind.REGISTRY,null,null,5,0,
            registryHive:SystemRegistryHive.CURRENT_USER,registryPath:@"SOFTWARE",
            eventId:1) &&
        !SystemSnapshot.Valid(SystemQueryKind.SCHEDULED_TASKS,null,null,5,0,
            taskFolder:@"\..\Windows") &&
        !SystemSnapshot.Valid(SystemQueryKind.PROCESSES,null,null,5,0,
            includeValue:true) &&
        !SystemSnapshot.Valid(SystemQueryKind.PROCESSES,null,null,5,0,
            eventProvider:SystemEventProviderFilter.APPLICATION_ERROR) &&
        !SystemSnapshot.Valid(SystemQueryKind.EVENT_LOG,null,null,5,0,
            eventLog:SystemEventLogKind.APPLICATION,eventProvider:(SystemEventProviderFilter)999) &&
        !SystemSnapshot.Valid(SystemQueryKind.REGISTRY,null,null,5,1001,
            registryHive:SystemRegistryHive.LOCAL_MACHINE,registryPath:@"SOFTWARE"),
        "issue72.system_query_windows_diagnostic_variant_validation");
    var eventLog=SystemSnapshot.Observe(SystemQueryKind.EVENT_LOG,null,null,5,0,
        CancellationToken.None,eventLog:SystemEventLogKind.APPLICATION);
    var wer=SystemSnapshot.Observe(SystemQueryKind.EVENT_LOG,null,null,5,0,
        CancellationToken.None,eventLog:SystemEventLogKind.APPLICATION,
        eventProvider:SystemEventProviderFilter.WINDOWS_ERROR_REPORTING);
    var registry=SystemSnapshot.Observe(SystemQueryKind.REGISTRY,null,null,5,0,
        CancellationToken.None,registryHive:SystemRegistryHive.LOCAL_MACHINE,
        registryPath:@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
    var tasks=SystemSnapshot.Observe(SystemQueryKind.SCHEDULED_TASKS,null,null,5,0,
        CancellationToken.None,taskFolder:@"\");
    Check(eventLog.Status==SystemQueryCode.COMPLETED && eventLog.Events is { Length: <=5 } &&
        wer.Status==SystemQueryCode.COMPLETED && wer.Events is { Length: <=5 } &&
        wer.Events.All(e=>e.Provider=="Windows Error Reporting") &&
        registry.Status==SystemQueryCode.COMPLETED && registry.RegistryEntries is { Length: >0 and <=5 } &&
        tasks.Status==SystemQueryCode.COMPLETED && tasks.ScheduledTasks is { Length: <=5 },
        "issue72.native_event_registry_scheduler_queries_read_only");
    string wire=System.Text.Json.JsonSerializer.Serialize(registry,PipeWire.Json);
    Check(wire.Contains("\"kind\":\"REGISTRY\"",StringComparison.Ordinal) &&
        wire.Contains("\"registry_entries\":",StringComparison.Ordinal) &&
        !wire.Contains("\"data_text\":",StringComparison.Ordinal) &&
        !wire.Contains("\"events\":",StringComparison.Ordinal) &&
        !wire.Contains("\"scheduled_tasks\":",StringComparison.Ordinal),
        "issue72.registry_value_content_opt_in_and_variant_wire");
}
{
    string fixture = """
        # HELP commands_enqueued_total total
        commands_enqueued_total{tunnel_id="sensitive-tunnel-identity"} 18
        commands_polled_total{tunnel_id="sensitive-tunnel-identity"} 17
        commands_queue_capacity{tunnel_id="sensitive-tunnel-identity"} 20
        commands_queue_length{tunnel_id="sensitive-tunnel-identity"} 1
        dispatcher_worker_pool_capacity{tunnel_id="sensitive-tunnel-identity"} 10
        dispatcher_worker_pool_occupancy{tunnel_id="sensitive-tunnel-identity"} 2
        commands_poll_latency_seconds_sum 0.5
        commands_poll_latency_seconds_count 5
        commands_age_seconds_sum 0.015
        commands_age_seconds_count 3
        command_end_to_end_latency_milliseconds_sum{latency_type="enqueue_to_response",request_method="tools/call",tunnel_id="sensitive-tunnel-identity"} 900
        command_end_to_end_latency_milliseconds_count{latency_type="enqueue_to_response",request_method="tools/call",tunnel_id="sensitive-tunnel-identity"} 3
        command_end_to_end_latency_milliseconds_sum{latency_type="poll_to_response",request_method="tools/call",tunnel_id="sensitive-tunnel-identity"} 1200
        command_end_to_end_latency_milliseconds_count{latency_type="poll_to_response",request_method="tools/call",tunnel_id="sensitive-tunnel-identity"} 3
        command_end_to_end_latency_milliseconds_sum{latency_type="enqueue_to_response",request_method="resources/list",tunnel_id="sensitive-tunnel-identity"} 999999
        command_end_to_end_latency_milliseconds_count{latency_type="enqueue_to_response",request_method="resources/list",tunnel_id="sensitive-tunnel-identity"} 1
        """;
    var sampled = TunnelMetrics.Parse(fixture, DateTimeOffset.UnixEpoch);
    string wire = System.Text.Json.JsonSerializer.Serialize(sampled, PipeWire.Json);
    Check(sampled is { Status: TunnelMetricsCode.OK, CommandsEnqueued: 18,
        CommandsPolled: 17, QueueLength: 1, QueueCapacity: 20,
        WorkerActive: 2, WorkerCapacity: 10, PollSamples: 5,
        PollMeanMs: 100, QueueAgeSamples: 3, QueueAgeMeanMs: 5,
        ToolsEnqueueResponseSamples: 3, ToolsEnqueueResponseMeanMs: 300,
        ToolsPollResponseSamples: 3, ToolsPollResponseMeanMs: 400 } &&
        !wire.Contains("sensitive-tunnel-identity",StringComparison.Ordinal) &&
        wire.Contains("\"tools_poll_response_mean_ms\":400",StringComparison.Ordinal),
        "issue69.vendor_prometheus_typed_aggregates_without_sensitive_labels");
    Check(TunnelMetrics.Parse("commands_queue_length 1\n",DateTimeOffset.UnixEpoch).Status ==
        TunnelMetricsCode.INVALID_METRICS &&
        TunnelMetrics.Parse(new string('x',TunnelMetrics.MaxBytes+1),DateTimeOffset.UnixEpoch).Status ==
        TunnelMetricsCode.TOO_LARGE &&
        TunnelMetrics.Parse(fixture.Replace("commands_queue_length{tunnel_id=\"sensitive-tunnel-identity\"} 1",
            "commands_queue_length 1e999"),DateTimeOffset.UnixEpoch).Status ==
        TunnelMetricsCode.INVALID_METRICS,
        "issue69.vendor_prometheus_rejects_missing_oversize_and_nonfinite");
    string defaultStatus = System.Text.Json.JsonSerializer.Serialize(
        new AdminStatus(StatusCode.OK, "fixture-id"), PipeWire.Json);
    Check(!defaultStatus.Contains("tunnel_metrics",StringComparison.Ordinal),
        "issue69.vendor_metrics_are_optional_on_status_wire");
}
{
    string id = Guid.NewGuid().ToString("N");
    var metadata = new TransportTrace(1,id,TransportTimingSource.MONOTONIC_PROCESS_LOCAL,
        128,256,0.12,0.35,0.24,1.2,0.06,0.1,0.05,0.04,0.15,0.02);
    var timing = new ToolTiming(Transport:metadata);
    string json = System.Text.Json.JsonSerializer.Serialize(timing,PipeWire.Json);
    var restored = System.Text.Json.JsonSerializer.Deserialize<ToolTiming>(json,PipeWire.Json);
    Check(restored?.Transport == metadata && json.Contains("\"diagnostics_version\":1",
            StringComparison.Ordinal) && json.Contains("\"trace_id\":",StringComparison.Ordinal) &&
        !json.Contains("command",StringComparison.OrdinalIgnoreCase) &&
        !json.Contains("stdout",StringComparison.OrdinalIgnoreCase),
        "trace.monotonic_typed_metadata_wire_without_payload");
    var defaultWire = System.Text.Json.JsonSerializer.Serialize(new AdminRequest(AdminOperation.Status),PipeWire.Json);
    var tracedWire = System.Text.Json.JsonSerializer.Serialize(new AdminRequest(AdminOperation.Status,
        TraceTiming:true),PipeWire.Json);
    Check(!defaultWire.Contains("trace_timing",StringComparison.Ordinal) &&
        tracedWire.Contains("\"trace_timing\":true",StringComparison.Ordinal) &&
        System.Text.Json.JsonSerializer.Deserialize<AdminRequest>(defaultWire,PipeWire.Json)?.TraceTiming==false &&
        System.Text.Json.JsonSerializer.Deserialize<AdminRequest>(tracedWire,PipeWire.Json)?.TraceTiming==true,
        "trace.optional_diagnostics_preserves_old_wire_by_default");
}
static async Task<ExecutionResult> Run(string command, int timeout = 10, string? cwd = null, CancellationToken ct = default)
{
    var result = await PowerShellRunner.RunAsync(command, timeout, cwd, ct);
    Console.WriteLine($"runner status={result.Status} exit={result.ExitCode} stage={result.Stage} native_error={result.NativeError} elapsed_ms={result.ElapsedMs}");
    return result;
}
if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
    throw new InvalidOperationException("Isolated Windows CI only");
{
    string root = Path.Combine(Path.GetTempPath(), "wmcp-native-read-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        NativeFileRequest FileRequest(NativeFileAction action, string path, int page = 50,
            string? cursor = null, bool hash = false, long offset = 0, int length = 65536,
            string? version = null) =>
            new(action, path, CommandContext.USER, page, cursor, hash, offset, length, version);
        async Task<NativeFileReply> FileCall(NativeFileRequest request) =>
            await NativeFileReader.ExecuteAsync(request, CancellationToken.None);

        string empty = Path.Combine(root, "empty.bin");
        File.WriteAllBytes(empty, []);
        var emptyStat = await FileCall(FileRequest(NativeFileAction.STAT, empty, hash:true));
        Console.WriteLine($"files.empty_stat_native status={emptyStat.Status} stage={emptyStat.Stage} " +
            $"native_error={emptyStat.NativeError} kind={emptyStat.Info?.Kind} " +
            $"size={emptyStat.Info?.Size} hash={emptyStat.Info?.Sha256}");
        Check(emptyStat is { Status: NativeFileCode.COMPLETED,
            Info: { Kind: NativeFileKind.FILE, Size: 0, Sha256: not null } } &&
            emptyStat.Info.Sha256 == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([])),
            "files.empty_sha256_and_identity");
        var emptyRead = await FileCall(FileRequest(NativeFileAction.READ_CHUNK, empty, offset:0));
        Check(emptyRead is { Status: NativeFileCode.COMPLETED, ActualLength: 0, Eof: true,
            DataBase64: "", ChunkSha256: not null }, "files.empty_eof_chunk");

        foreach (var (name, encoding, text) in new (string, System.Text.Encoding, string)[] {
            ("emoji-utf8.txt", new System.Text.UTF8Encoding(false), "привет 🌍\n"),
            ("utf16le.txt", System.Text.Encoding.Unicode, "тест 🌍"),
            ("utf16be.txt", System.Text.Encoding.BigEndianUnicode, "тест 🌍") })
        {
            string path = Path.Combine(root, name);
            byte[] original = encoding.GetBytes(text);
            File.WriteAllBytes(path, original);
            var stat = await FileCall(FileRequest(NativeFileAction.STAT, path, hash:true));
            var read = await FileCall(FileRequest(NativeFileAction.READ_CHUNK, path, offset:0,
                length:262144, version:stat.Info?.Version));
            Check(stat.Info?.Sha256 == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)) &&
                read.Status == NativeFileCode.COMPLETED && read.ActualLength == original.Length &&
                Convert.FromBase64String(read.DataBase64!) .SequenceEqual(original) && read.Eof == true,
                "files.binary_exact_" + name);
        }
        string largePath = Path.Combine(root, "large.bin");
        byte[] contents = new byte[5 * 1024 * 1024 + 13];
        System.Security.Cryptography.RandomNumberGenerator.Fill(contents);
        File.WriteAllBytes(largePath, contents);
        var largeStat = await FileCall(FileRequest(NativeFileAction.STAT, largePath, hash:true));
        Check(largeStat.Status == NativeFileCode.COMPLETED && largeStat.Info?.Size == contents.Length &&
            largeStat.Info.Sha256 == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(contents)),
            "files.five_megabytes_sha256");
        var roundtrip = new List<byte>(contents.Length);
        for (long offset = 0; offset < contents.Length; offset += 262144)
        {
            var chunk = await FileCall(FileRequest(NativeFileAction.READ_CHUNK, largePath,
                offset:offset, length:262144, version:largeStat.Info!.Version));
            byte[] bytes = Convert.FromBase64String(chunk.DataBase64!);
            Check(chunk.Status == NativeFileCode.COMPLETED && chunk.ActualLength == bytes.Length &&
                chunk.ChunkSha256 == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) &&
                chunk.Eof == (offset + bytes.Length == contents.Length),
                "files.chunk_at_" + offset);
            roundtrip.AddRange(bytes);
        }
        Check(roundtrip.SequenceEqual(contents), "files.five_megabytes_roundtrip");
        Check((await FileCall(FileRequest(NativeFileAction.READ_CHUNK, largePath,
            offset:contents.Length))).Eof == true, "files.exact_eof");
        Check((await FileCall(FileRequest(NativeFileAction.READ_CHUNK, largePath,
            offset:contents.Length+1))).Status == NativeFileCode.INVALID_ARGUMENT &&
            (await FileCall(FileRequest(NativeFileAction.READ_CHUNK, largePath,
            offset:0, length:262145))).Status == NativeFileCode.INVALID_ARGUMENT &&
            (await FileCall(FileRequest(NativeFileAction.READ_CHUNK, largePath,
            offset:0, length:0))).Status == NativeFileCode.INVALID_ARGUMENT,
            "files.reject_invalid_offsets_and_lengths");
        File.SetLastWriteTimeUtc(largePath, DateTime.UtcNow.AddMinutes(-2));
        Check((await FileCall(FileRequest(NativeFileAction.READ_CHUNK, largePath,
            offset:0, version:largeStat.Info!.Version))).Status == NativeFileCode.CHANGED_SINCE_READ,
            "files.reject_stale_version");
        var listing = await FileCall(FileRequest(NativeFileAction.LIST, root, page:2));
        Check(listing.Status == NativeFileCode.COMPLETED && listing.Entries?.Length == 2 &&
            listing.NextCursor is not null, "files.paged_list_first");
        var next = await FileCall(FileRequest(NativeFileAction.LIST, root, page:2,
            cursor:listing.NextCursor));
        Check(next.Status == NativeFileCode.COMPLETED && next.Entries?.Length == 2 &&
            next.Entries.All(e => string.CompareOrdinal(e.Name, listing.Entries![^1].Name) > 0),
            "files.paged_list_second_stable_order");
        File.WriteAllText(Path.Combine(root, "mutation.txt"), "changed");
        var changed = await FileCall(FileRequest(NativeFileAction.LIST, root, page:2,
            cursor:listing.NextCursor));
        Check(changed.Status == NativeFileCode.CHANGED_SINCE_LIST,
            "files.detect_mutated_directory_between_pages");
        Check((await FileCall(FileRequest(NativeFileAction.LIST, root, page:2,
            cursor:"not-base64"))).Status == NativeFileCode.INVALID_CURSOR &&
            (await FileCall(FileRequest(NativeFileAction.STAT, root))).Info?.Kind == NativeFileKind.DIRECTORY &&
            (await FileCall(FileRequest(NativeFileAction.LIST, empty))).Status == NativeFileCode.NOT_DIRECTORY &&
            (await FileCall(FileRequest(NativeFileAction.READ_CHUNK, root))).Status == NativeFileCode.NOT_FILE &&
            (await FileCall(FileRequest(NativeFileAction.STAT, Path.Combine(root,"missing")))).Status == NativeFileCode.NOT_FOUND,
            "files.typed_invalid_cursor_kinds_and_missing");
        Check(!NativeFileReader.Valid(FileRequest((NativeFileAction)99, empty)) &&
            !NativeFileReader.Valid(FileRequest(NativeFileAction.STAT, "relative\\file")) &&
            !NativeFileReader.Valid(FileRequest(NativeFileAction.LIST, root, page:101)),
            "files.reject_unknown_action_relative_path_and_unbounded_page");
    }
    finally { Directory.Delete(root, recursive:true); }
}
// A: Windows-only, synthetic files. No developer or operator data is touched.
{
    string root=Path.Combine(Path.GetTempPath(),"wmcp-native-write-"+Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        static string Hash(byte[] bytes)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        Check(new FileWriteReply(FileWriteCode.INVALID_ARGUMENT,"x",CommandContext.USER).SafeToRetry &&
            !new FileWriteReply(FileWriteCode.CONFLICT,"x",CommandContext.USER).SafeToRetry &&
            !new FileWriteReply(FileWriteCode.EFFECT_UNKNOWN,"x",CommandContext.USER,
                EffectState:WindowEffectState.EFFECT_UNKNOWN).SafeToRetry,
            "filewrite.safe_retry_deduced_only_for_no_dispatch");
        string path=Path.Combine(root,"roundtrip.bin"),id=Guid.NewGuid().ToString("N");
        byte[] payload=new byte[5*1024*1024+13];
        System.Security.Cryptography.RandomNumberGenerator.Fill(payload);
        FileWriteRequest Begin(string target,string operation,byte[] bytes,
            FileOverwritePolicy policy=FileOverwritePolicy.FAIL_IF_EXISTS,
            string? version=null,string? originalHash=null)=>
            new(FileWriteAction.BEGIN,target,operation,CommandContext.USER,
                bytes.LongLength,Hash(bytes),policy,version,originalHash);
        FileWriteRequest Step(FileWriteAction action,string target,string operation)=>
            new(action,target,operation,CommandContext.USER);
        FileWriteReply Write(FileWriteRequest request)=>NativeFileWriter.Execute(request);
        Check(NativeFileWriter.Valid(Begin(path,id,payload)) &&
            !NativeFileWriter.Valid(Begin("relative",id,payload)) &&
            !NativeFileWriter.Valid(Begin(path,"not-guid",payload)) &&
            !NativeFileWriter.Valid(Begin(path,id,new byte[1]) with {ExpectedBytes=NativeFileWriter.MaxUploadBytes+1}) &&
            !NativeFileWriter.Valid(Begin(path,id,payload) with {ExpectedSha256="bad"}),
            "filewrite.typed_bounds_and_operation_identity");
        // A one-call user operation is expanded before the existing SQLite
        // PREPARE/OBSERVE boundary. Its stages still have exactly one stable ID.
        string inlinePath=Path.Combine(root,"inline.txt"),inlineId=Guid.NewGuid().ToString("N");
        byte[] inlineBytes=System.Text.Encoding.UTF8.GetBytes("native one-call 🌍");
        var inlineRequest=new FileWriteRequest(FileWriteAction.WRITE_ATOMIC,inlinePath,inlineId,
            CommandContext.USER,DataBase64:Convert.ToBase64String(inlineBytes));
        Check(NativeFileWriter.TryInlineSteps(inlineRequest,out var inlineSteps,out _) &&
            inlineSteps.Length==3 && inlineSteps[0].Action==FileWriteAction.BEGIN &&
            inlineSteps[1].Action==FileWriteAction.APPEND &&
            inlineSteps[2].Action==FileWriteAction.COMMIT &&
            inlineSteps[0].ExpectedSha256==Hash(inlineBytes) &&
            inlineSteps.All(s=>s.OperationId==inlineId && NativeFileWriter.Valid(s)),
            "filewrite.inline_plan_uses_original_durable_writer");
        Check(NativeFileWriter.TryInlineSteps(inlineRequest with {
                ExpectedBytes=inlineBytes.LongLength,ExpectedSha256=Hash(inlineBytes)},
                out var guardedInline,out _) && guardedInline.Length==3 &&
            guardedInline[0].ExpectedBytes==inlineBytes.LongLength &&
            guardedInline[0].ExpectedSha256==Hash(inlineBytes) &&
            !NativeFileWriter.TryInlineSteps(inlineRequest with {
                ExpectedBytes=inlineBytes.LongLength+1},out _,out var wrongSize) &&
            wrongSize==FileWriteCode.INVALID_ARGUMENT,
            "filewrite.inline_optional_size_and_digest_guard_before_effect");
        Check(inlineSteps.Select(Write).Select(r=>r.Status).SequenceEqual(
            [FileWriteCode.STAGED,FileWriteCode.STAGED,FileWriteCode.COMMITTED]) &&
            File.ReadAllBytes(inlinePath).SequenceEqual(inlineBytes) &&
            Write(inlineSteps[0]).Status==FileWriteCode.COMMITTED &&
            Write(Step(FileWriteAction.STATUS,inlinePath,inlineId)).Status==FileWriteCode.COMMITTED,
            "filewrite.inline_same_id_lost_ack_is_read_only");
        Check(!NativeFileWriter.TryInlineSteps(inlineRequest with {DataBase64="not base64"},
                out _,out var invalidBase64) && invalidBase64==FileWriteCode.INVALID_ARGUMENT &&
            !NativeFileWriter.TryInlineSteps(inlineRequest with {OperationId="invalid"},out _,out _) &&
            !NativeFileWriter.TryInlineSteps(inlineRequest with {
                DataBase64=Convert.ToBase64String(new byte[NativeFileWriter.MaxInlineBytes+1])},out _,out _) &&
            !NativeFileWriter.TryInlineSteps(inlineRequest with {ExpectedSha256=Hash([1,2,3])},
                out _,out var invalidHash) && invalidHash==FileWriteCode.HASH_MISMATCH &&
            !NativeFileWriter.Valid(inlineRequest),
            "filewrite.inline_invalid_inputs_rejected_before_disk");
        string inlineEmpty=Path.Combine(root,"inline-empty.bin");
        Check(NativeFileWriter.TryInlineSteps(inlineRequest with {Path=inlineEmpty,
            OperationId=Guid.NewGuid().ToString("N"),DataBase64=""},out var emptySteps,out _) &&
            emptySteps.Length==2 && emptySteps.Select(Write).ToArray()[^1].Status==FileWriteCode.COMMITTED &&
            File.Exists(inlineEmpty) && new FileInfo(inlineEmpty).Length==0,
            "filewrite.inline_empty_file_is_atomic");
        var begin=Write(Begin(path,id,payload));
        Check(begin is {Status:FileWriteCode.STAGED,ReceivedBytes:0} && !File.Exists(path) &&
            Write(Begin(path,id,payload)).Status==FileWriteCode.STAGED &&
            Write(Begin(path,id,[1,2])).Status==FileWriteCode.CONFLICT,
            "filewrite.begin_idempotent_and_conflict");
        for(int offset=0;offset<payload.Length;offset+=NativeFileWriter.MaxChunkBytes)
        {
            byte[] bytes=payload.AsSpan(offset,Math.Min(payload.Length-offset,NativeFileWriter.MaxChunkBytes)).ToArray();
            var append=new FileWriteRequest(FileWriteAction.APPEND,path,id,CommandContext.USER,
                Offset:offset,DataBase64:Convert.ToBase64String(bytes),ChunkSha256:Hash(bytes));
            var response=Write(append);
            Check(response.Status==FileWriteCode.STAGED && response.ReceivedBytes==offset+bytes.Length,
                "filewrite.chunk_"+offset);
            if(offset==0)
            {
                Check(Write(append).Status==FileWriteCode.STAGED &&
                    Write(append with {ChunkSha256=Hash([1,2])}).Status==FileWriteCode.INVALID_ARGUMENT &&
                    Write(append with {Offset=bytes.Length+1}).Status==FileWriteCode.CONFLICT,
                    "filewrite.duplicate_chunk_hash_and_gap");
            }
        }
        Check(!File.Exists(path) && Write(Step(FileWriteAction.STATUS,path,id)) is
            {Status:FileWriteCode.STAGED,ReceivedBytes:>0} &&
            Write(Step(FileWriteAction.COMMIT,path,id)) is
            {Status:FileWriteCode.COMMITTED,EffectState:WindowEffectState.EFFECT_VERIFIED} &&
            File.ReadAllBytes(path).SequenceEqual(payload) &&
            Write(Step(FileWriteAction.COMMIT,path,id)).Status==FileWriteCode.COMMITTED &&
            Write(Step(FileWriteAction.STATUS,path,id)).Status==FileWriteCode.COMMITTED,
            "filewrite.atomic_commit_verified_sha_and_lost_ack_replay");
        Check(Write(Begin(path,Guid.NewGuid().ToString("N"),[3])).Status==FileWriteCode.CONFLICT,
            "filewrite.fail_if_exists_no_clobber");

        var stat=await NativeFileReader.ExecuteAsync(new(NativeFileAction.STAT,path,CommandContext.USER,
            IncludeSha256:true),CancellationToken.None);
        byte[] replacement=System.Text.Encoding.Unicode.GetBytes("unicode 🌍 safely updated");
        string replaceId=Guid.NewGuid().ToString("N");
        var replaceBegin=Begin(path,replaceId,replacement,FileOverwritePolicy.REPLACE_IF_MATCH,
            stat.Info!.Version,stat.Info.Sha256);
        Check(Write(replaceBegin).Status==FileWriteCode.STAGED,"filewrite.cas_begin");
        var replacementAppend=new FileWriteRequest(FileWriteAction.APPEND,path,replaceId,CommandContext.USER,
            Offset:0,DataBase64:Convert.ToBase64String(replacement),ChunkSha256:Hash(replacement));
        Check(Write(replacementAppend).Status==FileWriteCode.STAGED &&
            Write(Step(FileWriteAction.COMMIT,path,replaceId)).Status==FileWriteCode.COMMITTED &&
            File.ReadAllBytes(path).SequenceEqual(replacement),"filewrite.cas_atomic_replace");
        Check(Write(Begin(path,Guid.NewGuid().ToString("N"),payload,
            FileOverwritePolicy.REPLACE_IF_MATCH,stat.Info.Version,stat.Info.Sha256)).Status==FileWriteCode.CONFLICT,
            "filewrite.cas_stale_version_or_hash_rejected");

        string twoWriters=Path.Combine(root,"two-writers.bin");
        string firstId=Guid.NewGuid().ToString("N"),secondId=Guid.NewGuid().ToString("N");
        byte[] firstBytes=[1,2,3],secondBytes=[3,2,1];
        Check(Write(Begin(twoWriters,firstId,firstBytes)).Status==FileWriteCode.STAGED &&
            Write(Begin(twoWriters,secondId,secondBytes)).Status==FileWriteCode.STAGED,
            "filewrite.two_writers_stage_independently");
        FileWriteReply Upload(string target,string operation,byte[] data)=>Write(new(
            FileWriteAction.APPEND,target,operation,CommandContext.USER,
            Offset:0,DataBase64:Convert.ToBase64String(data),ChunkSha256:Hash(data)));
        Check(Upload(twoWriters,firstId,firstBytes).Status==FileWriteCode.STAGED &&
            Upload(twoWriters,secondId,secondBytes).Status==FileWriteCode.STAGED &&
            Write(Step(FileWriteAction.COMMIT,twoWriters,firstId)).Status==FileWriteCode.COMMITTED &&
            Write(Step(FileWriteAction.COMMIT,twoWriters,secondId)).Status==FileWriteCode.CONFLICT &&
            File.ReadAllBytes(twoWriters).SequenceEqual(firstBytes),
            "filewrite.concurrent_new_file_only_one_writer_commits");

        string racing=Path.Combine(root,"external-writer.bin");
        File.WriteAllBytes(racing,[4,4,4]);
        var racingStat=await NativeFileReader.ExecuteAsync(new(NativeFileAction.STAT,racing,
            CommandContext.USER,IncludeSha256:true),CancellationToken.None);
        string racingId=Guid.NewGuid().ToString("N");
        Check(Write(Begin(racing,racingId,[5,5,5],FileOverwritePolicy.REPLACE_IF_MATCH,
            racingStat.Info!.Version,racingStat.Info.Sha256)).Status==FileWriteCode.STAGED &&
            Upload(racing,racingId,[5,5,5]).Status==FileWriteCode.STAGED,
            "filewrite.cas_begin_original_version_verified");
        File.WriteAllBytes(racing,[6,6,6]);
        Check(Write(Step(FileWriteAction.COMMIT,racing,racingId)).Status==FileWriteCode.CONFLICT &&
            File.ReadAllBytes(racing).SequenceEqual(new byte[]{6,6,6}),
            "filewrite.external_writer_changed_after_begin_never_overwritten");

        string aborted=Path.Combine(root,"aborted.bin"),abortId=Guid.NewGuid().ToString("N");
        Check(Write(Begin(aborted,abortId,[])).Status==FileWriteCode.STAGED &&
            Write(Step(FileWriteAction.ABORT,aborted,abortId)).Status==FileWriteCode.ABORTED &&
            Write(Step(FileWriteAction.COMMIT,aborted,abortId)).Status==FileWriteCode.ABORTED &&
            !File.Exists(aborted),"filewrite.abort_is_final_no_target_effect");
        string empty=Path.Combine(root,"empty.bin"),emptyId=Guid.NewGuid().ToString("N");
        Check(Write(Begin(empty,emptyId,[])).Status==FileWriteCode.STAGED &&
            Write(Step(FileWriteAction.COMMIT,empty,emptyId)).Status==FileWriteCode.COMMITTED &&
            File.Exists(empty) && new FileInfo(empty).Length==0,"filewrite.zero_byte_atomic_commit");

        // Simulate the crash gap without touching the target: an absent stage
        // must never be treated as a successful commit just from equal content.
        string lost=Path.Combine(root,"lost.bin"),lostId=Guid.NewGuid().ToString("N");
        byte[] same=[8,9,10];
        Check(Write(Begin(lost,lostId,same)).Status==FileWriteCode.STAGED,"filewrite.recovery_fixture");
        // Several intentionally conflicted writers can leave their own stage.
        // Resolve only the exact synthetic operation, never an unrelated file.
        string lostFingerprint=Hash(System.Text.Encoding.UTF8.GetBytes(
            Path.GetFullPath(lost)+"\0"+lostId))[..32];
        string stage=Path.Combine(root,".wmcp-"+lostFingerprint+".stage");
        File.Delete(stage);
        File.WriteAllBytes(lost,same);
        Check(Write(Step(FileWriteAction.STATUS,lost,lostId)).Status==FileWriteCode.EFFECT_UNKNOWN &&
            Write(Step(FileWriteAction.COMMIT,lost,lostId)).Status==FileWriteCode.EFFECT_UNKNOWN,
            "filewrite.no_false_commit_from_matching_unrelated_target");

        // Native rename succeeded, but the worker died before writing the
        // final COMMITTED journal. Reconstruct from COMMITTING + target hash.
        string moved=Path.Combine(root,"moved.bin"),movedId=Guid.NewGuid().ToString("N");
        Check(Write(Begin(moved,movedId,same)).Status==FileWriteCode.STAGED,
            "filewrite.crash_window_begin");
        var moveAppend=new FileWriteRequest(FileWriteAction.APPEND,moved,movedId,CommandContext.USER,
            Offset:0,DataBase64:Convert.ToBase64String(same),ChunkSha256:Hash(same));
        Check(Write(moveAppend).Status==FileWriteCode.STAGED,"filewrite.crash_window_staging");
        string fingerprint=Hash(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(moved)+"\0"+movedId))[..32];
        string stagePrefix=Path.Combine(root,".wmcp-"+fingerprint);
        string journalFile=stagePrefix+".journal",stageFile=stagePrefix+".stage";
        var journal=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(journalFile))!;
        journal["state"]="COMMITTING";
        File.WriteAllText(journalFile,journal.ToJsonString());
        Check(Write(Step(FileWriteAction.STATUS,moved,movedId)).Status==FileWriteCode.STAGED,
            "filewrite.crash_before_atomic_move_detected_no_effect");
        journal["state"]="COMMITTING";
        File.WriteAllText(journalFile,journal.ToJsonString());
        File.Move(stageFile,moved);
        Check(Write(Step(FileWriteAction.STATUS,moved,movedId)) is
            {Status:FileWriteCode.COMMITTED,EffectState:WindowEffectState.EFFECT_VERIFIED} &&
            File.ReadAllBytes(moved).SequenceEqual(same),
            "filewrite.crash_after_atomic_move_before_ack_recovered");
    }
    finally {Directory.Delete(root,recursive:true);}
}
// Central receipt owner and same-volume file readback must remain consistent
// across a real SQLite close/reopen between COMMIT intent and native rename.
{
    string root=Path.Combine(Path.GetTempPath(),"wmcp-file-ledger-"+Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        string db=Path.Combine(root,"jobs.db"),path=Path.Combine(root,"ledger.bin");
        string id=Guid.NewGuid().ToString("N"),actor="S-1-5-21-999-111-222-1001";
        byte[] bytes=System.Text.Encoding.UTF8.GetBytes("receipt recovery 🌍");
        string sha=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var begin=new FileWriteRequest(FileWriteAction.BEGIN,path,id,CommandContext.USER,
            bytes.Length,sha);
        var commit=new FileWriteRequest(FileWriteAction.COMMIT,path,id,CommandContext.USER);
        var status=new FileWriteRequest(FileWriteAction.STATUS,path,id,CommandContext.USER);
        FileReceiptRequest Prepare(FileWriteRequest request)=>
            FileReceiptRequest.From(request,FileReceiptPhase.PREPARE);
        FileReceiptRequest Observe(FileWriteRequest request,FileWriteReply reply)=>
            FileReceiptRequest.From(request,FileReceiptPhase.OBSERVE,reply);
        using(var owner=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
        {
            owner.Initialize(db);
            Check(owner.FileReceipt(Prepare(commit),actor).Code==FileReceiptCode.NOT_FOUND &&
                owner.FileReceipt(Prepare(begin),actor).Code==FileReceiptCode.READY &&
                owner.FileReceipt(Prepare(begin),actor).Code==FileReceiptCode.READY &&
                owner.FileReceipt(Prepare(begin),"S-1-5-18").Code==FileReceiptCode.CONFLICT &&
                owner.FileReceipt(Prepare(begin with {ExpectedBytes=bytes.Length+1}),actor).Code==
                    FileReceiptCode.CONFLICT &&
                owner.FileReceipt(Prepare(begin with {Path=Path.Combine(root,"other.bin")}),actor).Code==
                    FileReceiptCode.CONFLICT,
                "fileledger.unique_operation_actor_and_immutable_file_identity");
            var staged=NativeFileWriter.Execute(begin);
            Check(staged.Status==FileWriteCode.STAGED &&
                owner.FileReceipt(Observe(begin,staged),actor).Code==FileReceiptCode.READY,
                "fileledger.sqlite_and_native_begin_coordinated");
            var append=new FileWriteRequest(FileWriteAction.APPEND,path,id,CommandContext.USER,
                Offset:0,DataBase64:Convert.ToBase64String(bytes),ChunkSha256:sha);
            var chunk=NativeFileWriter.Execute(append);
            Check(owner.FileReceipt(Prepare(append),actor).Code==FileReceiptCode.READY &&
                chunk.Status==FileWriteCode.STAGED &&
                owner.FileReceipt(Observe(append,chunk),actor) is
                    {Code:FileReceiptCode.READY,ReceivedBytes:>0},
                "fileledger.monotonic_chunk_receipts");
            var intention=owner.FileReceipt(Prepare(commit),actor);
            Check(intention is {Code:FileReceiptCode.READY,State:FileReceiptState.COMMITTING} &&
                owner.FileReceipt(Prepare(commit),actor).State==FileReceiptState.COMMITTING,
                "fileledger.intent_persisted_before_commit");
            var unsafeResult=new FileWriteReply(FileWriteCode.EFFECT_UNKNOWN,id,CommandContext.USER,
                EffectState:WindowEffectState.EFFECT_UNKNOWN);
            Check(owner.FileReceipt(Observe(commit,unsafeResult),actor).Code==FileReceiptCode.READY &&
                owner.FileReceipt(Prepare(commit),actor).Code==FileReceiptCode.EFFECT_UNKNOWN &&
                owner.Dispatch(new(AdminOperation.Job,JobAction.list),"ledger").Store?.Storage?.FileReceipts==1,
                "fileledger.unknown_effect_blocks_unverified_second_commit");
        }
        using(var owner=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
        {
            owner.Initialize(db);
            Check(owner.FileReceipt(Prepare(commit),actor).Code==FileReceiptCode.EFFECT_UNKNOWN,
                "fileledger.after_restart_unknown_effect_still_blocks_replay");
            Check(owner.FileReceipt(Prepare(status),actor).Code==FileReceiptCode.READY,
                "fileledger.status_probe_allowed_while_effect_unknown");
            var nativeStatus=NativeFileWriter.Execute(status);
            Check(nativeStatus.Status==FileWriteCode.STAGED &&
                owner.FileReceipt(Observe(status,nativeStatus),actor).State==FileReceiptState.STAGED,
                "fileledger.readback_proves_commit_did_not_execute");
            Check(owner.FileReceipt(Prepare(commit),actor).State==FileReceiptState.COMMITTING,
                "fileledger.safe_commit_after_confirmed_no_effect");
            var committed=NativeFileWriter.Execute(commit);
            Check(committed.Status==FileWriteCode.COMMITTED &&
                owner.FileReceipt(Observe(commit,committed),actor).State==FileReceiptState.COMMITTED &&
                File.ReadAllBytes(path).SequenceEqual(bytes),
                "fileledger.atomic_commit_and_sqlite_observation");
        }
        using(var owner=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
        {
            owner.Initialize(db);
            Check(owner.FileReceipt(Prepare(status),actor).State==FileReceiptState.COMMITTED &&
                owner.Dispatch(new(AdminOperation.Job,JobAction.list),"ledger") is
                    {Store:{Storage:{FileReceipts:1,MetadataTotal:1}}},
                "fileledger.reopen_committed_receipt_and_bounded_global_count");
        }
        byte[] database=File.ReadAllBytes(db);
        Check(!System.Text.Encoding.UTF8.GetString(database).Contains(path,StringComparison.Ordinal),
            "fileledger.sqlite_never_persists_cleartext_target_path");
    }
    finally{Directory.Delete(root,recursive:true);}
}
// Native file management: all effects are synthetic, with real SQLite owner,
// exact actor binding and the same replay gate used in the production SCM.
{
    string root=Path.Combine(Path.GetTempPath(),"wmcp-file-manage-"+Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    string db=Path.Combine(root,"jobs.db"),actor="S-1-5-21-999-111-222-1001";
    try
    {
        FileManageRequest Request(FileManageAction action,string path,string? dest=null,
            bool recursive=false,string? version=null,string? hash=null)=>
            new(action,path,Guid.NewGuid().ToString("N"),CommandContext.USER,dest,
                recursive,version,hash);
        async Task<FileManageReply> Execute(JobCoordinator owner,FileManageRequest request)=>
            await FileManageWorkflow.RunAsync(request,
                (receipt,_)=>Task.FromResult<FileReceiptReply?>(owner.FileReceipt(receipt,actor)),
                ct=>Task.FromResult(NativeFileManager.Execute(request,ct)),CancellationToken.None);

        var folder=Request(FileManageAction.MKDIR,Path.Combine(root,"nested","folder"));
        Check(NativeFileManager.Valid(folder) &&
            !NativeFileManager.Valid(folder with {Path="relative"}) &&
            !NativeFileManager.Valid(folder with {OperationId="invalid"}) &&
            !NativeFileManager.Valid(folder with {Action=(FileManageAction)99}) &&
            !NativeFileManager.Valid(folder with {Action=FileManageAction.MOVE}) &&
            !NativeFileManager.Valid(folder with {Recursive=true}),
            "filemanage.reject_invalid_actions_paths_ids_and_flags");
        using(var owner=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
        {
            owner.Initialize(db);
            var made=await Execute(owner,folder);
            Check(made is {Status:FileManageCode.COMPLETED,
                EffectState:WindowEffectState.EFFECT_VERIFIED,SafeToRetry:false} &&
                Directory.Exists(folder.Path) &&
                (await Execute(owner,folder)).Status==FileManageCode.COMPLETED &&
                (await Execute(owner,folder with {ReadOnly=true})).Status==FileManageCode.COMPLETED,
                "filemanage.mkdir_nested_verified_and_exactly_once");
            Check((await Execute(owner,folder with {Action=FileManageAction.DELETE,
                ReadOnly=true})).Status==FileManageCode.CONFLICT,
                "filemanage.same_guid_different_action_conflicts_even_on_status");

            string source=Path.Combine(folder.Path,"source.bin");
            byte[] bytes=System.Text.Encoding.UTF8.GetBytes("copy + move 🌍");
            File.WriteAllBytes(source,bytes);
            var stat=await NativeFileReader.ExecuteAsync(new(NativeFileAction.STAT,source,
                CommandContext.USER,IncludeSha256:true),CancellationToken.None);
            var copy=Request(FileManageAction.COPY,source,Path.Combine(folder.Path,"copy.bin"),
                version:stat.Info!.Version,hash:stat.Info.Sha256);
            Check((await Execute(owner,copy)).Status==FileManageCode.COMPLETED &&
                File.ReadAllBytes(copy.Destination!).SequenceEqual(bytes) &&
                File.ReadAllBytes(source).SequenceEqual(bytes) &&
                (await Execute(owner,copy)).Status==FileManageCode.COMPLETED &&
                (await Execute(owner,copy with {Destination=source})).Status==FileManageCode.INVALID_ARGUMENT,
                "filemanage.copy_atomic_cas_and_no_duplicate_effect");
            using(var opened=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read))
                Check(NativeFileReader.VersionOf(opened.SafeFileHandle)==stat.Info.Version,
                    "filemanage.copy_uses_same_native_identity_as_stat");
            string changedSource=Path.Combine(root,"changed-source.bin");
            File.WriteAllBytes(changedSource,bytes);
            var beforeChange=await NativeFileReader.ExecuteAsync(new(NativeFileAction.STAT,changedSource,
                CommandContext.USER,IncludeSha256:true),CancellationToken.None);
            File.AppendAllText(changedSource,"changed");
            var changedCopy=Request(FileManageAction.COPY,changedSource,
                Path.Combine(root,"changed-copy.bin"),version:beforeChange.Info!.Version,
                hash:beforeChange.Info.Sha256);
            Check((await Execute(owner,changedCopy)).Status==FileManageCode.SOURCE_CHANGED &&
                !File.Exists(changedCopy.Destination),
                "filemanage.copy_cas_rejects_changed_source_without_target");
            var stale=Request(FileManageAction.DELETE,source,version:"stale",hash:stat.Info.Sha256);
            Check((await Execute(owner,stale)).Status==FileManageCode.SOURCE_CHANGED &&
                File.Exists(source),"filemanage.stale_version_prevents_delete");
            var move=Request(FileManageAction.MOVE,copy.Destination!,Path.Combine(folder.Path,"moved.bin"));
            Check((await Execute(owner,move)).Status==FileManageCode.COMPLETED &&
                File.Exists(move.Destination) && !File.Exists(move.Path) &&
                (await Execute(owner,move)).Status==FileManageCode.COMPLETED,
                "filemanage.native_atomic_move_and_receipt_replay");
            var conflict=Request(FileManageAction.COPY,source,move.Destination);
            Check((await Execute(owner,conflict)).Status==FileManageCode.DESTINATION_EXISTS &&
                File.ReadAllBytes(move.Destination!).SequenceEqual(bytes),
                "filemanage.never_overwrites_existing_destination");
            var delete=Request(FileManageAction.DELETE,move.Destination!);
            Check((await Execute(owner,delete)).Status==FileManageCode.COMPLETED &&
                !File.Exists(delete.Path) &&
                (await Execute(owner,delete with {ReadOnly=true})).Status==FileManageCode.COMPLETED,
                "filemanage.delete_file_and_durable_readback");
            string fileTreeRoot=Path.Combine(root,"tree");
            Directory.CreateDirectory(Path.Combine(fileTreeRoot,"child"));
            File.WriteAllBytes(Path.Combine(fileTreeRoot,"child","payload.bin"),bytes);
            var treeCopy=Request(FileManageAction.COPY,fileTreeRoot,Path.Combine(root,"tree-copy"),true);
            Check((await Execute(owner,treeCopy)).Status==FileManageCode.COMPLETED &&
                File.ReadAllBytes(Path.Combine(treeCopy.Destination!,"child","payload.bin"))
                    .SequenceEqual(bytes),"filemanage.recursive_directory_copy_atomic_visibility");
            var notEmpty=Request(FileManageAction.DELETE,treeCopy.Destination!);
            Check((await Execute(owner,notEmpty)).Status==FileManageCode.NOT_EMPTY &&
                Directory.Exists(treeCopy.Destination!),"filemanage.directory_delete_requires_recursive");
            var treeDelete=Request(FileManageAction.DELETE,treeCopy.Destination!,recursive:true);
            Check((await Execute(owner,treeDelete)).Status==FileManageCode.COMPLETED &&
                !Directory.Exists(treeCopy.Destination!),"filemanage.explicit_recursive_directory_delete");

            var pending=Request(FileManageAction.MOVE,source,Path.Combine(root,"never-moved.bin"));
            Check(owner.FileReceipt(FileManageWorkflow.Receipt(pending,FileWriteAction.BEGIN,
                    FileReceiptPhase.PREPARE),actor).Code==FileReceiptCode.READY &&
                owner.FileReceipt(FileManageWorkflow.Receipt(pending,FileWriteAction.COMMIT,
                    FileReceiptPhase.PREPARE),actor).State==FileReceiptState.COMMITTING,
                "filemanage.durable_intent_before_native_effect");
            Check((await Execute(owner,pending)).Status==FileManageCode.EFFECT_UNKNOWN &&
                File.Exists(source) && !File.Exists(pending.Destination),
                "filemanage.no_blind_replay_after_lost_ack");
        }
        using(var owner=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
        {
            owner.Initialize(db);
            var receiptCount=owner.Dispatch(new(AdminOperation.Job,JobAction.list),"manage")
                .Store?.Storage?.FileReceipts;
            Check(receiptCount is >=8,"filemanage.sqlite_reopen_bounded_receipt_count");
        }
        Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(db))
                .Contains(Path.Combine(root,"tree"),StringComparison.Ordinal),
            "filemanage.sqlite_no_cleartext_paths_after_close");
    }
    finally{Directory.Delete(root,true);}
}
// A mid-read cancellation must stop before another chunk is written. This
// deterministic fault is safer and faster than a timing-dependent huge file.
{
    byte[] data=System.Text.Encoding.UTF8.GetBytes("native binary \0 copy 🌍");
    using(var input=new MemoryStream(data,false))
    using(var output=new MemoryStream())
    {
        string? actual=NativeFileManager.CopyFileChecked(input,output,CancellationToken.None,hash:true);
        Check(actual==Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)) &&
            output.ToArray().SequenceEqual(data),"filemanage.copy_chunk_hash_matches_actual_bytes");
    }
    using(var cancellation=new CancellationTokenSource())
    using(var input=new CancelOnFirstReadStream(data,cancellation))
    using(var output=new MemoryStream())
    {
        bool cancelled=false;
        try{NativeFileManager.CopyFileChecked(input,output,cancellation.Token,hash:false);}
        catch(OperationCanceledException){cancelled=true;}
        Check(cancelled && output.Length==0,
            "filemanage.copy_cancellation_before_first_staged_chunk");
    }
}
Check(File.Exists(PowerShellRunner.Executable), "r2.machine_wide_pwsh");
using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
{
    var si = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "whoami.exe"));
    si.ArgumentList.Add("/logonid"); si.UseShellExecute = false; si.RedirectStandardOutput = true;
    using var whoami = Process.Start(si)!;
    string output = await whoami.StandardOutput.ReadToEndAsync();
    await whoami.WaitForExitAsync();
    string expected = System.Text.RegularExpressions.Regex.Match(output, @"S-1-5-5-\d+-\d+").Value;
    Check(whoami.ExitCode == 0 && expected.Length > 0 && TokenLogon.Read(identity) == expected,
        "user.real_native_logon_sid_matches_whoami");
    Check(identity.Groups?.OfType<System.Security.Principal.SecurityIdentifier>().All(x => x.Value != expected) == true,
        "user.managed_groups_exclude_logon_sid_regression");
}
Check(AdminTools.ExecWaitBudget(120)==TimeSpan.FromSeconds(90) && AdminTools.ExecWaitBudget(85)==TimeSpan.FromSeconds(90) &&
    AdminTools.ExecWaitBudget(10)==TimeSpan.FromSeconds(15), "transport.exec_wait_budget");
Check(UpdateTools.ValidArguments(UpdateToolAction.REQUEST, false, null) &&
    UpdateTools.ValidArguments(UpdateToolAction.STATUS, false, null) &&
    UpdateTools.ValidArguments(UpdateToolAction.ACCEPT, true, new string('a', 40)) &&
    !UpdateTools.ValidArguments(UpdateToolAction.ACCEPT, false, new string('a', 40)) &&
    !UpdateTools.ValidArguments(UpdateToolAction.ACCEPT, true, null) &&
    !UpdateTools.ValidArguments(UpdateToolAction.ACCEPT, true, new string('z', 40)) &&
    !UpdateTools.ValidArguments(UpdateToolAction.REQUEST, true, null) &&
    !UpdateTools.ValidArguments(UpdateToolAction.STATUS, false, new string('a', 40)),
    "update.explicit_accept_confirmation_and_exact_sha");
Check(UpdateTools.ValidArguments(UpdateToolAction.FORGET_LEGACY,false,new string('a',40)) &&
    !UpdateTools.ValidArguments(UpdateToolAction.FORGET_LEGACY,false,null) &&
    !UpdateTools.ValidArguments(UpdateToolAction.FORGET_LEGACY,true,new string('a',40)) &&
    !UpdateTools.ValidArguments(UpdateToolAction.FORGET_LEGACY,false,new string('z',40)),
    "update.legacy_forget_requires_exact_commit_without_accept");
var doctorSnapshot = new DoctorSnapshot(DoctorState.DEGRADED,
    [new DoctorCheck(DoctorComponent.Store, DoctorState.DEGRADED,
        DoctorReason.RECONCILIATION_REQUIRED, DoctorAction.INSPECT_JOBS)],
    true, true, 17, "candidate", 42, DoctorCatalog.Observe(), true);
var doctorStatus = new AdminStatus(StatusCode.OK, "ci", Diagnostics: doctorSnapshot);
var doctorWire = System.Text.Json.JsonSerializer.Serialize(doctorStatus, PipeWire.Json);
Check(System.Text.Json.JsonSerializer.Deserialize<AdminStatus>(doctorWire, PipeWire.Json)?.Diagnostics is
    { Status: DoctorState.DEGRADED, ReconciliationCount: 17, LocalOperationalReady: true,
      RollbackPending: true, ToolCatalog: { ToolCount: 23, StructuredToolCount: 22,
          NativeDtoResultCount: 21 },
      Checks: [{ Reason: DoctorReason.RECONCILIATION_REQUIRED }] } &&
    !System.Text.Json.JsonSerializer.Serialize(new AdminStatus(StatusCode.OK, "ci"), PipeWire.Json)
        .Contains("diagnostics", StringComparison.Ordinal),
    "status.optional_typed_doctor_without_changing_default_status");
var doctorCatalog = DoctorCatalog.Observe();
Check(DoctorCatalog.IsComplete(doctorCatalog) && doctorCatalog.ToolCount == 23 &&
    doctorCatalog.ToolNames.Contains("windows.tcp_connections") &&
    doctorCatalog.ToolNames.Contains("windows.file_acl") &&
    doctorCatalog.ToolNames.Contains("windows.process_wait_chain") &&
    doctorCatalog.ToolNames.Contains("windows.network_probe") &&
    doctorCatalog.ToolNames.Contains("windows.admin_action") &&
    doctorCatalog.ToolNames.Contains("windows.chrome_connection") &&
    doctorCatalog.ToolNames.Contains("windows.chrome_tab") &&
    doctorCatalog.Source == DoctorCatalogSource.SERVER_ASSEMBLY_METADATA,
    "doctor.v2_declared_typed_catalog_without_claiming_published_client_schema");
string doctorCatalogJson=System.Text.Json.JsonSerializer.Serialize(doctorCatalog,PipeWire.Json);
Check(doctorCatalogJson.Contains("\"tool_count\":23", StringComparison.Ordinal) &&
    doctorCatalogJson.Contains("\"native_dto_result_count\":21", StringComparison.Ordinal) &&
    doctorWire.Contains("\"rollback_pending\":true", StringComparison.Ordinal),
    "doctor.v2_strict_catalog_and_update_trial_wire");
string missingRollbackRoot=Path.Combine(Path.GetTempPath(), "wmcp-doctor-test-" + Guid.NewGuid().ToString("N"));
Check(Doctor.ReadRollbackPending(missingRollbackRoot) == false,
    "doctor.v2_absent_rollback_journal_is_not_a_pending_trial");
Check(Doctor.OverallStatus([
        new(DoctorComponent.Service,DoctorState.OK,DoctorReason.OBSERVED,DoctorAction.NONE),
        new(DoctorComponent.Chrome,DoctorState.NOT_TESTED,DoctorReason.CHROME_NOT_PROBED,DoctorAction.CHECK_CHROME_CONNECTION),
        new(DoctorComponent.PublishedClientSchema,DoctorState.NOT_TESTED,DoctorReason.CLIENT_SCHEMA_NOT_VERIFIED,
            DoctorAction.VERIFY_PUBLISHED_CLIENT_SCHEMA),
        new(DoctorComponent.UpdateAcceptance,DoctorState.NOT_TESTED,DoctorReason.UPDATE_TRIAL_PENDING,
            DoctorAction.VERIFY_UPDATE_ACCEPTANCE)]) == DoctorState.OK &&
    Doctor.OverallStatus([
        new(DoctorComponent.Store,DoctorState.DEGRADED,DoctorReason.RECONCILIATION_REQUIRED,DoctorAction.INSPECT_JOBS),
        new(DoctorComponent.Chrome,DoctorState.NOT_TESTED,DoctorReason.CHROME_NOT_PROBED,DoctorAction.CHECK_CHROME_CONNECTION)])
        == DoctorState.DEGRADED &&
    Doctor.OverallStatus([
        new(DoctorComponent.Remote,DoctorState.NOT_TESTED,DoctorReason.REMOTE_NOT_TESTED,
            DoctorAction.VERIFY_SUPPORTED_REMOTE_ACCESS)]) == DoctorState.NOT_TESTED,
    "doctor.v2_optional_checks_do_not_mask_baseline_health");
var historicTrial=UpdateTools.WithCurrentReadiness(new(UpdateToolCode.COMPLETED,
    Stage:"trial_not_ready",RollbackPending:true),doctorStatus);
Check(historicTrial is {Stage:"trial_not_ready",RollbackPending:true,
    CurrentServiceStatus:StatusCode.OK,CurrentLocalOperationalReady:true,
    CurrentObservedUtc:not null} &&
    System.Text.Json.JsonSerializer.Serialize(historicTrial,PipeWire.Json)
        .Contains("current_local_operational_ready",StringComparison.Ordinal) &&
    UpdateTools.WithCurrentReadiness(historicTrial,new(StatusCode.SERVICE_UNAVAILABLE,"ci")) is
        {CurrentServiceStatus:StatusCode.SERVICE_UNAVAILABLE,CurrentLocalOperationalReady:null},
    "update.historic_trial_stage_separate_from_current_readback");
var testWindowIdentity = new WindowIdentity(0x12345, 4242, 133700000000000000, 1);
string testWindowId = WindowCatalog.FormatId(testWindowIdentity);
Check(WindowCatalog.TryParseId(testWindowId, out var decodedWindowId) && decodedWindowId == testWindowIdentity &&
    !WindowCatalog.TryParseId("w1:bad:token", out _) &&
    !WindowCatalog.TryParseId("w1:0:1:1:1", out _), "windows.stable_ref_contract");
var invalidWindowLimit = await WindowTools.ListWindows(max_windows: 0);
Check(invalidWindowLimit.Status == WindowListCode.INVALID_ARGUMENT && invalidWindowLimit.Windows.Length == 0,
    "windows.bounded_argument_validation");
Check(!WindowCatalog.TryParseId(new string('x', 256), out _), "windows.reject_unbounded_ref");
Check(WindowCatalog.ResolveId(testWindowId, 2, out _) == WindowResolveCode.WRONG_SESSION,
    "windows.reject_cross_session_ref");
var staleWindowId = WindowCatalog.FormatId(new WindowIdentity(long.MaxValue, 4242, 133700000000000000, 1));
Check(WindowCatalog.ResolveId(staleWindowId, 1, out _) == WindowResolveCode.STALE_WINDOW,
    "windows.reject_stale_hwnd");
Check(WindowManager.Valid(WindowManageAction.ACTIVATE,null,null,null,null) &&
    WindowManager.Valid(WindowManageAction.RESTORE,null,null,null,null) &&
    WindowManager.Valid(WindowManageAction.MINIMIZE,null,null,null,null) &&
    WindowManager.Valid(WindowManageAction.MAXIMIZE,null,null,null,null) &&
    WindowManager.Valid(WindowManageAction.MOVE,0,-25,null,null) &&
    WindowManager.Valid(WindowManageAction.RESIZE,null,null,1024,768) &&
    WindowManager.Valid(WindowManageAction.CLOSE,null,null,null,null) &&
    !WindowManager.Valid(WindowManageAction.CLOSE,1,null,null,null) &&
    !WindowManager.Valid(WindowManageAction.MOVE,1,null,null,null) &&
    !WindowManager.Valid(WindowManageAction.RESIZE,null,null,0,500) &&
    !WindowManager.Valid(WindowManageAction.ACTIVATE,1,1,null,null) &&
    !WindowManager.Valid((WindowManageAction)999,null,null,null,null) &&
    WindowManager.Execute(testWindowId,WindowManageAction.RESTORE,null,null,null,null,2).Status==
        WindowManageCode.USER_CONTEXT_UNAVAILABLE,
    "windows.manage_typed_bounds_and_cross_session_rejection");
Check(WindowPhysicalClick.PointerPlan(WindowActionKind.CLICK)==(0x0002u,0x0004u,1) &&
    WindowPhysicalClick.PointerPlan(WindowActionKind.DOUBLE_CLICK)==(0x0002u,0x0004u,2) &&
    WindowPhysicalClick.PointerPlan(WindowActionKind.RIGHT_CLICK)==(0x0008u,0x0010u,1) &&
    WindowChromeCdp.PointerAction(WindowActionKind.DOUBLE_CLICK) &&
    WindowChromeCdp.PointerAction(WindowActionKind.RIGHT_CLICK),
    "windows.native_and_background_mouse_sequences_typed");
Check(WindowChromeCdp.InspectTab(IntPtr.Zero,testWindowId,WindowInspectScope.TABS,null,10,0,
    null,null).ElapsedMs is >=0,
    "windows.chrome_inspection_emits_elapsed_ms_on_failures");
Check(new WindowManageResult(WindowManageCode.INVALID_ARGUMENT).SafeToRetry &&
    !new WindowManageResult(WindowManageCode.EFFECT_UNKNOWN,
        EffectState:WindowEffectState.EFFECT_UNKNOWN).SafeToRetry &&
    new WindowActionResult(WindowActionCode.CONFIRMATION_REQUIRED).SafeToRetry &&
    !new WindowActionResult(WindowActionCode.COMPLETED,EffectUnknown:true).SafeToRetry &&
    !new WindowActionResult(WindowActionCode.TIMEOUT_UNKNOWN_EFFECT,EffectUnknown:true).SafeToRetry,
    "windows.typed_safe_retry_never_replays_ambiguous_dispatch");
Check(WindowInspector.ValidArguments(testWindowId, 0, 1) &&
    WindowInspector.ValidArguments(testWindowId, 16, 100) &&
    WindowInspector.ValidArguments(testWindowId, 16, 10, includeDetails: true) &&
    WindowInspector.ValidArguments(testWindowId, 16, 10, nameContains: "status",
        roleFilter: WindowRoleFilter.BUTTON, waitMs: 1500) &&
    !WindowInspector.ValidArguments(testWindowId, 16, 10, waitMs: 50) &&
    !WindowInspector.ValidArguments(testWindowId, 16, 10, nameContains: "x", waitMs: 1501) &&
    !WindowInspector.ValidArguments(testWindowId, 16, 10, nameContains: " ") &&
    !WindowInspector.ValidArguments(testWindowId, 16, 10, nameContains: new string('x', 129)) &&
    !WindowInspector.ValidArguments(testWindowId, 16, 10,
        roleFilter: (WindowRoleFilter)int.MaxValue) &&
    !WindowInspector.ValidArguments(testWindowId, 16, 100, includeDetails: true) &&
    WindowInspector.ValidArguments(testWindowId, 16, 100, offset: 100) &&
    !WindowInspector.ValidArguments(testWindowId, 16, 1, "0.1", null) &&
    !WindowInspector.ValidArguments(testWindowId, 16, 100, offset: 1950) &&
    !WindowInspector.ValidArguments(testWindowId, 17, 50) &&
    !WindowInspector.ValidArguments(testWindowId, 3, 101) &&
    !WindowInspector.ValidArguments("invalid", 3, 60), "windows.inspect_bounded_arguments");
Check(!WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.TABS) &&
    WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.TABS, expectedProfile: "Valer") &&
    WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.TAB, tabId: "0123456789abcdef0123456789abcdef",
        expectedProfile: "Valer") &&
    WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.TAB, expectedProfile: "Valer",
        expectedOrigin: "https://example.com") &&
    !WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.TAB, expectedProfile: "Valer") &&
    !WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.TAB, tabId: "invalid") &&
    !WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.TABS, tabId: "0123456789abcdef0123456789abcdef") &&
    !WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.WINDOW, tabId: "0123456789abcdef0123456789abcdef") &&
    WindowInspector.ArgumentError(testWindowId, 16, 11, includeDetails: true)?.Contains(
        "max_elements", StringComparison.Ordinal) == true,
    "windows.typed_chrome_tab_scope_and_precise_invalid_argument");
Check(WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.TABS, expectedProfile: "Valer",
        expectedOrigin: "https://example.com") &&
    WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.TAB, tabId: "0123456789abcdef0123456789abcdef",
        expectedProfile: "Valer", expectedUrl: "https://example.com/form?a=1") &&
    WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.WINDOW, expectedProfile: "Valer") &&
    !WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.WINDOW, expectedOrigin: "https://example.com") &&
    !WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.TABS, expectedOrigin: "https://example.com/other") &&
    !WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.TABS, expectedOrigin: "https://example.com",
        expectedUrl: "https://other.example/form") &&
    WindowInspector.ArgumentError(testWindowId, 16, 50, scope: WindowInspectScope.TABS,
        expectedOrigin: "not an origin")?.StartsWith("expected_origin:") == true &&
    WindowChromeCdp.MatchesContext("https://example.com/form?a=1#tab", "https://example.com",
        "https://example.com/form?a=1#tab") &&
    !WindowChromeCdp.MatchesContext("https://example.com/form?a=2", "https://example.com",
        "https://example.com/form?a=1") &&
    !WindowChromeCdp.MatchesContext("https://other.example/form", "https://example.com", null),
    "chrome.explicit_native_profile_origin_and_full_url_scope");
Check(WindowChromeCdp.IsRequestedTabCandidate("tab-a", "https://example.com/form",
        null, "https://example.com", null) &&
    !WindowChromeCdp.IsRequestedTabCandidate("tab-b", "https://other.example/form",
        null, "https://example.com", null) &&
    WindowChromeCdp.IsRequestedTabCandidate("tab-a", "https://example.com/form",
        null, "https://example.com", "https://example.com/form") &&
    !WindowChromeCdp.IsRequestedTabCandidate("tab-a", "https://example.com/form?other=1",
        null, null, "https://example.com/form") &&
    WindowChromeCdp.IsRequestedTabCandidate("tab-a", "https://other.example/form",
        "tab-a", "https://example.com", null) && // Keep URL mismatch for typed post-selection rejection.
    !WindowChromeCdp.IsRequestedTabCandidate("tab-b", "https://example.com/form",
        "tab-a", null, null) &&
    !WindowChromeCdp.IsRequestedTabCandidate("tab-a", "file:///C:/other.html",
        "tab-a", null, null),
    "chrome.exact_target_mapping_skips_other_http_tabs_without_leaking_neighbor_urls");
var axTimeout = new OperationCanceledException();
axTimeout.Data["cdp_method"] = "Accessibility.getFullAXTree";
var unsafeMethodTimeout = new OperationCanceledException();
unsafeMethodTimeout.Data["cdp_method"] = "https://private.example/?token=secret";
Check(WindowChromeCdp.InspectTimeoutMs(true,false) == 18000 &&
    WindowChromeCdp.InspectTimeoutMs(true,true) == 12000 &&
    WindowChromeCdp.InspectTimeoutMs(false,true) == 24000 &&
    WindowChromeCdp.CdpTimeoutDetail(axTimeout) ==
        "CDP_TIMEOUT_Accessibility.getFullAXTree" &&
    WindowChromeCdp.CdpTimeoutDetail(unsafeMethodTimeout) == "CDP_TIMEOUT",
    "chrome.cold_target_readiness_and_redacted_cdp_method_timeout");
Check(WindowInspector.InspectCore(IntPtr.Zero, 1, 20).Status == WindowInspectCode.INVALID_ARGUMENT,
    "windows.inspect_reject_null_hwnd");
var invalidInspect = await WindowTools.InspectWindow("invalid");
Check(invalidInspect.Status == WindowInspectCode.INVALID_ARGUMENT, "windows.inspect_reject_bad_ref_before_ipc");
string freshElementId = "e1:" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x") +
    ":0123456789abcdef0123456789abcdef";
Check(WindowInspector.TryParseElementRef(freshElementId, out _) &&
    !WindowInspector.TryParseElementRef("bad", out _) &&
    !WindowInspector.TryParseElementRef("e1:1:0123456789abcdef0123456789abcdef", out _),
    "windows.action_fresh_reference_and_stale_rejection");
Check(WindowInspector.TryParsePath("0.1.0.25", out var parsedPath) &&
    parsedPath.SequenceEqual(new[] { 1, 0, 25 }) &&
    !WindowInspector.TryParsePath("0..1", out _) &&
    !WindowInspector.TryParsePath("0.01", out _) &&
    !WindowInspector.TryParsePath("1.0", out _) &&
    !WindowInspector.TryParsePath("0.2001", out _), "windows.inspect_strict_path_contract");
Check(WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.INVOKE, null)) &&
    WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.CLICK, null)) &&
    WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.TYPE_TEXT,
        "https://example.com/")) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.TYPE_TEXT,
        null)) &&
    WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.KEY_PRESS,
        null, Key: WindowKey.ENTER)) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.KEY_PRESS,
        null)) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.KEY_PRESS,
        null, Key: (WindowKey)99)) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.CLICK,
        null, Key: WindowKey.SPACE)) &&
    WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.CLICK, null,
        ExpectedName: "CI_UIA_CLICKED", ExpectedChange: WindowExpectedChange.APPEARS)) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.CLICK, null,
        ExpectedName: "CI_UIA_CLICKED")) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.CLICK, "unexpected")) &&
    WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.INVOKE, null, "0.1")) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.INVOKE, null, "0..1")) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.INVOKE, "unexpected")) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.SET_VALUE, new string('x', 4097))),
    "windows.action_bounded_arguments");
var backgroundPolicyClick = new WindowActionRequest(testWindowId, freshElementId,
    WindowActionKind.CLICK, null);
var foregroundOptIn = backgroundPolicyClick with { InputMode = WindowInputMode.ALLOW_FOREGROUND_INPUT };
string foregroundWire = System.Text.Json.JsonSerializer.Serialize(foregroundOptIn, PipeWire.Json);
var backgroundBlocked = new WindowActionResult(WindowActionCode.FOREGROUND_INPUT_DISALLOWED,
    Detail: "BACKGROUND_ONLY");
Check(backgroundPolicyClick.InputMode == WindowInputMode.BACKGROUND_ONLY &&
    !WindowPhysicalClick.ForegroundInputAllowed(backgroundPolicyClick) &&
    WindowPhysicalClick.ForegroundInputAllowed(foregroundOptIn) &&
    WindowAction.Valid(backgroundPolicyClick) && WindowAction.Valid(foregroundOptIn) &&
    !WindowAction.Valid(backgroundPolicyClick with { InputMode = (WindowInputMode)999 }) &&
    foregroundWire.Contains("\"input_mode\":\"ALLOW_FOREGROUND_INPUT\"", StringComparison.Ordinal) &&
    System.Text.Json.JsonSerializer.Deserialize<WindowActionRequest>(foregroundWire, PipeWire.Json)?.InputMode ==
        WindowInputMode.ALLOW_FOREGROUND_INPUT &&
    backgroundBlocked.EffectState == WindowEffectState.NO_EFFECT &&
    backgroundBlocked.ProviderOutcome == WindowProviderOutcome.NOT_DISPATCHED &&
    !backgroundBlocked.EffectUnknown,
    "windows.background_default_and_explicit_foreground_input_opt_in");
var cdpOnly = backgroundPolicyClick with { InputMode = WindowInputMode.CDP_ONLY };
var cdpOnlyWire = System.Text.Json.JsonSerializer.Serialize(cdpOnly, PipeWire.Json);
var cdpOnlyBlocked = WindowAction.CdpOnlyResult(null, Stopwatch.StartNew());
var cdpAccepted = new WindowActionResult(WindowActionCode.COMPLETED,
    ExecutionMethod: WindowExecutionMethod.CHROME_CDP);
Check(WindowAction.Valid(cdpOnly) && !WindowPhysicalClick.ForegroundInputAllowed(cdpOnly) &&
    cdpOnlyWire.Contains("\"input_mode\":\"CDP_ONLY\"", StringComparison.Ordinal) &&
    System.Text.Json.JsonSerializer.Deserialize<WindowActionRequest>(cdpOnlyWire, PipeWire.Json)?.InputMode ==
        WindowInputMode.CDP_ONLY &&
    cdpOnlyBlocked.Status == WindowActionCode.CDP_ONLY_UNAVAILABLE &&
    cdpOnlyBlocked.EffectState == WindowEffectState.NO_EFFECT && cdpOnlyBlocked.SafeToRetry &&
    cdpOnlyBlocked.NextStep == WindowNextStep.CHANGE_INPUT_MODE &&
    cdpOnlyBlocked.ProviderOutcome == WindowProviderOutcome.NOT_DISPATCHED &&
    ReferenceEquals(WindowAction.CdpOnlyResult(cdpAccepted, Stopwatch.StartNew()), cdpAccepted),
    "windows.cdp_only_never_silently_dispatches_uia_or_native_input");
Check(WindowAction.Valid(new WindowActionRequest(testWindowId,freshElementId,
        WindowActionKind.KEY_PRESS,null,VirtualKey:0x41,Ctrl:true,Shift:true)) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId,freshElementId,
        WindowActionKind.KEY_PRESS,null,VirtualKey:0)) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId,freshElementId,
        WindowActionKind.KEY_PRESS,null,Key:WindowKey.ENTER,VirtualKey:0x41)) &&
    WindowAction.Valid(new WindowActionRequest(testWindowId,freshElementId,
        WindowActionKind.SCROLL,null,ScrollDirection:WindowScrollDirection.DOWN,ScrollUnits:3)) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId,freshElementId,
        WindowActionKind.SCROLL,null,ScrollDirection:WindowScrollDirection.DOWN,ScrollUnits:11)) &&
    WindowAction.Valid(new WindowActionRequest(testWindowId,freshElementId,
        WindowActionKind.HOVER,null)) &&
    WindowAction.Valid(new WindowActionRequest(testWindowId,freshElementId,
        WindowActionKind.DRAG,null,TargetX:25,TargetY:50)) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId,freshElementId,
        WindowActionKind.DRAG,null,TargetX:25)) &&
    WindowAction.Valid(new WindowActionRequest(testWindowId,"locator",
        WindowActionKind.INVOKE,null,LocatorAutomationId:"submit")) &&
    WindowAction.Valid(new WindowActionRequest(testWindowId,"locator",
        WindowActionKind.CLICK,null,LocatorRole:WindowRoleFilter.BUTTON,LocatorName:"OK")) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId,"locator",
        WindowActionKind.INVOKE,null)) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId,"locator",
        WindowActionKind.INVOKE,null,LocatorName:new string('x',129))) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId,freshElementId,
        WindowActionKind.INVOKE,null,LocatorName:"unexpected")),
    "windows.pointer_keyboard_and_unique_locator_contracts");
Check(WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId,
        WindowActionKind.SET_VALUE, "next", ExpectedName: "CI_UIA_INPUT_NAME",
        ExpectedChange: WindowExpectedChange.APPEARS, ExpectedKind: WindowExpectedKind.UIA_VALUE,
        ExpectedValue: "next")) &&
    WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId,
        WindowActionKind.EXPAND, null)) &&
    WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId,
        WindowActionKind.COLLAPSE, null)) &&
    WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId,
        WindowActionKind.TYPE_TEXT, "next", ExpectedName: "CI_UIA_INPUT_NAME",
        ExpectedChange: WindowExpectedChange.APPEARS, ExpectedKind: WindowExpectedKind.UIA_VALUE,
        ExpectedValue: "next", ExpectedEnabledName: "CI_UIA_SAVE")) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId,
        WindowActionKind.TYPE_TEXT, "next", ExpectedEnabledName: " ")) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId,
        WindowActionKind.TYPE_TEXT, "next", ExpectedEnabled: false)) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId,
        WindowActionKind.INVOKE, null, ExpectedKind: WindowExpectedKind.UIA_VALUE,
        ExpectedValue: "next")) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId,
        WindowActionKind.INVOKE, null, ExpectedName: "field",
        ExpectedChange: WindowExpectedChange.APPEARS, ExpectedKind: WindowExpectedKind.UIA_ELEMENT,
        ExpectedValue: "next")) &&
    WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId,
        WindowActionKind.INVOKE, null, ExpectedProfile: "Valer")) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId,
        WindowActionKind.INVOKE, null, ExpectedUrl: "https://example.com")) &&
    WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId,
        WindowActionKind.INVOKE, null, ExpectedKind: WindowExpectedKind.PROCESS_STARTED,
        ExpectedName: "camoufox", ExpectedChange: WindowExpectedChange.APPEARS,
        ObservationTimeoutMs: 12000)) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId,
        WindowActionKind.INVOKE, null, ExpectedKind: WindowExpectedKind.PROCESS_STARTED,
        ExpectedName: "camoufox", ExpectedChange: WindowExpectedChange.DISAPPEARS)) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId,
        WindowActionKind.INVOKE, null, ObservationTimeoutMs: 16000)),
    "windows.value_postcondition_and_browser_scope_are_typed");
Check(WindowChromeCdp.ProfileLabelMatches("Valer", "Valer") &&
    WindowChromeCdp.ProfileLabelMatches("Valer (signed in)", "Valer") &&
    WindowChromeCdp.ProfileLabelMatches("Profile: Valer, signed in", "Valer") &&
    !WindowChromeCdp.ProfileLabelMatches("Valery (signed in)", "Valer") &&
    !WindowChromeCdp.ProfileLabelMatches("Another Valer", "Valer") &&
    !WindowChromeCdp.ProfileLabelMatches("Valer test profile", "Valer"),
    "chrome.profile_delimiters_are_unambiguous");
using (var self = Process.GetCurrentProcess())
{
    var absent = ChromeProfileCatalog.ReadWindowProfile("invalid_window", self.SessionId,
        out var absence);
    bool verified = WindowChromeCdp.VerifiedChromeProfile(IntPtr.Zero, "CI_PROFILE",
        out var invalidWindow);
    Check(absent is null && absence == ChromeProfileCatalog.ChromeProfileReadCode.STALE_WINDOW &&
        !verified && invalidWindow == ChromeProfileCatalog.ChromeProfileReadCode.STALE_WINDOW,
        "chrome.native_profile_probe_reports_typed_pre_dispatch_failure");
}
var plainAvatar = ChromeProfileCatalog.ParseAvatar("Valer");
var emailAvatar = ChromeProfileCatalog.ParseAvatar("Васил (vasilinn9@gmail.com)");
Check(plainAvatar is { Name: "Valer", Account: null } &&
    emailAvatar is { Name: "Васил", Account: "vasilinn9@gmail.com" } &&
    ChromeProfileCatalog.ParseAvatar("Valery")?.Name != "Valer" &&
    ChromeProfileCatalog.ParseAvatar("Invalid (not an email)")?.Account is null &&
    ChromeProfileCatalog.ParseAvatar(new string('x', 201)) is null,
    "chrome.native_avatar_profile_and_account_are_observed_not_inferred");
using (var profileFixture = System.Text.Json.JsonDocument.Parse("""
    {"profile":{"info_cache":{
      "Profile 5":{"name":"Valer","user_name":"pvisakp@gmail.com"},
      "Profile 7":{"name":"example@domain.test","user_name":""},
      "Profile 8":{"name":"No email","user_name":""},
      "Profile 9":{"name":"Valer","user_name":""}
    }}}
    """))
{
    var profiles = ChromeProfileCatalog.ParseInfoCache(profileFixture.RootElement);
    var unique = profiles.Take(3).ToArray();
    var signedIn = ChromeProfileCatalog.ResolveMetadata("Valer", null, unique);
    var ambiguous = ChromeProfileCatalog.ResolveMetadata("Valer", null, profiles);
    var address = ChromeProfileCatalog.ResolveMetadata("irrelevant", "pvisakp@gmail.com", profiles);
    var missingAddress = ChromeProfileCatalog.ResolveMetadata("Valer", "other@domain.test", profiles);
    Check(profiles.Length == 4 && signedIn?.Id == "Profile 5" &&
        signedIn.Email == "pvisakp@gmail.com" &&
        signedIn.Source == ChromeProfileEmailSource.SIGNED_IN_ACCOUNT &&
        profiles[1].Source == ChromeProfileEmailSource.PROFILE_NAME &&
        profiles[2].Email is null && profiles[2].Source == ChromeProfileEmailSource.NOT_AVAILABLE &&
        ambiguous is null && address?.Id == "Profile 5" && missingAddress is null &&
        ChromeProfileCatalog.MatchesSelector(new(null, signedIn.Id, signedIn.Name,
            signedIn.Email, signedIn.Source, null, null), "pvisakp@gmail.com") &&
        ChromeProfileCatalog.MatchesSelector(new(null, signedIn.Id, signedIn.Name,
            signedIn.Email, signedIn.Source, null, null), "Profile 5"),
        "chrome.profile_selection_uses_exact_native_identity_and_email_provenance");

    var openProfiles = new ChromeProfileInfo[]
    {
        new(testWindowId, "Profile 5", "Valer", "pvisakp@gmail.com",
            ChromeProfileEmailSource.SIGNED_IN_ACCOUNT, 4242, 1),
        new(WindowCatalog.FormatId(testWindowIdentity with { Handle = 0x22222 }),
            "Profile 9", "Valer", "other@domain.test",
            ChromeProfileEmailSource.SIGNED_IN_ACCOUNT, 4242, 1),
        new(null, "Profile 8", "Closed", "closed@domain.test",
            ChromeProfileEmailSource.SIGNED_IN_ACCOUNT, null, null)
    };
    Check(ChromeProfileCatalog.OpenMatches(openProfiles, "pvisakp@gmail.com").Length == 1 &&
        ChromeProfileCatalog.OpenMatches(openProfiles, "Valer").Length == 2 &&
        ChromeProfileCatalog.OpenMatches(openProfiles, "closed@domain.test").Length == 0,
        "chrome.email_selector_only_uses_unique_open_windows");
}
Check(ChromeConnectionTools.ValidArguments(ChromeConnectionAction.LIST, null, null) &&
    !ChromeConnectionTools.ValidArguments(ChromeConnectionAction.LIST, null, null, true) &&
    !ChromeConnectionTools.ValidArguments(ChromeConnectionAction.LIST, testWindowId, "Valer") &&
    ChromeConnectionTools.ValidArguments(ChromeConnectionAction.REQUEST, testWindowId, "Valer") &&
    ChromeConnectionTools.ValidArguments(ChromeConnectionAction.REQUEST, testWindowId, "Valer", true) &&
    ChromeConnectionTools.ValidArguments(ChromeConnectionAction.REQUEST, null, "pvisakp@gmail.com") &&
    ChromeConnectionTools.ValidArguments(ChromeConnectionAction.STATUS, null, "pvisakp@gmail.com") &&
    !ChromeConnectionTools.ValidArguments(ChromeConnectionAction.STATUS, testWindowId, "Valer", true) &&
    ChromeConnectionTools.ValidArguments(ChromeConnectionAction.STATUS, testWindowId, "Valer") &&
    ChromeConnectionTools.ValidArguments(ChromeConnectionAction.DISCONNECT, testWindowId, "Valer") &&
    !ChromeConnectionTools.ValidArguments(ChromeConnectionAction.REQUEST, testWindowId, null) &&
    !ChromeConnectionTools.ValidArguments(ChromeConnectionAction.STATUS, "invalid", "Valer") &&
    !ChromeConnectionTools.ValidArguments((ChromeConnectionAction)99, testWindowId, "Valer"),
    "chrome.connection_action_schema_rejects_ambiguous_profile_selection");
var connectRequest = new ChromeConnectionRequest(testWindowId, "Valer", ChromeConnectionAction.REQUEST);
var managedConnection = connectRequest with { Mode = ChromeConnectionMode.MANAGED_BROWSER,
    ManagedProfileId = "work-1" };
Check(ChromeManagedProfile.ValidId("work-1") && !ChromeManagedProfile.ValidId("../bad") &&
    !ChromeManagedProfile.ValidId("bad\\name") &&
    ChromeManagedProfile.DataDirectory("work-1") == Path.Combine(
        ChromeManagedProfile.Root, "work-1") &&
    ChromeConnectionTools.ValidArguments(ChromeConnectionAction.REQUEST, testWindowId,
        "Valer", false, ChromeConnectionMode.MANAGED_BROWSER, "work-1") &&
    !ChromeConnectionTools.ValidArguments(ChromeConnectionAction.REQUEST, testWindowId,
        "Valer", true, ChromeConnectionMode.MANAGED_BROWSER, "work-1") &&
    !ChromeConnectionTools.ValidArguments(ChromeConnectionAction.REQUEST, testWindowId,
        "Valer", false, ChromeConnectionMode.MANAGED_BROWSER, "../bad") &&
    new ChromeBrokerRequest(Connection: managedConnection).Valid &&
    System.Text.Json.JsonSerializer.Serialize(managedConnection, PipeWire.Json)
        .Contains("\"mode\":\"MANAGED_BROWSER\"", StringComparison.Ordinal),
    "chrome.managed_mode_is_typed_and_confined_to_owned_profile_root");
var sampleTabId = "0123456789abcdef0123456789abcdef";
var chromeTabRequest = new ChromeTabRequest(testWindowId, "Valer", ChromeTabAction.NAVIGATE,
    sampleTabId, "https://example.com/");
var chromeTabWire = System.Text.Json.JsonSerializer.Serialize(chromeTabRequest, PipeWire.Json);
Check(WindowChromeCdp.ValidTabRequest(chromeTabRequest) &&
    WindowChromeCdp.ValidTabRequest(chromeTabRequest with { Action = ChromeTabAction.LIST,
        TabId = null, Url = null }) &&
    !WindowChromeCdp.ValidTabRequest(chromeTabRequest with { Url = "javascript:alert(1)" }) &&
    !WindowChromeCdp.ValidTabRequest(chromeTabRequest with { TabId = "bad" }) &&
    !WindowChromeCdp.ValidTabRequest(chromeTabRequest with { Action = ChromeTabAction.CLOSE,
        Url = "https://example.com/" }) &&
    chromeTabWire.Contains("\"action\":\"NAVIGATE\"", StringComparison.Ordinal) &&
    new ChromeTabResult(ChromeTabCode.COMPLETED, ChromeTabAction.NAVIGATE).EffectState ==
        WindowEffectState.EFFECT_VERIFIED &&
    new ChromeTabResult(ChromeTabCode.COMPLETED, ChromeTabAction.LIST).EffectState ==
        WindowEffectState.NO_EFFECT &&
    !new ChromeTabResult(ChromeTabCode.EFFECT_UNKNOWN, ChromeTabAction.NAVIGATE).SafeToRetry &&
    new ChromeTabResult(ChromeTabCode.EFFECT_UNKNOWN, ChromeTabAction.NAVIGATE).NextStep ==
        ChromeTabNextStep.INSPECT_STATE_DO_NOT_RETRY,
    "chrome.typed_tab_lifecycle_requires_exact_target_url_and_never_retries_unknown_effect");
var invalidTabCall = await ChromeTabTools.ChromeTab("bad", "Valer", ChromeTabAction.LIST);
var unconfirmedTabCall = await ChromeTabTools.ChromeTab(testWindowId, "Valer",
    ChromeTabAction.CLOSE, sampleTabId, confirm: false);
Check(invalidTabCall.Status == ChromeTabCode.INVALID_ARGUMENT &&
    unconfirmedTabCall.Status == ChromeTabCode.CONFIRMATION_REQUIRED &&
    unconfirmedTabCall.EffectState == WindowEffectState.NO_EFFECT,
    "chrome.tab_tool_invalid_and_unconfirmed_fail_before_native_io");
var foregroundConsentRequest = connectRequest with { ActivateWindowForConsent = true };
string foregroundConsentWire = System.Text.Json.JsonSerializer.Serialize(foregroundConsentRequest, PipeWire.Json);
Check(!connectRequest.ActivateWindowForConsent &&
    foregroundConsentWire.Contains("\"activate_window_for_consent\":true", StringComparison.Ordinal) &&
    System.Text.Json.JsonSerializer.Deserialize<ChromeConnectionRequest>(foregroundConsentWire, PipeWire.Json)?
        .ActivateWindowForConsent == true &&
    System.Text.Json.JsonSerializer.Deserialize<ChromeConnectionNextAction>(
        "\"SHOW_CHROME_FOR_CONSENT\"", PipeWire.Json) ==
        ChromeConnectionNextAction.SHOW_CHROME_FOR_CONSENT &&
    WindowChromeCdp.ConnectionNextAction(ChromeConnectionState.CONSENT_WINDOW_UNAVAILABLE,
        ChromeConnectionFailureReason.FOREGROUND_DENIED) ==
        ChromeConnectionNextAction.SHOW_CHROME_FOR_CONSENT &&
    WindowChromeCdp.ConnectionNextAction(ChromeConnectionState.NOT_REQUESTED,
        ChromeConnectionFailureReason.FOREGROUND_DENIED) ==
        ChromeConnectionNextAction.SHOW_CHROME_FOR_CONSENT &&
    WindowChromeCdp.ConnectionNextAction(ChromeConnectionState.CONNECTION_FAILED,
        ChromeConnectionFailureReason.HANDSHAKE_FAILED) ==
        ChromeConnectionNextAction.RETRY_CONNECTION &&
    WindowChromeCdp.ConnectionNextAction(ChromeConnectionState.CONNECTED, null) ==
        ChromeConnectionNextAction.NONE,
    "chrome.native_consent_does_not_activate_desktop_without_explicit_opt_in");
var connectReply = new ChromeConnectionResult(ChromeConnectionCode.OK,
    ChromeConnectionState.CONSENT_PENDING, ChromeConnectionNextAction.ALLOW_CHROME_PROMPT);
Check(new ChromeBrokerRequest(Connection: connectRequest).Valid &&
    !new ChromeBrokerRequest(Connection: connectRequest, Inspect: new(testWindowId, 16, 50,
        null, null, 0, false, null, null, 0, WindowInspectScope.TABS,
        ExpectedProfile: "Valer")).Valid &&
    WindowChromeBroker.ReplyMatches(new(Connection: connectRequest), new(Connection: connectReply)) &&
    !WindowChromeBroker.ReplyMatches(new(Connection: connectRequest),
        new(Inspect: new(WindowInspectCode.COMPLETED, []))) &&
    System.Text.Json.JsonSerializer.Deserialize<ChromeConnectionResult>(
        System.Text.Json.JsonSerializer.Serialize(connectReply, PipeWire.Json), PipeWire.Json)?.State ==
        ChromeConnectionState.CONSENT_PENDING &&
    System.Text.Json.JsonSerializer.Deserialize<ChromeConnectionState>(
        "\"CONSENT_WINDOW_UNAVAILABLE\"", PipeWire.Json) ==
        ChromeConnectionState.CONSENT_WINDOW_UNAVAILABLE &&
    System.Text.Json.JsonSerializer.Deserialize<ChromeConnectionState>(
        "\"CONSENT_TIMED_OUT\"", PipeWire.Json) == ChromeConnectionState.CONSENT_TIMED_OUT &&
    WindowChromeCdp.ConsentTimeout == TimeSpan.FromMinutes(5) &&
    connectReply.NextAction == ChromeConnectionNextAction.ALLOW_CHROME_PROMPT,
    "chrome.connection_consent_pending_is_typed_and_not_a_grant");
var lostConnection = new ChromeConnectionResult(ChromeConnectionCode.OK,
    ChromeConnectionState.CONNECTION_LOST, ChromeConnectionNextAction.RETRY_CONNECTION,
    FailureReason: ChromeConnectionFailureReason.SOCKET_CLOSED,
    LastLossReason: ChromeConnectionFailureReason.SOCKET_CLOSED);
string lostWire = System.Text.Json.JsonSerializer.Serialize(lostConnection, PipeWire.Json);
Check(WindowChromeCdp.MissingConnectionState(false) == ChromeConnectionState.NOT_REQUESTED &&
    WindowChromeCdp.MissingConnectionState(true) == ChromeConnectionState.CONNECTION_LOST &&
    lostWire.Contains("\"failure_reason\":\"SOCKET_CLOSED\"", StringComparison.Ordinal) &&
    lostWire.Contains("\"last_loss_reason\":\"SOCKET_CLOSED\"", StringComparison.Ordinal) &&
    System.Text.Json.JsonSerializer.Deserialize<ChromeConnectionResult>(lostWire, PipeWire.Json)?.FailureReason ==
        ChromeConnectionFailureReason.SOCKET_CLOSED &&
    System.Text.Json.JsonSerializer.Deserialize<ChromeConnectionResult>(lostWire, PipeWire.Json)?.LastLossReason ==
        ChromeConnectionFailureReason.SOCKET_CLOSED,
    "chrome.lost_approved_connection_has_typed_reason_not_not_requested");
Check(WindowChromeCdp.CdpTransportFailure(new IOException("CDP_SOCKET_CLOSED")) &&
    !WindowChromeCdp.CdpTransportFailure(new OperationCanceledException()) &&
    WindowChromeCdp.CdpTransportFailure(new System.Text.Json.JsonException("bad payload")) &&
    !WindowChromeCdp.CdpTransportFailure(new InvalidOperationException("CDP_COMMAND_REJECTED")) &&
    !WindowChromeCdp.CdpTransportFailure(new InvalidOperationException("CDP_TARGET_DETACHED")) &&
    !WindowChromeCdp.CdpTransportFailure(new KeyNotFoundException()),
    "chrome.command_or_target_error_preserves_authorized_browser_socket");
Check(WindowChromeCdp.CanRetryCdpAction(new(WindowActionCode.CDP_UNAVAILABLE), connected: false) &&
    !WindowChromeCdp.CanRetryCdpAction(new(WindowActionCode.CDP_UNAVAILABLE), connected: true) &&
    !WindowChromeCdp.CanRetryCdpAction(new(WindowActionCode.CDP_FAILED_UNKNOWN_EFFECT,
        EffectUnknown: true, ExecutionMethod: WindowExecutionMethod.CHROME_CDP), connected: false) &&
    !WindowChromeCdp.CanRetryCdpAction(new(WindowActionCode.COMPLETED,
        ExecutionMethod: WindowExecutionMethod.CHROME_CDP), connected: false),
    "chrome.retry_only_before_any_action_dispatch");
Check(WindowAction.WindowTitleMatches("Example Domain — Camoufox", "Example Domain") &&
    WindowAction.WindowTitleMatches("Example Domain - Google Chrome", "Example Domain") &&
    WindowAction.WindowTitleMatches("Exact title", "Exact title") &&
    !WindowAction.WindowTitleMatches("Unrelated Example Domain — Camoufox", "Example Domain") &&
    !WindowAction.WindowTitleMatches("Example Domainish — Camoufox", "Example Domain") &&
    !WindowAction.WindowTitleMatches("Example Domain", "example domain"),
    "windows.window_title_matches_exact_or_browser_suffix_not_substrings");
var updateRequested = System.Text.Json.JsonSerializer.Deserialize<UpdateTaskWire>(
    """{"status":"REQUESTED","operation_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","task":"WindowsAdminMCP-OperatorUpdate"}""", PipeWire.Json);
var updateIdle = System.Text.Json.JsonSerializer.Deserialize<UpdateTaskWire>(
    """{"status":"NOT_REQUESTED","operation_id":null,"stage":null,"commit":null,"reason":null,"rollback_pending":null,"task_state":"Absent","last_task_result":null}""", PipeWire.Json);
var updateAccept = System.Text.Json.JsonSerializer.Deserialize<UpdateTaskWire>(
    """{"status":"COMPLETED","operation_id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","stage":"accepted","operation_action":"ACCEPT","rollback_pending":false}""", PipeWire.Json);
Check(updateRequested?.Status == "REQUESTED" && updateRequested.Task == "WindowsAdminMCP-OperatorUpdate" &&
    updateIdle?.Status == "NOT_REQUESTED" && updateIdle.TaskState == "Absent" && updateIdle.LastTaskResult is null &&
    updateAccept?.OperationAction == UpdateToolAction.ACCEPT && updateAccept.Stage == "accepted" &&
    updateAccept.RollbackPending == false,
    "operator.strict_json_accepts_request_and_status_receipts");
Check(WindowChromeCdp.ValidTargetId("0123456789abcdef0123456789abcdef") &&
    !WindowChromeCdp.ValidTargetId("https://example.com") &&
    !WindowChromeCdp.ValidTabElementRef("c1:1:bad:1:bad:deadbeef"),
    "chrome.cdp_target_and_element_reference_boundaries");
string freshTabRef = "c1:" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x") +
    ":0123456789abcdef0123456789abcdef:1:loader:0123456789abcdef0123456789abcdef";
var chromeAction = new WindowActionRequest(testWindowId, freshTabRef,
    WindowActionKind.CLICK, null, ExpectedProfile: "Valer");
var chromeUiaAction = new WindowActionRequest(testWindowId, freshElementId,
    WindowActionKind.CLICK, null, ExpectedProfile: "Valer");
string expiredStamp = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds().ToString("x");
string staleElementId = "e1:" + expiredStamp + ":0123456789abcdef0123456789abcdef";
string staleTabRef = "c1:" + expiredStamp +
    ":0123456789abcdef0123456789abcdef:1:loader:0123456789abcdef0123456789abcdef";
Check(!WindowInspector.TryParseElementRef(staleElementId, out _) &&
    WindowInspector.WellFormedElementRef(staleElementId, out _) &&
    !WindowChromeCdp.ValidTabElementRef(staleTabRef) &&
    WindowChromeCdp.WellFormedTabElementRef(staleTabRef) &&
    WindowAction.InvalidCode(chromeUiaAction with { ElementId = staleElementId }) ==
        WindowActionCode.STALE_ELEMENT &&
    WindowAction.InvalidCode(chromeAction with { ElementId = staleTabRef }) ==
        WindowActionCode.STALE_ELEMENT &&
    WindowAction.InvalidCode(chromeAction with { ElementId = "broken" }) ==
        WindowActionCode.INVALID_ARGUMENT &&
    new ChromeBrokerRequest(Action: chromeAction with { ElementId = staleTabRef }).Valid,
    "windows.expired_element_refs_are_stale_not_invalid_arguments");
Check(WindowChromeCdp.ValidTabElementRef(freshTabRef) && WindowAction.Valid(chromeAction) &&
    !WindowAction.Valid(chromeAction with { ExpectedProfile = null }) &&
    new ChromeBrokerRequest(Action: chromeAction).Valid &&
    new ChromeBrokerRequest(Action: chromeUiaAction).Valid &&
    new ChromeBrokerRequest(Action: chromeUiaAction with { ExpectedProfile = null }).Valid &&
    !new ChromeBrokerRequest(Action: chromeAction with { ExpectedProfile = null }).Valid &&
    !new ChromeBrokerRequest(Action: chromeUiaAction with { ResolveOnly = true }).Valid &&
    new ChromeBrokerRequest(Inspect: new(testWindowId, 16, 50, null, null, 0,
        false, null, null, 0, WindowInspectScope.TABS, ExpectedProfile: "Valer")).Valid &&
    !new ChromeBrokerRequest(Inspect: new(testWindowId, 16, 50, null, null, 0,
        false, null, null, 0, WindowInspectScope.WINDOW, ExpectedProfile: "Valer")).Valid &&
    WindowChromeBroker.SameBrowser(testWindowId, testWindowId) &&
    WindowChromeBroker.SameBrowser(testWindowId, WindowCatalog.FormatId(
        testWindowIdentity with { Handle = 0x99999 })) &&
    !WindowChromeBroker.SameBrowser(testWindowId, WindowCatalog.FormatId(
        testWindowIdentity with { ProcessId = testWindowIdentity.ProcessId + 1 })) &&
    WindowChromeBroker.ReplyMatches(new(Action: chromeAction),
        new(Action: new(WindowActionCode.COMPLETED))) &&
    !WindowChromeBroker.ReplyMatches(new(Action: chromeAction),
        new(Inspect: new(WindowInspectCode.COMPLETED, []))),
    "chrome.browser_bound_broker_reuses_connection_across_profiles");
string trace = Guid.NewGuid().ToString("N");
Check(WindowChromeBroker.ReplyMatches(new(Action: chromeAction, TraceId: trace),
        new(Action: new(WindowActionCode.COMPLETED), TraceId: trace)) &&
    !WindowChromeBroker.ReplyMatches(new(Action: chromeAction, TraceId: trace),
        new(Action: new(WindowActionCode.COMPLETED), TraceId: Guid.NewGuid().ToString("N"))) &&
    WindowChromeBroker.ExchangeTimeoutMs(new(Inspect: new(testWindowId,16,50,null,null,0,
        false,null,null,0,WindowInspectScope.TAB,ExpectedProfile:"Valer",
        ExpectedOrigin:"https://example.com")),false) >= 24000 &&
    WindowChromeBroker.ExchangeTimeoutMs(new(Action:chromeAction),false) >=
        chromeAction.ObservationTimeoutMs+9000 &&
    new WindowActionResult(WindowActionCode.LOCATOR_TIMEOUT).SafeToRetry,
    "chrome.trace_identity_and_locator_only_timeout_are_typed");
using (var brokerStop = new CancellationTokenSource())
{
    Task owner = ChromeBrokerPipe.RunOwnerAsync(brokerStop.Token);
    Check(ChromeBrokerPipe.IsOwner, "chrome.single_user_owner_started_in_existing_process");
    bool duplicateRejected = false;
    try { await ChromeBrokerPipe.RunOwnerAsync(brokerStop.Token); }
    catch (InvalidOperationException e) when (e.Message == "CHROME_BROKER_ALREADY_OWNED")
    { duplicateRejected = true; }
    Check(duplicateRejected, "chrome.second_user_owner_denied_by_session_semaphore");
    var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
    {
        var request = new ChromeBrokerRequest(Connection: new(testWindowId,
            "CI_NO_CHROME", ChromeConnectionAction.STATUS),
            TraceId: Guid.NewGuid().ToString("N"));
        return ChromeBrokerPipe.ExchangeAsync(request, CancellationToken.None);
    }));
    Check(responses.All(reply => reply?.OwnerPid == Environment.ProcessId &&
        reply.Connection?.State == ChromeConnectionState.NOT_REQUESTED &&
        reply.TraceId is { Length: 32 }),
        "chrome.ten_independent_pipe_requests_share_one_owner_without_consent");
    var processProbes = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
    {
        string executable = Environment.ProcessPath!;
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet",
            StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--chrome-pipe-probe");
        start.ArgumentList.Add(testWindowId);
        using var child = Process.Start(start);
        if (child is null) return false;
        Task<string> response = child.StandardOutput.ReadToEndAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        if (child.ExitCode != 0) return false;
        using var parsed = System.Text.Json.JsonDocument.Parse(await response);
        var root = parsed.RootElement;
        return root.GetProperty("owner_pid").GetInt32() == Environment.ProcessId &&
            root.GetProperty("state").GetString() == "NOT_REQUESTED" &&
            root.GetProperty("trace_verified").GetBoolean();
    }));
    Check(processProbes.All(ok => ok),
        "chrome.ten_distinct_frontend_processes_share_one_session_owner");
    string clientExe = Environment.ProcessPath!;
    var malformedStart = new ProcessStartInfo(clientExe)
    {
        UseShellExecute = false, CreateNoWindow = true
    };
    if (Path.GetFileNameWithoutExtension(clientExe).Equals("dotnet",
        StringComparison.OrdinalIgnoreCase))
        malformedStart.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
    malformedStart.ArgumentList.Add("--chrome-pipe-invalid-frame");
    using (var malformedClient = Process.Start(malformedStart))
    {
        Check(malformedClient is not null, "chrome.malformed_frame_test_client_started");
        await malformedClient!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Check(malformedClient!.ExitCode == 0,
            "chrome.oversized_frame_header_rejected_on_authorized_pipe");
    }
    var tooLarge = new ChromeBrokerRequest(Inspect: new(testWindowId, 16, 50,
        null, null, 0, false, new string('x', 40_000), null, 0,
        WindowInspectScope.TAB, ExpectedProfile: "CI_NO_CHROME"),
        TraceId: Guid.NewGuid().ToString("N"));
    Check(tooLarge.Valid &&
        await ChromeBrokerPipe.ExchangeAsync(tooLarge, CancellationToken.None) is null,
        "chrome.oversized_frontend_request_fails_closed_without_dispatch");
    var afterMalformed = new ChromeBrokerRequest(Connection: new(testWindowId,
        "CI_NO_CHROME", ChromeConnectionAction.STATUS),
        TraceId: Guid.NewGuid().ToString("N"));
    var survived = await ChromeBrokerPipe.ExchangeAsync(afterMalformed, CancellationToken.None);
    Check(ChromeBrokerPipe.IsOwner && survived?.OwnerPid == Environment.ProcessId &&
        survived.Connection?.State == ChromeConnectionState.NOT_REQUESTED &&
        survived.TraceId == afterMalformed.TraceId,
        "chrome.invalid_client_frame_does_not_terminate_shared_cdp_owner");
    await brokerStop.CancelAsync();
    await owner;
    Check(!ChromeBrokerPipe.IsOwner, "chrome.user_owner_shutdown_closes_pipe");
}
Check(Enum.GetValues<WindowActionKind>().Where(WindowChromeCdp.PointerAction).ToHashSet().SetEquals(
        [WindowActionKind.CLICK, WindowActionKind.DOUBLE_CLICK, WindowActionKind.RIGHT_CLICK,
         WindowActionKind.INVOKE, WindowActionKind.SELECT,
         WindowActionKind.TOGGLE, WindowActionKind.EXPAND, WindowActionKind.COLLAPSE]) &&
    !WindowChromeCdp.PointerAction(WindowActionKind.SET_VALUE) &&
    !WindowChromeCdp.PointerAction(WindowActionKind.TYPE_TEXT) &&
    !WindowChromeCdp.PointerAction(WindowActionKind.KEY_PRESS),
    "chrome.cdp_all_semantic_pointer_actions_ignore_ax_role_whitelist");
Check(Enum.GetValues<WindowActionKind>().Where(WindowChromeCdp.CdpPointerAction).ToHashSet().SetEquals(
        [WindowActionKind.CLICK, WindowActionKind.DOUBLE_CLICK, WindowActionKind.RIGHT_CLICK,
         WindowActionKind.INVOKE, WindowActionKind.SELECT, WindowActionKind.TOGGLE,
         WindowActionKind.EXPAND, WindowActionKind.COLLAPSE,
         WindowActionKind.HOVER, WindowActionKind.SCROLL]) &&
    WindowChromeCdp.WheelDelta(WindowScrollDirection.UP, 3) == (0, -360) &&
    WindowChromeCdp.WheelDelta(WindowScrollDirection.DOWN, 3) == (0, 360) &&
    WindowChromeCdp.WheelDelta(WindowScrollDirection.LEFT, 3) == (-360, 0) &&
    WindowChromeCdp.WheelDelta(WindowScrollDirection.RIGHT, 3) == (360, 0) &&
    !WindowChromeCdp.CdpPointerAction(WindowActionKind.DRAG) &&
    !WindowChromeCdp.CdpPointerAction(WindowActionKind.KEY_PRESS),
    "chrome.cdp_hover_and_wheel_are_bounded_background_pointer_actions");
using (var states = System.Text.Json.JsonDocument.Parse("""
    {"nodes":[{"properties":[{"name":"expanded","value":{"value":false}},
                               {"name":"selected","value":{"value":true}},
                               {"name":"checked","value":{"value":"true"}}]},
              {"properties":[{"name":"pressed","value":{"value":true}}]}]}
    """))
{
    var node = states.RootElement.GetProperty("nodes")[0];
    Check(WindowChromeCdp.AxActionState(node, WindowActionKind.EXPAND) == false &&
        WindowChromeCdp.AxActionState(node, WindowActionKind.COLLAPSE) == false &&
        WindowChromeCdp.AxActionState(node, WindowActionKind.SELECT) == true &&
        WindowChromeCdp.AxActionState(node, WindowActionKind.TOGGLE) == true &&
        WindowChromeCdp.AxActionState(node, WindowActionKind.CLICK) is null &&
        WindowChromeCdp.AxActionState(states.RootElement.GetProperty("nodes")[1],
            WindowActionKind.TOGGLE) == true &&
        WindowChromeCdp.AxActionState(states.RootElement.GetProperty("nodes")[1],
            WindowActionKind.EXPAND) is null,
        "chrome.cdp_semantic_actions_observe_actual_ax_state");
}
using (var contexts = System.Text.Json.JsonDocument.Parse("""
    {"normal":{"targetId":"A"},"explicit":{"browserContextId":"profile5"},
     "empty":{"browserContextId":""},"wrongType":{"browserContextId":42}}
    """))
{
    var root = contexts.RootElement;
    Check(WindowChromeCdp.TryTargetContextId(root.GetProperty("normal"), out var ordinaryContext) &&
        ordinaryContext is null &&
        WindowChromeCdp.TryTargetContextId(root.GetProperty("explicit"), out var namedContext) &&
        namedContext == "profile5" &&
        !WindowChromeCdp.TryTargetContextId(root.GetProperty("empty"), out _) &&
        !WindowChromeCdp.TryTargetContextId(root.GetProperty("wrongType"), out _),
        "chrome.cdp_targetinfo_optional_browser_context_with_strict_value_checks");
}
var matchingCdpWindow = WindowChromeCdp.UniqueWindowId(new (int WindowId, string? ContextId)[]
    { (12, "profile5"), (12, "profile5") });
var normalCdpWindow = WindowChromeCdp.UniqueWindowId(new (int WindowId, string? ContextId)[]
    { (12, null), (12, null) }, out int normalCount);
var sameWindowMixedContexts = WindowChromeCdp.UniqueWindowId(new (int WindowId, string? ContextId)[]
    { (12, null), (12, "profile5"), (12, "profile9") }, out int sameWindowCount);
var missingCdpWindow = WindowChromeCdp.UniqueWindowId(
    Array.Empty<(int WindowId, string? ContextId)>(), out int missingCount);
var ambiguousCdpWindow = WindowChromeCdp.UniqueWindowId(new (int WindowId, string? ContextId)[]
    { (12, null), (13, "profile5") }, out int ambiguousCount);
Check(matchingCdpWindow == 12 &&
    normalCdpWindow == 12 && normalCount == 1 &&
    sameWindowMixedContexts == 12 && sameWindowCount == 1 &&
    missingCdpWindow is null && missingCount == 0 &&
    ambiguousCdpWindow is null && ambiguousCount == 2 &&
    WindowChromeCdp.InspectMappingCode(WindowChromeCdp.CdpWindowMappingState.NOT_FOUND) ==
        WindowInspectCode.CDP_TARGET_NOT_FOUND &&
    WindowChromeCdp.InspectMappingCode(WindowChromeCdp.CdpWindowMappingState.AMBIGUOUS) ==
        WindowInspectCode.CDP_TARGET_AMBIGUOUS &&
    WindowChromeCdp.UniqueWindowId(new (int WindowId, string? ContextId)[]
    { (12, "profile5"), (13, "profile5") }) is null &&
    WindowChromeCdp.UniqueWindowId(new (int WindowId, string? ContextId)[]
    { (12, "profile5"), (13, "profile9") }) is null &&
    WindowChromeCdp.UniqueWindowId(new (int WindowId, string? ContextId)[]
    { (12, "") }) is null,
    "chrome.cdp_native_window_unique_across_optional_distinct_contexts");
var chromeTabStrip = WindowChromeCdp.NativeTabStripTitles(new (string Path, string Name)[]
{
    ("0.1.0", "Вкладка \"Profile Control Plane\" использует 58 МБ памяти"),
    ("0.1.1", "Unique Native Tab"),
    ("0.2.0", "Different web page ARIA tab")
}, "Profile Control Plane - Google Chrome");
var secondTabStrip = WindowChromeCdp.NativeTabStripTitles(new (string Path, string Name)[]
{
    ("0.1.0", "Profile Control Plane"), ("0.2.0", "Tab \"Profile Control Plane\" inactive")
}, "Profile Control Plane - Google Chrome");
var candidatePages = new (int WindowId, string Title)[]
{
    (12, "Profile Control Plane"), (12, "Unique Native Tab"),
    (13, "Profile Control Plane"), (13, "Other Window's Tab")
};
Check(chromeTabStrip is { Length: 2 } && chromeTabStrip.Contains("Profile Control Plane") &&
    chromeTabStrip.Contains("Unique Native Tab") && secondTabStrip is null &&
    WindowChromeCdp.NativeTabStripTitles(new (string Path, string Name)[]
    { ("0.1.0", "Profile Control Plane") }, "Different title - Google Chrome") is null &&
    WindowChromeCdp.ChromeTabLabel("Вкладка \"Name\" неактивна") == "Name" &&
    WindowChromeCdp.ChromeTabLabel("Tab \"English\" inactive") == "English" &&
    WindowChromeCdp.ChromeTabLabel("Вкладка \"\" неактивна") is null &&
    WindowChromeCdp.UniqueTabFingerprintWindow(new[] { 12, 13 }, candidatePages,
        chromeTabStrip!) == 12 &&
    WindowChromeCdp.UniqueTabFingerprintWindow(new[] { 12, 13 }, candidatePages,
        new[] { "Profile Control Plane" }) is null &&
    WindowChromeCdp.UniqueTabFingerprintWindow(new[] { 12, 13 }, candidatePages,
        new[] { "Profile Control Plane", "Unique Native Tab", "Other Window's Tab" }) is null,
    "chrome.cdp_ambiguous_overlapping_windows_resolved_only_by_exclusive_native_tab_strip");
var staleActiveTitleNative = new[] { "Stale Active Title", "Unique A", "Unique B", "Shared" };
var fallbackPages = new (int WindowId, string Title)[]
{
    (12,"Unique A"),(12,"Unique B"),(12,"Shared"),(13,"Shared"),(13,"Other")
};
Check(WindowChromeCdp.UniqueStrongTabFingerprintWindow(new[] { 12,13 },fallbackPages,
        staleActiveTitleNative)==12 &&
    WindowChromeCdp.UniqueStrongTabFingerprintWindow(new[] { 12,13 },
        new[] { (12,"Unique A"),(13,"Shared") },staleActiveTitleNative) is null &&
    WindowChromeCdp.UniqueStrongTabFingerprintWindow(new[] { 12,13 },
        new[] { (12,"Unique A"),(12,"Unique B"),(13,"Shared"),(13,"Other"),
            (13,"Unique B") },staleActiveTitleNative) is null &&
    WindowChromeCdp.UniqueStrongTabFingerprintWindow(new[] { 12,13 },
        new[] { (12,"Unique A"),(12,"Unique B") },
        new[] { "Stale Active Title","Unique A","Unique B","Third","Fourth" }) is null &&
    WindowChromeCdp.UniqueStrongTabFingerprintWindow(new[] { 12,13 },fallbackPages,
        new[] { "Stale Active Title","Shared" }) is null,
    "chrome.cdp_missing_active_title_requires_multiple_exclusive_near_complete_tab_matches");
var maximizedSameTitlePages = new (int WindowId, string Title)[]
{
    (12,"All workflows"),(12,"Profile Control Plane"),(12,"Unique Test Tab"),
    (13,"All workflows"),(13,"Other Profile Tab")
};
Check(WindowChromeCdp.UniqueStrongTabFingerprintWindow(new[] { 12,13 },
        maximizedSameTitlePages,
        new[] { "All workflows", "Profile Control Plane", "Unique Test Tab" }) == 12 &&
    WindowChromeCdp.UniqueStrongTabFingerprintWindow(new[] { 12,13 },
        maximizedSameTitlePages,
        new[] { "All workflows", "Profile Control Plane" }) is null &&
    WindowChromeCdp.UniqueStrongTabFingerprintWindow(new[] { 12,13 },
        maximizedSameTitlePages,
        new[] { "All workflows", "Profile Control Plane", "Other Profile Tab" }) is null,
    "chrome.maximized_windows_with_mismatched_bounds_require_two_exclusive_native_tab_titles");
string mappingWire=System.Text.Json.JsonSerializer.Serialize(new WindowInspectResult(
    WindowInspectCode.CDP_TARGET_NOT_FOUND,[],Detail:"CDP_WINDOW_NO_MATCH",
    CdpMappingFailure:WindowCdpMappingFailure.FINGERPRINT_AMBIGUOUS,
    CdpTitleCandidates:0,CdpFingerprintCandidates:9),PipeWire.Json);
Check(mappingWire.Contains("\"cdp_mapping_failure\":\"FINGERPRINT_AMBIGUOUS\"",
        StringComparison.Ordinal) &&
    mappingWire.Contains("\"cdp_title_candidates\":0",StringComparison.Ordinal) &&
    mappingWire.Contains("\"cdp_fingerprint_candidates\":9",StringComparison.Ordinal) &&
    !mappingWire.Contains("targetIds",StringComparison.OrdinalIgnoreCase),
    "chrome.cdp_window_mapping_failure_typed_bounded_metadata_without_target_leak");
string tabMappingWire=System.Text.Json.JsonSerializer.Serialize(new ChromeTabResult(
    ChromeTabCode.TARGET_NOT_FOUND, ChromeTabAction.LIST,
    Detail:"CDP_WINDOW_NO_MATCH", CdpMappingFailure:WindowCdpMappingFailure.BOUNDS_MISMATCH,
    CdpTitleCandidates:5, CdpFingerprintCandidates:26),PipeWire.Json);
Check(tabMappingWire.Contains("\"cdp_mapping_failure\":\"BOUNDS_MISMATCH\"",
        StringComparison.Ordinal) &&
    tabMappingWire.Contains("\"cdp_title_candidates\":5",StringComparison.Ordinal) &&
    tabMappingWire.Contains("\"cdp_fingerprint_candidates\":26",StringComparison.Ordinal) &&
    !tabMappingWire.Contains("targetIds",StringComparison.OrdinalIgnoreCase),
    "chrome.tab_management_mapping_failure_reports_typed_candidates_without_target_leak");
Check(WindowPhysicalClick.CanUseNativeFallback(null) &&
    WindowPhysicalClick.CanUseNativeFallback(new(WindowActionCode.CDP_UNAVAILABLE,
        FailureStage: WindowFailureStage.CDP_CONNECT)) &&
    !WindowPhysicalClick.CanUseNativeFallback(new(WindowActionCode.CDP_UNAVAILABLE,
        EffectUnknown: true, FailureStage: WindowFailureStage.CDP_CONNECT)) &&
    !WindowPhysicalClick.CanUseNativeFallback(new(WindowActionCode.CDP_CONTEXT_MISMATCH)) &&
    !WindowPhysicalClick.CanUseNativeFallback(new(WindowActionCode.TARGET_OCCLUDED)) &&
    !WindowPhysicalClick.CanUseNativeFallback(new(WindowActionCode.CDP_FAILED_UNKNOWN_EFFECT,
        EffectUnknown: true, FailureStage: WindowFailureStage.DISPATCH_INPUT)),
    "chrome.native_fallback_only_before_any_cdp_dispatch");
var textRequest = new WindowActionRequest(testWindowId, freshElementId,
    WindowActionKind.TYPE_TEXT, "CI_NEW");
var textUnverified = WindowAction.RequireVerifiedTextEffect(textRequest,
    new(WindowActionCode.COMPLETED, ExpectedStateObserved: false), true);
Check(textUnverified.Status == WindowActionCode.ACTION_FAILED_UNKNOWN_EFFECT &&
    textUnverified.EffectUnknown && textUnverified.FailureStage == WindowFailureStage.VERIFY_EFFECT &&
    WindowAction.RequireVerifiedTextEffect(textRequest,
        new(WindowActionCode.COMPLETED, ExpectedStateObserved: true), true).Status == WindowActionCode.COMPLETED &&
    WindowAction.RequireVerifiedTextEffect(textRequest,
        new(WindowActionCode.COMPLETED), false).Status == WindowActionCode.COMPLETED,
    "windows.text_input_requires_observed_value_before_success");
using (var protectedNode = System.Text.Json.JsonDocument.Parse(
    "{\"properties\":[{\"name\":\"protected\",\"value\":{\"value\":true}}]}"))
    Check(WindowChromeCdp.IsProtectedAx(protectedNode.RootElement),
        "chrome.cdp_protected_fields_stay_redacted_during_inspection");
using (var ax = System.Text.Json.JsonDocument.Parse("""
    {"nodes":[{"name":{"value":"test field"},"value":{"value":"updated"}},
              {"name":{"value":"Save"}},
              {"name":{"value":"Disabled"},"properties":[{"name":"disabled","value":{"value":true}}]},
              {"name":{"value":"hidden"},"value":{"value":"secret"},
               "properties":[{"name":"protected","value":{"value":true}}]}]}
    """))
    Check(WindowChromeCdp.HasAxValue(ax.RootElement, "test field", "updated") &&
        !WindowChromeCdp.HasAxValue(ax.RootElement, "test field", "wrong") &&
        !WindowChromeCdp.HasAxValue(ax.RootElement, "hidden", "secret") &&
        !WindowChromeCdp.HasAxValue(ax.RootElement, "unknown", ""),
        "chrome.cdp_value_postcondition_is_exact_and_ignores_protected_fields");
using (var ax = System.Text.Json.JsonDocument.Parse("""
    {"nodes":[{"name":{"value":"Save"}},
              {"name":{"value":"Disabled"},"properties":[{"name":"disabled","value":{"value":true}}]},
              {"name":{"value":"Duplicate"}}, {"name":{"value":"Duplicate"}},
              {"name":{"value":"Secret"},"properties":[{"name":"protected","value":{"value":true}}]}]}
    """))
    Check(WindowChromeCdp.HasAxEnabled(ax.RootElement, "Save", true) &&
        WindowChromeCdp.HasAxEnabled(ax.RootElement, "Disabled", false) &&
        !WindowChromeCdp.HasAxEnabled(ax.RootElement, "Disabled", true) &&
        !WindowChromeCdp.HasAxEnabled(ax.RootElement, "Duplicate", true) &&
        !WindowChromeCdp.HasAxEnabled(ax.RootElement, "Secret", true) &&
        !WindowChromeCdp.HasAxEnabled(ax.RootElement, "Missing", true),
        "chrome.cdp_enabled_postcondition_is_unique_and_provider_verified");
Check(new WindowActionResult(WindowActionCode.COMPLETED).EffectState == WindowEffectState.EFFECT_UNKNOWN &&
    new WindowActionResult(WindowActionCode.COMPLETED, ExpectedStateObserved: true).EffectState ==
        WindowEffectState.EFFECT_VERIFIED &&
    new WindowActionResult(WindowActionCode.STALE_ELEMENT).EffectState == WindowEffectState.NO_EFFECT &&
    new WindowActionResult(WindowActionCode.ACTION_FAILED_UNKNOWN_EFFECT, EffectUnknown: true).EffectState ==
        WindowEffectState.EFFECT_UNKNOWN, "windows.action_effect_state_never_assumes_success");
var providerAck = new WindowActionResult(WindowActionCode.COMPLETED,
    ExecutionMethod: WindowExecutionMethod.UIA_PATTERN, ForegroundChanged: false);
var verifiedAction = providerAck with {ExpectedStateObserved = true};
var missingPostcondition = providerAck with {ExpectedStateObserved = false};
var timedOutAction = new WindowActionResult(WindowActionCode.TIMEOUT_UNKNOWN_EFFECT, EffectUnknown: true);
Check(providerAck.EffectEvidence == WindowEffectEvidence.PROVIDER_ACK_ONLY &&
    providerAck.NextStep == WindowNextStep.INSPECT_AND_SUPPLY_POSTCONDITION &&
    providerAck.FocusBehavior == WindowFocusBehavior.UIA_PROVIDER_MAY_ACTIVATE &&
    providerAck.ForegroundChanged == false && !providerAck.SafeToRetry &&
    verifiedAction.EffectEvidence == WindowEffectEvidence.POSTCONDITION_VERIFIED &&
    verifiedAction.NextStep == WindowNextStep.NONE &&
    missingPostcondition.EffectEvidence == WindowEffectEvidence.POSTCONDITION_NOT_OBSERVED &&
    missingPostcondition.NextStep == WindowNextStep.INSPECT_STATE_DO_NOT_RETRY &&
    timedOutAction.EffectEvidence == WindowEffectEvidence.DISPATCH_OUTCOME_UNKNOWN &&
    timedOutAction.NextStep == WindowNextStep.INSPECT_STATE_DO_NOT_RETRY &&
    timedOutAction.FocusBehavior == WindowFocusBehavior.UNKNOWN_METHOD_MAY_ACTIVATE &&
    cdpAccepted.FocusBehavior == WindowFocusBehavior.CDP_NO_NATIVE_ACTIVATION &&
    new WindowActionResult(WindowActionCode.FOREGROUND_INPUT_DISALLOWED).NextStep ==
        WindowNextStep.CHANGE_INPUT_MODE,
    "windows.typed_effect_evidence_retry_guidance_and_uia_focus_risk");
var guidanceWire = System.Text.Json.JsonSerializer.Serialize(providerAck, PipeWire.Json);
Check(guidanceWire.Contains("\"effect_evidence\":\"PROVIDER_ACK_ONLY\"", StringComparison.Ordinal) &&
    guidanceWire.Contains("\"next_step\":\"INSPECT_AND_SUPPLY_POSTCONDITION\"", StringComparison.Ordinal) &&
    guidanceWire.Contains("\"focus_behavior\":\"UIA_PROVIDER_MAY_ACTIVATE\"", StringComparison.Ordinal),
    "windows.typed_action_guidance_wire_contract");
var executionWire = System.Text.Json.JsonSerializer.Serialize(new WindowActionResult(
    WindowActionCode.COMPLETED, ExecutionMethod: WindowExecutionMethod.WIN32_SENDINPUT), PipeWire.Json);
Check(executionWire.Contains("\"execution_method\":\"WIN32_SENDINPUT\"", StringComparison.Ordinal) &&
    System.Text.Json.JsonSerializer.Deserialize<WindowActionResult>(executionWire, PipeWire.Json)?.ExecutionMethod ==
        WindowExecutionMethod.WIN32_SENDINPUT &&
    !System.Text.Json.JsonSerializer.Serialize(new WindowActionResult(WindowActionCode.STALE_ELEMENT), PipeWire.Json)
        .Contains("execution_method", StringComparison.Ordinal),
    "windows.typed_execution_method_only_after_dispatch");
Check(WindowChromeCdp.TryReadEndpoint(
        "9222\n/devtools/browser/01234567-89ab-cdef-0123-456789abcdef\n", out var chromeEndpoint) &&
    chromeEndpoint?.Host == "127.0.0.1" && chromeEndpoint.Port == 9222 &&
    !WindowChromeCdp.TryReadEndpoint(
        "9222\nhttp://example.com/steal", out _) &&
    !WindowChromeCdp.TryReadEndpoint(
        "0\n/devtools/browser/01234567-89ab-cdef-0123-456789abcdef", out _) &&
    !WindowChromeCdp.TryReadEndpoint(
        "99999\n/devtools/browser/01234567-89ab-cdef-0123-456789abcdef", out _),
    "chrome.cdp_local_opt_in_endpoint_only");
using (var listener = new TcpListener(IPAddress.Loopback, 0))
{
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    Check(WindowChromeCdp.LoopbackPortOpen(port) &&
        !WindowChromeCdp.LoopbackPortOpen(0),
        "chrome.cdp_endpoint_checks_live_loopback_listener");
    listener.Stop();
    Check(!WindowChromeCdp.LoopbackPortOpen(port),
        "chrome.cdp_stale_port_is_not_a_valid_endpoint");
}
Check(!WindowPhysicalClick.CanUseNativeFallback(new(WindowActionCode.TARGET_OCCLUDED)) &&
    WindowPhysicalClick.CanUseNativeFallback(new(WindowActionCode.CDP_UNAVAILABLE,
        FailureStage: WindowFailureStage.CDP_CONNECT)),
    "windows.physical_click_uses_actual_hit_test_not_role_restrictions");
Check(WindowPhysicalClick.ChromeChunkUnits("abc", 0) == 1 &&
    WindowPhysicalClick.ChromeChunkUnits("a🚀b", 1) == 2 &&
    WindowPhysicalClick.ChromeChunkUnits(new string('a', 79) + "🚀z", 64) == 17 &&
    WindowPhysicalClick.ChromeChunkUnits(new string('a', 96), 64) == 16,
    "windows.chrome_unicode_pacing_preserves_surrogate_pairs_and_chunk_bound");
using (var validInfo = System.Text.Json.JsonDocument.Parse(
    "{\"targetInfo\":{\"targetId\":\"0123456789abcdef\",\"type\":\"page\",\"url\":\"https://staging.example.test/form\"}}"))
using (var navigatedInfo = System.Text.Json.JsonDocument.Parse(
    "{\"targetInfo\":{\"targetId\":\"0123456789abcdef\",\"type\":\"page\",\"url\":\"https://staging.example.test/other\"}}"))
using (var replacedInfo = System.Text.Json.JsonDocument.Parse(
    "{\"targetInfo\":{\"targetId\":\"fedcba9876543210\",\"type\":\"page\",\"url\":\"https://staging.example.test/form\"}}"))
using (var malformedInfo = System.Text.Json.JsonDocument.Parse("{\"targetInfo\":{\"url\":17}}"))
{
    Check(WindowChromeCdp.MatchesDispatchTarget(validInfo.RootElement,
            "0123456789abcdef", "https://staging.example.test/form",
            "https://staging.example.test", null) &&
        !WindowChromeCdp.MatchesDispatchTarget(navigatedInfo.RootElement,
            "0123456789abcdef", "https://staging.example.test/form",
            "https://staging.example.test", null) &&
        !WindowChromeCdp.MatchesDispatchTarget(replacedInfo.RootElement,
            "0123456789abcdef", "https://staging.example.test/form", null, null) &&
        !WindowChromeCdp.MatchesDispatchTarget(malformedInfo.RootElement,
            "0123456789abcdef", "https://staging.example.test/form", null, null),
        "chrome.cdp_predispatch_snapshot_rejects_navigation_target_swap_and_invalid_response");
}
using (var model = System.Text.Json.JsonDocument.Parse("{\"quad\":[10,20,30,20,30,40,10,40],\"viewport\":{\"clientWidth\":100,\"clientHeight\":100}}"))
{
    var root = model.RootElement;
    Check(WindowChromeCdp.TryPoint(root.GetProperty("quad"), root.GetProperty("viewport"),
            out var cx, out var cy) && cx == 20 && cy == 30 &&
        !WindowChromeCdp.TryPoint(root.GetProperty("quad"),
            System.Text.Json.JsonDocument.Parse("{\"clientWidth\":15,\"clientHeight\":100}").RootElement,
            out _, out _), "chrome.cdp_reject_off_viewport_click");
}
var providerFailed = new WindowActionResult(WindowActionCode.ACTION_FAILED_UNKNOWN_EFFECT,
    EffectUnknown: true, FailureStage: WindowFailureStage.DISPATCH_UIA,
    HResult: "80040200", ExceptionType: "COMException", ProviderResponse: "FAIL",
    ExecutionMethod: WindowExecutionMethod.UIA_PATTERN);
var reconciled = WindowAction.ReconcileObserved(providerFailed, true);
var unverified = WindowAction.ReconcileObserved(providerFailed, false);
var disabledAtDispatch = providerFailed with
{
    Status = WindowActionNoFocus.ProviderErrorCode(unchecked((int)0x80040200)),
    EnabledBefore = true
};
var disabledObserved = WindowAction.ReconcileObserved(disabledAtDispatch, true);
var disabledUnknown = WindowAction.ReconcileObserved(disabledAtDispatch, false);
Check(disabledAtDispatch.Status == WindowActionCode.UIA_ELEMENTNOTENABLED_UNKNOWN_EFFECT &&
    WindowActionNoFocus.ProviderErrorCode(unchecked((int)0x80004005)) ==
        WindowActionCode.ACTION_FAILED_UNKNOWN_EFFECT &&
    disabledUnknown.Status == WindowActionCode.UIA_ELEMENTNOTENABLED_UNKNOWN_EFFECT &&
    disabledUnknown.ProviderOutcome == WindowProviderOutcome.REJECTED &&
    disabledUnknown.EnabledBefore == true &&
    disabledUnknown.EffectState == WindowEffectState.EFFECT_UNKNOWN &&
    !disabledUnknown.SafeToRetry &&
    !WindowAction.CanFallbackAfterNativeClick(disabledUnknown) &&
    disabledObserved.Status == WindowActionCode.EFFECT_VERIFIED_PROVIDER_REJECTED &&
    disabledObserved.EffectState == WindowEffectState.EFFECT_VERIFIED &&
    disabledObserved.ProviderOutcome == WindowProviderOutcome.REJECTED,
    "windows.uia_elementnotenabled_typed_rejection_or_verified_effect_no_replay");
Check(reconciled.Status == WindowActionCode.EFFECT_VERIFIED_PROVIDER_REJECTED &&
    reconciled.EffectState == WindowEffectState.EFFECT_VERIFIED && !reconciled.EffectUnknown &&
    reconciled.ProviderOutcome == WindowProviderOutcome.REJECTED && !reconciled.SafeToRetry &&
    reconciled.ExpectedStateObserved == true &&
    reconciled.HResult == providerFailed.HResult &&
    reconciled.FailureStage == WindowFailureStage.DISPATCH_UIA &&
    reconciled.ProviderResponse == providerFailed.ProviderResponse &&
    unverified.EffectState == WindowEffectState.EFFECT_UNKNOWN &&
    unverified.Status == WindowActionCode.ACTION_FAILED_UNKNOWN_EFFECT &&
    unverified.FailureStage == WindowFailureStage.DISPATCH_UIA,
    "windows.provider_error_reconciles_actual_effect_without_retry_or_losing_diagnostics");
Check(WindowAction.CanFallbackAfterNativeClick(new(WindowActionCode.UIA_UNAVAILABLE,
        FailureStage: WindowFailureStage.PREPARE_UIA)) &&
    WindowAction.CanFallbackAfterNativeClick(new(WindowActionCode.UNSUPPORTED_ACTION)) &&
    !WindowAction.CanFallbackAfterNativeClick(providerFailed) &&
    !WindowAction.CanFallbackAfterNativeClick(new(WindowActionCode.UIA_UNAVAILABLE,
        FailureStage: WindowFailureStage.DISPATCH_UIA)) &&
    !WindowAction.CanFallbackAfterNativeClick(new(WindowActionCode.COMPLETED)),
    "windows.semantic_click_only_falls_back_before_any_possible_dispatch");
Check(WindowAction.CanFallbackToSemanticAfterPhysical(new(WindowActionCode.FOREGROUND_UNAVAILABLE,
        FailureStage: WindowFailureStage.ACTIVATE_WINDOW)) &&
    WindowAction.CanFallbackToSemanticAfterPhysical(new(WindowActionCode.TARGET_OCCLUDED,
        FailureStage: WindowFailureStage.HIT_TEST)) &&
    !WindowAction.CanFallbackToSemanticAfterPhysical(new(WindowActionCode.INPUT_FAILED_UNKNOWN_EFFECT,
        EffectUnknown: true, FailureStage: WindowFailureStage.DISPATCH_INPUT)) &&
    !WindowAction.CanFallbackToSemanticAfterPhysical(new(WindowActionCode.COMPLETED)) &&
    !WindowAction.CanFallbackToSemanticAfterPhysical(new(WindowActionCode.STALE_ELEMENT)),
    "windows.web_click_only_falls_back_to_semantic_before_physical_dispatch");
Check(WindowAction.PreferPhysicalWebClick(WindowActionKind.INVOKE, true) &&
    !WindowAction.PreferPhysicalWebClick(WindowActionKind.INVOKE, false) &&
    !WindowAction.PreferPhysicalWebClick(WindowActionKind.EXPAND, true) &&
    !WindowAction.PreferPhysicalWebClick(WindowActionKind.TOGGLE, true) &&
    !WindowAction.PreferPhysicalWebClick(null, true),
    "windows.web_invoke_precedes_unreliable_uia_provider_without_changing_native_disclosures");
var expectedClick = new WindowActionRequest(testWindowId, freshElementId,
    WindowActionKind.CLICK, null, ExpectedName: "CI_CLICKED",
    ExpectedChange: WindowExpectedChange.APPEARS);
var notObserved = WindowAction.RequireVerifiedActionEffect(expectedClick,
    new(WindowActionCode.COMPLETED, ExpectedStateObserved: false,
        ObservationState: WindowObservationState.NOT_OBSERVED));
Check(notObserved.Status == WindowActionCode.ACTION_FAILED_UNKNOWN_EFFECT &&
    notObserved.EffectUnknown && notObserved.EffectState == WindowEffectState.EFFECT_UNKNOWN &&
    notObserved.FailureStage == WindowFailureStage.VERIFY_EFFECT &&
    WindowAction.RequireVerifiedActionEffect(expectedClick,
        new(WindowActionCode.COMPLETED, ExpectedStateObserved: true)).Status == WindowActionCode.COMPLETED,
    "windows.semantic_click_requires_requested_effect_not_provider_acknowledgement");
var invalidAction = await WindowTools.InvokeElement("bad", "bad", WindowActionKind.INVOKE, confirm: true);
Check(invalidAction.Status == WindowActionCode.INVALID_ARGUMENT, "windows.action_reject_bad_ref_before_ipc");
var missingConfirmation = await WindowTools.InvokeElement(testWindowId, freshElementId,
    WindowActionKind.INVOKE, confirm: false);
Check(missingConfirmation.Status == WindowActionCode.CONFIRMATION_REQUIRED,
    "windows.action_supports_explicit_confirmation_opt_out");
// This is an owned throwaway fixture. No real user window or screen pixels are read.
var uiaDesktop = DesktopCapture.ObserveDesktop(Process.GetCurrentProcess().SessionId);
if (uiaDesktop.Code == CaptureCode.COMPLETED)
{
    var ready = new TaskCompletionSource<(IntPtr Handle, System.Windows.Forms.Form Form, System.Windows.Forms.TextBox Input)>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    int clickCount = 0;
    int deepClickCount = 0;
    int pagedClickCount = 0;
    int keyboardClickCount = 0;
    int asyncDispatchCount = 0;
    int titleDispatchCount = 0;
    int disabledSaveClickCount = 0;
    int gestureHoverCount = 0;
    int gestureLeftDownCount = 0;
    int gestureRightDownCount = 0;
    int scrollWheelCount = 0;
    System.Windows.Forms.Form? testCover = null;
    var uiThread = new Thread(() =>
    {
        try
        {
            using var form = new System.Windows.Forms.Form { Text = "CI_UIA_OWNED_WINDOW", Width = 330, Height = 600 };
            using var button = new System.Windows.Forms.Button
                { Text = "CI_UIA_OWNED_BUTTON", AccessibleName = "CI_UIA_OWNED_BUTTON", Width = 250 };
            using var input = new System.Windows.Forms.TextBox
                { Text = "CI_UIA_SECRET_FIXTURE", AccessibleName = "CI_UIA_INPUT_NAME", Top = 40, Width = 250 };
            using var checkbox = new System.Windows.Forms.CheckBox
                { Text = "CI_UIA_OWNED_CHECKBOX", AccessibleName = "CI_UIA_OWNED_CHECKBOX", Top = 76 };
            using var radio = new System.Windows.Forms.RadioButton
                { Text = "CI_UIA_OWNED_RADIO", AccessibleName = "CI_UIA_OWNED_RADIO", Top = 108 };
            using var cover = new System.Windows.Forms.Form
                { Text = "CI_UIA_COVER", FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                  TopMost = true, ShowInTaskbar = false };
            button.Click += (_, _) =>
            {
                if (Interlocked.Increment(ref clickCount) == 2)
                {
                    button.Text = "CI_UIA_CLICKED";
                    button.AccessibleName = "CI_UIA_CLICKED";
                }
            };
            form.Controls.Add(button);
            form.Controls.Add(input);
            form.Controls.Add(checkbox);
            form.Controls.Add(radio);
            var keyboardButton = new System.Windows.Forms.Button
                { Text = "CI_UIA_KEYBOARD_BUTTON", AccessibleName = "CI_UIA_KEYBOARD_BUTTON",
                  Top = 205, Width = 250 };
            keyboardButton.Click += (_, _) =>
            {
                Interlocked.Increment(ref keyboardClickCount);
                keyboardButton.Text = "CI_UIA_KEY_ACCEPTED";
                keyboardButton.AccessibleName = "CI_UIA_KEY_ACCEPTED";
            };
            form.Controls.Add(keyboardButton);
            var delayedButton = new System.Windows.Forms.Button
                { Text = "CI_UIA_PENDING", AccessibleName = "CI_UIA_PENDING",
                  Top = 244, Width = 250 };
            form.Controls.Add(delayedButton);
            var asyncButton = new System.Windows.Forms.Button
                { Text = "CI_UIA_ASYNC_START", AccessibleName = "CI_UIA_ASYNC_START",
                  Top = 280, Width = 250 };
            asyncButton.Click += (_, _) =>
            {
                Interlocked.Increment(ref asyncDispatchCount);
                var timer = new System.Windows.Forms.Timer { Interval = 1700 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    asyncButton.Text = "CI_UIA_ASYNC_DONE";
                    asyncButton.AccessibleName = "CI_UIA_ASYNC_DONE";
                    timer.Dispose();
                };
                timer.Start();
            };
            form.Controls.Add(asyncButton);
            var titleButton = new System.Windows.Forms.Button
                { Text = "CI_UIA_TITLE_START", AccessibleName = "CI_UIA_TITLE_START",
                  Top = 316, Width = 250 };
            titleButton.Click += (_, _) =>
            {
                Interlocked.Increment(ref titleDispatchCount);
                var timer = new System.Windows.Forms.Timer { Interval = 800 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    form.Text = "CI_UIA_TITLE_READY — Fixture";
                    timer.Dispose();
                };
                timer.Start();
            };
            form.Controls.Add(titleButton);
            using var saveButton = new System.Windows.Forms.Button
                { Text = "CI_UIA_SAVE", AccessibleName = "CI_UIA_SAVE", Top = 347,
                  Width = 250, Enabled = false };
            saveButton.Click += (_, _) => Interlocked.Increment(ref disabledSaveClickCount);
            form.Controls.Add(saveButton);
            input.TextChanged += (_, _) =>
            {
                if (input.Text != "CI_UIA_UPDATED_VALUE") return;
                var timer = new System.Windows.Forms.Timer { Interval = 250 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    saveButton.Enabled = true;
                    timer.Dispose();
                };
                timer.Start();
            };
            using var expandable = new System.Windows.Forms.ComboBox
                { Name = "CI_UIA_EXPANDABLE", AccessibleName = "CI_UIA_EXPANDABLE",
                  DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList,
                  Top = 382, Width = 250 };
            expandable.Items.AddRange(["Option A", "Option B"]);
            expandable.SelectedIndex = 0;
            form.Controls.Add(expandable);
            // Every physical pointer event below is delivered only to this
            // disposable CI-owned window. Counters prove actual OS delivery,
            // not merely a successful SendInput return value.
            using var gestureButton = new System.Windows.Forms.Button
                { Text = "CI_UIA_GESTURE_BUTTON", AccessibleName = "CI_UIA_GESTURE_BUTTON",
                  Left = 10, Top = 425, Width = 250, Height = 30 };
            gestureButton.MouseEnter += (_, _) => Interlocked.Increment(ref gestureHoverCount);
            gestureButton.MouseDown += (_, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left)
                    Interlocked.Increment(ref gestureLeftDownCount);
                if (e.Button == System.Windows.Forms.MouseButtons.Right)
                    Interlocked.Increment(ref gestureRightDownCount);
            };
            form.Controls.Add(gestureButton);
            using var scrollList = new System.Windows.Forms.ListBox
                { AccessibleName = "CI_UIA_SCROLL_LIST", Left = 10, Top = 470,
                  Width = 250, Height = 60 };
            scrollList.Items.AddRange(Enumerable.Range(0, 40)
                .Select(i => (object)$"Owned item {i}").ToArray());
            form.Controls.Add(scrollList);
            // A custom mouse-wheel widget changes an independent owned view.
            // The pointer target is the already-addressable gesture button;
            // the list's TopIndex proves the resulting UI effect.
            gestureButton.MouseWheel += (_, e) =>
            {
                Interlocked.Increment(ref scrollWheelCount);
                if (e.Delta < 0) scrollList.TopIndex = Math.Min(scrollList.Items.Count - 1,
                    scrollList.TopIndex + 2);
            };
            // Emulate Chromium's deeply nested ControlView hierarchy without a browser dependency.
            var nested = new System.Windows.Forms.Panel
                { Left = 10, Top = 142, Width = 275, Height = 42,
                  AccessibleRole = System.Windows.Forms.AccessibleRole.Grouping };
            form.Controls.Add(nested);
            for (int level = 0; level < 7; level++)
            {
                var inner = new System.Windows.Forms.Panel
                    { Dock = System.Windows.Forms.DockStyle.Fill,
                      AccessibleRole = System.Windows.Forms.AccessibleRole.Grouping };
                nested.Controls.Add(inner);
                nested = inner;
            }
            var deepButton = new System.Windows.Forms.Button
                { Text = "CI_UIA_DEEP_BUTTON", AccessibleName = "CI_UIA_DEEP_BUTTON",
                  Dock = System.Windows.Forms.DockStyle.Fill };
            deepButton.Click += (_, _) => Interlocked.Increment(ref deepClickCount);
            nested.Controls.Add(deepButton);
            // More than one page of UIA controls. An addressable action must
            // work even when the target lies beyond the first 100 elements.
            var many = new System.Windows.Forms.Panel
                { Left = 292, Top = 10, Width = 30, Height = 80, AutoScroll = true };
            form.Controls.Add(many);
            for (int i = 0; i < 125; i++)
            {
                var item = new System.Windows.Forms.Button
                {
                    Text = i == 124 ? "CI_UIA_PAGED_BUTTON" : $"CI_UIA_PAGE_{i}",
                    AccessibleName = i == 124 ? "CI_UIA_PAGED_BUTTON" : $"CI_UIA_PAGE_{i}",
                    Left = 0, Top = i * 25, Width = 50, Height = 24
                };
                if (i == 124) item.Click += (_, _) => Interlocked.Increment(ref pagedClickCount);
                many.Controls.Add(item);
            }
            form.Shown += (_, _) =>
            {
                testCover = cover;
                cover.BackColor = System.Drawing.Color.Magenta;
                cover.Bounds = form.Bounds;
                cover.Show();
                ready.TrySetResult((form.Handle, form, input));
            };
            System.Windows.Forms.Application.Run(form);
        }
        catch (Exception error) { ready.TrySetException(error); }
    }) { IsBackground = true };
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    System.Windows.Forms.Form? ownedForm = null;
    try
    {
        // This is only the isolated CI fixture's cold WinForms startup budget,
        // not an action/worker deadline. Hosted Windows can delay first Shown
        // during unrelated UIA provider initialization.
        var created = await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        ownedForm = created.Form;
        using var current = Process.GetCurrentProcess();
        var realWindowId = WindowCatalog.FormatId(new WindowIdentity(created.Handle.ToInt64(),
            current.Id, current.StartTime.ToFileTimeUtc(), current.SessionId));
        Check(WindowCatalog.TryReadTitle(realWindowId, current.SessionId, out var initialTitle) &&
            initialTitle == "CI_UIA_OWNED_WINDOW",
            "windows.read_title_of_exact_verified_window_identity");
        var geometry=WindowManager.Execute(realWindowId,WindowManageAction.MOVE,
            created.Form.Left,created.Form.Top,null,null,current.SessionId);
        Check(geometry.Status==WindowManageCode.COMPLETED &&
            geometry.EffectState==WindowEffectState.EFFECT_VERIFIED &&
            geometry.After?.X==created.Form.Left && geometry.After?.Y==created.Form.Top,
            "windows.manage_real_window_move_and_readback");
        var result = await Task.Run(() => WindowInspector.InspectCore(created.Handle, 3, 40, realWindowId))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(result.Status == WindowInspectCode.COMPLETED &&
            result.Elements.Any(e => e.Name == "CI_UIA_OWNED_BUTTON") &&
            result.Elements.Any(e => e.Role.Contains("Edit", StringComparison.Ordinal) &&
                                     e.Name == "[redacted]") &&
            result.Elements.All(e => !e.Name.Contains("CI_UIA_SECRET_FIXTURE", StringComparison.Ordinal)),
            "windows.inspect_owned_occluded_winforms_tree_and_redaction");
        Check(result.ElementRefExpiresUtc > DateTimeOffset.UtcNow &&
            result.Elements.All(e => WindowInspector.TryParseElementRef(e.ElementId, out _)),
            "windows.action_inspection_returns_fresh_selectors");
        var detailed = await Task.Run(() => WindowInspector.InspectCore(created.Handle, 3, 10,
            realWindowId, includeDetails: true)).WaitAsync(TimeSpan.FromSeconds(5));
        var detailedButton = detailed.Elements.Single(e => e.Name == "CI_UIA_OWNED_BUTTON");
        Check(detailedButton.Bounds is { Width: > 0, Height: > 0 } &&
            detailedButton.KeyboardFocusable == true && !detailedButton.Password,
            "windows.inspect_opt_in_geometry_and_focus_details");
        var matched = await Task.Run(() => WindowInspector.InspectCore(created.Handle, 3, 10,
            realWindowId, nameContains: "owned_button", roleFilter: WindowRoleFilter.BUTTON,
            includeDetails: true)).WaitAsync(TimeSpan.FromSeconds(5));
        Check(matched.Status == WindowInspectCode.COMPLETED &&
            matched.Elements.Length == 1 && matched.Elements[0].Name == "CI_UIA_OWNED_BUTTON" &&
            matched.Elements[0].Bounds is not null && matched.Elements[0].Path.StartsWith("0."),
            "windows.find_semantic_name_role_and_details_on_occluded_window");
        var missing = await Task.Run(() => WindowInspector.InspectCore(created.Handle, 3, 10,
            realWindowId, nameContains: "CI_UIA_DOES_NOT_EXIST", waitMs: 150))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(missing.Status == WindowInspectCode.STATE_NOT_OBSERVED && missing.Elements.Length == 0,
            "windows.wait_for_missing_state_returns_explicit_timeout");
        created.Form.Invoke(new Action(() =>
        {
            var target = created.Form.Controls.OfType<System.Windows.Forms.Button>()
                .Single(b => b.AccessibleName == "CI_UIA_PENDING");
            target.Text = "CI_UIA_DELAYED_READY";
            target.AccessibleName = "CI_UIA_DELAYED_READY";
        }));
        var delayed = await Task.Run(() => WindowInspector.InspectCore(created.Handle, 3, 10,
            realWindowId, nameContains: "CI_UIA_DELAYED_READY", roleFilter: WindowRoleFilter.BUTTON,
            waitMs: 1200)).WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine($"DELAYED_SEARCH status={delayed.Status} nodes={delayed.Elements.Length}");
        Check(delayed.Status == WindowInspectCode.COMPLETED &&
            delayed.Elements.Length == 1 && delayed.Elements[0].Name == "CI_UIA_DELAYED_READY",
            "windows.wait_for_changed_react_style_element_without_focus");
        var located=await Task.Run(()=>WindowAction.InvokeInWorker(new WindowActionRequest(
            realWindowId,"locator",WindowActionKind.INVOKE,null,
            LocatorName:"CI_UIA_DELAYED_READY",LocatorRole:WindowRoleFilter.BUTTON)))
            .WaitAsync(TimeSpan.FromSeconds(8));
        Check(located.Status==WindowActionCode.COMPLETED && located.TargetVerified,
            "windows.locator_unique_match_dispatches_only_once");
        int clicksBeforeResolve = Volatile.Read(ref clickCount);
        var resolvedOnly = await Task.Run(() => WindowAction.InvokeInWorker(new WindowActionRequest(
            realWindowId, "locator", WindowActionKind.INVOKE, null,
            LocatorName: "CI_UIA_DELAYED_READY", LocatorRole: WindowRoleFilter.BUTTON,
            ResolveOnly: true))).WaitAsync(TimeSpan.FromSeconds(8));
        Check(resolvedOnly.Status == WindowActionCode.LOCATOR_RESOLVED &&
            WindowInspector.TryParseElementRef(resolvedOnly.ResolvedElementId, out _) &&
            WindowInspector.TryParsePath(resolvedOnly.ResolvedElementPath, out _) &&
            Volatile.Read(ref clickCount) == clicksBeforeResolve,
            "windows.disposable_locator_resolves_without_dispatching_click");
        var strictBackground = await Task.Run(() => WindowAction.InvokeInWorker(
            new WindowActionRequest(realWindowId, "locator", WindowActionKind.INVOKE, null,
                LocatorName: "CI_UIA_DELAYED_READY", LocatorRole: WindowRoleFilter.BUTTON,
                InputMode: WindowInputMode.CDP_ONLY))).WaitAsync(TimeSpan.FromSeconds(8));
        Check(strictBackground.Status == WindowActionCode.CDP_ONLY_UNAVAILABLE &&
            strictBackground.EffectState == WindowEffectState.NO_EFFECT &&
            strictBackground.ExecutionMethod is null && strictBackground.SafeToRetry &&
            Volatile.Read(ref clickCount) == clicksBeforeResolve,
            "windows.cdp_only_native_button_fails_without_provider_invoke_or_focus");
        var ambiguous=await Task.Run(()=>WindowAction.InvokeInWorker(new WindowActionRequest(
            realWindowId,"locator",WindowActionKind.INVOKE,null,
            LocatorRole:WindowRoleFilter.BUTTON))).WaitAsync(TimeSpan.FromSeconds(8));
        Check(ambiguous.Status==WindowActionCode.LOCATOR_AMBIGUOUS,
            "windows.locator_rejects_ambiguous_button_without_dispatch");
        var absent=await Task.Run(()=>WindowAction.InvokeInWorker(new WindowActionRequest(
            realWindowId,"locator",WindowActionKind.INVOKE,null,
            LocatorAutomationId:"CI_ABSENT_AUTOMATION_ID"))).WaitAsync(TimeSpan.FromSeconds(8));
        Check(absent.Status==WindowActionCode.LOCATOR_NOT_FOUND,
            "windows.locator_rejects_missing_target_without_dispatch");
        var nativeRoot = System.Windows.Automation.AutomationElement.FromHandle(created.Handle);
        var blockedPointer = await Task.Run(() => WindowPhysicalClick.Click(
            IntPtr.Zero, nativeRoot, new WindowActionRequest(realWindowId,
                delayed.Elements[0].ElementId, WindowActionKind.CLICK, null), Stopwatch.StartNew()))
            .WaitAsync(TimeSpan.FromSeconds(5));
        var blockedKey = await Task.Run(() => WindowPhysicalClick.PressKey(
            IntPtr.Zero, nativeRoot, new WindowActionRequest(realWindowId,
                delayed.Elements[0].ElementId, WindowActionKind.KEY_PRESS, null,
                Key: WindowKey.SPACE), Stopwatch.StartNew())).WaitAsync(TimeSpan.FromSeconds(5));
        Check(blockedPointer.Status == WindowActionCode.FOREGROUND_INPUT_DISALLOWED &&
            blockedKey.Status == WindowActionCode.FOREGROUND_INPUT_DISALLOWED &&
            blockedPointer.EffectState == WindowEffectState.NO_EFFECT &&
            blockedKey.EffectState == WindowEffectState.NO_EFFECT &&
            blockedPointer.ExecutionMethod is null && blockedKey.ExecutionMethod is null &&
            Volatile.Read(ref keyboardClickCount) == 0,
            "windows.background_policy_never_dispatches_native_mouse_or_keyboard");
        var rejectedKeyboard = await Task.Run(() => WindowPhysicalClick.PressKey(
            IntPtr.Zero, System.Windows.Automation.AutomationElement.FromHandle(created.Handle),
            new WindowActionRequest(realWindowId, delayed.Elements[0].ElementId,
                WindowActionKind.KEY_PRESS, null, Key: WindowKey.SPACE,
                InputMode: WindowInputMode.ALLOW_FOREGROUND_INPUT), Stopwatch.StartNew()))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(rejectedKeyboard.Status == WindowActionCode.FOCUS_UNAVAILABLE &&
            Volatile.Read(ref keyboardClickCount) == 0,
            "windows.key_press_rejects_invalid_window_before_input");
        var rejectedText = await Task.Run(() => WindowPhysicalClick.TypeText(
            IntPtr.Zero, System.Windows.Automation.AutomationElement.FromHandle(created.Handle),
            new WindowActionRequest(realWindowId, delayed.Elements[0].ElementId,
                WindowActionKind.TYPE_TEXT, "test",
                InputMode: WindowInputMode.ALLOW_FOREGROUND_INPUT), Stopwatch.StartNew()))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(rejectedText.Status == WindowActionCode.FOCUS_UNAVAILABLE,
            "windows.type_text_requires_exact_focus_not_a_role_whitelist");
        if (Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported())
        {
            var image = await WindowCapture.CaptureCoreAsync(created.Handle, realWindowId, 640)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Check(image.Result.Status == WindowCaptureCode.COMPLETED &&
                image.Png is { Length: > 100 } && image.Png.Length <= 2_000_000 &&
                image.Png[0] == 137 && image.Png[1] == 80 &&
                image.Result.Width is > 0 and <= 640,
                "windows.capture_owned_occluded_winforms_png");
            using var pngData = new MemoryStream(image.Png!);
            using var capturedBitmap = new System.Drawing.Bitmap(pngData);
            int magenta = 0;
            for (int y = 0; y < capturedBitmap.Height; y += 10)
                for (int x = 0; x < capturedBitmap.Width; x += 10)
                {
                    var pixel = capturedBitmap.GetPixel(x, y);
                    if (pixel.R > 230 && pixel.G < 40 && pixel.B > 230) magenta++;
                }
            Check(magenta == 0, "windows.capture_target_not_magenta_cover");
        }
        else Console.WriteLine("NOT_TESTED windows.capture_owned_occluded_winforms_png wgc_unsupported");
        var buttonTarget = result.Elements.Single(e => e.Name == "CI_UIA_OWNED_BUTTON");
        var click = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, buttonTarget.ElementId, WindowActionKind.INVOKE, null)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(click.Status == WindowActionCode.COMPLETED &&
            Volatile.Read(ref clickCount) == 1 &&
            click.ExecutionMethod == WindowExecutionMethod.UIA_PATTERN,
            "windows.action_invoke_occluded_button");
        Check(click.ForegroundChanged == false && click.TargetActivated == false,
            "windows.action_invoke_preserves_foreground");
        var asyncTree = await Task.Run(() => WindowInspector.InspectCore(created.Handle, 2, 50,
            realWindowId, nameContains: "CI_UIA_ASYNC_START", roleFilter: WindowRoleFilter.BUTTON));
        var asyncTarget = asyncTree.Elements.Single(e => e.Name == "CI_UIA_ASYNC_START");
        var asyncClock = Stopwatch.StartNew();
        var asyncResult = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, asyncTarget.ElementId, WindowActionKind.INVOKE,
                null, ExpectedName: "CI_UIA_ASYNC_DONE", ExpectedChange: WindowExpectedChange.APPEARS,
                ObservationTimeoutMs: 5000))).WaitAsync(TimeSpan.FromSeconds(7));
        Check(asyncResult.EffectState == WindowEffectState.EFFECT_VERIFIED &&
            asyncResult.ObservationState == WindowObservationState.OBSERVED &&
            asyncClock.ElapsedMilliseconds >= 1300 && Volatile.Read(ref asyncDispatchCount) == 1,
            "windows.async_provider_effect_observed_after_one_second_without_redispach");
        created.Form.Invoke(new Action(() => testCover!.Text = "CI_UIA_TITLE_READY — Fixture"));
        Check(WindowCatalog.List(100, current.SessionId).Windows.Any(w =>
                w.WindowId != realWindowId && w.Title == "CI_UIA_TITLE_READY — Fixture") &&
            !WindowAction.WindowTitlePresent(realWindowId, "CI_UIA_TITLE_READY"),
            "windows.title_observation_rejects_matching_unrelated_window");
        var titleTree = await Task.Run(() => WindowInspector.InspectCore(created.Handle, 3, 10,
            realWindowId, nameContains: "CI_UIA_TITLE_START", roleFilter: WindowRoleFilter.BUTTON));
        var titleTarget = titleTree.Elements.Single(e => e.Name == "CI_UIA_TITLE_START");
        var titleClock = Stopwatch.StartNew();
        var titleResult = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, titleTarget.ElementId, WindowActionKind.INVOKE,
                null, ExpectedName: "CI_UIA_TITLE_READY", ExpectedChange: WindowExpectedChange.APPEARS,
                ExpectedKind: WindowExpectedKind.WINDOW_TITLE, ObservationTimeoutMs: 4000)))
            .WaitAsync(TimeSpan.FromSeconds(6));
        Check(titleResult.EffectState == WindowEffectState.EFFECT_VERIFIED &&
            titleResult.ObservationState == WindowObservationState.OBSERVED &&
            titleClock.ElapsedMilliseconds >= 600 && Volatile.Read(ref titleDispatchCount) == 1 &&
            WindowAction.WindowTitlePresent(realWindowId, "CI_UIA_TITLE_READY"),
            "windows.async_title_change_observed_on_target_hwnd_exactly_once");
        // This native button is still disabled. The normal route must not
        // invoke its provider, while a direct test-only native UIA2 call
        // diagnoses how the actual Windows provider rejects Invoke.
        var disabledTree = await Task.Run(() => WindowInspector.InspectCore(created.Handle,
            3, 20, realWindowId, nameContains: "CI_UIA_SAVE",
            roleFilter: WindowRoleFilter.BUTTON)).WaitAsync(TimeSpan.FromSeconds(5));
        var disabledButton = disabledTree.Elements.Single(e => e.Name == "CI_UIA_SAVE");
        var disabledRequest = new WindowActionRequest(realWindowId,
            disabledButton.ElementId, WindowActionKind.INVOKE, null);
        var disabledPreflight = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            disabledRequest)).WaitAsync(TimeSpan.FromSeconds(5));
        Check(disabledPreflight.Status == WindowActionCode.DISABLED &&
            disabledPreflight.EnabledBefore == false && disabledPreflight.TargetVerified &&
            disabledPreflight.TargetIdentity == disabledButton.ElementId &&
            disabledPreflight.FailureStage == WindowFailureStage.PREPARE_UIA &&
            disabledPreflight.EffectState == WindowEffectState.NO_EFFECT &&
            disabledPreflight.ProviderOutcome == WindowProviderOutcome.NOT_DISPATCHED &&
            !disabledPreflight.EffectUnknown &&
            Volatile.Read(ref disabledSaveClickCount) == 0,
            "windows.disabled_native_button_rejected_before_provider_dispatch");
        Check(WindowInspector.TryParsePath(disabledButton.Path, out var disabledPath),
            "windows.disabled_native_button_has_exact_uia_path");
        var nativeDisabledRoot = System.Windows.Automation.AutomationElement.FromHandle(created.Handle);
        var nativeDisabled = nativeDisabledRoot.FindFirst(System.Windows.Automation.TreeScope.Descendants,
            new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.NameProperty, "CI_UIA_SAVE"));
        Check(nativeDisabled is not null && !nativeDisabled.Current.IsEnabled,
            "windows.disabled_native_button_reports_current_isenabled_false");
        var providerDisabled = await Task.Run(() => WindowActionNoFocus.Invoke(created.Handle,
            disabledPath, nativeDisabled!.GetRuntimeId(), disabledRequest, Stopwatch.StartNew()))
            .WaitAsync(TimeSpan.FromSeconds(5));
        // Actual WinForms providers can report success AND dispatch a Click
        // even with IsEnabled=false. This is why the production preflight
        // must reject before entering the provider. The direct test-only
        // call is dispatched exactly once, with independent event readback.
        int saveClicks = Volatile.Read(ref disabledSaveClickCount);
        bool enabledAfter = nativeDisabled!.Current.IsEnabled;
        Console.WriteLine($"CI_UIA_DISABLED_PROVIDER_RESULT status={providerDisabled.Status} " +
            $"stage={providerDisabled.FailureStage} hresult={providerDisabled.HResult} " +
            $"clicks={saveClicks} enabled_after={enabledAfter}");
        Check(saveClicks is >= 0 and <= 1 && !enabledAfter,
            "windows.disabled_native_provider_exact_event_count_and_enabled_readback");
        if (providerDisabled.Status == WindowActionCode.UIA_ELEMENTNOTENABLED_UNKNOWN_EFFECT)
        {
            Check(providerDisabled.HResult == "80040200" &&
                providerDisabled.ExecutionMethod == WindowExecutionMethod.UIA_PATTERN &&
                providerDisabled.FailureStage == WindowFailureStage.DISPATCH_UIA &&
                providerDisabled.ProviderOutcome == WindowProviderOutcome.REJECTED &&
                providerDisabled.EffectState == WindowEffectState.EFFECT_UNKNOWN &&
                !providerDisabled.SafeToRetry,
                "windows.real_uia_provider_elementnotenabled_hresult_is_typed_no_replay");
            var reconciledDisabled = WindowAction.ReconcileObserved(providerDisabled, saveClicks == 1);
            Check(saveClicks == 0 || reconciledDisabled.Status ==
                WindowActionCode.EFFECT_VERIFIED_PROVIDER_REJECTED,
                "windows.provider_rejection_with_real_event_is_reconciled_without_replay");
        }
        else if (providerDisabled.Status == WindowActionCode.COMPLETED)
        {
            Check(providerDisabled.ProviderOutcome == WindowProviderOutcome.ACKNOWLEDGED &&
                providerDisabled.EffectState == WindowEffectState.EFFECT_UNKNOWN &&
                providerDisabled.ExecutionMethod == WindowExecutionMethod.UIA_PATTERN,
                "windows.uia_provider_ack_alone_does_not_prove_effect");
            Check(WindowAction.ReconcileObserved(providerDisabled, saveClicks == 1)
                    .EffectState == (saveClicks == 1 ? WindowEffectState.EFFECT_VERIFIED :
                        WindowEffectState.EFFECT_UNKNOWN),
                "windows.disabled_native_provider_effect_requires_independent_event");
            Console.WriteLine("NOT_TESTED / EXTERNAL_BLOCKER " +
                "windows.real_uia_provider_elementnotenabled reason=PROVIDER_ACK");
        }
        else Console.WriteLine("NOT_TESTED / EXTERNAL_BLOCKER " +
            $"windows.real_uia_provider_elementnotenabled reason={providerDisabled.Status} " +
            $"stage={providerDisabled.FailureStage} hresult={providerDisabled.HResult}");
        var inputTarget = result.Elements.Single(e => e.Role.Contains("Edit", StringComparison.Ordinal));
        var edit = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, inputTarget.ElementId, WindowActionKind.SET_VALUE,
                "CI_UIA_UPDATED_VALUE", ExpectedName: "CI_UIA_INPUT_NAME",
                ExpectedChange: WindowExpectedChange.APPEARS,
                ExpectedKind: WindowExpectedKind.UIA_VALUE,
                ExpectedValue: "CI_UIA_UPDATED_VALUE", ObservationTimeoutMs: 3500,
                ExpectedEnabledName: "CI_UIA_SAVE"))).WaitAsync(TimeSpan.FromSeconds(6));
        var edited = (string)created.Form.Invoke(new Func<string>(() => created.Input.Text));
        Check(edit.Status == WindowActionCode.COMPLETED && edited == "CI_UIA_UPDATED_VALUE" &&
            edit.EffectState == WindowEffectState.EFFECT_VERIFIED &&
            edit.ExpectedEnabledObserved == true &&
            (bool)created.Form.Invoke(new Func<bool>(() => created.Form.Controls
                .OfType<System.Windows.Forms.Button>().Single(b => b.AccessibleName == "CI_UIA_SAVE").Enabled)),
            "windows.action_set_value_verifies_field_and_delayed_save_enabled");
        Check(edit.ForegroundChanged == false && edit.TargetActivated == false,
            "windows.action_set_value_preserves_foreground");
        var repeatedEdit = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, inputTarget.ElementId, WindowActionKind.SET_VALUE,
                "CI_UIA_UPDATED_VALUE", ExpectedName: "CI_UIA_INPUT_NAME",
                ExpectedChange: WindowExpectedChange.APPEARS,
                ExpectedKind: WindowExpectedKind.UIA_VALUE,
                ExpectedValue: "CI_UIA_UPDATED_VALUE"))).WaitAsync(TimeSpan.FromSeconds(5));
        Check(repeatedEdit.Status == WindowActionCode.EXPECTED_STATE_ALREADY_TRUE &&
            (string)created.Form.Invoke(new Func<string>(() => created.Input.Text)) == "CI_UIA_UPDATED_VALUE",
            "windows.value_precondition_prevents_repeated_dispatch");
        var checkboxTarget = result.Elements.Single(e => e.Role.Contains("CheckBox", StringComparison.Ordinal));
        var toggle = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, checkboxTarget.ElementId, WindowActionKind.TOGGLE, null)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        bool checkedAfter = (bool)created.Form.Invoke(new Func<bool>(() =>
            created.Form.Controls.OfType<System.Windows.Forms.CheckBox>().Single().Checked));
        Check(toggle.Status == WindowActionCode.COMPLETED && toggle.ForegroundChanged == false &&
            toggle.TargetActivated == false &&
            checkedAfter, "windows.action_toggle_occluded_checkbox_without_focus");
        var radioTarget = result.Elements.Single(e => e.Role.Contains("RadioButton", StringComparison.Ordinal));
        var select = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, radioTarget.ElementId, WindowActionKind.SELECT, null)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        bool selectedAfter = (bool)created.Form.Invoke(new Func<bool>(() =>
            created.Form.Controls.OfType<System.Windows.Forms.RadioButton>().Single().Checked));
        Check(select.Status == WindowActionCode.COMPLETED && select.ForegroundChanged == false &&
            select.TargetActivated == false &&
            selectedAfter, "windows.action_select_occluded_radio_without_focus");
        var expandTree = await Task.Run(() => WindowInspector.InspectCore(created.Handle, 3, 10,
            realWindowId, nameContains: "CI_UIA_EXPANDABLE", roleFilter: WindowRoleFilter.COMBO_BOX));
        var expandTarget = expandTree.Elements.Single(e => e.Name == "CI_UIA_EXPANDABLE");
        Check(expandTarget.Patterns.Contains("ExpandCollapsePatternIdentifiers.Pattern"),
            "windows.expand_collapse_native_pattern_available_on_combo_box");
        var expanded = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, expandTarget.ElementId, WindowActionKind.EXPAND,
                null, ObservationTimeoutMs: 3000))).WaitAsync(TimeSpan.FromSeconds(5));
        Check(expanded.EffectState == WindowEffectState.EFFECT_VERIFIED &&
            expanded.ObservationState == WindowObservationState.OBSERVED &&
            expanded.Status == WindowActionCode.COMPLETED,
            "windows.expand_native_uia_pattern_with_verified_state");
        var expandAgain = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, expandTarget.ElementId, WindowActionKind.EXPAND,
                null))).WaitAsync(TimeSpan.FromSeconds(5));
        Check(expandAgain.Status == WindowActionCode.EXPECTED_STATE_ALREADY_TRUE,
            "windows.expand_precondition_rejects_duplicate_dispatch");
        var collapsed = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, expandTarget.ElementId, WindowActionKind.COLLAPSE,
                null, ObservationTimeoutMs: 3000))).WaitAsync(TimeSpan.FromSeconds(5));
        Check(collapsed.EffectState == WindowEffectState.EFFECT_VERIFIED &&
            collapsed.ObservationState == WindowObservationState.OBSERVED &&
            collapsed.Status == WindowActionCode.COMPLETED,
            "windows.collapse_native_uia_pattern_with_verified_state");
        var clickExpanded = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, expandTarget.ElementId, WindowActionKind.CLICK,
                null, expandTarget.Path, ObservationTimeoutMs: 3000))).WaitAsync(TimeSpan.FromSeconds(5));
        Check(clickExpanded.Status == WindowActionCode.COMPLETED &&
            clickExpanded.EffectState == WindowEffectState.EFFECT_VERIFIED &&
            clickExpanded.ObservationState == WindowObservationState.OBSERVED,
            "windows.click_expands_native_disclosure_without_physical_foreground_input");
        var expandedClickTree = await Task.Run(() => WindowInspector.InspectCore(created.Handle,
            3, 10, realWindowId, nameContains: "CI_UIA_EXPANDABLE", roleFilter: WindowRoleFilter.COMBO_BOX));
        var expandedClickTarget = expandedClickTree.Elements.Single(e => e.Name == "CI_UIA_EXPANDABLE");
        var clickCollapsed = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, expandedClickTarget.ElementId, WindowActionKind.CLICK,
                null, expandedClickTarget.Path, ObservationTimeoutMs: 3000))).WaitAsync(TimeSpan.FromSeconds(5));
        Check(clickCollapsed.Status == WindowActionCode.COMPLETED &&
            clickCollapsed.EffectState == WindowEffectState.EFFECT_VERIFIED &&
            clickCollapsed.ObservationState == WindowObservationState.OBSERVED,
            "windows.click_collapses_native_disclosure_without_physical_foreground_input");
        var deepTree = await Task.Run(() => WindowInspector.InspectCore(
            created.Handle, WindowInspector.MaxDepth, 100, realWindowId))
            .WaitAsync(TimeSpan.FromSeconds(5));
        var deepTarget = deepTree.Elements.SingleOrDefault(e => e.Name == "CI_UIA_DEEP_BUTTON");
        if (deepTarget is null && deepTree.NextOffset is int nextPage)
        {
            var nextDeepTree = await Task.Run(() => WindowInspector.InspectCore(
                created.Handle, WindowInspector.MaxDepth, 100, realWindowId, offset: nextPage))
                .WaitAsync(TimeSpan.FromSeconds(5));
            deepTarget = nextDeepTree.Elements.SingleOrDefault(e => e.Name == "CI_UIA_DEEP_BUTTON");
        }
        Check(deepTree.Status == WindowInspectCode.COMPLETED && deepTarget is { Depth: > 5 },
            "windows.inspect_deep_controls_under_bounded_tree");
        var deepInvoke = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, deepTarget!.ElementId, WindowActionKind.INVOKE, null)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(deepInvoke.Status == WindowActionCode.COMPLETED &&
            deepInvoke.ForegroundChanged == false && deepInvoke.TargetActivated == false &&
            Volatile.Read(ref deepClickCount) == 1,
            "windows.action_deep_control_without_focus");
        var page1 = await Task.Run(() => WindowInspector.InspectCore(
            created.Handle, WindowInspector.MaxDepth, 100, realWindowId))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(page1.Status == WindowInspectCode.COMPLETED && page1.Truncated &&
            page1.NextOffset == 100, "windows.inspect_bounded_first_page");
        var page2 = await Task.Run(() => WindowInspector.InspectCore(
            created.Handle, WindowInspector.MaxDepth, 100, realWindowId, offset: 100))
            .WaitAsync(TimeSpan.FromSeconds(5));
        var paged = page2.Elements.SingleOrDefault(e => e.Name == "CI_UIA_PAGED_BUTTON");
        Check(page2.Status == WindowInspectCode.COMPLETED && paged is not null &&
            page1.Elements.All(e => e.Name != paged.Name), "windows.inspect_second_page_exposes_deep_sibling");
        if (paged is null) throw new InvalidOperationException("PAGED_TARGET_MISSING");
        var pathInvoke = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, paged.ElementId, WindowActionKind.INVOKE,
                null, paged.Path))).WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine($"PAGED_ACTION status={pathInvoke.Status} detail={pathInvoke.Detail} " +
            $"focus={pathInvoke.ForegroundChanged} clicks={Volatile.Read(ref pagedClickCount)} " +
            $"path={paged.Path}");
        Check(pathInvoke.Status == WindowActionCode.COMPLETED &&
            pathInvoke.ForegroundChanged == false && pathInvoke.TargetActivated == false &&
            Volatile.Read(ref pagedClickCount) == 1,
            "windows.action_paged_element_exact_path_without_focus");
        var parentPath = paged.Path[..paged.Path.LastIndexOf('.')];
        var parent = page1.Elements.Concat(page2.Elements).Single(e => e.Path == parentPath);
        var subtree = await Task.Run(() => WindowInspector.InspectCore(
            created.Handle, WindowInspector.MaxDepth, 100, realWindowId,
            parent.Path, parent.ElementId, offset: 100)).WaitAsync(TimeSpan.FromSeconds(5));
        Check(subtree.Status == WindowInspectCode.COMPLETED &&
            subtree.Elements.Any(e => e.Name == paged.Name),
            "windows.inspect_verified_subtree_page");
        var staleSubtree = await Task.Run(() => WindowInspector.InspectCore(
            created.Handle, WindowInspector.MaxDepth, 10, realWindowId,
            parent.Path, paged.ElementId)).WaitAsync(TimeSpan.FromSeconds(5));
        Check(staleSubtree.Status == WindowInspectCode.STALE_ELEMENT,
            "windows.inspect_reject_mismatched_subtree_ref");
        var stalePath = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, paged.ElementId, WindowActionKind.INVOKE,
                null, parent.Path))).WaitAsync(TimeSpan.FromSeconds(5));
        Check(stalePath.Status == WindowActionCode.STALE_ELEMENT &&
            stalePath.FailureStage == WindowFailureStage.RESOLVE_ELEMENT &&
            stalePath.EffectState == WindowEffectState.NO_EFFECT &&
            stalePath.ProviderOutcome == WindowProviderOutcome.NOT_DISPATCHED &&
            Volatile.Read(ref pagedClickCount) == 1,
            "windows.action_reject_mismatched_exact_path");
        // WinForms may reposition its main form after Shown (DPI/nonclient
        // corrections). Realign the independent topmost cover before using it
        // as a physical occlusion fixture, or the button becomes exposed.
        created.Form.Invoke(new Action(() =>
        {
            testCover!.Bounds = created.Form.Bounds;
            testCover.BringToFront();
        }));
        var freshClickTree = await Task.Run(() => WindowInspector.InspectCore(
            created.Handle, 3, 40, realWindowId)).WaitAsync(TimeSpan.FromSeconds(5));
        var freshEdit = freshClickTree.Elements.Single(e => e.Role.Contains("Edit", StringComparison.Ordinal));
        var blocked = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, freshEdit.ElementId, WindowActionKind.CLICK, null,
                freshEdit.Path, InputMode: WindowInputMode.ALLOW_FOREGROUND_INPUT)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        var overlayState = (string)created.Form.Invoke(new Func<string>(() =>
            $"visible={testCover!.Visible} topmost={testCover.TopMost} " +
            $"cover={testCover.Bounds} main={created.Form.Bounds}"));
        Console.WriteLine($"OCCLUDED_PHYSICAL_FALLBACK status={blocked.Status} stage={blocked.FailureStage} " +
            $"detail={blocked.Detail} {overlayState}");
        Check((blocked.Status is WindowActionCode.TARGET_OCCLUDED or
            WindowActionCode.CLICK_POINT_UNAVAILABLE or WindowActionCode.FOREGROUND_UNAVAILABLE) &&
            (string)created.Form.Invoke(new Func<string>(() => created.Input.Text)) ==
                "CI_UIA_UPDATED_VALUE", "windows.physical_fallback_rejects_occluded_edit_before_input");
        var freshButton = freshClickTree.Elements.Single(e => e.Name == "CI_UIA_OWNED_BUTTON");
        var backgroundHover = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, freshButton.ElementId, WindowActionKind.HOVER,
                null, freshButton.Path))).WaitAsync(TimeSpan.FromSeconds(5));
        var backgroundScroll = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, freshButton.ElementId, WindowActionKind.SCROLL,
                null, freshButton.Path, ScrollDirection: WindowScrollDirection.DOWN,
                ScrollUnits: 2))).WaitAsync(TimeSpan.FromSeconds(5));
        Check(backgroundHover.Status == WindowActionCode.FOREGROUND_INPUT_DISALLOWED &&
            backgroundScroll.Status == WindowActionCode.FOREGROUND_INPUT_DISALLOWED &&
            backgroundHover.EffectState == WindowEffectState.NO_EFFECT &&
            backgroundScroll.EffectState == WindowEffectState.NO_EFFECT &&
            backgroundHover.ProviderOutcome == WindowProviderOutcome.NOT_DISPATCHED &&
            backgroundScroll.ProviderOutcome == WindowProviderOutcome.NOT_DISPATCHED &&
            Volatile.Read(ref clickCount) == 1,
            "windows.background_hover_scroll_reject_physical_input_on_owned_uia_fixture");
        var backgroundClick = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, freshButton.ElementId, WindowActionKind.CLICK,
                null, freshButton.Path, ExpectedName: "CI_UIA_CLICKED",
                ExpectedChange: WindowExpectedChange.APPEARS))).WaitAsync(TimeSpan.FromSeconds(5));
        Check(backgroundClick.Status == WindowActionCode.COMPLETED &&
            backgroundClick.EffectState == WindowEffectState.EFFECT_VERIFIED &&
            backgroundClick.TargetVerified && Volatile.Read(ref clickCount) == 2 &&
            (bool)created.Form.Invoke(new Func<bool>(() => testCover!.Visible)),
            "windows.click_invokes_occluded_uia_button_once_without_foreground_precondition");
        var renamedTree = await Task.Run(() => WindowInspector.InspectCore(
            created.Handle, 3, 40, realWindowId, nameContains: "CI_UIA_CLICKED"));
        var renamedButton = renamedTree.Elements.Single(e => e.Name == "CI_UIA_CLICKED");
        var notRepeated = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, renamedButton.ElementId, WindowActionKind.CLICK,
                null, renamedButton.Path, ExpectedName: "CI_UIA_CLICKED",
                ExpectedChange: WindowExpectedChange.APPEARS))).WaitAsync(TimeSpan.FromSeconds(5));
        Check(notRepeated.Status == WindowActionCode.EXPECTED_STATE_ALREADY_TRUE &&
            Volatile.Read(ref clickCount) == 2,
            "windows.click_does_not_repeat_an_already_observed_effect");
        created.Form.Invoke(new Action(() => testCover!.Hide()));
        var keyboardTree = await Task.Run(() => WindowInspector.InspectCore(created.Handle, 3, 10,
            realWindowId, nameContains: "CI_UIA_KEYBOARD_BUTTON", roleFilter: WindowRoleFilter.BUTTON))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(keyboardTree.Status == WindowInspectCode.COMPLETED &&
            keyboardTree.Elements.Length == 1, "windows.key_press_locates_exact_target");
        var keyTarget = keyboardTree.Elements[0];
        var staleKey = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, keyTarget.ElementId,
                WindowActionKind.KEY_PRESS, null, "0.999", Key: WindowKey.SPACE)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(staleKey.Status == WindowActionCode.STALE_ELEMENT &&
            Volatile.Read(ref keyboardClickCount) == 0,
            "windows.key_press_rejects_stale_path_before_focus_or_input");
        var keyboard = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, keyTarget.ElementId,
                WindowActionKind.KEY_PRESS, null, keyTarget.Path,
                "CI_UIA_KEY_ACCEPTED", WindowExpectedChange.APPEARS, WindowKey.SPACE,
                InputMode: WindowInputMode.ALLOW_FOREGROUND_INPUT)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        if (keyboard.Status is WindowActionCode.FOREGROUND_UNAVAILABLE or WindowActionCode.FOCUS_UNAVAILABLE)
        {
            Check(Volatile.Read(ref keyboardClickCount) == 0,
                "windows.key_press_never_sends_when_focus_cannot_be_proven");
            Console.WriteLine($"NOT_TESTED windows.key_press_owned_button focus={keyboard.Status}");
        }
        else
            Check(keyboard.Status == WindowActionCode.COMPLETED &&
                keyboard.EffectState == WindowEffectState.EFFECT_VERIFIED &&
                keyboard.TargetVerified && Volatile.Read(ref keyboardClickCount) == 1,
                "windows.key_press_owned_button_exactly_once_with_verified_effect");
        var inputTree = await Task.Run(() => WindowInspector.InspectCore(created.Handle, 3, 50,
            realWindowId)).WaitAsync(TimeSpan.FromSeconds(5));
        var nativeTypingTarget = inputTree.Elements.Single(e => e.Role.Contains("Edit", StringComparison.Ordinal));
        var editClick = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, nativeTypingTarget.ElementId,
                WindowActionKind.CLICK, null, nativeTypingTarget.Path,
                InputMode: WindowInputMode.ALLOW_FOREGROUND_INPUT)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        if (editClick.Status is WindowActionCode.FOREGROUND_UNAVAILABLE or
            WindowActionCode.TARGET_OCCLUDED or WindowActionCode.CLICK_POINT_UNAVAILABLE)
            Console.WriteLine($"NOT_TESTED windows.click_owned_edit status={editClick.Status}");
        else
        {
            // Native SendInput reports successful queueing, not synchronous
            // processing by WinForms' message loop on a separate thread.
            // Observe the actual focus with a bound, rather than racing the
            // mouse-up dispatch and treating its delayed effect as failure.
            bool editFocused = false;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                editFocused = (bool)created.Form.Invoke(new Func<bool>(() => created.Input.Focused));
                if (editFocused) break;
                await Task.Delay(25);
            }
            Console.WriteLine($"EDIT_CLICK status={editClick.Status} verified={editClick.TargetVerified} " +
                $"focused={editFocused} stage={editClick.FailureStage} detail={editClick.Detail}");
            Check(editClick.Status == WindowActionCode.COMPLETED && editClick.TargetVerified &&
                editFocused, "windows.click_owned_edit_focuses_exact_field");
        }
        const string typedValue = "CI_UIA_NATIVE_TYPED";
        var typed = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, nativeTypingTarget.ElementId,
                WindowActionKind.TYPE_TEXT, typedValue,
                ExpectedName: "CI_UIA_INPUT_NAME", ExpectedChange: WindowExpectedChange.APPEARS,
                ExpectedKind: WindowExpectedKind.UIA_VALUE, ExpectedValue: typedValue,
                ObservationTimeoutMs: 3000,
                InputMode: WindowInputMode.ALLOW_FOREGROUND_INPUT))).WaitAsync(TimeSpan.FromSeconds(7));
        if (typed.Status is WindowActionCode.FOREGROUND_UNAVAILABLE or WindowActionCode.FOCUS_UNAVAILABLE)
            Console.WriteLine($"NOT_TESTED windows.type_text_owned_edit focus={typed.Status}");
        else
        {
            string actualText = (string)created.Form.Invoke(new Func<string>(() => created.Input.Text));
            Check(typed.Status == WindowActionCode.COMPLETED &&
                typed.EffectState == WindowEffectState.EFFECT_VERIFIED &&
                typed.ExpectedStateObserved == true && actualText == typedValue,
                "windows.type_text_owned_edit_value_observed_after_native_input");
        }
        // Physical input is restricted to the isolated GitHub Windows runner.
        // An API dispatch acknowledgement is not proof: the owned controls
        // count actual WinForms events, and unknown effects are never retried.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" &&
            Environment.GetEnvironmentVariable("RUNNER_OS") == "Windows")
        {
            static bool InputNotAvailable(WindowActionResult action) => action.Status is
                WindowActionCode.FOREGROUND_UNAVAILABLE or WindowActionCode.WINDOW_NOT_VISIBLE or
                WindowActionCode.CLICK_POINT_UNAVAILABLE or WindowActionCode.TARGET_OCCLUDED;
            static async Task<bool> Observed(Func<bool> condition)
            {
                for (int i = 0; i < 20; i++)
                {
                    if (condition()) return true;
                    await Task.Delay(25);
                }
                return condition();
            }
            var gestureTree = await Task.Run(() => WindowInspector.InspectCore(created.Handle,
                3, 50, realWindowId, nameContains: "CI_UIA_GESTURE_BUTTON",
                roleFilter: WindowRoleFilter.BUTTON)).WaitAsync(TimeSpan.FromSeconds(5));
            Check(gestureTree.Status == WindowInspectCode.COMPLETED &&
                gestureTree.Elements.Length == 1, "windows.owned_pointer_fixture_resolves_exact_button");
            var gesture = gestureTree.Elements[0];
            var hover = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
                new WindowActionRequest(realWindowId, gesture.ElementId, WindowActionKind.HOVER,
                    null, gesture.Path, InputMode: WindowInputMode.ALLOW_FOREGROUND_INPUT)))
                .WaitAsync(TimeSpan.FromSeconds(5));
            if (InputNotAvailable(hover))
                Console.WriteLine($"NOT_TESTED windows.hover_owned_button status={hover.Status}");
            else
                Check(hover.Status == WindowActionCode.COMPLETED &&
                    hover.ExecutionMethod == WindowExecutionMethod.WIN32_SENDINPUT &&
                    await Observed(() => Volatile.Read(ref gestureHoverCount) > 0) &&
                    (bool)created.Form.Invoke(new Func<bool>(() =>
                    {
                        var target = created.Form.Controls.OfType<System.Windows.Forms.Button>()
                            .Single(b => b.AccessibleName == "CI_UIA_GESTURE_BUTTON");
                        return target.RectangleToScreen(target.ClientRectangle)
                            .Contains(System.Windows.Forms.Cursor.Position);
                    })), "windows.hover_owned_button_observes_pointer_and_mouseenter");

            var right = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
                new WindowActionRequest(realWindowId, gesture.ElementId, WindowActionKind.RIGHT_CLICK,
                    null, gesture.Path, InputMode: WindowInputMode.ALLOW_FOREGROUND_INPUT)))
                .WaitAsync(TimeSpan.FromSeconds(5));
            if (InputNotAvailable(right))
                Console.WriteLine($"NOT_TESTED windows.right_click_owned_button status={right.Status}");
            else
                Check(right.Status == WindowActionCode.COMPLETED &&
                    right.ExecutionMethod == WindowExecutionMethod.WIN32_SENDINPUT &&
                    await Observed(() => Volatile.Read(ref gestureRightDownCount) == 1),
                    "windows.right_click_owned_button_observes_single_right_mouse_down");

            var doubled = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
                new WindowActionRequest(realWindowId, gesture.ElementId, WindowActionKind.DOUBLE_CLICK,
                    null, gesture.Path, InputMode: WindowInputMode.ALLOW_FOREGROUND_INPUT)))
                .WaitAsync(TimeSpan.FromSeconds(5));
            if (InputNotAvailable(doubled))
                Console.WriteLine($"NOT_TESTED windows.double_click_owned_button status={doubled.Status}");
            else
                Check(doubled.Status == WindowActionCode.COMPLETED &&
                    doubled.ExecutionMethod == WindowExecutionMethod.WIN32_SENDINPUT &&
                    await Observed(() => Volatile.Read(ref gestureLeftDownCount) == 2),
                    "windows.double_click_owned_button_observes_two_left_mouse_downs");

            var destination = (System.Drawing.Point)created.Form.Invoke(
                new Func<System.Drawing.Point>(() =>
                {
                    var target = created.Form.Controls.OfType<System.Windows.Forms.Button>()
                        .Single(b => b.AccessibleName == "CI_UIA_GESTURE_BUTTON");
                    return target.PointToScreen(new System.Drawing.Point(target.Width * 3 / 4,
                        target.Height / 2));
                }));
            var dragged = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
                new WindowActionRequest(realWindowId, gesture.ElementId, WindowActionKind.DRAG,
                    null, gesture.Path, TargetX: destination.X, TargetY: destination.Y,
                    InputMode: WindowInputMode.ALLOW_FOREGROUND_INPUT)))
                .WaitAsync(TimeSpan.FromSeconds(5));
            if (InputNotAvailable(dragged))
                Console.WriteLine($"NOT_TESTED windows.drag_owned_button status={dragged.Status}");
            else
                Check(dragged.Status == WindowActionCode.COMPLETED &&
                    dragged.ExecutionMethod == WindowExecutionMethod.WIN32_SENDINPUT &&
                    System.Windows.Forms.Cursor.Position == destination &&
                    await Observed(() => Volatile.Read(ref gestureLeftDownCount) == 3),
                    "windows.drag_owned_button_observes_down_and_destination");

            var scrolled = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
                new WindowActionRequest(realWindowId, gesture.ElementId, WindowActionKind.SCROLL,
                    null, gesture.Path, ScrollDirection: WindowScrollDirection.DOWN,
                    ScrollUnits: 2, InputMode: WindowInputMode.ALLOW_FOREGROUND_INPUT)))
                .WaitAsync(TimeSpan.FromSeconds(5));
            if (InputNotAvailable(scrolled))
                Console.WriteLine($"NOT_TESTED windows.scroll_owned_wheel_widget status={scrolled.Status}");
            else
                Check(scrolled.Status == WindowActionCode.COMPLETED &&
                    scrolled.ExecutionMethod == WindowExecutionMethod.WIN32_SENDINPUT &&
                    await Observed(() => Volatile.Read(ref scrollWheelCount) > 0) &&
                    await Observed(() => (bool)created.Form.Invoke(new Func<bool>(() =>
                        created.Form.Controls.OfType<System.Windows.Forms.ListBox>()
                            .Single(p => p.AccessibleName == "CI_UIA_SCROLL_LIST")
                            .TopIndex > 0))),
                    "windows.scroll_owned_wheel_widget_observes_event_and_ui_effect");
            Console.WriteLine($"OWNED_POINTER_EVENTS hover={Volatile.Read(ref gestureHoverCount)} " +
                $"right={Volatile.Read(ref gestureRightDownCount)} " +
                $"left={Volatile.Read(ref gestureLeftDownCount)} " +
                $"wheel={Volatile.Read(ref scrollWheelCount)}");
        }
        else Console.WriteLine("NOT_TESTED windows.physical_pointer_suite ISOLATED_WINDOWS_CI_ONLY");
        // Last action on our own synthetic form: WM_CLOSE must be observed,
        // never confused with mere PostMessage acceptance or process killing.
        var closed=await Task.Run(() => WindowManager.Execute(realWindowId,
            WindowManageAction.CLOSE,null,null,null,null,current.SessionId))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(closed is {Status:WindowManageCode.COMPLETED,
            EffectState:WindowEffectState.EFFECT_VERIFIED} &&
            WindowCatalog.ResolveId(realWindowId,current.SessionId,out _)==WindowResolveCode.STALE_WINDOW,
            "windows.manage_graceful_close_verified_exact_hwnd_disappears");
    }
    finally
    {
        if (ownedForm is { IsDisposed: false, IsHandleCreated: true })
        {
            try { ownedForm.BeginInvoke(new Action(ownedForm.Close)); }
            catch (InvalidOperationException) { }
        }
        uiThread.Join(2000);
    }
}
else Console.WriteLine($"NOT_TESTED windows.inspect_owned_occluded_winforms_tree desktop={uiaDesktop.Code}");
var simple = await Run("Write-Output 'hello'");
Check(simple.Status == ExecutionState.COMPLETED && simple.ExitCode == 0 && simple.Stdout.Trim() == "hello", "r2.stdout_exit");
string prefix = "Write-Output 'Проверка Unicode 🚀'\n#";
var unicode = await Run(prefix + new string('x', 11982 - prefix.Length));
Check(unicode.Status == ExecutionState.COMPLETED && unicode.Stdout.Trim() == "Проверка Unicode 🚀", "r2.long_unicode_stdin");
var explicitExit = await Run("exit 7");
Check(explicitExit.Status == ExecutionState.COMMAND_ERROR && explicitExit.ExitCode == 7, "r2.explicit_exit");
var thrown = await Run("throw 'EXPECTED_PROBE_FAILURE'");
Check(thrown.Status == ExecutionState.COMMAND_ERROR && thrown.Stderr.Contains("EXPECTED_PROBE_FAILURE"), "r2.exception");
var warning = await Run("Write-Warning 'expected warning'; Write-Output 'after'");
Check(warning.Status == ExecutionState.COMPLETED && warning.Warnings?.Contains("expected warning") == true &&
    warning.Stdout.Trim() == "after", "r2.warning_stream");
var nonTerminating = await Run("Write-Error 'expected error' -ErrorAction Continue; Write-Output 'after'");
Check(nonTerminating.Status == ExecutionState.COMMAND_ERROR && nonTerminating.Stderr.Contains("expected error"), "r2.nonterminating_error");
var large = await Run("[Console]::Out.Write(('a' * 100000)); [Console]::Error.Write(('b' * 100000))");
Check(large.Status == ExecutionState.COMPLETED && large.OutputTruncated && large.Stdout.Length == 16000 &&
    large.Stderr.Length == PowerShellRunner.OutputLimit, "r2.bounded_concurrent_drain");
foreach(int count in new[]{15998,15999,16000,30000})
{
    var boundary=await Run($"[Console]::Error.Write(('e'*{count})); Write-Output 'boundary'");
    Check(boundary.Status==ExecutionState.COMPLETED &&
        boundary.Stderr==new string('e',Math.Min(count,PowerShellRunner.OutputLimit)) &&
        boundary.OutputTruncated==(count>PowerShellRunner.OutputLimit),
        $"h1.stderr_exact_boundary_{count}");
}
var lf=await Run("for($i=0;$i -lt 8000;$i++){[Console]::Error.Write(\"e+`n\")}");
Check(lf.Status==ExecutionState.COMPLETED && lf.Stderr.Length==PowerShellRunner.OutputLimit &&
    lf.Stderr==string.Concat(Enumerable.Repeat("e+",5333).Select(value=>value+"\n"))+"e" &&
    lf.OutputTruncated,"h1.lf_stream_never_expands");
var unicodeBoundary=await Run("[Console]::Error.Write(('e'*15999)+'🚀')");
Check(unicodeBoundary.Status==ExecutionState.COMPLETED && unicodeBoundary.Stderr==new string('e',15999) &&
    unicodeBoundary.OutputTruncated,"h1.stderr_surrogate_safe_boundary");
var mixedWarnings=await Run("Write-Warning 'fixture'; [Console]::Error.Write(('e'*30000))");
Check(mixedWarnings.Status==ExecutionState.COMPLETED && mixedWarnings.Warnings?.Contains("fixture")==true &&
    mixedWarnings.Stderr.Length<PowerShellRunner.OutputLimit && mixedWarnings.OutputTruncated,
    "h1.warning_filter_preserves_capture_bound");
var cwd = await Run("(Get-Location).Path", cwd: Path.GetTempPath());
Check(cwd.Status == ExecutionState.COMPLETED && Path.TrimEndingDirectorySeparator(cwd.Stdout.Trim()) ==
    Path.TrimEndingDirectorySeparator(Path.GetTempPath()), "r2.working_directory");
var timeout = await Run("Start-Sleep -Seconds 30", 1);
Check(timeout.Status == ExecutionState.TIMEOUT_UNKNOWN_EFFECT && timeout.ElapsedMs < 10000, "r2.timeout");
using (var cancel = new CancellationTokenSource())
{
    cancel.Cancel();
    var before = await Run("Write-Output 'must not execute'", ct: cancel.Token);
    Check(before.Status == ExecutionState.CANCELLED_BEFORE_EXECUTION, "r2.cancel_before_launch");
}
using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
{
    var running = await Run("Start-Sleep -Seconds 30", ct: cancel.Token);
    Check(running.Status == ExecutionState.CANCELLED_UNKNOWN_EFFECT, "r2.cancel_running");
}
// No remote services or arbitrary processes: only a known inert local child.
var tree = await Run("$p=Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\\ping.exe') -ArgumentList '-n','60','127.0.0.1' -PassThru; [Console]::Out.WriteLine($p.Id); Start-Sleep -Seconds 30", 4);
Check(tree.Status == ExecutionState.TIMEOUT_UNKNOWN_EFFECT && int.TryParse(tree.Stdout.Trim(), out _), "r2.child_pid_observed");
int pid = int.Parse(tree.Stdout.Trim(), System.Globalization.CultureInfo.InvariantCulture);
bool gone;
try { using var child = Process.GetProcessById(pid); gone = child.WaitForExit(5000); }
catch (ArgumentException) { gone = true; }
Check(gone, "r2.job_terminated_child");
Console.WriteLine("R2_POWERSHELL_RUNNER=PASS (actual candidate runner, direct CI; not exposed through MCP yet)");

using(var bitmap=new System.Drawing.Bitmap(2400,1800))
{
    using(var graphics=System.Drawing.Graphics.FromImage(bitmap))graphics.Clear(System.Drawing.Color.SteelBlue);
    var png=DesktopCapture.Encode(bitmap,1280);
    Check(png is not null && png.Width==1280 && png.Height==960 &&
        png.Png.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),"r5.synthetic_png_dimensions_and_signature");
}
Check(!DesktopCapture.ValidArguments(0,0,0,10,null,1280),"r5.invalid_region_pair");

Check(TunnelLifetime.UnexpectedExitCode(0)!=0 && TunnelLifetime.UnexpectedExitCode(17)==17,
    "h5.unexpected_vendor_exit_zero_is_restartable");
var fixtureCredential=new TunnelHost.Credential(1,"tunnel_"+new string('a',32),"inert-test-key");
Check(TunnelHost.Valid(fixtureCredential)&&!TunnelHost.Valid(fixtureCredential with {Version=2})&&
    !TunnelHost.Valid(fixtureCredential with {RuntimeKey="bad\nkey"}),"r6.typed_tunnel_credential");
var launch=TunnelHost.StartInfo(@"C:\Program Files\WindowsAdminMCP\client\tunnel-client-runtime.exe",
    @"C:\Program Files\WindowsAdminMCP\native\WindowsAdminMCP.exe",fixtureCredential);
Check(launch.FileName.EndsWith(@"\client\tunnel-client-runtime.exe",StringComparison.Ordinal)&&
    launch.ArgumentList.Contains("command=\"C:/Program Files/WindowsAdminMCP/native/WindowsAdminMCP.exe\" stdio,channel=main")&&
    launch.ArgumentList.Contains("env:CONTROL_PLANE_API_KEY")&&
    !launch.ArgumentList.Any(a=>a.Contains("inert-test-key",StringComparison.Ordinal))&&
    launch.Environment["CONTROL_PLANE_API_KEY"]=="inert-test-key","r6.stable_vendor_binding_no_key_in_argv");
var secondary=fixtureCredential with {ProfileId="secondary",HealthPort=8081,
    TunnelId="tunnel_"+new string('b',32),RuntimeKey="other-inert-test-key"};
var secondaryLaunch=TunnelHost.StartInfo(@"C:\Program Files\WindowsAdminMCP\client\tunnel-client-runtime.exe",
    @"C:\Program Files\WindowsAdminMCP\native\WindowsAdminMCP.exe",secondary);
Check(TunnelProfiles.ValidSet([
    new(TunnelProfiles.Primary,fixtureCredential,"f-primary"),
    new("secondary",secondary,"f-secondary")]) &&
    !TunnelProfiles.ValidSet([new(TunnelProfiles.Primary,fixtureCredential,"a"),new("secondary",secondary with {TunnelId=fixtureCredential.TunnelId},"b")]) &&
    !TunnelProfiles.ValidSet([new(TunnelProfiles.Primary,fixtureCredential,"a"),new("secondary",secondary with {HealthPort=8080},"b")]) &&
    !TunnelProfiles.ValidSet([new(TunnelProfiles.Primary,fixtureCredential,"a"),new("secondary",secondary with {ProfileId="wrong"},"b")]) &&
    !TunnelProfiles.ValidName("../secret") && TunnelProfiles.ValidName("second-account"),
    "m01.multiprofile_unique_ids_ports_safe_names");
Check(launch.ArgumentList.Contains("127.0.0.1:8080") &&
    secondaryLaunch.ArgumentList.Contains("127.0.0.1:8081") &&
    !secondaryLaunch.ArgumentList.Contains("127.0.0.1:8080") &&
    launch.Environment["CONTROL_PLANE_API_KEY"]=="inert-test-key" &&
    secondaryLaunch.Environment["CONTROL_PLANE_API_KEY"]=="other-inert-test-key" &&
    !secondaryLaunch.ArgumentList.Any(a=>a.Contains("other-inert-test-key",StringComparison.Ordinal)) &&
    secondaryLaunch.ArgumentList.Contains("command=\"C:/Program Files/WindowsAdminMCP/native/WindowsAdminMCP.exe\" stdio,channel=main"),
    "m02.independent_vendor_arguments_credentials_stdio");
using(var oldCredential=System.Text.Json.JsonDocument.Parse("""{"version":1,"tunnel_id":"tunnel_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","runtime_key":"legacy-inert"}"""))
{
    var legacy=System.Text.Json.JsonSerializer.Deserialize<TunnelHost.Credential>(oldCredential.RootElement.GetRawText());
    Check(legacy is not null && TunnelHost.Valid(legacy) && legacy.ProfileId==TunnelProfiles.Primary &&
        legacy.HealthPort==8080,"m03.legacy_primary_credential_compatible");
}

var up=System.Net.NetworkInformation.OperationalStatus.Up;
var down=System.Net.NetworkInformation.OperationalStatus.Down;
Check(TunDependency.Classify([up],1)==TunReadiness.READY,"r6.tun_ready");
Check(TunDependency.Classify([],1)==TunReadiness.ADAPTER_MISSING,"r6.tun_missing");
Check(TunDependency.Classify([down],1)==TunReadiness.ADAPTER_DOWN,"r6.tun_down");
Check(TunDependency.Classify([up],0)==TunReadiness.DATAPLANE_NOT_RUNNING,"r6.tun_stale_adapter");
Check(TunDependency.Classify([up,up],1)==TunReadiness.ADAPTER_AMBIGUOUS,"r6.tun_duplicate_adapter");
Check(TunDependency.Classify([up],2)==TunReadiness.DATAPLANE_AMBIGUOUS,"r6.tun_duplicate_process");

using(var denied=System.Text.Json.JsonDocument.Parse("""{"schema_version":1,"component":"control-plane","details":{"http_status":403,"consecutive_failures":1}}"""))
    Check(TunnelLifetime.IsAccessDenied(denied.RootElement),"r6.authorization_denial_terminal");
using(var stale=System.Text.Json.JsonDocument.Parse("""{"schema_version":1,"component":"control-plane","details":{"http_status":403,"consecutive_failures":0}}"""))
    Check(!TunnelLifetime.IsAccessDenied(stale.RootElement),"r6.no_stale_denial");
using(var other=System.Text.Json.JsonDocument.Parse("""{"schema_version":1,"component":"control-plane","details":{"http_status":503,"consecutive_failures":2}}"""))
    Check(!TunnelLifetime.IsAccessDenied(other.RootElement),"r6.transient_error_not_auth_denial");
ProcessStartInfo InertChild()
{
    var info=new ProcessStartInfo(PowerShellRunner.Executable){UseShellExecute=false,WorkingDirectory=Environment.SystemDirectory};
    foreach(string arg in new[]{"-NoLogo","-NoProfile","-NonInteractive","-Command","Start-Sleep -Seconds 60"})info.ArgumentList.Add(arg);
    return info;
}
using(var first=NativeChildProcess.Start(InertChild()))
using(var second=NativeChildProcess.Start(InertChild()))
{
    first.Input.Close();second.Input.Close();
    Check(first.Process.Id!=second.Process.Id && !first.Process.HasExited && !second.Process.HasExited,
        "m04.two_independent_owned_vendor_processes");
    first.StopTree();
    Check(first.Process.WaitForExit(5000) && !second.Process.HasExited,
        "m05.stopping_one_tunnel_preserves_other");
    second.StopTree();
    Check(second.Process.WaitForExit(5000),"m06.second_tunnel_cleanup");
}
using(var fixture=NativeChildProcess.Start(InertChild()))
{
    fixture.Input.Close();
    int checks=0;
    var end=await TunnelLifetime.WatchAsync(fixture.Process,()=>++checks==1?TunReadiness.READY:TunReadiness.ADAPTER_DOWN,
        ()=>Task.FromResult(false),TimeSpan.FromMilliseconds(50));
    Check(end==TunnelEnd.TUN_LOST && !fixture.Process.HasExited,"r6.tun_loss_detected_on_real_child");
    fixture.StopTree();
    Check(fixture.Process.WaitForExit(5000),"r6.owned_child_terminated");
}
using(var fixture=NativeChildProcess.Start(InertChild()))
{
    fixture.Input.Close();
    var end=await TunnelLifetime.WatchAsync(fixture.Process,()=>TunReadiness.READY,
        ()=>Task.FromResult(true),TimeSpan.FromMilliseconds(50));
    Check(end==TunnelEnd.ACCESS_DENIED,"r6.real_child_auth_stop");
    fixture.StopTree();
    Check(fixture.Process.WaitForExit(5000),"r6.auth_denied_child_terminated");
}

// Real child, local fault injection only, never touch the installed tunnel.
using(var fixture=NativeChildProcess.Start(InertChild()))
{
    fixture.Input.Close();
    var workerFailure=Task.FromException(new IOException("isolated USER worker fault"));
    int checks=0;
    // The pre-H3 watch path has no worker signal: it observes only vendor/TUN.
    var priorEnd=await TunnelLifetime.WatchAsync(fixture.Process,
        ()=>++checks==2?TunReadiness.ADAPTER_DOWN:TunReadiness.READY,
        ()=>Task.FromResult(false),TimeSpan.FromMilliseconds(10));
    Check(workerFailure.IsFaulted && priorEnd==TunnelEnd.TUN_LOST&&!fixture.Process.HasExited,
        "h1.old_watch_without_worker_signal_misses_fault");
    var candidateEnd=await TunnelLifetime.WatchAsync(fixture.Process,()=>TunReadiness.READY,
        ()=>Task.FromResult(false),TimeSpan.FromMilliseconds(10),workerFailure);
    Check(candidateEnd==TunnelEnd.USER_WORKER_STOPPED && !fixture.Process.HasExited,
        "h1.worker_fault_observed_while_vendor_alive");
    _=workerFailure.Exception;
    fixture.StopTree();
    Check(fixture.Process.WaitForExit(5000),"h1.worker_fault_owned_child_terminated");
}

var startUtc=new DateTimeOffset(2026,1,1,12,0,0,TimeSpan.Zero);
var priorBoot=new BootStamp(1000,startUtc,1008,startUtc.AddMilliseconds(1),0);
// A wall clock adjustment between boots must not defeat chronological event evidence.
var nextBoot=new BootStamp(2000,startUtc.AddHours(-1),2008,startUtc.AddHours(-1),0);
Check(BootEvidence.Classify(priorBoot,priorBoot,priorBoot)==BootEvidenceCode.SAME_BOOT,"r4.boot_same_kernel_session");
Check(BootEvidence.Classify(priorBoot,priorBoot,nextBoot)==BootEvidenceCode.NEW_FULL_BOOT_OBSERVED,"r4.boot_new_full_fixture");
Check(BootEvidence.Classify(priorBoot,null,nextBoot)==BootEvidenceCode.AMBIGUOUS,"r4.boot_log_cleared_or_rotated");
Check(BootEvidence.Classify(priorBoot,priorBoot with {TypeUtc=startUtc.AddSeconds(2)},nextBoot)==BootEvidenceCode.AMBIGUOUS,"r4.boot_record_not_same");
Check(BootEvidence.Classify(priorBoot,priorBoot,nextBoot with {BootType=1})==BootEvidenceCode.AMBIGUOUS,"r4.boot_hybrid_not_accepted");
Check(BootEvidence.Classify(priorBoot,priorBoot,nextBoot with {TypeUtc=startUtc})==BootEvidenceCode.AMBIGUOUS,"r4.boot_unpaired_records_rejected");
Check(BootEvidence.Classify(priorBoot,priorBoot,nextBoot with {StartRecordId=999,TypeRecordId=1001})==BootEvidenceCode.AMBIGUOUS,"r4.boot_reused_record_ids_rejected");
var actualBoot=BootEvidence.Observe();
if(actualBoot.Status==BootEvidenceCode.FULL_BOOT_OBSERVED && actualBoot.Stamp is {} stamp)
{
    Check(BootEvidence.After(stamp).Status==BootEvidenceCode.SAME_BOOT,"r4.boot_same_session_native_event_readback");
}
else Console.WriteLine($"NOT_TESTED r4.boot_native_event_readback status={actualBoot.Status}");
Console.WriteLine("R4_BOOT_EVIDENCE_PROBE_COMPLETE (no actual OS reboot; not wired to job transitions)");

// Exact v2 schema from merged main, actual SQLite + real DPAPI ciphertext.
// No service or production directory is used.
string migrationRoot=Path.Combine(Path.GetTempPath(),"WindowsAdminMCP-migration-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(migrationRoot);
try
{
    string database=Path.Combine(migrationRoot,"jobs.db");
    string id=Guid.NewGuid().ToString("N"), key="v2-completed-fixture";
    string command="Write-Output 'v2 preserved'", v2Cwd=Environment.SystemDirectory;
    string Hash(string text)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    // Same anonymous object/property order and defaults as schema v2.
    string payload=Hash(System.Text.Json.JsonSerializer.Serialize(new {steps=new[] {new {command,timeout_seconds=30,pause_after=false}},cwd=v2Cwd}));
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+database+";Pooling=False"))
    {
        db.Open();
        using var create=db.CreateCommand();
        create.CommandText="""
                CREATE TABLE IF NOT EXISTS jobs(
                  seq INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL UNIQUE, op_key TEXT NOT NULL UNIQUE,
                  payload_hash TEXT NOT NULL, command_hash TEXT NOT NULL, cwd BLOB NOT NULL,
                  state TEXT NOT NULL CHECK(state IN ('QUEUED','RUNNING','CANCEL_REQUESTED','WAITING_FOR_RESUME','COMPLETED','FAILED','CANCELLED','NEEDS_RECONCILIATION')),
                  created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL,
                  completed_steps INTEGER NOT NULL DEFAULT 0, step_count INTEGER NOT NULL CHECK(step_count BETWEEN 1 AND 8),
                  CHECK(completed_steps BETWEEN 0 AND step_count));
                CREATE TABLE IF NOT EXISTS job_steps(
                  job_id TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE CASCADE,
                  step_index INTEGER NOT NULL CHECK(step_index BETWEEN 1 AND 8),
                  command BLOB, timeout_seconds INTEGER NOT NULL CHECK(timeout_seconds BETWEEN 1 AND 3600),
                  pause_after INTEGER NOT NULL CHECK(pause_after IN (0,1)), result BLOB,
                  PRIMARY KEY(job_id,step_index));
                PRAGMA user_version=2;
                """;
        create.ExecuteNonQuery();
        using var row=db.CreateCommand();
        row.CommandText="""
            INSERT INTO jobs(job_id,op_key,payload_hash,command_hash,cwd,state,created_utc,updated_utc,completed_steps,step_count)
            VALUES($id,$key,$payload,$hash,$cwd,'COMPLETED',$now,$now,1,1);
            INSERT INTO job_steps(job_id,step_index,command,timeout_seconds,pause_after,result)
            VALUES($id,1,NULL,30,0,$result);
            """;
        row.Parameters.AddWithValue("$id",id);
        row.Parameters.AddWithValue("$key",key);
        row.Parameters.AddWithValue("$payload",payload);
        row.Parameters.AddWithValue("$hash",Hash(command));
        row.Parameters.AddWithValue("$cwd",JobDataProtection.Protect(v2Cwd,id,"cwd"));
        row.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        row.Parameters.AddWithValue("$result",JobDataProtection.Protect(System.Text.Json.JsonSerializer.Serialize(
            new ExecutionResult(ExecutionState.COMPLETED,Hash(command),12,0,"v2 preserved"),PipeWire.Json),id,"step/1/result"));
        row.ExecuteNonQuery();
    }
    using(var store=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
    {
        store.Initialize(database);
        var saved=store.Dispatch(new(AdminOperation.Job,JobAction.logs,JobId:id),"migration");
        Check(saved.Status==StatusCode.OK && saved.Job?.State==JobState.COMPLETED &&
            saved.Job.Execution is {Stdout:"v2 preserved",AuditPersisted:true},"r4.real_v2_migration_preserves_encrypted_result_and_audit");
        var duplicate=store.Dispatch(new(AdminOperation.Job,JobAction.submit,Key:key,Command:command,WorkingDirectory:v2Cwd),"migration");
        Check(duplicate.Status==StatusCode.OK && duplicate.Job?.JobId==id,"r4.v2_payload_hash_unchanged");
        var counts=store.Summary();
        Check(counts.SchemaVersion==4 && counts.Total==1 && counts.Completed==1,"r4.migration_store_counts");
    }
    Check(StoreSchema.ObserveFile(database) is {Status:StoreSchemaCode.OBSERVED,Version:4},"r6.offline_store_schema_v4");
    Check(StoreSchema.ObserveFile(Path.Combine(migrationRoot,"missing.db")).Status==StoreSchemaCode.ABSENT,"r6.offline_store_absent");
    var orphan=Path.Combine(migrationRoot,"orphan.db");
    File.WriteAllText(orphan+"-wal","fixture");
    Check(StoreSchema.ObserveFile(orphan).Status==StoreSchemaCode.READBACK_FAILED,"r6.offline_store_orphan_wal_rejected");
    var backups=Directory.GetFiles(migrationRoot,"jobs.db.schema2-*.backup");
    Check(backups.Length==1,"r4.v2_backup_retained");
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+backups[0]+";Mode=ReadOnly;Pooling=False"))
    {
        db.Open();using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version";
        Check((long)cmd.ExecuteScalar()! ==2,"r4.v2_backup_schema");
    }
    using(var store=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
    {
        store.Initialize(database);
        Check(store.Summary().Completed==1 && Directory.GetFiles(migrationRoot,"jobs.db.schema2-*.backup").Length==1,
            "r4.v4_reopen_no_duplicate_migration");
    }
}
finally{Directory.Delete(migrationRoot,true);}


// L01-L06: previous-generation unknown-effect records are deleted only from
// isolated fixtures; production jobs and Windows service are not touched by CI.
string legacyRoot=Path.Combine(Path.GetTempPath(),"WindowsAdminMCP-legacy56-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(legacyRoot);
try
{
    string database=Path.Combine(legacyRoot,"jobs.db");
    using(var store=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
    {
        store.Initialize(database);
        for(int index=0;index<LegacyUnknownCleanup.ExpectedCount;index++)
            Check(store.Dispatch(new(AdminOperation.Job,JobAction.submit,
                Key:"legacy56-"+index,Command:"Write-Output fixture"),"legacy").Status==StatusCode.OK,
                "legacy56.submit_"+index);
        Check(store.Dispatch(new(AdminOperation.Job,JobAction.submit,
            Key:"legacy56-preserved",Command:"Write-Output kept"),"legacy").Status==StatusCode.OK,
            "legacy56.unrelated_created");
    }
    Check(LegacyUnknownCleanup.OnDatabase(database,false,null).Status==LegacyCleanupCode.ACTIVE_JOBS,
        "legacy56.active_jobs_refused_without_effect");
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+database+";Pooling=False"))
    {
        db.Open();
        using var update=db.CreateCommand();
        update.CommandText="""
            UPDATE jobs SET state='NEEDS_RECONCILIATION',
                created_utc=CASE WHEN op_key='legacy56-0' THEN '2026-11-17T23:36:25.6892759+00:00' ELSE '2026-11-21T12:00:00.0000000+00:00' END
            WHERE op_key LIKE 'legacy56-%' AND op_key<>'legacy56-preserved';
            UPDATE jobs SET state='COMPLETED' WHERE op_key='legacy56-preserved';
            """;
        update.ExecuteNonQuery();
    }
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+database+";Pooling=False"))
    {
        db.Open();
        using var pending=db.CreateCommand();
        pending.CommandText="UPDATE jobs SET state='WAITING_FOR_RESUME' WHERE op_key='legacy56-preserved'";
        Check(pending.ExecuteNonQuery()==1,"legacy56.fixture_waiting_guard");
    }
    Check(LegacyUnknownCleanup.OnDatabase(database,false,null).Status==
        LegacyCleanupCode.ACTIVE_JOBS,"legacy56.refuses_other_waiting_jobs");
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+database+";Pooling=False"))
    {
        db.Open();
        using var resumed=db.CreateCommand();
        resumed.CommandText="UPDATE jobs SET state='COMPLETED' WHERE op_key='legacy56-preserved'";
        Check(resumed.ExecuteNonQuery()==1,"legacy56.fixture_waiting_reset");
    }
    var plan=LegacyUnknownCleanup.OnDatabase(database,false,null);
    Check(plan is {Status:LegacyCleanupCode.READY,LegacyCount:17} &&
        plan.Fingerprint is {Length:64},"legacy56.exact_plan_without_secret_commands");
    Check(LegacyUnknownCleanup.OnDatabase(database,true,new string('0',64)).Status==
        LegacyCleanupCode.PLAN_CHANGED,"legacy56.hash_guard");
    bool injected=false;
    try { _=LegacyUnknownCleanup.OnDatabase(database,true,plan.Fingerprint,
        ()=>throw new InvalidOperationException("fixture before commit")); }
    catch(InvalidOperationException){injected=true;}
    Check(injected && LegacyUnknownCleanup.OnDatabase(database,false,null).Fingerprint==plan.Fingerprint,
        "legacy56.before_commit_rolls_back");
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+database+";Pooling=False"))
    {
        db.Open();
        using var edit=db.CreateCommand();
        edit.CommandText="UPDATE jobs SET state='FAILED' WHERE op_key='legacy56-0'";
        Check(edit.ExecuteNonQuery()==1,"legacy56.fixture_single_change");
    }
    Check(LegacyUnknownCleanup.OnDatabase(database,false,null).Status==
        LegacyCleanupCode.LEGACY_SET_MISMATCH,"legacy56.rejected_16_records");
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+database+";Pooling=False"))
    {
        db.Open();
        using var edit=db.CreateCommand();
        edit.CommandText="UPDATE jobs SET state='NEEDS_RECONCILIATION' WHERE op_key='legacy56-0'";
        Check(edit.ExecuteNonQuery()==1,"legacy56.fixture_restored");
    }
    var applied=LegacyUnknownCleanup.OnDatabase(database,true,plan.Fingerprint);
    Check(applied is {Status:LegacyCleanupCode.CLEARED,DeletedCount:17,RemainingCount:0,SchemaVersion:4},
        "legacy56.atomic_exact_17_cleared");
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+database+";Pooling=False"))
    {
        db.Open();
        using var counts=db.CreateCommand();
        counts.CommandText="SELECT count(*) FROM jobs";
        Check((long)counts.ExecuteScalar()! == 1,"legacy56.unrelated_job_preserved");
        counts.CommandText="SELECT count(*) FROM job_steps";
        Check((long)counts.ExecuteScalar()! == 1,"legacy56.foreign_keys_cascade_exact");
        counts.CommandText="SELECT count(*) FROM jobs WHERE op_key='legacy56-preserved'";
        Check((long)counts.ExecuteScalar()! == 1,"legacy56.unrelated_key_preserved");
    }
    Check(Directory.GetFiles(legacyRoot,"jobs.db.legacy-unknown-*.backup").Length==0,
        "legacy56.no_old_backup_left_after_success");
    Check(LegacyUnknownCleanup.OnDatabase(database,false,null).Status==
        LegacyCleanupCode.ALREADY_CLEARED,"legacy56.second_plan_no_replay");
}
finally{Directory.Delete(legacyRoot,true);}


// Isolated real store: keyset pagination is stable, filtered and rejects invalid ranges.
string pagingRoot=Path.Combine(Path.GetTempPath(),"WindowsAdminMCP-paging-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(pagingRoot);
try
{
    using var store=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance);
    store.Initialize(Path.Combine(pagingRoot,"jobs.db"));
    JobReply Call(AdminRequest req)=>store.Dispatch(req,"paging");
    for(int i=0;i<5;i++)
        Check(Call(new(AdminOperation.Job,JobAction.submit,Key:"paging-"+i,Command:"Write-Output 1")).Status==StatusCode.OK,"paging.admission."+i);
    Check(Call(new(AdminOperation.Job,JobAction.cancel,Key:"paging-3")).Status==StatusCode.OK,"paging.cancel");
    var p1=Call(new(AdminOperation.Job,JobAction.list,StateFilter:JobState.QUEUED,PageSize:2));
    Check(p1.Status==StatusCode.OK && p1.Jobs is {Length:2} && p1.Jobs[0].OperationKey=="paging-4" &&
        p1.Jobs[1].OperationKey=="paging-2" && p1.NextBeforeSeq is >0,"paging.first");
    var p2=Call(new(AdminOperation.Job,JobAction.list,StateFilter:JobState.QUEUED,PageSize:2,BeforeSeq:p1.NextBeforeSeq));
    Check(p2.Status==StatusCode.OK && p2.Jobs is {Length:2} && p2.Jobs[0].OperationKey=="paging-1" &&
        p2.Jobs[1].OperationKey=="paging-0" && p2.NextBeforeSeq is null,"paging.second");
    Check(Call(new(AdminOperation.Job,JobAction.list,PageSize:0)).Status==StatusCode.INVALID_ARGUMENT &&
        Call(new(AdminOperation.Job,JobAction.status,Key:"paging-0",BeforeSeq:1)).Status==StatusCode.INVALID_ARGUMENT,"paging.invalid");
    var lookup=Call(new(AdminOperation.Job,JobAction.status,Key:"paging-3"));
    Check(lookup.Job?.State==JobState.CANCELLED,"paging.key_lookup");
}
finally{Directory.Delete(pagingRoot,true);}

// Native updater quiesces admission atomically, cancels queued work and
// requests active-worker cancellation without discarding unknown-effect audit.
string legacyJobWire=System.Text.Json.JsonSerializer.Serialize(
    new AdminRequest(AdminOperation.Job,JobAction.list,StateFilter:JobState.RUNNING),PipeWire.Json);
Check(!legacyJobWire.Contains("\"maintenance\"",StringComparison.Ordinal) &&
    System.Text.Json.JsonSerializer.Serialize(new AdminRequest(AdminOperation.Maintenance,Maintenance:MaintenanceAction.Drain),PipeWire.Json)
        .Contains("\"maintenance\":\"Drain\"",StringComparison.Ordinal),
    "maintenance.legacy_job_wire_compatible");
string drainRoot=Path.Combine(Path.GetTempPath(),"WindowsAdminMCP-drain-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(drainRoot);
try
{
    using var coordinator=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance);
    coordinator.Initialize(Path.Combine(drainRoot,"jobs.db"));
    var owner=new UserSession("S-1-5-21-9-8-7-1001",1,"S-1-5-5-9-8");
    coordinator.DispatchWorker(new(WorkerAction.poll),owner);
    var one=coordinator.Dispatch(new(AdminOperation.Job,JobAction.submit,Key:"drain-active",Command:"Start-Sleep -Seconds 20",
        ExecutionContext:CommandContext.USER),"fixture",owner.Sid,owner);
    var lease=coordinator.DispatchWorker(new(WorkerAction.poll),owner).Work;
    Check(one.Status==StatusCode.OK && lease is not null && coordinator.AdmissionOpen,
        "maintenance.owned_worker_running");
    if(lease is null)throw new InvalidOperationException("Missing maintenance fixture lease");
    var queued=coordinator.Dispatch(new(AdminOperation.Job,JobAction.submit,Key:"drain-queued",Command:"Write-Output never"),"fixture");
    Check(queued.Status==StatusCode.OK,"maintenance.queued_fixture");
    var drained=coordinator.Maintain(MaintenanceAction.Drain);
    Check(drained is {Status:StatusCode.OK,Draining:true,CancelledQueued:1,CancelRequested:1} &&
        !coordinator.AdmissionOpen &&
        coordinator.Summary() is {Queued:0,Running:0,CancelRequested:1},"maintenance.atomic_cancel_and_gate");
    Check(coordinator.Dispatch(new(AdminOperation.Job,JobAction.submit,Key:"late",Command:"Write-Output late"),"fixture").Status==StatusCode.BUSY &&
        coordinator.DispatchWorker(new(WorkerAction.poll),owner).Work is null &&
        coordinator.DispatchWorker(new(WorkerAction.heartbeat,lease.LeaseId),owner).CancelRequested,
        "maintenance.no_new_admission_or_take");
    string hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(lease.Command))).ToLowerInvariant();
    Check(coordinator.DispatchWorker(new(WorkerAction.complete,lease.LeaseId,
        new(ExecutionState.CANCELLED_UNKNOWN_EFFECT,hash,100)),owner).Status==StatusCode.OK &&
        coordinator.Summary() is {Queued:0,Running:0,CancelRequested:0,NeedsReconciliation:1},
        "maintenance.unknown_effect_retained");
    Check(coordinator.Maintain(MaintenanceAction.Resume) is {Status:StatusCode.OK,Draining:false} &&
        coordinator.AdmissionOpen &&
        coordinator.Dispatch(new(AdminOperation.Job,JobAction.submit,Key:"post-drain",Command:"Write-Output ok"),"fixture").Status==StatusCode.OK,
        "maintenance.release_restores_admission");
}
finally{Directory.Delete(drainRoot,true);}


// Schema 3 fixture: migrate encrypted legacy results and retain permanent job keys.
string retentionRoot=Path.Combine(Path.GetTempPath(),"WindowsAdminMCP-retention-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(retentionRoot);
try
{
    string path=Path.Combine(retentionRoot,"jobs.db"), cmdText="Write-Output 'old-audit'";
    string Digest(string value)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    string payload=Digest(System.Text.Json.JsonSerializer.Serialize(new {steps=new[]{new JobStep(cmdText)},cwd=Environment.SystemDirectory}));
    string old=DateTimeOffset.UtcNow.AddDays(-45).ToString("O");
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+path+";Pooling=False"))
    {
        db.Open();
        using var schema=db.CreateCommand();
        schema.CommandText="""
            CREATE TABLE jobs(seq INTEGER PRIMARY KEY AUTOINCREMENT,job_id TEXT NOT NULL UNIQUE,op_key TEXT NOT NULL UNIQUE,
              payload_hash TEXT NOT NULL,command_hash TEXT NOT NULL,cwd BLOB NOT NULL,state TEXT NOT NULL,
              created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL,completed_steps INTEGER NOT NULL,step_count INTEGER NOT NULL,
              reboot_stamp TEXT,reboot_evidence TEXT);
            CREATE TABLE job_steps(job_id TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE CASCADE,step_index INTEGER NOT NULL,
              command BLOB,timeout_seconds INTEGER NOT NULL,pause_after INTEGER NOT NULL,result BLOB,reboot_after INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(job_id,step_index));
            PRAGMA user_version=3;
            """;
        schema.ExecuteNonQuery();
        foreach(var (id,state,completed) in new[]{("old-finished","COMPLETED",1),("old-unknown","NEEDS_RECONCILIATION",0)})
        {
            using var seed=db.CreateCommand();
            seed.CommandText="""
                INSERT INTO jobs(job_id,op_key,payload_hash,command_hash,cwd,state,created_utc,updated_utc,completed_steps,step_count)
                VALUES($id,$id,$payload,$hash,$cwd,$state,$old,$old,$completed,1);
                INSERT INTO job_steps(job_id,step_index,command,timeout_seconds,pause_after,result)
                VALUES($id,1,NULL,30,0,$result);
                """;
            seed.Parameters.AddWithValue("$id",id);
            seed.Parameters.AddWithValue("$payload",payload);
            seed.Parameters.AddWithValue("$hash",Digest(cmdText));
            seed.Parameters.AddWithValue("$cwd",JobDataProtection.Protect(Environment.SystemDirectory,id,"cwd"));
            seed.Parameters.AddWithValue("$state",state);
            seed.Parameters.AddWithValue("$old",old);
            seed.Parameters.AddWithValue("$completed",completed);
            var execution=new ExecutionResult(completed==1?ExecutionState.COMPLETED:ExecutionState.TIMEOUT_UNKNOWN_EFFECT,Digest(cmdText),345,completed==1?0:null,"old-audit");
            seed.Parameters.AddWithValue("$result",JobDataProtection.Protect(System.Text.Json.JsonSerializer.Serialize(execution,PipeWire.Json),id,"step/1/result"));
            seed.ExecuteNonQuery();
        }
    }
    using(var store=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
    {
        store.Initialize(path);
        var original=store.Dispatch(new(AdminOperation.Job,JobAction.logs,JobId:"old-unknown"),"fixture");
        Check(original.Job is {State:JobState.NEEDS_RECONCILIATION,InterruptedSourceAvailable:false,Execution.Status:ExecutionState.TIMEOUT_UNKNOWN_EFFECT},"reconcile.v3_unknown_preserved");
        var prune=store.Dispatch(new(AdminOperation.Job,JobAction.prune),"fixture");
        Check(prune.Status==StatusCode.OK && prune.PrunedCount==1 && prune.Store is {Detailed:1,Pruned:1,NeedsReconciliation:1},"history.compacts_only_eligible");
        var logs=store.Dispatch(new(AdminOperation.Job,JobAction.logs,Key:"old-finished"),"fixture");
        Check(logs.Status==StatusCode.HISTORY_PRUNED && logs.Job is {HistoryPrunedUtc:not null,Execution:{Status:ExecutionState.COMPLETED,Stdout:"",ElapsedMs:345,AuditPersisted:true,PrunedOutput:{StdoutBytes:9}}},"history.retains_audit_metadata");
        var retry=store.Dispatch(new(AdminOperation.Job,JobAction.submit,Key:"old-finished",Command:cmdText),"fixture");
        Check(retry.Status==StatusCode.OK && retry.Job is {JobId:"old-finished",HistoryPrunedUtc:not null},"history.permanent_idempotency");
        Check(store.Dispatch(new(AdminOperation.Job,JobAction.submit,Key:"old-finished",Command:"different"),"fixture").Status==StatusCode.CONFLICT,"history.pruned_key_conflict");
        var resolve=store.Dispatch(new(AdminOperation.Job,JobAction.reconcile,JobId:"old-unknown",Decision:ReconciliationDecision.UNKNOWN,Evidence:"No source retained in v3."),"fixture","S-1-5-18");
        Check(resolve.Job is {State:JobState.RECONCILED,Reconciliation.Decision:ReconciliationDecision.UNKNOWN},"reconcile.legacy_unknown_closed_without_success");
        var noPrune=store.Dispatch(new(AdminOperation.Job,JobAction.prune),"fixture");
        Check(noPrune.PrunedCount==0 && noPrune.Store is {Reconciled:1,NeedsReconciliation:0},"reconcile.fresh_evidence_not_pruned");
    }
    Check(Directory.GetFiles(retentionRoot,"jobs.db.schema3-*.backup").Length==1 &&
        StoreSchema.ObserveFile(path) is {Status:StoreSchemaCode.OBSERVED,Version:4},"migration.v3_backup_and_v4_schema");
    using(var store=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
    {
        store.Initialize(path);
        var resolved=store.Dispatch(new(AdminOperation.Job,JobAction.logs,JobId:"old-unknown"),"fixture");
        Check(resolved.Job is {State:JobState.RECONCILED,Reconciliation.Decision:ReconciliationDecision.UNKNOWN,Execution.Stdout:"old-audit"} &&
            Directory.GetFiles(retentionRoot,"jobs.db.schema3-*.backup").Length==1,"reconcile.v4_reopen_preserves_evidence");
    }
}
finally{Directory.Delete(retentionRoot,true);}


// R: bounded storage, cold terminal tombstones, and exact key survival.
Check(JobCoordinator.ClassifyStorage(10,4096,0,metadataLimit:10,bytesLimit:2*1024*1024)==StoreStorageState.FULL &&
    JobCoordinator.ClassifyStorage(8,4096,0,metadataLimit:10,bytesLimit:2*1024*1024)==StoreStorageState.WARNING &&
    JobCoordinator.ClassifyStorage(1,4096,0,metadataLimit:10,bytesLimit:2*1024*1024)==StoreStorageState.AVAILABLE &&
    JobCoordinator.ClassifyStorage(1,null,0,metadataLimit:10,bytesLimit:2*1024*1024)==StoreStorageState.READBACK_FAILED &&
    JobCoordinator.ClassifyStorage(1,2*1024*1024,0,metadataLimit:10,bytesLimit:2*1024*1024)==StoreStorageState.FULL,
    "storage.finite_bounds_fail_closed_and_typed");
// R: 20k durable SQLite mutations under a real WAL transaction, reopen,
// integrity readback and exact idempotency-key retention. No live store touched.
string stressRoot=Path.Combine(Path.GetTempPath(),"WindowsAdminMCP-stress-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(stressRoot);
try
{
    string dbPath=Path.Combine(stressRoot,"jobs.db"),key="r-twenty-thousand-transitions";
    using(var owner=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
    {
        owner.Initialize(dbPath);
        Check(owner.Dispatch(new(AdminOperation.Job,JobAction.submit,Key:key,
            Command:"Write-Output deterministic"),"stress").Status==StatusCode.OK,
            "storage.stress_fixture_admitted");
    }
    var clock=Stopwatch.StartNew();
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+dbPath+";Pooling=False"))
    {
        db.Open();
        using(var tx=db.BeginTransaction())
        using(var update=db.CreateCommand())
        {
            update.Transaction=tx;
            update.CommandText="UPDATE jobs SET updated_utc=$utc WHERE op_key=$key";
            update.Parameters.AddWithValue("$key",key);
            var utc=update.Parameters.Add("$utc",Microsoft.Data.Sqlite.SqliteType.Text);
            for(int i=0;i<20000;i++)
            {
                utc.Value=DateTimeOffset.UtcNow.AddTicks(i).ToString("O");
                if(update.ExecuteNonQuery()!=1)throw new InvalidOperationException("SQLite churn lost the job row");
            }
            tx.Commit();
        }
        using var verify=db.CreateCommand();
        verify.CommandText="PRAGMA integrity_check";
        Check((string?)verify.ExecuteScalar()=="ok","storage.stress_integrity_check_after_20000_updates");
        using var checkpoint=db.CreateCommand();
        checkpoint.CommandText="PRAGMA wal_checkpoint(TRUNCATE)";
        checkpoint.ExecuteNonQuery();
    }
    clock.Stop();
    using(var owner=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
    {
        owner.Initialize(dbPath);
        Check(owner.Dispatch(new(AdminOperation.Job,JobAction.status,Key:key),"stress") is
            {Status:StatusCode.OK,Job:{OperationKey:"r-twenty-thousand-transitions"}} &&
            owner.Dispatch(new(AdminOperation.Job,JobAction.submit,Key:key,
                Command:"Write-Output changed"),"stress").Status==StatusCode.CONFLICT &&
            new FileInfo(dbPath).Length<512L*1024*1024,
            "storage.stress_reopen_key_survives_and_size_bounded");
    }
    Console.WriteLine($"BENCH storage.twenty_thousand_update_ms={clock.ElapsedMilliseconds} " +
        $"db_bytes={new FileInfo(dbPath).Length}");
}
finally{Directory.Delete(stressRoot,true);}
// R: approach the actual 100,000 metadata cap without 100,000 individual
// maintenance commands. Seed offline, then reopen via the *single* real owner.
string capRoot=Path.Combine(Path.GetTempPath(),"WindowsAdminMCP-metadata-cap-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(capRoot);
try
{
    string capDb=Path.Combine(capRoot,"jobs.db");
    using(var initial=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
        initial.Initialize(capDb);
    var benchmark=Stopwatch.StartNew();
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+capDb+";Pooling=False"))
    {
        db.Open();
        using var insert=db.CreateCommand();
        insert.CommandText="""
            WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM seq WHERE n<100000)
            INSERT INTO file_receipts(operation_id,actor_sid,target_hash,context,total_bytes,
                sha256,policy,original_version,original_sha256,state,received_bytes,
                created_utc,updated_utc)
            SELECT printf('%032x',n),'S-1-5-18',hex(zeroblob(32)),'SYSTEM',0,
                hex(zeroblob(32)),'FAIL_IF_EXISTS',NULL,NULL,'COMMITTED',0,
                '2026-01-01T00:00:00Z','2026-01-01T00:00:00Z' FROM seq
            """;
        Check(insert.ExecuteNonQuery()==100000,"storage.hundred_thousand_fixture_rows_inserted");
        using var verify=db.CreateCommand();
        verify.CommandText="PRAGMA integrity_check";
        Check((string?)verify.ExecuteScalar()=="ok","storage.hundred_thousand_rows_sqlite_integrity");
    }
    using(var owner=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
    {
        owner.Initialize(capDb);
        var read=owner.Dispatch(new(AdminOperation.Job,JobAction.list),"cap");
        Check(read.Store?.Storage is
            {Status:StoreStorageState.FULL,FileReceipts:100000,MetadataTotal:100000} &&
            owner.Dispatch(new(AdminOperation.Job,JobAction.submit,Key:"cap-new",
                Command:"Write-Output not-admitted"),"cap").Status==StatusCode.STORE_FULL,
            "storage.hundred_thousand_admission_refused_but_readback_available");
        var foreign=owner.FileReceipt(FileReceiptRequest.From(new FileWriteRequest(
            FileWriteAction.BEGIN,Path.Combine(capRoot,"no-write.bin"),Guid.NewGuid().ToString("N"),
            CommandContext.SYSTEM,0,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([]))),
            FileReceiptPhase.PREPARE),"S-1-5-18");
        Check(foreign.Code==FileReceiptCode.STORE_FULL &&
            !File.Exists(Path.Combine(capRoot,"no-write.bin")),
            "storage.hundred_thousand_file_write_admission_fails_before_effect");
    }
    benchmark.Stop();
    Console.WriteLine($"BENCH storage.hundred_thousand_metadata_ms={benchmark.ElapsedMilliseconds} " +
        $"db_bytes={new FileInfo(capDb).Length}");
}
finally{Directory.Delete(capRoot,true);}
string coldRoot=Path.Combine(Path.GetTempPath(),"WindowsAdminMCP-cold-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(coldRoot);
try
{
    string coldDb=Path.Combine(coldRoot,"jobs.db");
    using(var owner=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
    {
        owner.Initialize(coldDb);
        foreach(string key in new[]{"r-cold-terminal","r-cold-unknown","r-cold-waiting"})
            Check(owner.Dispatch(new(AdminOperation.Job,JobAction.submit,Key:key,Command:"Write-Output r-fixture"),"r").Status==StatusCode.OK,
                "storage.fixture_submit_"+key);
    }
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+coldDb+";Pooling=False"))
    {
        db.Open();
        using var age=db.CreateCommand();
        age.CommandText="""
            UPDATE jobs SET state='COMPLETED',completed_steps=1,updated_utc=$old WHERE op_key='r-cold-terminal';
            UPDATE jobs SET state='NEEDS_RECONCILIATION',updated_utc=$old WHERE op_key='r-cold-unknown';
            UPDATE jobs SET state='WAITING_FOR_RESUME',updated_utc=$old WHERE op_key='r-cold-waiting';
            """;
        age.Parameters.AddWithValue("$old",DateTimeOffset.UtcNow.AddDays(-100).ToString("O"));
        age.ExecuteNonQuery();
        using var count=db.CreateCommand();
        count.CommandText="SELECT count(*) FROM jobs WHERE updated_utc < $today";
        count.Parameters.AddWithValue("$today",DateTimeOffset.UtcNow.AddDays(-99).ToString("O"));
        Check((long)count.ExecuteScalar()! ==3,"storage.fixture_old_states");
    }
    using(var owner=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
    {
        owner.Initialize(coldDb);
        bool rolledBack=false;
        try {_=owner.MaintainHistory(()=>throw new InvalidOperationException("injected precommit"));}
        catch(InvalidOperationException){rolledBack=true;}
        var unmodified=owner.Dispatch(new(AdminOperation.Job,JobAction.list,PageSize:10),"r");
        Check(rolledBack && unmodified.Store is {Pruned:0,Storage:{Tombstoned:0}},
            "storage.injected_crash_rolls_back_both_stages");
        var pruned=owner.Dispatch(new(AdminOperation.Job,JobAction.prune),"r");
        Check(pruned.Status==StatusCode.OK && pruned.PrunedCount==1 && pruned.TombstonedCount==1 &&
            pruned.Store is {Total:3,NeedsReconciliation:1,WaitingForResume:1,
                Pruned:1,Storage:{Tombstoned:1,DbBytes:>0,PageCount:>0,Status:StoreStorageState.AVAILABLE}},
            "storage.only_aged_terminal_becomes_tombstone");
        var logs=owner.Dispatch(new(AdminOperation.Job,JobAction.logs,Key:"r-cold-terminal"),"r");
        Check(logs.Status==StatusCode.HISTORY_PRUNED && logs.Job is
            {State:JobState.COMPLETED,HistoryPrunedUtc:not null,Execution:null},
            "storage.cold_logs_return_typed_history_pruned");
        var retry=owner.Dispatch(new(AdminOperation.Job,JobAction.submit,
            Key:"r-cold-terminal",Command:"Write-Output r-fixture"),"r");
        Check(retry.Status==StatusCode.OK && retry.Job is {State:JobState.COMPLETED} &&
            owner.Dispatch(new(AdminOperation.Job,JobAction.submit,Key:"r-cold-terminal",
                Command:"Write-Output changed"),"r").Status==StatusCode.CONFLICT,
            "storage.permanent_key_prevents_replay_after_payload_removed");
        Check(owner.Dispatch(new(AdminOperation.Job,JobAction.status,Key:"r-cold-unknown"),"r")
            .Job?.State==JobState.NEEDS_RECONCILIATION &&
            owner.Dispatch(new(AdminOperation.Job,JobAction.status,Key:"r-cold-waiting"),"r")
            .Job?.State==JobState.WAITING_FOR_RESUME,
            "storage.never_purges_unknown_or_waiting");
    }
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+coldDb+";Pooling=False"))
    {
        db.Open();
        using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT count(*) FROM job_steps WHERE job_id=(SELECT job_id FROM jobs WHERE op_key='r-cold-terminal')";
        Check((long)cmd.ExecuteScalar()! ==0,"storage.cold_steps_removed");
        cmd.CommandText="SELECT count(*) FROM job_steps WHERE job_id IN(SELECT job_id FROM jobs WHERE op_key IN ('r-cold-unknown','r-cold-waiting'))";
        Check((long)cmd.ExecuteScalar()! ==2,"storage.other_steps_preserved");
        cmd.CommandText="SELECT length(cwd) FROM jobs WHERE op_key='r-cold-terminal'";
        Check((long)cmd.ExecuteScalar()! ==0,"storage.cold_cwd_removed");
        cmd.CommandText="PRAGMA foreign_key_check";
        Check(cmd.ExecuteScalar() is null,"storage.cold_foreign_keys_valid");
        cmd.CommandText="PRAGMA integrity_check";
        Check((string?)cmd.ExecuteScalar()=="ok","storage.cold_integrity_valid");
    }
}
finally{Directory.Delete(coldRoot,true);}

// A genuine SQLite/DPAPI fixture exercises session routing without impersonating users.
string routingRoot=Path.Combine(Path.GetTempPath(),"WindowsAdminMCP-routing-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(routingRoot);
try
{
    using var routing=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance);
    routing.Initialize(Path.Combine(routingRoot,"jobs.db"));
    var owner=new UserSession("S-1-5-21-1-2-3-1001",1,"S-1-5-5-1-2");
    var other=new UserSession("S-1-5-21-1-2-3-1002",2,"S-1-5-5-1-3");
    var laterLogin=owner with {LogonSid="S-1-5-5-1-4"};
    var req=new AdminRequest(AdminOperation.Job,JobAction.submit,Key:"user-job",Command:"Write-Output 1",ExecutionContext:CommandContext.USER);
    Check(routing.Dispatch(req,"fixture",owner.Sid,owner).Status==StatusCode.USER_CONTEXT_UNAVAILABLE,"user.no_system_fallback");
    Check(routing.DispatchWorker(new(WorkerAction.poll),owner).Status==StatusCode.OK,"user.worker_presence");
    var admission=routing.Dispatch(req,"fixture",owner.Sid,owner);
    Check(admission.Job is {ExecutionContext:CommandContext.USER,ExecutionSessionId:1},"user.bound_admission");
    Check(routing.DispatchWorker(new(WorkerAction.poll),other).Work is null &&
        routing.DispatchWorker(new(WorkerAction.poll),laterLogin).Work is null,"user.no_other_session_claim");
    var lease=routing.DispatchWorker(new(WorkerAction.poll),owner).Work;
    Check(lease is not null && lease.JobId==admission.Job?.JobId,"user.owner_claim");
    if(lease is null)throw new InvalidOperationException("Missing fixture lease");
    var secondAdmission=routing.Dispatch(req with {Key="user-job-two",Command="Write-Output 2"},"fixture",owner.Sid,owner);
    var secondLease=routing.DispatchWorker(new(WorkerAction.poll),owner).Work;
    Check(secondLease is not null && secondLease.JobId==secondAdmission.Job?.JobId && secondLease.LeaseId!=lease.LeaseId,
        "user.two_independent_leases");
    if(secondLease is null)throw new InvalidOperationException("Missing second fixture lease.");
    Check(routing.Dispatch(new(AdminOperation.Job,JobAction.cancel,JobId:secondLease.JobId),"fixture",owner.Sid,owner).Job?.State==JobState.CANCEL_REQUESTED,
        "user.cancel_individual_lease");
    Check(routing.DispatchWorker(new(WorkerAction.heartbeat,secondLease.LeaseId),owner).CancelRequested &&
        !routing.DispatchWorker(new(WorkerAction.heartbeat,lease.LeaseId),owner).CancelRequested,"user.cancel_not_cross_job");
    string secondHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secondLease.Command))).ToLowerInvariant();
    Check(routing.DispatchWorker(new(WorkerAction.complete,secondLease.LeaseId,new(ExecutionState.CANCELLED_UNKNOWN_EFFECT,secondHash,100)),owner).Status==StatusCode.OK,
        "user.second_lease_independent_completion");
    Check(routing.DispatchWorker(new(WorkerAction.heartbeat,lease.LeaseId),other).Status==StatusCode.LEASE_LOST,"user.no_other_session_heartbeat");
    string hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(lease.Command))).ToLowerInvariant();
    var result=new ExecutionResult(ExecutionState.COMPLETED,hash,100,0,"user fixture");
    Check(routing.DispatchWorker(new(WorkerAction.complete,lease.LeaseId,result),other).Status==StatusCode.LEASE_LOST,"user.no_other_session_completion");
    Check(routing.DispatchWorker(new(WorkerAction.complete,lease.LeaseId,result),owner).Status==StatusCode.OK,"user.durable_completion");
    var readback=routing.Dispatch(new(AdminOperation.Job,JobAction.logs,Key:"user-job"),"fixture",owner.Sid,owner);
    Check(readback.Job is {State:JobState.COMPLETED,Execution:{ExecutionContext:CommandContext.USER,AuditPersisted:true,ExecutionSessionId:1}},"user.persisted_context");
    Check(routing.Dispatch(req,"fixture",owner.Sid,laterLogin).Status==StatusCode.CONFLICT,"user.no_replay_new_logon");
    // Real PowerShell result through the real coordinator and real SQLite.
    // The fixture intentionally does not impersonate another Windows logon.
    foreach(string errorScript in new[]{
        "[Console]::Error.Write(('e'*15999))",
        "[Console]::Error.Write(('e'*16000))",
        "[Console]::Out.Write(('o'*30000)); [Console]::Error.Write(('e'*30000))"})
    {
        string key="bounded-output-"+Guid.NewGuid().ToString("N");
        routing.DispatchWorker(new(WorkerAction.poll),owner);
        var accepted=routing.Dispatch(req with {Key=key,Command=errorScript},"fixture",owner.Sid,owner);
        var claim=routing.DispatchWorker(new(WorkerAction.poll),owner).Work;
        Check(accepted.Status==StatusCode.OK && claim is not null && claim.JobId==accepted.Job?.JobId,
            "h1.real_output_claim");
        if(claim is null)throw new InvalidOperationException("Missing bounded output lease");
        var actual=await Run(claim.Command);
        Check(actual.Status==ExecutionState.COMPLETED && actual.Stderr.Length<=PowerShellRunner.OutputLimit,
            "h1.real_runner_contract");
        Check(routing.DispatchWorker(new(WorkerAction.complete,claim.LeaseId,actual),owner).Status==StatusCode.OK,
            "h1.real_completion_ack");
        var saved=routing.Dispatch(new(AdminOperation.Job,JobAction.logs,Key:key),"fixture",owner.Sid,owner);
        Check(saved.Job is {State:JobState.COMPLETED,Execution:{AuditPersisted:true}} &&
            saved.Job.Execution.Stderr==actual.Stderr && saved.Job.Execution.Stdout==actual.Stdout &&
            saved.Job.Execution.OutputTruncated==actual.OutputTruncated,"h1.real_output_persisted");
    }
}
finally{Directory.Delete(routingRoot,true);}


// Receipt matching is separate from live remote acceptance.
var observed=DateTimeOffset.UtcNow;
var serviceFixture=new AdminStatus(StatusCode.OK,Guid.NewGuid().ToString("N"),123,StartedUtc:observed.AddMinutes(-5));
var receiptFixture=new AcceptanceReceipt(1,new string('a',40),"fixture",observed,123,serviceFixture.StartedUtc!.Value,
    456,observed.AddMinutes(-4),Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),
    Guid.NewGuid().ToString("N"),1,16,16,148,"image/png");
Check(AcceptanceEvidence.Validate(receiptFixture,receiptFixture.Commit,serviceFixture,456,receiptFixture.TunnelStartedUtc,1,observed)==EvidenceCode.MATCHED,
    "doctor.external_receipt_matches_generation");
Check(AcceptanceEvidence.Validate(receiptFixture with {TunnelPid=789},receiptFixture.Commit,serviceFixture,456,receiptFixture.TunnelStartedUtc,1,observed)==EvidenceCode.STALE_OR_DIFFERENT_INSTALLATION,
    "doctor.receipt_rejects_other_tunnel");
Check(AcceptanceEvidence.Validate(receiptFixture with {ScreenshotSessionId=2},receiptFixture.Commit,serviceFixture,456,receiptFixture.TunnelStartedUtc,1,observed)==EvidenceCode.INVALID,
    "doctor.receipt_rejects_other_desktop");


// Deterministic budget arithmetic: no fixed worker count, fail closed on memory pressure.
const ulong gib=1024UL*1024UL*1024UL;
Check(ExecutionBudget.Evaluate(5,32*gib,20*gib,20*gib).CanStart,"parallel.capacity_above_four");
Check(!ExecutionBudget.Evaluate(5,32*gib,1*gib,20*gib).CanStart,"parallel.physical_pressure");
Check(!ExecutionBudget.Evaluate(5,32*gib,20*gib,1*gib).CanStart,"parallel.commit_pressure");
Check(ExecutionBudget.Evaluate(40,128*gib,100*gib,100*gib).CanStart,"parallel.no_fixed_slot_count");
ulong admissionThreshold=(32*gib)/20+ExecutionBudget.Reservation;
Check(ExecutionBudget.Evaluate(0,32*gib,2*gib,20*gib) is {CanStart:true,ReserveBytes:var loweredReserve} &&
    loweredReserve==32*gib/20,"budget.lowered_five_percent_large_machine");
Check(ExecutionBudget.Evaluate(0,8*gib,2*gib,20*gib).ReserveBytes==gib,
    "budget.preserve_one_gib_minimum");
Check(ExecutionBudget.Evaluate(0,32*gib,admissionThreshold,20*gib).CanStart &&
    !ExecutionBudget.Evaluate(0,32*gib,admissionThreshold-1,20*gib).CanStart,
    "budget.physical_exact_admission_boundary");
Check(!ExecutionBudget.Evaluate(0,32*gib,20*gib,admissionThreshold-1).CanStart,
    "budget.commit_exact_admission_boundary");


// USER fault injection through the production execution loop (no live service or real commands).
Check(UserWorker.RemainingAuthorityMs(1000,13000)==0 &&
    UserWorker.RemainingAuthorityMs(1000,12000)==1000,"user.authority_from_request_dispatch");
Check(UserWorker.IsTransient(StatusCode.IPC_TIMEOUT) &&
    !UserWorker.IsTransient(StatusCode.LEASE_LOST) &&
    !UserWorker.IsTransient(StatusCode.ACCESS_DENIED),"user.authoritative_denial_not_retryable");
var workerFixture=new UserWork("lease","job",1,"fixture",30,Environment.SystemDirectory);
var finished=new TaskCompletionSource<ExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
int pulseCount=0,launches=0;
ExecutionResult? delivered=null;
WorkerObservation? workerObservation=null;
Task<ExecutionResult> FakeRunner(string cmd,int timeout,string? cwd,CancellationToken token)
{
    launches++;
    token.Register(()=>finished.TrySetResult(new(ExecutionState.CANCELLED_UNKNOWN_EFFECT,"fixture-hash",1)));
    return finished.Task;
}
Task<AdminResponse> TransientThenSuccess(WorkerRequest request,CancellationToken token)
{
    if(request.Action==WorkerAction.complete)
    {
        delivered=request.Result;
        return Task.FromResult(new AdminResponse(StatusCode.OK,"complete",Worker:new(StatusCode.OK)));
    }
    pulseCount++;
    if(pulseCount==1)return Task.FromResult(new AdminResponse(StatusCode.IPC_TIMEOUT,"injected-timeout"));
    finished.TrySetResult(new(ExecutionState.COMPLETED,"fixture-hash",10,0));
    return Task.FromResult(new AdminResponse(StatusCode.OK,"pulse",Worker:new(StatusCode.OK)));
}
await UserWorker.ExecuteAsync(workerFixture,Environment.TickCount64,TransientThenSuccess,FakeRunner,
    CancellationToken.None,TimeSpan.FromMilliseconds(1),observe:value=>workerObservation=value).WaitAsync(TimeSpan.FromSeconds(5));
Check(launches==1 && pulseCount==2 &&
    delivered is {Status:ExecutionState.COMPLETED,Stage:null} &&
    workerObservation is {Signal:WorkerStopSignal.NONE,TransientFailures:1,LastFailure:StatusCode.IPC_TIMEOUT,Correlation:"injected-timeout"},
    "user.transient_ipc_does_not_cancel_or_replay");

finished=new(TaskCreationOptions.RunContinuationsAsynchronously);
delivered=null;
launches=0;
Task<AdminResponse> LeaseLost(WorkerRequest request,CancellationToken token)
{
    if(request.Action==WorkerAction.complete)delivered=request.Result;
    return Task.FromResult(new AdminResponse(StatusCode.LEASE_LOST,"lease-lost",Worker:new(StatusCode.LEASE_LOST)));
}
await UserWorker.ExecuteAsync(workerFixture,Environment.TickCount64,LeaseLost,FakeRunner,
    CancellationToken.None,TimeSpan.FromMilliseconds(1)).WaitAsync(TimeSpan.FromSeconds(5));
Check(launches==1 && delivered is {Status:ExecutionState.CANCELLED_UNKNOWN_EFFECT,
    Stage:"USER_LEASE_LOST"},"user.authoritative_lease_loss_stops");

finished=new(TaskCreationOptions.RunContinuationsAsynchronously);
delivered=null;
launches=0;
Task<AdminResponse> ExplicitCancel(WorkerRequest request,CancellationToken token)
{
    if(request.Action==WorkerAction.complete)delivered=request.Result;
    return Task.FromResult(new AdminResponse(StatusCode.OK,"cancel",Worker:new(StatusCode.OK,CancelRequested:true)));
}
await UserWorker.ExecuteAsync(workerFixture,Environment.TickCount64,ExplicitCancel,FakeRunner,
    CancellationToken.None,TimeSpan.FromMilliseconds(1)).WaitAsync(TimeSpan.FromSeconds(5));
Check(launches==1 && delivered is {Status:ExecutionState.CANCELLED_UNKNOWN_EFFECT,
    Stage:"USER_OPERATOR_CANCELLED"},"user.explicit_cancel_distinguished");

using(var siblingLifetime=new CancellationTokenSource())
{
    var sibling=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var siblingTask=UserWorker.RunIsolatedAsync(()=>sibling.Task,_=>siblingLifetime.Cancel());
    Exception? observedFault=null;
    await UserWorker.RunIsolatedAsync(()=>Task.FromException(new IOException("injected")),e=>observedFault=e);
    Check(observedFault is IOException && !siblingTask.IsCompleted && !siblingLifetime.IsCancellationRequested,
        "user.faulted_job_does_not_cancel_sibling");
    sibling.SetResult();
    await siblingTask;
}

// Completion delivery: exact durable readback after lost ACK, bounded identical
// retry when no commit is observed, and no false success for a different result.
var receiptPayload=new ExecutionResult(ExecutionState.COMPLETED,"receipt-hash",33,0,"one");
var persistedReceipt=receiptPayload with {AuditPersisted=true,ExecutionContext=CommandContext.USER,
    ExecutionSid="fixture",ExecutionSessionId=1,QueueWaitMs=2,StepStartedUtc="start",StepFinishedUtc="finish"};
int completionCalls=0,readbackCalls=0;
Task<AdminResponse> LostAcknowledgement(WorkerRequest request,CancellationToken token)
{
    completionCalls++;
    Check(request.Action==WorkerAction.complete && request.Result==receiptPayload,"user.completion_retry_same_payload");
    return Task.FromResult(new AdminResponse(StatusCode.IPC_TIMEOUT,"lost-ack"));
}
Task<ExecutionResult?> CommittedReadback(UserWork work,CancellationToken token)
{
    readbackCalls++;
    return Task.FromResult<ExecutionResult?>(persistedReceipt);
}
var unconfirmed=await UserWorker.ConfirmCompletionAsync(workerFixture,receiptPayload,LostAcknowledgement,
    CommittedReadback,CancellationToken.None);
Check(unconfirmed is null && completionCalls==1 && readbackCalls==1,"user.lost_ack_exact_durable_readback");
completionCalls=0;
readbackCalls=0;
Task<ExecutionResult?> DifferentReadback(UserWork work,CancellationToken token)
{
    readbackCalls++;
    return Task.FromResult<ExecutionResult?>(persistedReceipt with {Stdout="different"});
}
unconfirmed=await UserWorker.ConfirmCompletionAsync(workerFixture,receiptPayload,LostAcknowledgement,
    DifferentReadback,CancellationToken.None);
Check(unconfirmed?.Code==StatusCode.IPC_TIMEOUT && completionCalls==2 && readbackCalls==2,
    "user.completion_mismatch_never_confirmed");
Check(!UserWorker.CompletionMatches(receiptPayload,persistedReceipt with {AuditPersisted=false}),
    "user.completion_not_durable_never_confirmed");

// Frozen public/durable schema-4 field set from fed40dc0. The old strict JSON
// reader rejects unknown members. Keep new causes in the existing stage string.
var rollbackShape=System.Text.Json.JsonSerializer.Serialize(delivered,PipeWire.Json);
using(var rollbackJson=System.Text.Json.JsonDocument.Parse(rollbackShape))
{
    string[] legacyFields=["status","command_sha256","elapsed_ms","exit_code","stdout","stderr",
        "warnings","output_truncated","stage","native_error","job_id","operation_key","correlation_id",
        "audit_persisted","execution_context","execution_sid","execution_session_id","queue_wait_ms",
        "step_started_utc","step_finished_utc","timing","pruned_output"];
    Check(rollbackJson.RootElement.EnumerateObject().Select(p=>p.Name).ToHashSet(StringComparer.Ordinal)
        .SetEquals(legacyFields) && rollbackJson.RootElement.GetProperty("stage").GetString()=="USER_OPERATOR_CANCELLED",
        "h1.schema4_durable_result_exact_legacy_shape");
}

// Actual elapsed watchdog budget: a late successful heartbeat may not revive
// an execution after the last confirmed authority has expired.
var expiredClaim=new UserWork("lease-expiry","job-expiry",1,"fixture",60,Environment.SystemDirectory);
var stopped=new TaskCompletionSource<ExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
ExecutionResult? lateResult=null;
int lateHeartbeat=0;
Task<ExecutionResult> AuthorityRunner(string command,int timeout,string? cwd,CancellationToken cancel)
{
    cancel.Register(()=>stopped.TrySetResult(new(ExecutionState.CANCELLED_UNKNOWN_EFFECT,"fixture-hash",1,Stage:"PROCESS_INTERRUPTED")));
    return stopped.Task;
}
async Task<AdminResponse> LateHeartbeat(WorkerRequest request,CancellationToken cancel)
{
    if(request.Action==WorkerAction.complete)
    {
        lateResult=request.Result;
        return new(StatusCode.OK,"complete",Worker:new(StatusCode.OK));
    }
    lateHeartbeat++;
    await Task.Delay(3000,cancel);
    return new(StatusCode.OK,"late-success",Worker:new(StatusCode.OK));
}
await UserWorker.ExecuteAsync(expiredClaim,Environment.TickCount64-10000,LateHeartbeat,AuthorityRunner,
    CancellationToken.None,TimeSpan.FromMilliseconds(10)).WaitAsync(TimeSpan.FromSeconds(10));
Check(lateHeartbeat==1 && lateResult is {Status:ExecutionState.CANCELLED_UNKNOWN_EFFECT,Stage:"USER_WATCHDOG_EXPIRED"},
    "h1.late_heartbeat_cannot_revive_expired_authority");

// An isolated, debugging-enabled Chrome profile on the Windows CI machine;
// never attach to or manipulate a user's Chrome profile or desktop.

internal sealed class CancelOnFirstReadStream(byte[] bytes,CancellationTokenSource cancellation)
    :MemoryStream(bytes,false)
{
    private bool first=true;
    public override int Read(byte[] buffer,int offset,int count)
    {
        int received=base.Read(buffer,offset,count);
        if(first){first=false;cancellation.Cancel();}
        return received;
    }
}
