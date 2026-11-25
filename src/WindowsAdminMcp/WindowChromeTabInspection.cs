using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Automation;

// Read tab accessibility and document state without focus.
internal static partial class WindowChromeCdp
{
    internal static WindowInspectResult InspectTab(IntPtr hwnd, string windowId,
        WindowInspectScope scope, string? tabId, int maxElements, int offset,
        string? nameFilter, WindowRoleFilter? roleFilter,
        string? expectedProfile = null, string? expectedOrigin = null, string? expectedUrl = null)
    {
        var clock=Stopwatch.StartNew();
        return InspectTabCore(hwnd,windowId,scope,tabId,maxElements,offset,nameFilter,
            roleFilter,expectedProfile,expectedOrigin,expectedUrl) with
            {ElapsedMs=clock.ElapsedMilliseconds};
    }

    private static WindowInspectResult InspectTabCore(IntPtr hwnd, string windowId,
        WindowInspectScope scope, string? tabId, int maxElements, int offset,
        string? nameFilter, WindowRoleFilter? roleFilter,
        string? expectedProfile, string? expectedOrigin, string? expectedUrl)
    {
        if (hwnd == IntPtr.Zero)
            return new(WindowInspectCode.CDP_UNAVAILABLE, [], Detail: "WINDOW_NOT_AVAILABLE");
        if (!TryEndpoint(out var endpoint, out string endpointFailure))
            return new(WindowInspectCode.CDP_UNAVAILABLE, [], Detail: endpointFailure);
        try
        {
            WindowCatalog.TryParseId(windowId, out var identity);
            using var owner = Process.GetProcessById(identity.ProcessId);
            if (!owner.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase))
                return new(WindowInspectCode.CDP_UNAVAILABLE, [], Detail: "NOT_CHROME");
            if (expectedProfile is not null &&
                !VerifiedChromeProfile(hwnd, expectedProfile, out var profileFailure))
                return new(WindowInspectCode.CDP_CONTEXT_MISMATCH, [],
                    Detail: $"PROFILE_{profileFailure}");
            var connection = ConnectionState(endpoint!, request: false, hwnd);
            if (connection == ChromeConnectionState.CONSENT_PENDING)
                return new(WindowInspectCode.CDP_CONSENT_PENDING, [], Detail: "ALLOW_CHROME_PROMPT");
            if (connection != ChromeConnectionState.CONNECTED)
                return new(WindowInspectCode.CDP_UNAVAILABLE, [], Detail: connection.ToString());
            try
            {
                return InspectTabAsync(endpoint!, hwnd, windowId, scope, tabId, maxElements,
                    offset, nameFilter, roleFilter, expectedProfile, expectedOrigin, expectedUrl)
                    .GetAwaiter().GetResult();
            }
            catch (CdpWindowMappingException e)
            {
                // A window-mapping failure is not CDP transport loss.
                return new(InspectMappingCode(e.State), [], Detail:e.Message,
                    CdpMappingFailure:e.Failure, CdpTitleCandidates:e.TitleCandidates,
                    CdpFingerprintCandidates:e.FingerprintCandidates);
            }
            catch (Exception e) when (e is WebSocketException or IOException or JsonException or
                OperationCanceledException or InvalidOperationException or KeyNotFoundException)
            {
                bool transportLost = CdpTransportFailure(e);
                ObserveCdpFailure(e);
                // Only explicit REQUEST may reconnect. Preserve the causal
                // method instead of replacing it with CONNECTION_LOST.
                return new(WindowInspectCode.CDP_UNAVAILABLE, [],
                    Detail: e is OperationCanceledException canceled ? CdpTimeoutDetail(canceled) :
                        e is InvalidOperationException ? e.Message :
                        transportLost ? "CDP_TRANSPORT_LOST" : e.GetType().Name);
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or JsonException or
            OperationCanceledException or InvalidOperationException or KeyNotFoundException or
            ArgumentException or Win32Exception)
        {
            ObserveCdpFailure(e);
            return new(WindowInspectCode.CDP_UNAVAILABLE,
                [], Detail: e is OperationCanceledException canceled ? CdpTimeoutDetail(canceled) :
                    e is InvalidOperationException ? e.Message : e is WebSocketException ws
                    ? $"CDP_WEBSOCKET_{ws.WebSocketErrorCode}" : e.GetType().Name);
        }
    }

    private static async Task<WindowInspectResult> InspectTabAsync(Uri endpoint, IntPtr hwnd,
        string windowId, WindowInspectScope scope, string? tabId, int maxElements, int offset,
        string? nameFilter, WindowRoleFilter? roleFilter,
        string? expectedProfile, string? expectedOrigin, string? expectedUrl)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(
            InspectTimeoutMs(TabConnected, tabId is not null)));
        var cdp = await TabSessionAsync(endpoint, timeout.Token);
        var tabs = await TargetsForWindow(cdp, hwnd, windowId, timeout.Token,
            expectedOrigin, expectedUrl, tabId, expectedProfile is not null);
        if (scope == WindowInspectScope.TABS)
        {
            tabs = tabs.Where(t => MatchesContext(t.Url, expectedOrigin, expectedUrl)).ToList();
            var page = tabs.Skip(offset).Take(maxElements).Select(t =>
            {
                var uri = new Uri(t.Url);
                return new WindowTab(t.Id, t.Title, uri.GetLeftPart(UriPartial.Path),
                    uri.GetLeftPart(UriPartial.Authority), t.BrowserWindowId,
                    uri.Query.Length != 0 || uri.Fragment.Length != 0);
            }).ToArray();
            return new(WindowInspectCode.COMPLETED, [], tabs.Count > offset + page.Length,
                NextOffset: tabs.Count > offset + page.Length ? offset + page.Length : null,
                Tabs: page);
        }
        TabTarget? selected;
        if (tabId is null)
        {
            // No preliminary TABS enumeration: resolve only the caller's
            // explicitly addressed domain/URL, never expose other targets.
            var matching = tabs.Where(t => MatchesContext(t.Url, expectedOrigin, expectedUrl))
                .Take(2).ToArray();
            if (matching.Length == 0) return new(WindowInspectCode.CDP_TARGET_NOT_FOUND, []);
            if (matching.Length != 1) return new(WindowInspectCode.CDP_TARGET_AMBIGUOUS, []);
            selected = matching[0];
        }
        else selected = tabs.SingleOrDefault(t => string.Equals(t.Id, tabId, StringComparison.Ordinal));
        if (selected is null) return new(WindowInspectCode.CDP_TARGET_NOT_FOUND, []);
        if (!MatchesContext(selected.Url, expectedOrigin, expectedUrl))
            return new(WindowInspectCode.CDP_CONTEXT_MISMATCH, [], Detail: "TAB_URL_MISMATCH");
        string sessionId = await Attach(cdp, selected.Id);
        string loader = await ReadFrameLoaderAsync(cdp, sessionId, timeout.Token);
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
            bool password = IsProtectedAx(node);
            bool hiddenName = role is "textbox" or "searchbox" or "combobox" or "RootWebArea";
            string token = backend > 0 ? ElementToken(stamp, windowId, selected.Id, backend,
                loader, selected.Url, role, name) : "";
            elements.Add(new($"0.{position - 1}", null, 1, "ControlType." + RoleName(role),
                hiddenName ? "[redacted]" : name.Length > 128 ? name[..128] : name,
                "", true, false, password,
                backend > 0 ? ["CDP"] : [], token));
        }
        var selectedAddress = new Uri(selected.Url);
        return new(WindowInspectCode.COMPLETED, [.. elements], more,
            ElementRefExpiresUtc: DateTimeOffset.FromUnixTimeMilliseconds(stamp).AddSeconds(30),
            NextOffset: more ? offset + elements.Count : null,
            Tabs: [new WindowTab(selected.Id, selected.Title,
                selectedAddress.GetLeftPart(UriPartial.Path),
                selectedAddress.GetLeftPart(UriPartial.Authority), selected.BrowserWindowId,
                selectedAddress.Query.Length != 0 || selectedAddress.Fragment.Length != 0)]);
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

    internal static bool IsProtectedAx(JsonElement node)
    {
        if (!node.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Array) return false;
        return properties.EnumerateArray().Any(property =>
            property.TryGetProperty("name", out var name) && name.GetString() == "protected" &&
            property.TryGetProperty("value", out var value) &&
            value.TryGetProperty("value", out var boolean) && boolean.ValueKind == JsonValueKind.True);
    }

    private static async Task<string> Attach(Session cdp, string targetId)
    {
        if (AttachedTabs.TryGetValue(targetId, out string? existing)) return existing;
        var response = await cdp.Call("Target.attachToTarget", new { targetId, flatten = true });
        string sessionId = response.GetProperty("sessionId").GetString()!;
        AttachedTabs.Add(targetId, sessionId);
        return sessionId;
    }

    // Page.getFrameTree can stall on an independently unresponsive background
    // renderer while the browser's one consented CDP socket stays healthy.
    // Page.getResourceTree returns the same main-frame loaderId, so it is a
    // read-only alternative; neither route focuses, reloads or thaws a page.
    // The first command has its *own* deadline: the canceled waiter leaves the
    // socket reader alive and a late reply cannot be mistaken for the fallback.
    // A tab-local command rejection or a response without a usable loaderId
    // also permits this read-only alternative; other failures retain their
    // original cause instead of being silently hidden.
    // Both paths must yield a real loaderId, otherwise no actionable element
    // fingerprint can be minted and no input can be dispatched.
    internal static async Task<string> ReadFrameLoaderAsync(Session cdp, string sessionId,
        CancellationToken requestDeadline, int frameTreeProbeMs = 4500)
    {
        requestDeadline.ThrowIfCancellationRequested();
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(requestDeadline);
        probe.CancelAfter(frameTreeProbeMs);
        cdp.SetDeadline(probe.Token);
        try
        {
            var first = await cdp.Call("Page.getFrameTree", new { }, sessionId);
            return MainFrameLoader(first);
        }
        catch (OperationCanceledException) when (!requestDeadline.IsCancellationRequested)
        {
            // An unanswered *individual* command is not proof of a lost CDP
            // connection, and no subsequent command may reuse its response.
        }
        catch (InvalidOperationException e) when (e.Message is
            "CDP_COMMAND_REJECTED" or "CDP_TAB_LOADER_UNAVAILABLE")
        {
            // A rejected frame-tree query is not a failed WebSocket. Do not
            // reconnect or retry the same command; query the alternative.
        }
        finally
        {
            cdp.SetDeadline(requestDeadline);
        }

        var fallback = await cdp.Call("Page.getResourceTree", new { }, sessionId);
        return MainFrameLoader(fallback);
    }

    private static string MainFrameLoader(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("frameTree", out var tree) ||
            tree.ValueKind != JsonValueKind.Object ||
            !tree.TryGetProperty("frame", out var frame) ||
            frame.ValueKind != JsonValueKind.Object ||
            !frame.TryGetProperty("loaderId", out var value) ||
            value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("CDP_TAB_LOADER_UNAVAILABLE");
        string? loader = value.GetString();
        if (loader is null || loader.Length is < 1 or > 80 || loader.Contains(':'))
            throw new InvalidOperationException("CDP_TAB_LOADER_UNAVAILABLE");
        return loader;
    }

    private static bool HasAxName(JsonElement ax, string name)
    {
        foreach (var node in ax.GetProperty("nodes").EnumerateArray())
            if (!(node.TryGetProperty("ignored", out var ignored) && ignored.GetBoolean()) &&
                AxValue(node, "name") == name) return true;
        return false;
    }

    internal static bool HasAxValue(JsonElement ax, string name, string value)
    {
        int matches = 0;
        bool equals = false;
        foreach (var node in ax.GetProperty("nodes").EnumerateArray())
        {
            if (node.TryGetProperty("ignored", out var ignored) && ignored.GetBoolean() ||
                AxValue(node, "name") != name || IsProtectedAx(node)) continue;
            matches++;
            equals = node.TryGetProperty("value", out var axValue) &&
                axValue.TryGetProperty("value", out var text) && text.ValueKind == JsonValueKind.String &&
                string.Equals(text.GetString(), value, StringComparison.Ordinal);
        }
        return matches == 1 && equals;
    }

    private static bool AxDisabled(JsonElement node)
    {
        if (!node.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Array) return false;
        return properties.EnumerateArray().Any(property =>
            property.TryGetProperty("name", out var name) && name.GetString() == "disabled" &&
            property.TryGetProperty("value", out var value) &&
            value.TryGetProperty("value", out var boolean) && boolean.ValueKind == JsonValueKind.True);
    }

    internal static bool HasAxEnabled(JsonElement ax, string name, bool enabled)
    {
        int matches = 0;
        bool state = false;
        foreach (var node in ax.GetProperty("nodes").EnumerateArray())
        {
            if (node.TryGetProperty("ignored", out var ignored) && ignored.GetBoolean() ||
                AxValue(node, "name") != name || IsProtectedAx(node)) continue;
            matches++;
            state = AxDisabled(node) != enabled;
        }
        return matches == 1 && state;
    }

    private static bool ExpectedAxState(JsonElement ax, WindowActionRequest request) =>
        request.ExpectedKind switch
        {
            WindowExpectedKind.UIA_VALUE => HasAxValue(ax, request.ExpectedName!, request.ExpectedValue!),
            WindowExpectedKind.UIA_ENABLED => HasAxEnabled(ax, request.ExpectedName!, true),
            _ => HasAxName(ax, request.ExpectedName!)
        };
}
