using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

// Only the existing USER TunnelHost may spawn the consented CDP child.
// MCP stdio processes reach it through the USER-session pipe, never by
// constructing their own broker/consent state.
internal sealed record ChromeBrokerRequest(WindowInspectRequest? Inspect = null,
    WindowActionRequest? Action = null, ChromeConnectionRequest? Connection = null,
    string? TraceId = null, ChromeTabRequest? TabManage = null)
{
    internal string WindowId => Inspect?.WindowId ?? Action?.WindowId ?? Connection?.WindowId ??
        TabManage?.WindowId ?? "";
    internal string? Profile => Inspect?.ExpectedProfile ?? Action?.ExpectedProfile ??
        Connection?.ExpectedProfile ?? TabManage?.ExpectedProfile;
    internal bool Valid => (Inspect is not null ? 1 : 0) + (Action is not null ? 1 : 0) +
        (Connection is not null ? 1 : 0) + (TabManage is not null ? 1 : 0) == 1 &&
        WindowCatalog.TryParseId(WindowId, out _) &&
        (TraceId is null || Guid.TryParseExact(TraceId, "N", out _)) &&
        (Profile is { Length: >= 1 and <= 100 } && !string.IsNullOrWhiteSpace(Profile) ||
            Action is { ExpectedProfile: null } actionWithoutProfile &&
            WindowInspector.WellFormedElementRef(actionWithoutProfile.ElementId, out _)) &&
        (Inspect is null || Inspect.Scope is WindowInspectScope.TABS or WindowInspectScope.TAB) &&
        (Action is null || !Action.ResolveOnly &&
            (WindowChromeCdp.WellFormedTabElementRef(Action.ElementId) ||
             WindowInspector.WellFormedElementRef(Action.ElementId, out _))) &&
        (TabManage is null || WindowChromeCdp.ValidTabRequest(TabManage)) &&
        (Connection is null || Connection.Action is ChromeConnectionAction.REQUEST or
            ChromeConnectionAction.STATUS or ChromeConnectionAction.DISCONNECT &&
            Enum.IsDefined(Connection.Mode) &&
            (Connection.Mode == ChromeConnectionMode.USER_BROWSER
                ? Connection.ManagedProfileId is null
                : ChromeManagedProfile.ValidId(Connection.ManagedProfileId) &&
                    !Connection.ActivateWindowForConsent));
}

internal sealed record ChromeBrokerReply(WindowInspectResult? Inspect = null,
    WindowActionResult? Action = null, ChromeConnectionResult? Connection = null,
    int? OwnerPid = null, string? TraceId = null, ChromeTabResult? TabManage = null);

internal static class WindowChromeBroker
{
    private const int MaxReplyChars = 256000;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Process? _worker;
    private static string? _binding;
    private static bool _firstExchange;
    private static bool _hadConnected;
    private static string? _lastConnectedBinding;
    private static bool _dedicatedWorker;

    internal static bool IsDedicatedWorker => _dedicatedWorker;

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
        reply is not null && (request.TraceId is null || request.TraceId == reply.TraceId) &&
        (request.TabManage is not null
            ? reply.TabManage is not null && reply.Action is null && reply.Inspect is null &&
                reply.Connection is null :
        (request.Inspect is not null
            ? reply.Inspect is not null && reply.Action is null && reply.Connection is null &&
                reply.TabManage is null
            : request.Action is not null
                ? reply.Action is not null && reply.Inspect is null && reply.Connection is null &&
                    reply.TabManage is null
                : request.Connection is not null && reply.Connection is not null &&
                    reply.Inspect is null && reply.Action is null && reply.TabManage is null));

    internal static int ExchangeTimeoutMs(ChromeBrokerRequest request, bool firstExchange) =>
        request.Connection is not null ? 10000 : request.TabManage is not null ? 26000 : firstExchange
            ? 30000 + (request.Action?.ObservationTimeoutMs ?? 0)
            : request.Action is { } action ? action.ObservationTimeoutMs + 10000 : 26000;

    internal static async Task<ChromeConnectionResult> ConnectionAsync(ChromeConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var reply = await RouteAsync(new(Connection: request), cancellationToken);
        return reply?.Connection is { } connection
            ? connection with { BrokerOwnerPid = reply.OwnerPid, TraceId = reply.TraceId,
                Mode = request.Mode, ManagedProfileId = request.ManagedProfileId }
            : new(ChromeConnectionCode.WORKER_FAILED,
                ChromeConnectionState.NOT_REQUESTED, Detail: "CDP_OWNER_UNAVAILABLE");
    }

    internal static async Task<WindowInspectResult> InspectAsync(WindowInspectRequest request,
        CancellationToken cancellationToken)
    {
        var reply = await RouteAsync(new(Inspect: request), cancellationToken);
        return reply?.Inspect is { } inspect
            ? inspect with { BrokerOwnerPid = reply.OwnerPid, TraceId = reply.TraceId }
            : new(WindowInspectCode.WORKER_FAILED, [], Detail: "CDP_OWNER_UNAVAILABLE");
    }

    internal static async Task<WindowActionResult> InvokeAsync(WindowActionRequest request,
        CancellationToken cancellationToken, string? traceId = null)
    {
        var reply = await RouteAsync(new(Action: request), cancellationToken, traceId);
        return reply?.Action is { } action
            ? action with { BrokerOwnerPid = reply.OwnerPid, TraceId = reply.TraceId }
            : new(WindowActionCode.WORKER_FAILED_UNKNOWN_EFFECT,
            EffectUnknown: true, FailureStage: WindowFailureStage.WORKER,
            Detail: "CDP_OWNER_UNAVAILABLE");
    }

    internal static async Task<ChromeTabResult> ManageTabAsync(ChromeTabRequest request,
        CancellationToken cancellationToken, string traceId)
    {
        var reply = await RouteAsync(new(TabManage: request), cancellationToken, traceId);
        return reply?.TabManage is { } result
            ? result with { BrokerOwnerPid = reply.OwnerPid, TraceId = reply.TraceId }
            : new(request.Action == ChromeTabAction.LIST ? ChromeTabCode.CDP_UNAVAILABLE :
                ChromeTabCode.EFFECT_UNKNOWN, request.Action, Detail: "CDP_OWNER_UNAVAILABLE");
    }

    private static Task<ChromeBrokerReply?> RouteAsync(ChromeBrokerRequest request,
        CancellationToken cancellationToken, string? traceId = null)
    {
        request = request with { TraceId = traceId ?? Guid.NewGuid().ToString("N") };
        return ChromeBrokerPipe.IsOwner
            ? ExchangeOwnedAsync(request, cancellationToken)
            : ChromeBrokerPipe.ExchangeAsync(request, cancellationToken);
    }

    internal static async Task<ChromeBrokerReply?> ExchangeOwnedAsync(ChromeBrokerRequest request,
        CancellationToken cancellationToken)
    {
        if (!ChromeBrokerPipe.IsOwner || !request.Valid) return null;
        if (request.Connection?.Action != ChromeConnectionAction.DISCONNECT)
        {
            var reply = await ExchangeLocalAsync(request, cancellationToken);
            return reply is null ? null : reply with { TraceId = request.TraceId };
        }
        try { await Gate.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { return null; }
        try
        {
            if (_binding == Binding(request))
            {
                Stop();
                _hadConnected = false;
                _lastConnectedBinding = null;
            }
            return new(Connection: new(ChromeConnectionCode.OK, ChromeConnectionState.DISCONNECTED),
                TraceId: request.TraceId);
        }
        finally { Gate.Release(); }
    }

    internal static void StopOwnedWorker() => Stop();

    private static async Task<ChromeBrokerReply?> ExchangeLocalAsync(ChromeBrokerRequest request,
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
            // New address-only tab inspections may take 18s while Chrome
            // creates its AX tree; background actions can take
            // observation_timeout+5.5s. Leave serialization/worker headroom:
            // killing the owner first would discard the consented socket and
            // turn a known pre-dispatch timeout into an unknown-effect result.
            int timeout = ExchangeTimeoutMs(request, _firstExchange);
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
                    reply?.Action?.ExecutionMethod == WindowExecutionMethod.CHROME_CDP ||
                    reply?.TabManage?.Status == ChromeTabCode.COMPLETED)
                {
                    _hadConnected = true;
                    _lastConnectedBinding = binding;
                }
                _firstExchange = false;
                return reply! with { TraceId = request.TraceId };
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
        _dedicatedWorker = true;
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
                            : request.TabManage is { } tab
                            ? new(TabManage: WindowChromeCdp.ManageTabInWorker(tab))
                            : request.Inspect is { } inspect
                            ? new(Inspect: WindowInspector.InspectInWorker(inspect.WindowId,
                                inspect.MaxDepth, inspect.MaxElements, inspect.StartPath,
                                inspect.StartElementId, inspect.Offset, inspect.IncludeDetails,
                                inspect.NameContains, inspect.RoleFilter, inspect.WaitMs,
                                inspect.Scope, inspect.TabId, inspect.ExpectedProfile,
                                inspect.ExpectedOrigin, inspect.ExpectedUrl))
                            : new(Action: InvokeBoundAction(request.Action!));
                    }
                    reply = reply with { TraceId = request?.TraceId,
                        Inspect = reply.Inspect is { } inspectedReply ? inspectedReply with
                            { SessionGeneration = WindowChromeCdp.CurrentGeneration } : null,
                        Action = reply.Action is { } action ? action with
                            { SessionGeneration = WindowChromeCdp.CurrentGeneration } : null,
                        TabManage = reply.TabManage is { } tabResult ? tabResult with
                            { SessionGeneration = WindowChromeCdp.CurrentGeneration } : null };
                }
                catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
                { reply = new(Inspect: new(WindowInspectCode.WORKER_FAILED, [], Detail: e.GetType().Name)); }
                Console.Out.WriteLine(JsonSerializer.Serialize(reply, PipeWire.Json));
                await Console.Out.FlushAsync();
            }
        }
        finally
        {
            WindowChromeCdp.CloseTabConnection();
            _dedicatedWorker = false;
        }
    }

    private static WindowActionResult InvokeBoundAction(WindowActionRequest request)
    {
        if (request.ExpectedProfile is null)
        {
            using var self = Process.GetCurrentProcess();
            var profile = ChromeProfileCatalog.ReadWindowProfile(request.WindowId, self.SessionId);
            if (profile is null)
                return new(WindowActionCode.CDP_CONTEXT_MISMATCH,
                    FailureStage: WindowFailureStage.CDP_TARGET, Detail: "PROFILE_NOT_VERIFIED");
            request = request with { ExpectedProfile = profile.ProfileId ?? profile.Email ??
                profile.DisplayName };
        }
        return WindowAction.InvokeInWorker(request);
    }

    private static Process? TryParent(int pid)
    {
        try { return Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; }
    }
}
