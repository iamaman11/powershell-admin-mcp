using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Address individual Chrome tabs without using foreground focus. This is a
// bounded CDP adapter, not a second browser manager or arbitrary script tool.
internal static partial class WindowChromeCdp
{
    private sealed record TabTarget(string Id, string Title, string Url, int BrowserWindowId, bool TitleMatch);
    private sealed record TabElementRef(long Stamp, string TargetId, int BackendId, string LoaderId,
        string Digest);

    internal static bool ValidTargetId(string? id) => id is { Length: >= 16 and <= 64 } &&
        id.All(Uri.IsHexDigit);

    private static bool TryParseTabElementRef(string? token, out TabElementRef? result)
    {
        result = null;
        if (token is null || token.Length > 256) return false;
        string[] fields = token.Split(':');
        if (fields.Length != 6 || fields[0] != "c1" || !ValidTargetId(fields[2]) ||
            fields[4].Length is < 1 or > 80 || fields[5].Length != 32 ||
            !fields[5].All(Uri.IsHexDigit) ||
            !long.TryParse(fields[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                out long stamp) ||
            !int.TryParse(fields[3], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                out int backendId) || backendId <= 0) return false;
        long age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - stamp;
        if (age is < -5000 or > 30000) return false;
        result = new(stamp, fields[2], backendId, fields[4], fields[5]);
        return true;
    }

    internal static bool ValidTabElementRef(string? token) => TryParseTabElementRef(token, out _);

    private static string ElementDigest(string windowId, string targetId, int backendId,
        string loaderId, string url, string role, string name)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',
            windowId, targetId, backendId.ToString(CultureInfo.InvariantCulture),
            loaderId, url, role, name)));
        return Convert.ToHexString(bytes.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static string ElementToken(long stamp, string windowId, string targetId, int backendId,
        string loaderId, string url, string role, string name) =>
        $"c1:{stamp:x}:{targetId}:{backendId:x}:{loaderId}:" +
        ElementDigest(windowId, targetId, backendId, loaderId, url, role, name);

    private static bool TryEndpoint(out Uri? endpoint)
    {
        endpoint = null;
        string data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(data)) return false;
        try
        {
            return TryReadEndpoint(File.ReadAllText(Path.Combine(data, "Google", "Chrome",
                "User Data", "DevToolsActivePort")), out endpoint);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool MatchesBounds(WindowBounds native, WindowBounds chrome, double dpi)
    {
        static bool Close(int a, double b) => Math.Abs(a - b) <= BoundsTolerance;
        bool At(double scale) => Close(native.X, chrome.X * scale) &&
            Close(native.Y, chrome.Y * scale) && Close(native.Width, chrome.Width * scale) &&
            Close(native.Height, chrome.Height * scale);
        return chrome.Width > 0 && chrome.Height > 0 && (At(1) || dpi is >= 1 and <= 4 && At(dpi));
    }

    private static async Task<List<TabTarget>> TargetsForWindow(Session cdp, IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect)) return [];
        string nativeTitle = GetTitle(hwnd);
        var native = new WindowBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        uint reportedDpi = GetDpiForWindow(hwnd);
        double dpi = reportedDpi == 0 ? 1 : reportedDpi / 96.0;
        var targets = await cdp.Call("Target.getTargets", new { });
        var found = new List<TabTarget>();
        var matchingWindowIds = new HashSet<int>();
        var boundCache = new Dictionary<int, bool>();
        int visited = 0;
        foreach (var info in targets.GetProperty("targetInfos").EnumerateArray())
        {
            if (++visited > 150) break;
            if (info.GetProperty("type").GetString() != "page" ||
                !info.TryGetProperty("url", out var addressValue) ||
                !Uri.TryCreate(addressValue.GetString(), UriKind.Absolute, out var address) ||
                address.Scheme is not ("https" or "http")) continue;
            string id = info.GetProperty("targetId").GetString() ?? "";
            if (!ValidTargetId(id)) continue;
            string title = info.GetProperty("title").GetString() ?? "";
            var owner = await cdp.Call("Browser.getWindowForTarget", new { targetId = id });
            int browserWindowId = owner.GetProperty("windowId").GetInt32();
            if (!boundCache.TryGetValue(browserWindowId, out bool boundsMatch))
            {
                var response = await cdp.Call("Browser.getWindowBounds", new { windowId = browserWindowId });
                var b = response.GetProperty("bounds");
                boundsMatch = b.TryGetProperty("left", out var left) &&
                    b.TryGetProperty("top", out var top) && b.TryGetProperty("width", out var width) &&
                    b.TryGetProperty("height", out var height) &&
                    MatchesBounds(native, new(left.GetInt32(), top.GetInt32(),
                        width.GetInt32(), height.GetInt32()), dpi);
                boundCache.Add(browserWindowId, boundsMatch);
            }
            if (!boundsMatch) continue;
            bool titleMatch = nativeTitle.StartsWith(title + " - Google Chrome", StringComparison.Ordinal);
            if (titleMatch) matchingWindowIds.Add(browserWindowId);
            found.Add(new(id, title, address.AbsoluteUri, browserWindowId, titleMatch));
        }
        // Geometry alone is insufficient when two browser windows overlap.
        if (matchingWindowIds.Count != 1)
            throw new InvalidOperationException("CDP_WINDOW_NOT_UNIQUE");
        int selected = matchingWindowIds.Single();
        return found.Where(t => t.BrowserWindowId == selected).ToList();
    }

    internal static WindowInspectResult InspectTab(IntPtr hwnd, string windowId,
        WindowInspectScope scope, string? tabId, int maxElements, int offset,
        string? nameFilter, WindowRoleFilter? roleFilter)
    {
        if (hwnd == IntPtr.Zero || !TryEndpoint(out var endpoint))
            return new(WindowInspectCode.CDP_UNAVAILABLE, [], Detail: "CHROME_REMOTE_DEBUGGING_UNAVAILABLE");
        try
        {
            WindowCatalog.TryParseId(windowId, out var identity);
            using var owner = Process.GetProcessById(identity.ProcessId);
            if (!owner.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase))
                return new(WindowInspectCode.CDP_UNAVAILABLE, [], Detail: "NOT_CHROME");
            return InspectTabAsync(endpoint!, hwnd, windowId, scope, tabId, maxElements,
                offset, nameFilter, roleFilter).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is WebSocketException or IOException or JsonException or
            OperationCanceledException or InvalidOperationException or KeyNotFoundException or
            ArgumentException or Win32Exception)
        {
            return new(e is InvalidOperationException && e.Message == "CDP_WINDOW_NOT_UNIQUE"
                ? WindowInspectCode.CDP_TARGET_AMBIGUOUS : WindowInspectCode.CDP_UNAVAILABLE,
                [], Detail: e is InvalidOperationException ? e.Message : e.GetType().Name);
        }
    }

    private static async Task<WindowInspectResult> InspectTabAsync(Uri endpoint, IntPtr hwnd,
        string windowId, WindowInspectScope scope, string? tabId, int maxElements, int offset,
        string? nameFilter, WindowRoleFilter? roleFilter)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(endpoint, timeout.Token);
        var cdp = new Session(socket, timeout.Token);
        var tabs = await TargetsForWindow(cdp, hwnd);
        if (scope == WindowInspectScope.TABS)
        {
            var page = tabs.Skip(offset).Take(maxElements).Select(t =>
            {
                var uri = new Uri(t.Url);
                return new WindowTab(t.Id, t.Title, uri.GetLeftPart(UriPartial.Path),
                    t.TitleMatch);
            }).ToArray();
            return new(WindowInspectCode.COMPLETED, [], tabs.Count > offset + page.Length,
                NextOffset: tabs.Count > offset + page.Length ? offset + page.Length : null,
                Tabs: page);
        }
        var selected = tabs.SingleOrDefault(t => string.Equals(t.Id, tabId, StringComparison.Ordinal));
        if (selected is null) return new(WindowInspectCode.CDP_TARGET_NOT_FOUND, []);
        string sessionId = await Attach(cdp, selected.Id);
        string loader = await Loader(cdp, sessionId);
        var ax = await cdp.Call("Accessibility.getFullAXTree", new { }, sessionId);
        long stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var elements = new List<WindowElement>(maxElements);
        int position = 0;
        bool more = false;
        foreach (var node in ax.GetProperty("nodes").EnumerateArray())
        {
            if (node.TryGetProperty("ignored", out var ignored) && ignored.GetBoolean()) continue;
            string role = AxValue(node, "role");
            string name = AxValue(node, "name");
            if (nameFilter is not null && !name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (roleFilter is not null && !MatchesAxRole(role, roleFilter.Value)) continue;
            if (position++ < offset) continue;
            if (elements.Count == maxElements) { more = true; break; }
            int backend = node.TryGetProperty("backendDOMNodeId", out var backendValue) &&
                backendValue.ValueKind == JsonValueKind.Number ? backendValue.GetInt32() : 0;
            bool hiddenName = role is "textbox" or "searchbox" or "combobox" or "RootWebArea";
            string token = backend > 0 ? ElementToken(stamp, windowId, selected.Id, backend,
                loader, selected.Url, role, name) : "";
            elements.Add(new($"0.{position - 1}", null, 1, "ControlType." + RoleName(role),
                hiddenName ? "[redacted]" : name.Length > 128 ? name[..128] : name,
                "", true, false, false,
                backend > 0 ? ["CDP"] : [], token));
        }
        return new(WindowInspectCode.COMPLETED, [.. elements], more,
            ElementRefExpiresUtc: DateTimeOffset.FromUnixTimeMilliseconds(stamp).AddSeconds(30),
            NextOffset: more ? offset + elements.Count : null);
    }

    private static string RoleName(string role) => role switch
    {
        "button" => "Button", "link" => "Hyperlink", "textbox" or "searchbox" => "Edit",
        "combobox" => "ComboBox", "checkbox" => "CheckBox", "radio" => "RadioButton",
        "tab" => "TabItem", "listitem" => "ListItem", "menuitem" => "MenuItem",
        "StaticText" => "Text", "RootWebArea" => "Document", _ => "Custom"
    };

    private static bool MatchesAxRole(string role, WindowRoleFilter filter) => filter switch
    {
        WindowRoleFilter.BUTTON => role == "button",
        WindowRoleFilter.HYPERLINK => role == "link",
        WindowRoleFilter.EDIT => role is "textbox" or "searchbox",
        WindowRoleFilter.COMBO_BOX => role == "combobox",
        WindowRoleFilter.CHECK_BOX => role == "checkbox",
        WindowRoleFilter.RADIO_BUTTON => role == "radio",
        WindowRoleFilter.TAB_ITEM => role == "tab",
        WindowRoleFilter.LIST_ITEM => role == "listitem",
        WindowRoleFilter.MENU_ITEM => role == "menuitem",
        WindowRoleFilter.TEXT => role == "StaticText",
        WindowRoleFilter.DOCUMENT => role == "RootWebArea",
        _ => filter == WindowRoleFilter.CUSTOM
    };

    private static string AxValue(JsonElement node, string field) =>
        node.TryGetProperty(field, out var value) &&
        value.TryGetProperty("value", out var item) && item.ValueKind == JsonValueKind.String
        ? item.GetString() ?? "" : "";

    private static async Task<string> Attach(Session cdp, string targetId)
    {
        var response = await cdp.Call("Target.attachToTarget", new { targetId, flatten = true });
        return response.GetProperty("sessionId").GetString()!;
    }

    private static async Task<string> Loader(Session cdp, string sessionId)
    {
        var response = await cdp.Call("Page.getFrameTree", new { }, sessionId);
        return response.GetProperty("frameTree").GetProperty("frame")
            .GetProperty("loaderId").GetString()!;
    }

    private static bool HasAxName(JsonElement ax, string name)
    {
        foreach (var node in ax.GetProperty("nodes").EnumerateArray())
            if (!(node.TryGetProperty("ignored", out var ignored) && ignored.GetBoolean()) &&
                AxValue(node, "name") == name) return true;
        return false;
    }

    internal static WindowActionResult InvokeTab(IntPtr hwnd, WindowActionRequest request,
        Stopwatch clock)
    {
        if (!TryParseTabElementRef(request.ElementId, out var target) || target is null)
            return new(WindowActionCode.STALE_ELEMENT);
        if (!TryEndpoint(out var endpoint))
            return new(WindowActionCode.CDP_UNAVAILABLE, FailureStage: WindowFailureStage.CDP_CONNECT);
        try
        {
            return InvokeTabAsync(endpoint!, hwnd, request, target, clock).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is WebSocketException or IOException or JsonException or
            OperationCanceledException or InvalidOperationException or KeyNotFoundException)
        {
            return new(WindowActionCode.CDP_UNAVAILABLE, FailureStage: WindowFailureStage.CDP_CONNECT,
                ExceptionType: e.GetType().Name, ElapsedMs: clock.ElapsedMilliseconds);
        }
    }

    private static async Task<WindowActionResult> InvokeTabAsync(Uri endpoint, IntPtr hwnd,
        WindowActionRequest request, TabElementRef target, Stopwatch clock)
    {
        bool dispatched = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(endpoint, timeout.Token);
            var cdp = new Session(socket, timeout.Token);
            var tabs = await TargetsForWindow(cdp, hwnd);
            var selected = tabs.SingleOrDefault(t => t.Id == target.TargetId);
            if (selected is null) return Rejected(WindowActionCode.CDP_TARGET_AMBIGUOUS);
            string sessionId = await Attach(cdp, selected.Id);
            string loader = await Loader(cdp, sessionId);
            if (loader != target.LoaderId) return Rejected(WindowActionCode.STALE_ELEMENT);
            var ax = await cdp.Call("Accessibility.getFullAXTree", new { }, sessionId);
            var nodeMatches = ax.GetProperty("nodes").EnumerateArray().Where(node =>
                node.TryGetProperty("backendDOMNodeId", out var backend) &&
                backend.ValueKind == JsonValueKind.Number && backend.GetInt32() == target.BackendId &&
                !(node.TryGetProperty("ignored", out var ignored) && ignored.GetBoolean())).ToArray();
            if (nodeMatches.Length != 1) return Rejected(WindowActionCode.STALE_ELEMENT);
            string role = AxValue(nodeMatches[0], "role"), name = AxValue(nodeMatches[0], "name");
            if (!string.Equals(ElementDigest(request.WindowId, selected.Id, target.BackendId,
                loader, selected.Url, role, name), target.Digest, StringComparison.OrdinalIgnoreCase))
                return Rejected(WindowActionCode.STALE_ELEMENT);
            if (request.ExpectedName is not null)
            {
                bool before = request.ExpectedKind == WindowExpectedKind.UIA_ELEMENT
                    ? HasAxName(ax, request.ExpectedName)
                    : WindowAction.ExternalStatePresent(request.ExpectedKind, request.ExpectedName);
                if (before == (request.ExpectedChange == WindowExpectedChange.APPEARS))
                    return Rejected(WindowActionCode.EXPECTED_STATE_ALREADY_TRUE);
            }
            string provider = "CDP_BACKGROUND";
            if (request.Action == WindowActionKind.CLICK)
            {
                if (role is not ("button" or "link" or "checkbox" or "radio" or "tab" or
                    "listitem" or "menuitem")) return Rejected(WindowActionCode.UNSUPPORTED_ACTION);
                var box = await cdp.Call("DOM.getBoxModel", new { backendNodeId = target.BackendId }, sessionId);
                var metrics = await cdp.Call("Page.getLayoutMetrics", new { }, sessionId);
                if (!TryPoint(box.GetProperty("model").GetProperty("content"),
                    metrics.GetProperty("cssVisualViewport"), out double x, out double y))
                    return Rejected(WindowActionCode.CDP_TARGET_NOT_VISIBLE);
                var resolved = await cdp.Call("DOM.resolveNode", new { backendNodeId = target.BackendId }, sessionId);
                string objectId = resolved.GetProperty("object").GetProperty("objectId").GetString()!;
                var hit = await cdp.Call("Runtime.callFunctionOn", new
                {
                    objectId,
                    functionDeclaration = "function(x,y){const h=document.elementFromPoint(x,y);return !!h && (h===this || this.contains(h)) && !this.matches(':disabled') && !this.closest('[inert]');}",
                    arguments = new[] { new { value = x }, new { value = y } },
                    returnByValue = true, silent = true
                }, sessionId);
                if (!hit.GetProperty("result").TryGetProperty("value", out var ok) ||
                    ok.ValueKind != JsonValueKind.True) return Rejected(WindowActionCode.TARGET_OCCLUDED);
                dispatched = true;
                await cdp.Call("Input.dispatchMouseEvent", new { type = "mousePressed", x, y,
                    button = "left", clickCount = 1 }, sessionId);
                await cdp.Call("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y,
                    button = "left", clickCount = 1 }, sessionId);
            }
            else if (request.Action is WindowActionKind.KEY_PRESS or WindowActionKind.TYPE_TEXT)
            {
                if (request.Action == WindowActionKind.TYPE_TEXT &&
                    role is not ("textbox" or "searchbox" or "combobox"))
                    return Rejected(WindowActionCode.UNSUPPORTED_ACTION);
                await cdp.Call("DOM.focus", new { backendNodeId = target.BackendId }, sessionId);
                if (request.Action == WindowActionKind.TYPE_TEXT)
                {
                    await cdp.Call("Input.dispatchKeyEvent", new { type = "keyDown", key = "Control",
                        code = "ControlLeft", windowsVirtualKeyCode = 17 }, sessionId);
                    await cdp.Call("Input.dispatchKeyEvent", new { type = "keyDown", key = "a",
                        code = "KeyA", windowsVirtualKeyCode = 65, modifiers = 2 }, sessionId);
                    await cdp.Call("Input.dispatchKeyEvent", new { type = "keyUp", key = "a",
                        code = "KeyA", windowsVirtualKeyCode = 65, modifiers = 2 }, sessionId);
                    await cdp.Call("Input.dispatchKeyEvent", new { type = "keyUp", key = "Control",
                        code = "ControlLeft", windowsVirtualKeyCode = 17 }, sessionId);
                    dispatched = true;
                    await cdp.Call("Input.insertText", new { text = request.Value! }, sessionId);
                }
                else
                {
                    var (key, code, vk, value) = request.Key switch
                    {
                        WindowKey.ENTER => ("Enter", "Enter", 13, "\r"),
                        WindowKey.SPACE => (" ", "Space", 32, " "),
                        WindowKey.ESCAPE => ("Escape", "Escape", 27, ""),
                        _ => throw new InvalidOperationException("INVALID_KEY")
                    };
                    dispatched = true;
                    await cdp.Call("Input.dispatchKeyEvent", new { type = "keyDown", key, code,
                        windowsVirtualKeyCode = vk, text = value }, sessionId);
                    await cdp.Call("Input.dispatchKeyEvent", new { type = "keyUp", key, code,
                        windowsVirtualKeyCode = vk }, sessionId);
                }
            }
            else return Rejected(WindowActionCode.UNSUPPORTED_ACTION);

            bool? effect = null;
            if (request.ExpectedName is not null)
            {
                bool latest = request.ExpectedKind == WindowExpectedKind.UIA_ELEMENT
                    ? HasAxName(await cdp.Call("Accessibility.getFullAXTree", new { }, sessionId),
                        request.ExpectedName)
                    : WindowAction.ExternalStatePresent(request.ExpectedKind, request.ExpectedName);
                effect = latest ==
                    (request.ExpectedChange == WindowExpectedChange.APPEARS);
            }
            else if (request.Action == WindowActionKind.TYPE_TEXT)
            {
                var resolved = await cdp.Call("DOM.resolveNode", new { backendNodeId = target.BackendId }, sessionId);
                string objectId = resolved.GetProperty("object").GetProperty("objectId").GetString()!;
                var readback = await cdp.Call("Runtime.callFunctionOn", new
                {
                    objectId,
                    functionDeclaration = "function(){return 'value' in this ? this.value : this.textContent;}",
                    returnByValue = true, silent = true
                }, sessionId);
                effect = readback.GetProperty("result").TryGetProperty("value", out var value) &&
                    value.ValueKind == JsonValueKind.String && value.GetString() == request.Value;
            }
            return new(WindowActionCode.COMPLETED, TargetVerified: true,
                TargetIdentity: request.ElementId, ProviderResponse: provider,
                ExpectedStateObserved: effect, ElapsedMs: clock.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is WebSocketException or IOException or JsonException or
            OperationCanceledException or InvalidOperationException or KeyNotFoundException)
        {
            return new(dispatched ? WindowActionCode.CDP_FAILED_UNKNOWN_EFFECT : WindowActionCode.CDP_UNAVAILABLE,
                EffectUnknown: dispatched, FailureStage: dispatched ? WindowFailureStage.DISPATCH_INPUT :
                    WindowFailureStage.CDP_TARGET, ExceptionType: e.GetType().Name,
                ElapsedMs: clock.ElapsedMilliseconds);
        }
        WindowActionResult Rejected(WindowActionCode status) => new(status,
            TargetVerified: status == WindowActionCode.EXPECTED_STATE_ALREADY_TRUE,
            FailureStage: WindowFailureStage.CDP_TARGET, ElapsedMs: clock.ElapsedMilliseconds);
    }
}
