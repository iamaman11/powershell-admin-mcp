using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Automation;

// An opt-in background path for web content in the user's existing Chrome.
// Chrome itself owns the permission prompt and DevToolsActivePort. Never start
// Chrome, alter its flags, read browser credentials, or select a tab by URL alone.
internal static partial class WindowChromeCdp
{
    private const int MaxMessageBytes = 1_048_576;
    private const int BoundsTolerance = 40;

    internal static bool TryReadEndpoint(string? contents, out Uri? endpoint)
    {
        endpoint = null;
        if (contents is null || contents.Length > 256)
            return false;
        var lines = contents.Split('\n', StringSplitOptions.TrimEntries);
        if (lines.Length < 2 || !ushort.TryParse(lines[0], NumberStyles.None,
            CultureInfo.InvariantCulture, out ushort port) || port == 0 ||
            !lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal) ||
            lines[1].Length is < 25 or > 100 ||
            lines[1]["/devtools/browser/".Length..].Any(c => !Uri.IsHexDigit(c) && c != '-'))
            return false;
        endpoint = new Uri($"ws://127.0.0.1:{port}{lines[1]}");
        return true;
    }

    internal static bool MatchesWindow(string nativeTitle, string targetTitle,
        WindowBounds native, WindowBounds chrome, double dpiScale)
    {
        const string suffix = " - Google Chrome";
        int marker = nativeTitle.LastIndexOf(suffix, StringComparison.Ordinal);
        if (marker <= 0 || !string.Equals(nativeTitle[..marker], targetTitle, StringComparison.Ordinal) ||
            chrome.Width <= 0 || chrome.Height <= 0 || native.Width <= 0 || native.Height <= 0)
            return false;
        static bool Close(int a, double b) => Math.Abs(a - b) <= BoundsTolerance;
        bool MatchesAt(double scale) =>
            Close(native.X, chrome.X * scale) && Close(native.Y, chrome.Y * scale) &&
            Close(native.Width, chrome.Width * scale) && Close(native.Height, chrome.Height * scale);
        return MatchesAt(1) || dpiScale is >= 1 and <= 4 && MatchesAt(dpiScale);
    }

    internal static bool TryPoint(JsonElement content, JsonElement viewport, out double x, out double y)
    {
        x = y = 0;
        if (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() != 8 ||
            viewport.ValueKind != JsonValueKind.Object ||
            !TryNumber(viewport, "clientWidth", out double width) ||
            !TryNumber(viewport, "clientHeight", out double height) ||
            width <= 0 || height <= 0) return false;
        double[] quad = new double[8];
        int index = 0;
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out quad[index]) ||
                !double.IsFinite(quad[index])) return false;
            index++;
        }
        x = (quad[0] + quad[2] + quad[4] + quad[6]) / 4;
        y = (quad[1] + quad[3] + quad[5] + quad[7]) / 4;
        return x > 0 && y > 0 && x < width && y < height &&
            Math.Abs(quad[0] - quad[2]) > 1 && Math.Abs(quad[1] - quad[7]) > 1;
    }

    private static bool TryNumber(JsonElement obj, string name, out double number)
    {
        number = 0;
        return obj.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number &&
            item.TryGetDouble(out number) && double.IsFinite(number);
    }

    // Null means the target is not Chrome web content or remote debugging was
    // not enabled; preserve existing native UIA/physical behavior in that case.
    internal static WindowActionResult? TryDispatch(IntPtr hwnd, AutomationElement target,
        WindowActionRequest request, Stopwatch clock)
    {
        // Disposable UIA/locator workers cannot initiate Chrome consent or
        // acquire their own CDP socket, even for a missing expected_profile.
        if (!WindowChromeBroker.IsDedicatedWorker) return null;
        if (request.Action is not (WindowActionKind.CLICK or WindowActionKind.DOUBLE_CLICK or
            WindowActionKind.RIGHT_CLICK or WindowActionKind.KEY_PRESS or
            WindowActionKind.TYPE_TEXT or WindowActionKind.SET_VALUE or
            WindowActionKind.HOVER or WindowActionKind.SCROLL) ||
            request.Action==WindowActionKind.KEY_PRESS && request.VirtualKey is not null ||
            !TryWebRole(target, out string? role, out string? name) ||
            !GetWindowRect(hwnd, out var rect)) return null;
        WindowCatalog.TryParseId(request.WindowId, out var identity);
        try
        {
            using var owner = Process.GetProcessById(identity.ProcessId);
            if (!owner.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase)) return null;
        }
        catch (ArgumentException) { return null; }
        catch (Win32Exception) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (System.Security.SecurityException) { return null; }
        if (!TryEndpoint(out var endpoint, out string endpointFailure))
        {
            if (endpointFailure == "CHROME_REMOTE_DEBUGGING_NOT_ENABLED") return null;
            return new(WindowActionCode.CDP_UNAVAILABLE, FailureStage: WindowFailureStage.CDP_CONNECT,
                Detail: endpointFailure, ElapsedMs: clock.ElapsedMilliseconds);
        }
        // Only windows.chrome_connection(REQUEST) may initiate Chrome consent.
        var connection = ConnectionState(endpoint!, request: false, hwnd);
        if (connection == ChromeConnectionState.CONSENT_PENDING)
            return new(WindowActionCode.CDP_CONSENT_PENDING,
                FailureStage: WindowFailureStage.CDP_CONNECT, Detail: "ALLOW_CHROME_PROMPT",
                ElapsedMs: clock.ElapsedMilliseconds);
        if (connection != ChromeConnectionState.CONNECTED)
            return new(WindowActionCode.CDP_UNAVAILABLE,
                FailureStage: WindowFailureStage.CDP_CONNECT, Detail: connection.ToString(),
                ElapsedMs: clock.ElapsedMilliseconds);
        try
        {
            var bounds = new WindowBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            return DispatchAsync(endpoint!, hwnd, target, bounds, name!, role!, request, clock)
                .GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is WebSocketException or IOException or JsonException or
                                   OperationCanceledException or InvalidOperationException or KeyNotFoundException)
        {
            return new(WindowActionCode.CDP_UNAVAILABLE, FailureStage: WindowFailureStage.CDP_CONNECT,
                ExceptionType: e.GetType().Name,
                Detail: e is WebSocketException ws ? $"CDP_WEBSOCKET_{ws.WebSocketErrorCode}" : null,
                ElapsedMs: clock.ElapsedMilliseconds);
        }
    }

    private static bool TryWebRole(AutomationElement element, out string? role, out string? name)
    {
        role = name = null;
        var current = element.Current;
        if (!current.IsEnabled ||
            string.IsNullOrWhiteSpace(current.Name) || current.Name.Length > 128)
            return false;
        role = current.ControlType == ControlType.Button ? "button" :
            current.ControlType == ControlType.Edit ? "textbox" :
            current.ControlType == ControlType.ComboBox ? "combobox" :
            current.ControlType == ControlType.Hyperlink ? "link" :
            current.ControlType == ControlType.CheckBox ? "checkbox" :
            current.ControlType == ControlType.RadioButton ? "radio" :
            current.ControlType == ControlType.MenuItem ? "menuitem" :
            current.ControlType == ControlType.ListItem ? "listitem" :
            current.ControlType == ControlType.TabItem ? "tab" : null;
        if (!IsInsideDocument(element)) return false;
        name = current.Name;
        return true;
    }

    internal static bool IsInsideDocument(AutomationElement element)
    {
        var walker = TreeWalker.ControlViewWalker;
        AutomationElement? cursor = element;
        for (int i = 0; i < 24; i++)
        {
            cursor = walker.GetParent(cursor);
            if (cursor is null) return false;
            if (cursor.Current.ControlType == ControlType.Document) return true;
        }
        return false;
    }

    private static async Task<WindowActionResult> DispatchAsync(Uri endpoint, IntPtr hwnd,
        AutomationElement element, WindowBounds nativeBounds, string name, string? role,
        WindowActionRequest request, Stopwatch clock)
    {
        bool dispatched = false;
        var foregroundAtEntry = GetForegroundWindow();
        using var deadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(TabConnected ? 6 : 24));
        try
        {
            var cdp = await TabSessionAsync(endpoint, deadline.Token);
            var targets = await cdp.Call("Target.getTargets", new { });
            string title = GetTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title))
                return Rejected(WindowActionCode.CDP_TARGET_AMBIGUOUS);
            var candidates = new List<(string TargetId, string Url, int WindowId)>();
            foreach (var info in targets.GetProperty("targetInfos").EnumerateArray())
            {
                if (info.GetProperty("type").GetString() != "page" ||
                    !info.TryGetProperty("url", out var url) ||
                    !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var address) ||
                    address.Scheme is not ("https" or "http")) continue;
                string? targetTitle = info.GetProperty("title").GetString();
                if (targetTitle is null || !title.StartsWith(targetTitle + " - Google Chrome",
                    StringComparison.Ordinal)) continue;
                string targetId = info.GetProperty("targetId").GetString()!;
                var window = await cdp.Call("Browser.getWindowForTarget", new { targetId });
                int windowId = window.GetProperty("windowId").GetInt32();
                var bound = await cdp.Call("Browser.getWindowBounds", new { windowId });
                var b = bound.GetProperty("bounds");
                if (!b.TryGetProperty("left", out var left) ||
                    !b.TryGetProperty("top", out var top) ||
                    !b.TryGetProperty("width", out var width) ||
                    !b.TryGetProperty("height", out var height)) continue;
                uint dpi = GetDpiForWindow(hwnd);
                if (MatchesWindow(title, targetTitle,
                    nativeBounds, new(left.GetInt32(), top.GetInt32(), width.GetInt32(), height.GetInt32()),
                    dpi == 0 ? 1 : dpi / 96.0))
                    candidates.Add((targetId, url.GetString()!, windowId));
            }
            if (candidates.Count != 1) return Rejected(WindowActionCode.CDP_TARGET_AMBIGUOUS);
            var selected = candidates[0];
            string sessionId = await Attach(cdp, selected.TargetId);
            string loaderId = await Loader(cdp, sessionId);
            var document = await cdp.Call("DOM.getDocument", new { depth = 0 }, sessionId);
            int nodeId = document.GetProperty("root").GetProperty("nodeId").GetInt32();
            var matches = role is null
                ? await cdp.Call("Accessibility.queryAXTree", new { nodeId, accessibleName = name }, sessionId)
                : await cdp.Call("Accessibility.queryAXTree", new { nodeId, accessibleName = name, role }, sessionId);
            var ids = new List<int>();
            foreach (var node in matches.GetProperty("nodes").EnumerateArray())
            {
                if (node.TryGetProperty("ignored", out var ignored) && ignored.GetBoolean() ||
                    !node.TryGetProperty("name", out var nodeName) ||
                    !nodeName.TryGetProperty("value", out var nodeNameValue) ||
                    nodeNameValue.GetString() != name ||
                    !node.TryGetProperty("role", out var nodeRole) ||
                    !nodeRole.TryGetProperty("value", out var nodeRoleValue) ||
                    role is not null && nodeRoleValue.GetString() != role ||
                    !node.TryGetProperty("backendDOMNodeId", out var backend)) continue;
                int backendId = backend.GetInt32();
                if (backendId > 0 && !ids.Contains(backendId)) ids.Add(backendId);
            }
            if (ids.Count != 1) return Rejected(WindowActionCode.CDP_TARGET_AMBIGUOUS);
            int backendNodeId = ids[0];
            if (!WindowInspector.TryParseElementRef(request.ElementId, out long snapshot) ||
                !string.Equals(WindowInspector.ElementRef(element, request.WindowId, snapshot),
                    request.ElementId, StringComparison.Ordinal) ||
                !string.Equals(GetTitle(hwnd), title, StringComparison.Ordinal) ||
                WindowCatalog.ResolveId(request.WindowId, Process.GetCurrentProcess().SessionId,
                out var activeHwnd) != WindowResolveCode.OK || activeHwnd != hwnd ||
                !element.Current.IsEnabled)
                return Rejected(WindowActionCode.STALE_ELEMENT);
            string providerResponse = "CDP_BACKGROUND";
            if (request.Action is WindowActionKind.CLICK or WindowActionKind.DOUBLE_CLICK or
                WindowActionKind.RIGHT_CLICK or WindowActionKind.HOVER or WindowActionKind.SCROLL)
            {
                var box = await cdp.Call("DOM.getBoxModel", new { backendNodeId }, sessionId);
                var metrics = await cdp.Call("Page.getLayoutMetrics", new { }, sessionId);
                if (!TryPoint(box.GetProperty("model").GetProperty("content"),
                    metrics.GetProperty("cssVisualViewport"), out double x, out double y))
                    return Rejected(WindowActionCode.CDP_TARGET_NOT_VISIBLE);
                // A different desktop window is irrelevant to CDP. Within the
                // page, a popup may be a deliberate confirmation gate: never
                // bypass it with HTMLElement.click().
                var resolvedNode = await cdp.Call("DOM.resolveNode", new { backendNodeId }, sessionId);
                string objectId = resolvedNode.GetProperty("object").GetProperty("objectId").GetString()!;
                var hit = await cdp.Call("Runtime.callFunctionOn", new
                {
                    objectId,
                    functionDeclaration = "function(x,y) { const h = document.elementFromPoint(x,y); return h !== null && (h === this || this.contains(h)); }",
                    arguments = new[] { new { value = x }, new { value = y } },
                    returnByValue = true,
                    silent = true
                }, sessionId);
                bool pointerHitsTarget = hit.GetProperty("result").TryGetProperty("value", out var hitVerified) &&
                    hitVerified.ValueKind == JsonValueKind.True;
                if (pointerHitsTarget)
                {
                    var checkpoint = await DispatchCheckpoint(cdp, sessionId, hwnd, request,
                        selected.TargetId, selected.Url, selected.WindowId, loaderId);
                    if (checkpoint.Code is not null)
                        return Rejected(checkpoint.Code.Value, checkpoint.Detail);
                    dispatched = true;
                    await DispatchPointer(cdp, sessionId, request, x, y);
                }
                else return Rejected(WindowActionCode.TARGET_OCCLUDED);
            }
            else if (request.Action is WindowActionKind.TYPE_TEXT or WindowActionKind.SET_VALUE)
            {
                if (!await EditableDomNode(cdp, sessionId, backendNodeId))
                    return Rejected(WindowActionCode.UNSUPPORTED_ACTION);
                var checkpoint = await DispatchCheckpoint(cdp, sessionId, hwnd, request,
                    selected.TargetId, selected.Url, selected.WindowId, loaderId);
                if (checkpoint.Code is not null)
                    return Rejected(checkpoint.Code.Value, checkpoint.Detail);
                // Focus may itself fire a page event, so it is not a safe
                // pre-dispatch probe and must not be replayed on timeout.
                dispatched = true;
                await cdp.Call("DOM.focus", new { backendNodeId }, sessionId);
                await ReplaceFocusedText(cdp, sessionId, request.Value!);
            }
            else
            {
                var (key, code, vk, keyText) = request.Key switch
                {
                    WindowKey.ENTER => ("Enter", "Enter", 13, "\r"),
                    WindowKey.SPACE => (" ", "Space", 32, " "),
                    WindowKey.ESCAPE => ("Escape", "Escape", 27, ""),
                    _ => throw new InvalidOperationException("INVALID_KEY")
                };
                var checkpoint = await DispatchCheckpoint(cdp, sessionId, hwnd, request,
                    selected.TargetId, selected.Url, selected.WindowId, loaderId);
                if (checkpoint.Code is not null)
                    return Rejected(checkpoint.Code.Value, checkpoint.Detail);
                dispatched = true;
                await cdp.Call("DOM.focus", new { backendNodeId }, sessionId);
                await cdp.Call("Input.dispatchKeyEvent", new { type = "keyDown", key, code,
                    windowsVirtualKeyCode = vk, text = keyText }, sessionId);
                await cdp.Call("Input.dispatchKeyEvent", new { type = "keyUp", key, code,
                    windowsVirtualKeyCode = vk }, sessionId);
            }
            var foregroundAtExit = GetForegroundWindow();
            return new(WindowActionCode.COMPLETED,
                ForegroundChanged: foregroundAtEntry != foregroundAtExit,
                TargetActivated: foregroundAtEntry != hwnd && foregroundAtExit == hwnd,
                ProviderResponse: providerResponse, ElapsedMs: clock.ElapsedMilliseconds,
                ExpectedStateObserved: (request.Action is WindowActionKind.TYPE_TEXT or WindowActionKind.SET_VALUE) &&
                    request.ExpectedName is null
                    ? await ReadTextValue(cdp, sessionId, backendNodeId, request.Value!) : null,
                ExecutionMethod: WindowExecutionMethod.CHROME_CDP);
        }
        catch (Exception e) when (e is WebSocketException or IOException or JsonException or
                                   OperationCanceledException or InvalidOperationException or KeyNotFoundException)
        {
            ObserveCdpFailure(e);
            return new(dispatched ? WindowActionCode.CDP_FAILED_UNKNOWN_EFFECT : WindowActionCode.CDP_UNAVAILABLE,
                EffectUnknown: dispatched, FailureStage: dispatched ? WindowFailureStage.DISPATCH_INPUT :
                    WindowFailureStage.CDP_CONNECT, ExceptionType: e.GetType().Name,
                ElapsedMs: clock.ElapsedMilliseconds,
                ExecutionMethod: dispatched ? WindowExecutionMethod.CHROME_CDP : null);
        }

        WindowActionResult Rejected(WindowActionCode code, string? detail = null) => new(code,
            FailureStage: WindowFailureStage.CDP_TARGET, Detail: detail,
            ElapsedMs: clock.ElapsedMilliseconds);
    }

    private sealed class Session(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        private int _nextId;
        private CancellationToken _deadline = cancellationToken;
        internal void SetDeadline(CancellationToken deadline) => _deadline = deadline;
        internal async Task<JsonElement> Call(string method, object parameters, string? sessionId = null)
        {
            try
            {
                return await CallCore(method, parameters, sessionId);
            }
            catch (OperationCanceledException e)
            {
                // The command name is an in-process literal, never page data.
                // Preserve it through the typed error path for causal diagnosis.
                e.Data["cdp_method"] = method;
                throw;
            }
        }

        private async Task<JsonElement> CallCore(string method, object parameters, string? sessionId)
        {
            int id = ++_nextId;
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
                { id, method, @params = parameters, sessionId },
                new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
            await socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, _deadline);
            var buffer = new byte[16384];
            using var stream = new MemoryStream();
            while (true)
            {
                stream.SetLength(0);
                WebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _deadline);
                    if (part.MessageType == WebSocketMessageType.Close)
                        throw new IOException("CDP_SOCKET_CLOSED");
                    if (part.MessageType != WebSocketMessageType.Text || stream.Length + part.Count > MaxMessageBytes)
                        throw new JsonException("CDP_MESSAGE_INVALID");
                    stream.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                using var response = JsonDocument.Parse(stream.ToArray());
                if (!response.RootElement.TryGetProperty("id", out var responseId))
                {
                    // A page may detach without closing the browser socket.
                    // Invalidate its tab attachment, not the authorized CDP link.
                    if (response.RootElement.TryGetProperty("method", out var eventMethod) &&
                        eventMethod.GetString() == "Inspector.detached" &&
                        response.RootElement.TryGetProperty("sessionId", out var eventSession) &&
                        eventSession.GetString() == sessionId)
                        throw new InvalidOperationException("CDP_TARGET_DETACHED");
                    continue;
                }
                if (responseId.GetInt32() != id) continue;
                if (response.RootElement.TryGetProperty("error", out _))
                    throw new InvalidOperationException("CDP_COMMAND_REJECTED");
                return response.RootElement.GetProperty("result").Clone();
            }
        }
    }

    private static string GetTitle(IntPtr hwnd)
    {
        var title = new StringBuilder(512);
        _ = GetWindowTextW(hwnd, title, title.Capacity);
        return title.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int length);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
