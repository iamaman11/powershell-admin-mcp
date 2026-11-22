using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

// One USER-session child owns Chrome's consented CDP connection. Profiles in
// the same browser process share that connection; each request still verifies
// its own native window and explicit profile selector. No extra service.
internal sealed record ChromeBrokerRequest(WindowInspectRequest? Inspect = null,
    WindowActionRequest? Action = null, ChromeConnectionRequest? Connection = null)
{
    internal string WindowId => Inspect?.WindowId ?? Action?.WindowId ?? Connection?.WindowId ?? "";
    internal string? Profile => Inspect?.ExpectedProfile ?? Action?.ExpectedProfile ?? Connection?.ExpectedProfile;
    internal bool Valid => (Inspect is not null ? 1 : 0) + (Action is not null ? 1 : 0) +
        (Connection is not null ? 1 : 0) == 1 &&
        WindowCatalog.TryParseId(WindowId, out _) &&
        Profile is { Length: >= 1 and <= 100 } && !string.IsNullOrWhiteSpace(Profile) &&
        (Inspect is null || Inspect.Scope is WindowInspectScope.TABS or WindowInspectScope.TAB) &&
        (Action is null || WindowChromeCdp.WellFormedTabElementRef(Action.ElementId) ||
            WindowInspector.WellFormedElementRef(Action.ElementId, out _)) &&
        (Connection is null || Connection.Action is ChromeConnectionAction.REQUEST or
            ChromeConnectionAction.STATUS);
}

internal sealed record ChromeBrokerReply(WindowInspectResult? Inspect = null,
    WindowActionResult? Action = null, ChromeConnectionResult? Connection = null);

internal static class WindowChromeBroker
{
    private const int MaxReplyChars = 256000;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Process? _worker;
    private static string? _binding;
    private static bool _firstExchange;
    private static bool _hadConnected;
    private static string? _lastConnectedBinding;

    static WindowChromeBroker() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();

    internal static bool SameBrowser(string windowId, string otherWindowId) =>
        WindowCatalog.TryParseId(windowId, out var first) &&
        WindowCatalog.TryParseId(otherWindowId, out var second) &&
        first.ProcessId == second.ProcessId && first.StartFileTimeUtc == second.StartFileTimeUtc &&
        first.SessionId == second.SessionId;

    private static string Binding(ChromeBrokerRequest request)
    {
        WindowCatalog.TryParseId(request.WindowId, out var id);
        return $"{id.ProcessId:x}:{id.StartFileTimeUtc:x}:{id.SessionId:x}";
    }

    internal static bool ReplyMatches(ChromeBrokerRequest request, ChromeBrokerReply? reply) =>
        reply is not null && (request.Inspect is not null
            ? reply.Inspect is not null && reply.Action is null && reply.Connection is null
            : request.Action is not null
                ? reply.Action is not null && reply.Inspect is null && reply.Connection is null
                : request.Connection is not null && reply.Connection is not null &&
                    reply.Inspect is null && reply.Action is null);

    internal static async Task<ChromeConnectionResult> ConnectionAsync(ChromeConnectionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Action == ChromeConnectionAction.DISCONNECT)
        {
            try { await Gate.WaitAsync(cancellationToken); }
            catch (OperationCanceledException)
            { return new(ChromeConnectionCode.WORKER_FAILED, ChromeConnectionState.NOT_REQUESTED); }
            try
            {
                if (_binding == Binding(new(Connection: request)))
                {
                    Stop();
                    _hadConnected = false;
                    _lastConnectedBinding = null;
                }
                return new(ChromeConnectionCode.OK, ChromeConnectionState.DISCONNECTED);
            }
            finally { Gate.Release(); }
        }
        var reply = await ExchangeAsync(new(Connection: request), cancellationToken);
        return reply?.Connection ?? new(ChromeConnectionCode.WORKER_FAILED,
            ChromeConnectionState.NOT_REQUESTED, Detail: "CDP_BROKER_UNAVAILABLE");
    }

    internal static async Task<WindowInspectResult> InspectAsync(WindowInspectRequest request,
        CancellationToken cancellationToken)
    {
        var reply = await ExchangeAsync(new(Inspect: request), cancellationToken);
        return reply?.Inspect ?? new(WindowInspectCode.WORKER_FAILED, [], Detail: "CDP_BROKER_UNAVAILABLE");
    }

    internal static async Task<WindowActionResult> InvokeAsync(WindowActionRequest request,
        CancellationToken cancellationToken)
    {
        var reply = await ExchangeAsync(new(Action: request), cancellationToken);
        return reply?.Action ?? new(WindowActionCode.WORKER_FAILED_UNKNOWN_EFFECT,
            EffectUnknown: true, FailureStage: WindowFailureStage.WORKER,
            Detail: "CDP_BROKER_UNAVAILABLE");
    }

    private static async Task<ChromeBrokerReply?> ExchangeAsync(ChromeBrokerRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Valid) return null;
        try { await Gate.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { return null; }
        try
        {
            string binding = Binding(request);
            if (request.Connection?.Action == ChromeConnectionAction.STATUS &&
                (_worker is null || _worker.HasExited || _binding != binding))
                return new(Connection: new(ChromeConnectionCode.OK,
                    _hadConnected && _lastConnectedBinding == binding ? ChromeConnectionState.CONNECTION_LOST :
                        ChromeConnectionState.NOT_REQUESTED,
                    ChromeConnectionNextAction.RETRY_CONNECTION,
                    FailureReason: _hadConnected && _lastConnectedBinding == binding
                        ? ChromeConnectionFailureReason.BROKER_EXITED : null,
                    LastLossReason: _hadConnected && _lastConnectedBinding == binding
                        ? ChromeConnectionFailureReason.BROKER_EXITED : null));
            if (_worker is null || _worker.HasExited || _binding != binding)
            {
                if (_binding is not null && _binding != binding)
                {
                    _hadConnected = false;
                    _lastConnectedBinding = null;
                }
                Stop();
                _worker = Start();
                _binding = _worker is null ? null : binding;
                _firstExchange = true;
            }
            if (_worker is null) return null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            int timeout = request.Connection is not null ? 10000 : _firstExchange
                ? 30000 + (request.Action?.ObservationTimeoutMs ?? 0)
                : request.Action is { } action ? action.ObservationTimeoutMs + 6000 : 8000;
            deadline.CancelAfter(timeout);
            try
            {
                string payload = JsonSerializer.Serialize(request, PipeWire.Json);
                await _worker.StandardInput.WriteLineAsync(payload.AsMemory(), deadline.Token);
                await _worker.StandardInput.FlushAsync(deadline.Token);
                string? json = await _worker.StandardOutput.ReadLineAsync(deadline.Token);
                if (json is null || json.Length > MaxReplyChars)
                    throw new JsonException("CDP_BROKER_OUTPUT_INVALID");
                var reply = JsonSerializer.Deserialize<ChromeBrokerReply>(json, PipeWire.Json);
                if (!ReplyMatches(request, reply)) throw new JsonException("CDP_BROKER_REPLY_MISMATCH");
                if (reply?.Connection?.State == ChromeConnectionState.CONNECTED ||
                    reply?.Inspect?.Status == WindowInspectCode.COMPLETED ||
                    reply?.Action?.ExecutionMethod == WindowExecutionMethod.CHROME_CDP)
                {
                    _hadConnected = true;
                    _lastConnectedBinding = binding;
                }
                _firstExchange = false;
                return reply;
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or
                InvalidOperationException or JsonException)
            {
                // Never replay an action whose dispatch may have happened.
                Stop();
                return null;
            }
        }
        finally { Gate.Release(); }
    }

    private static Process? Start()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return null;
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("window-cdp-worker");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.Environment.Remove("CONTROL_PLANE_API_KEY");
        start.Environment.Remove("OPENAI_API_KEY");
        var process = new Process { StartInfo = start };
        try { if (process.Start()) return process; }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
        process.Dispose();
        return null;
    }

    private static void Stop()
    {
        var process = _worker;
        _worker = null;
        _binding = null;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
        process.Dispose();
    }

    internal static async Task RunWorkerAsync(int parentPid)
    {
        if (parentPid <= 0) return;
        using var parent = TryParent(parentPid);
        if (parent is null) return;
        Task parentExit = parent.WaitForExitAsync();
        string? boundWindow = null;
        try
        {
            while (true)
            {
                Task<string?> read = Console.In.ReadLineAsync();
                Task finished = await Task.WhenAny(read, parentExit);
                if (finished != read) return;
                string? json = await read;
                if (json is null) return;
                ChromeBrokerReply reply;
                try
                {
                    var request = json.Length <= 30000
                        ? JsonSerializer.Deserialize<ChromeBrokerRequest>(json, PipeWire.Json) : null;
                    if (request is null || !request.Valid || boundWindow is not null &&
                        !SameBrowser(boundWindow, request.WindowId))
                        reply = new(Inspect: new(WindowInspectCode.INVALID_ARGUMENT, []));
                    else
                    {
                        boundWindow ??= request.WindowId;
                        reply = request.Connection is { } connection
                            ? new(Connection: WindowChromeCdp.ConnectInWorker(connection))
                            : request.Inspect is { } inspect
                            ? new(Inspect: WindowInspector.InspectInWorker(inspect.WindowId,
                                inspect.MaxDepth, inspect.MaxElements, inspect.StartPath,
                                inspect.StartElementId, inspect.Offset, inspect.IncludeDetails,
                                inspect.NameContains, inspect.RoleFilter, inspect.WaitMs,
                                inspect.Scope, inspect.TabId, inspect.ExpectedProfile,
                                inspect.ExpectedOrigin, inspect.ExpectedUrl))
                            : new(Action: WindowAction.InvokeInWorker(request.Action!));
                    }
                }
                catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
                { reply = new(Inspect: new(WindowInspectCode.WORKER_FAILED, [], Detail: e.GetType().Name)); }
                Console.Out.WriteLine(JsonSerializer.Serialize(reply, PipeWire.Json));
                await Console.Out.FlushAsync();
            }
        }
        finally { WindowChromeCdp.CloseTabConnection(); }
    }

    private static Process? TryParent(int pid)
    {
        try { return Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; }
    }
}
