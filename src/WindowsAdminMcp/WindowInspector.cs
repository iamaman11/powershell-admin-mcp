using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Automation;

[JsonConverter(typeof(JsonStringEnumConverter<WindowInspectCode>))]
public enum WindowInspectCode
{
    COMPLETED, INVALID_ARGUMENT, SERVICE_UNAVAILABLE, USER_CONTEXT_UNAVAILABLE, DESKTOP_UNAVAILABLE,
    STALE_WINDOW, STALE_ELEMENT, ACCESS_DENIED, UIA_UNAVAILABLE, TIMEOUT, WORKER_FAILED,
    STATE_NOT_OBSERVED, CDP_UNAVAILABLE, CDP_TARGET_NOT_FOUND, CDP_TARGET_AMBIGUOUS,
    CDP_CONTEXT_MISMATCH, CDP_CONSENT_PENDING
}

[JsonConverter(typeof(JsonStringEnumConverter<WindowRoleFilter>))]
public enum WindowRoleFilter
{
    BUTTON, TEXT, EDIT, COMBO_BOX, CHECK_BOX, RADIO_BUTTON, TAB_ITEM, LIST_ITEM,
    HYPERLINK, MENU_ITEM, PANE, GROUP, WINDOW, DOCUMENT, CUSTOM
}

[JsonConverter(typeof(JsonStringEnumConverter<WindowInspectScope>))]
public enum WindowInspectScope { WINDOW, TABS, TAB }

[JsonConverter(typeof(JsonStringEnumConverter<WindowCdpMappingFailure>))]
public enum WindowCdpMappingFailure
{
    TITLE_NOT_FOUND, BOUNDS_MISMATCH, TAB_STRIP_UNAVAILABLE,
    FINGERPRINT_NOT_FOUND, FINGERPRINT_AMBIGUOUS, FINGERPRINT_LIMIT
}

public sealed record WindowTab(
    [property: JsonPropertyName("tab_id")] string TabId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("origin")] string Origin);

public sealed record WindowElement(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("parent_index")] int? ParentIndex,
    [property: JsonPropertyName("depth")] int Depth,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("automation_id")] string AutomationId,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("offscreen")] bool Offscreen,
    [property: JsonPropertyName("password")] bool Password,
    [property: JsonPropertyName("patterns")] string[] Patterns,
    [property: JsonPropertyName("element_id")] string ElementId,
    [property: JsonPropertyName("bounds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WindowBounds? Bounds = null,
    [property: JsonPropertyName("keyboard_focusable"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? KeyboardFocusable = null,
    [property: JsonPropertyName("has_keyboard_focus"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? HasKeyboardFocus = null,
    [property: JsonPropertyName("clickable_point"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WindowPoint? ClickablePoint = null);

public sealed record WindowBounds(int X, int Y, int Width, int Height);
public sealed record WindowPoint(int X, int Y);

public sealed record WindowInspectResult(
    [property: JsonPropertyName("status")] WindowInspectCode Status,
    [property: JsonPropertyName("elements")] WindowElement[] Elements,
    [property: JsonPropertyName("truncated")] bool Truncated = false,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("elapsed_ms")] long? ElapsedMs = null,
    [property: JsonPropertyName("element_ref_expires_utc")] DateTimeOffset? ElementRefExpiresUtc = null,
    [property: JsonPropertyName("next_offset")] int? NextOffset = null,
    [property: JsonPropertyName("tabs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WindowTab[]? Tabs = null,
    [property: JsonPropertyName("broker_owner_pid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? BrokerOwnerPid = null,
    [property: JsonPropertyName("trace_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TraceId = null,
    [property: JsonPropertyName("session_generation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SessionGeneration = null,
    [property: JsonPropertyName("cdp_mapping_failure"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WindowCdpMappingFailure? CdpMappingFailure = null,
    [property: JsonPropertyName("cdp_title_candidates"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CdpTitleCandidates = null,
    [property: JsonPropertyName("cdp_fingerprint_candidates"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CdpFingerprintCandidates = null);

internal sealed record WindowInspectRequest(
    string WindowId, int MaxDepth, int MaxElements,
    string? StartPath, string? StartElementId, int Offset, bool IncludeDetails,
    string? NameContains, WindowRoleFilter? RoleFilter, int WaitMs,
    WindowInspectScope Scope = WindowInspectScope.WINDOW, string? TabId = null,
    string? ExpectedProfile = null, string? ExpectedOrigin = null, string? ExpectedUrl = null);

// UI Automation can block inside a third-party provider. Run it in a bounded,
// on-demand copy of this executable, never on the MCP request/USER worker thread.
internal static class WindowInspector
{
    internal const int ElementRefSeconds = 30;
    // Chromium's page document can be nested well below its browser chrome.
    // Keep node count and the isolated worker deadline unchanged.
    internal const int MaxDepth = 16;
    internal const int MaxTraversal = 2000;
    private const long FutureSkewMs = 5000;

    // Runtime IDs are ephemeral. A fresh token also binds the window identity
    // and control metadata. It is an identity check, never an authorization.
    internal static string ElementRef(AutomationElement element, string windowId, long unixMs)
    {
        int[] runtimeId = element.GetRuntimeId();
        var current = element.Current;
        string fingerprint = windowId + "|" + string.Join(",", runtimeId) + "|" +
            current.ControlType.ProgrammaticName + "|" + current.AutomationId + "|" + current.Name;
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint));
        return "e1:" + unixMs.ToString("x", CultureInfo.InvariantCulture) + ":" +
            Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }

    internal static bool TryParseElementRef(string? text, out long unixMs)
    {
        if (!WellFormedElementRef(text, out unixMs)) return false;
        long age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - unixMs;
        return age >= -FutureSkewMs && age <= ElementRefSeconds * 1000L;
    }

    internal static bool WellFormedElementRef(string? text, out long unixMs)
    {
        unixMs = 0;
        if (text is null || text.Length > 80) return false;
        var fields = text.Split(':');
        if (fields.Length != 3 || fields[0] != "e1" || fields[2].Length != 32 ||
            !fields[2].All(Uri.IsHexDigit) ||
            !long.TryParse(fields[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out unixMs))
            return false;
        return true;
    }

    internal static bool TryParsePath(string? path, out int[] indices)
    {
        indices = [];
        if (string.IsNullOrEmpty(path) || path.Length > 96) return false;
        var pieces = path.Split('.');
        if (pieces.Length is < 1 or > MaxDepth + 1 || pieces[0] != "0") return false;
        var result = new int[pieces.Length - 1];
        for (int i = 1; i < pieces.Length; i++)
        {
            if (!int.TryParse(pieces[i], NumberStyles.None, CultureInfo.InvariantCulture, out int index) ||
                index is < 0 or > MaxTraversal ||
                index.ToString(CultureInfo.InvariantCulture) != pieces[i]) return false;
            result[i - 1] = index;
        }
        indices = result;
        return true;
    }

    internal static bool ValidArguments(string? windowId, int maxDepth, int maxElements,
        string? startPath = null, string? startElementId = null, int offset = 0,
        bool includeDetails = false, string? nameContains = null,
        WindowRoleFilter? roleFilter = null, int waitMs = 0,
        WindowInspectScope scope = WindowInspectScope.WINDOW, string? tabId = null,
        string? expectedProfile = null, string? expectedOrigin = null, string? expectedUrl = null) =>
        ArgumentError(windowId, maxDepth, maxElements, startPath, startElementId,
            offset, includeDetails, nameContains, roleFilter, waitMs, scope, tabId,
            expectedProfile, expectedOrigin, expectedUrl) is null;

    // Report the offending typed field without exposing browser contents.
    internal static string? ArgumentError(string? windowId, int maxDepth, int maxElements,
        string? startPath = null, string? startElementId = null, int offset = 0,
        bool includeDetails = false, string? nameContains = null,
        WindowRoleFilter? roleFilter = null, int waitMs = 0,
        WindowInspectScope scope = WindowInspectScope.WINDOW, string? tabId = null,
        string? expectedProfile = null, string? expectedOrigin = null, string? expectedUrl = null)
    {
        if (!WindowCatalog.TryParseId(windowId, out _)) return "window_id: invalid or missing window reference";
        if (!Enum.IsDefined(scope)) return "scope: expected WINDOW, TABS or TAB";
        if (scope == WindowInspectScope.WINDOW &&
            (expectedOrigin is not null || expectedUrl is not null))
            return "scope: expected_origin/url require TABS or TAB";
        string? contextError = WindowChromeCdp.ContextArgumentError(expectedProfile, expectedOrigin, expectedUrl);
        if (contextError is not null) return contextError;
        if (scope == WindowInspectScope.WINDOW && tabId is not null)
            return "tab_id: only valid with scope=TAB";
        if (scope == WindowInspectScope.TABS && tabId is not null)
            return "tab_id: omit for scope=TABS";
        if (scope == WindowInspectScope.TAB &&
            (tabId is not null && !WindowChromeCdp.ValidTargetId(tabId) ||
             tabId is null && expectedOrigin is null && expectedUrl is null))
            return "tab_id: provide a target ID or an exact expected_origin/expected_url";
        if (scope != WindowInspectScope.WINDOW && expectedProfile is null)
            return "expected_profile: required for Chrome TABS/TAB";
        if (scope != WindowInspectScope.WINDOW && (startPath is not null || startElementId is not null || waitMs != 0))
            return "scope: TAB/TABS cannot use UIA subtree or wait arguments";
        if (scope != WindowInspectScope.WINDOW && includeDetails)
            return "include_details: available only with scope=WINDOW";
        if (maxDepth is < 0 or > MaxDepth) return $"max_depth: expected 0..{MaxDepth}";
        if (maxElements is < 1 or > 100) return "max_elements: expected 1..100";
        if (includeDetails && maxElements > 10)
            return "max_elements: include_details=true requires max_elements<=10";
        if (offset < 0 || (long)offset + maxElements > MaxTraversal)
            return $"offset: expected offset>=0 and offset+max_elements<={MaxTraversal}";
        if (nameContains is not null && (nameContains.Length is < 1 or > 128 ||
            string.IsNullOrWhiteSpace(nameContains) || nameContains.Contains('\0')))
            return "name_contains: expected 1..128 non-whitespace characters without NUL";
        if (roleFilter is not null && !Enum.IsDefined(roleFilter.Value))
            return "role_filter: unsupported value";
        if (waitMs is < 0 or > 1500) return "wait_ms: expected 0..1500";
        if (waitMs > 0 && nameContains is null && roleFilter is null)
            return "wait_ms: requires name_contains or role_filter";
        if ((startPath is null) != (startElementId is null))
            return "start_path/start_element_id: both are required for subtree inspection";
        if (startPath is not null && (!TryParsePath(startPath, out var indices) || indices.Length > maxDepth))
            return "start_path: invalid path or exceeds max_depth";
        if (startElementId is not null && !TryParseElementRef(startElementId, out _))
            return "start_element_id: invalid or expired reference";
        return null;
    }

    internal static WindowInspectResult InspectInWorker(string windowId, int maxDepth, int maxElements,
        string? startPath = null, string? startElementId = null, int offset = 0,
        bool includeDetails = false, string? nameContains = null,
        WindowRoleFilter? roleFilter = null, int waitMs = 0,
        WindowInspectScope scope = WindowInspectScope.WINDOW, string? tabId = null,
        string? expectedProfile = null, string? expectedOrigin = null, string? expectedUrl = null)
    {
        if (!ValidArguments(windowId, maxDepth, maxElements, startPath, startElementId,
            offset, includeDetails, nameContains, roleFilter, waitMs, scope, tabId,
            expectedProfile, expectedOrigin, expectedUrl))
            return new(WindowInspectCode.INVALID_ARGUMENT, [], Detail: ArgumentError(windowId, maxDepth,
                maxElements, startPath, startElementId, offset, includeDetails, nameContains,
                roleFilter, waitMs, scope, tabId, expectedProfile, expectedOrigin, expectedUrl));
        using var self = Process.GetCurrentProcess();
        var desktop = DesktopCapture.ObserveDesktop(self.SessionId);
        if (desktop.Code != CaptureCode.COMPLETED)
            return new(WindowInspectCode.DESKTOP_UNAVAILABLE, [], Detail: desktop.Code.ToString());
        var resolved = WindowCatalog.ResolveId(windowId, self.SessionId, out var hwnd);
        if (resolved != WindowResolveCode.OK)
            return new(resolved == WindowResolveCode.ACCESS_DENIED ? WindowInspectCode.ACCESS_DENIED :
                resolved == WindowResolveCode.WRONG_SESSION ? WindowInspectCode.USER_CONTEXT_UNAVAILABLE :
                WindowInspectCode.STALE_WINDOW, [], Detail: resolved.ToString());
        if (scope == WindowInspectScope.WINDOW && expectedProfile is not null &&
            !WindowChromeCdp.VerifiedChromeProfile(hwnd, expectedProfile))
            return new(WindowInspectCode.CDP_CONTEXT_MISMATCH, [], Detail: "PROFILE_NOT_VERIFIED");
        return scope == WindowInspectScope.WINDOW
            ? InspectCore(hwnd, maxDepth, maxElements, windowId, startPath, startElementId,
                offset, includeDetails, nameContains, roleFilter, waitMs)
            : WindowChromeCdp.InspectTab(hwnd, windowId, scope, tabId, maxElements,
                offset, nameContains, roleFilter, expectedProfile, expectedOrigin, expectedUrl);
    }

    // Paths are never authority on their own. Callers must also provide the
    // fresh fingerprint returned for that exact node by a previous inspection.
    internal static AutomationElement? ResolvePath(AutomationElement root,
        TreeWalker walker, int[] indices)
    {
        var current = root;
        foreach (int index in indices)
        {
            var child = walker.GetFirstChild(current);
            for (int i = 0; i < index && child is not null; i++)
                child = walker.GetNextSibling(child);
            if (child is null) return null;
            current = child;
        }
        return current;
    }

    internal static WindowInspectResult InspectCore(IntPtr hwnd, int maxDepth, int maxElements,
        string? windowId = null, string? startPath = null, string? startElementId = null,
        int offset = 0, bool includeDetails = false, string? nameContains = null,
        WindowRoleFilter? roleFilter = null, int waitMs = 0)
    {
        var clock = Stopwatch.StartNew();
        if (hwnd == IntPtr.Zero || maxDepth is < 0 or > MaxDepth || maxElements is < 1 or > 100 ||
            (includeDetails && maxElements > 10) || offset < 0 || offset + maxElements > MaxTraversal ||
            nameContains is not null && (nameContains.Length is < 1 or > 128 ||
                string.IsNullOrWhiteSpace(nameContains) || nameContains.Contains('\0')) ||
            roleFilter is not null && !Enum.IsDefined(roleFilter.Value) ||
            waitMs is < 0 or > 1500 || waitMs > 0 && nameContains is null && roleFilter is null ||
            (startPath is not null || startElementId is not null) &&
            (windowId is null || !ValidArguments(windowId, maxDepth, maxElements,
                startPath, startElementId, offset, includeDetails, nameContains, roleFilter, waitMs)))
            return new(WindowInspectCode.INVALID_ARGUMENT, [], ElapsedMs: clock.ElapsedMilliseconds);
        if (waitMs > 0)
        {
            do
            {
                var observed = InspectCore(hwnd, maxDepth, maxElements, windowId,
                    startPath, startElementId, offset, includeDetails, nameContains, roleFilter);
                if (observed.Status != WindowInspectCode.COMPLETED || observed.Elements.Length > 0)
                    return observed with { ElapsedMs = clock.ElapsedMilliseconds };
                if (clock.ElapsedMilliseconds >= waitMs) break;
                Thread.Sleep(75);
            } while (true);
            return new(WindowInspectCode.STATE_NOT_OBSERVED, [], ElapsedMs: clock.ElapsedMilliseconds);
        }
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return new(WindowInspectCode.UIA_UNAVAILABLE, [], ElapsedMs: clock.ElapsedMilliseconds);
            var walker = TreeWalker.ControlViewWalker;
            string rootPath = "0";
            int rootDepth = 0;
            if (startPath is not null)
            {
                TryParsePath(startPath, out var path);
                var selected = ResolvePath(root, walker, path);
                if (selected is null) return new(WindowInspectCode.STALE_ELEMENT, []);
                TryParseElementRef(startElementId, out long previousSnapshot);
                if (!string.Equals(ElementRef(selected, windowId!, previousSnapshot), startElementId,
                    StringComparison.Ordinal)) return new(WindowInspectCode.STALE_ELEMENT, []);
                root = selected;
                rootPath = startPath;
                rootDepth = path.Length;
            }
            long snapshotMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var nodes = new List<WindowElement>(maxElements);
            var indices = new Dictionary<string, int>(StringComparer.Ordinal);
            var stack = new Stack<(AutomationElement Element, string Path, int Depth, string? ParentPath)>();
            stack.Push((root, rootPath, rootDepth, null));
            bool truncated = false;
            int visited = 0;
            bool filtered = nameContains is not null || roleFilter is not null;
            while (stack.Count != 0 && nodes.Count < maxElements && visited < MaxTraversal)
            {
                var item = stack.Pop();
                AutomationElement.AutomationElementInformation current;
                try { current = item.Element.Current; }
                catch (ElementNotAvailableException) { continue; }
                bool eligible = visited++ >= offset &&
                    (nameContains is null ||
                     current.ControlType != ControlType.Edit && current.ControlType != ControlType.Document &&
                     !current.IsPassword &&
                     (current.Name?.Contains(nameContains, StringComparison.OrdinalIgnoreCase) ?? false)) &&
                    (roleFilter is null || MatchesRole(current.ControlType, roleFilter.Value));
                if (eligible)
                {
                    bool password = current.IsPassword;
                    bool valueBearing = current.ControlType == ControlType.Edit ||
                        current.ControlType == ControlType.Document;
                    string name = password || valueBearing ? "[redacted]" : Bounded(current.Name);
                    string role = Bounded(current.ControlType?.ProgrammaticName);
                    string automationId = password ? "" : Bounded(current.AutomationId);
                    string[] patterns;
                    try { patterns = item.Element.GetSupportedPatterns().Take(12)
                        .Select(p => Bounded(p.ProgrammaticName, 64)).ToArray(); }
                    catch (ElementNotAvailableException) { patterns = []; }
                    string elementId = "";
                    if (windowId is not null)
                    {
                        try { elementId = ElementRef(item.Element, windowId, snapshotMs); }
                        catch (ElementNotAvailableException) { continue; }
                    }
                    int? parentIndex = item.ParentPath is not null &&
                        indices.TryGetValue(item.ParentPath, out int found) ? found : null;
                    indices[item.Path] = nodes.Count;
                    WindowBounds? bounds = null;
                    WindowPoint? clickable = null;
                    bool? focusable = null, focused = null;
                    if (includeDetails)
                    {
                        try
                        {
                            var rect = current.BoundingRectangle;
                            if (!rect.IsEmpty && double.IsFinite(rect.X) && double.IsFinite(rect.Y) &&
                                double.IsFinite(rect.Width) && double.IsFinite(rect.Height) &&
                                Math.Abs(rect.X) < 1_000_000 && Math.Abs(rect.Y) < 1_000_000 &&
                                rect.Width is >= 0 and < 1_000_000 && rect.Height is >= 0 and < 1_000_000)
                                bounds = new((int)Math.Round(rect.X), (int)Math.Round(rect.Y),
                                    (int)Math.Round(rect.Width), (int)Math.Round(rect.Height));
                            focusable = current.IsKeyboardFocusable;
                            focused = current.HasKeyboardFocus;
                            if (!current.IsOffscreen && item.Element.TryGetClickablePoint(out var point))
                                clickable = new((int)Math.Round(point.X), (int)Math.Round(point.Y));
                        }
                        catch (Exception e) when (e is ElementNotAvailableException or COMException or InvalidOperationException)
                        { /* Geometry is optional; the element reference remains useful. */ }
                    }
                    nodes.Add(new(item.Path, parentIndex, item.Depth, role, name, automationId,
                        current.IsEnabled, current.IsOffscreen, password, patterns, elementId,
                        bounds, focusable, focused, clickable));
                }
                if (item.Depth >= maxDepth) continue;

                var children = new List<AutomationElement>();
                try
                {
                    var child = walker.GetFirstChild(item.Element);
                    // No page can consume more than offset + maxElements
                    // siblings at a single level. Avoid probing thousands of
                    // Chrome tabs just to return the first 100 controls.
                    while (child is not null && children.Count < (filtered ? MaxTraversal : offset + maxElements))
                    {
                        children.Add(child);
                        child = walker.GetNextSibling(child);
                    }
                    if (child is not null) truncated = true;
                }
                catch (ElementNotAvailableException) { continue; }
                for (int i = children.Count - 1; i >= 0; i--)
                    stack.Push((children[i], item.Path + "." + i, item.Depth + 1, item.Path));
            }
            bool hasMore = truncated || stack.Count != 0;
            return new(WindowInspectCode.COMPLETED, nodes.ToArray(), hasMore,
                ElapsedMs: clock.ElapsedMilliseconds,
                ElementRefExpiresUtc:windowId is null ? null :
                    DateTimeOffset.FromUnixTimeMilliseconds(snapshotMs).AddSeconds(ElementRefSeconds),
                NextOffset: hasMore && visited < MaxTraversal && nodes.Count > 0
                    ? (filtered ? visited : offset + nodes.Count) : null);
        }
        catch (ElementNotAvailableException) { return new(WindowInspectCode.STALE_WINDOW, [], ElapsedMs: clock.ElapsedMilliseconds); }
        catch (COMException e) { return new(WindowInspectCode.UIA_UNAVAILABLE, [], Detail: e.HResult.ToString("X8"), ElapsedMs: clock.ElapsedMilliseconds); }
        catch (InvalidOperationException) { return new(WindowInspectCode.UIA_UNAVAILABLE, [], ElapsedMs: clock.ElapsedMilliseconds); }
    }

    internal static bool MatchesRole(ControlType control, WindowRoleFilter role) => role switch
    {
        WindowRoleFilter.BUTTON => control == ControlType.Button,
        WindowRoleFilter.TEXT => control == ControlType.Text,
        WindowRoleFilter.EDIT => control == ControlType.Edit,
        WindowRoleFilter.COMBO_BOX => control == ControlType.ComboBox,
        WindowRoleFilter.CHECK_BOX => control == ControlType.CheckBox,
        WindowRoleFilter.RADIO_BUTTON => control == ControlType.RadioButton,
        WindowRoleFilter.TAB_ITEM => control == ControlType.TabItem,
        WindowRoleFilter.LIST_ITEM => control == ControlType.ListItem,
        WindowRoleFilter.HYPERLINK => control == ControlType.Hyperlink,
        WindowRoleFilter.MENU_ITEM => control == ControlType.MenuItem,
        WindowRoleFilter.PANE => control == ControlType.Pane,
        WindowRoleFilter.GROUP => control == ControlType.Group,
        WindowRoleFilter.WINDOW => control == ControlType.Window,
        WindowRoleFilter.DOCUMENT => control == ControlType.Document,
        WindowRoleFilter.CUSTOM => control == ControlType.Custom,
        _ => false
    };

    internal static async Task<WindowInspectResult> RunIsolatedAsync(string windowId, int maxDepth, int maxElements,
        CancellationToken cancellationToken, string? startPath = null, string? startElementId = null,
        int offset = 0, bool includeDetails = false, string? nameContains = null,
        WindowRoleFilter? roleFilter = null, int waitMs = 0,
        WindowInspectScope scope = WindowInspectScope.WINDOW, string? tabId = null,
        string? expectedProfile = null, string? expectedOrigin = null, string? expectedUrl = null)
    {
        if (!ValidArguments(windowId, maxDepth, maxElements, startPath, startElementId,
            offset, includeDetails, nameContains, roleFilter, waitMs, scope, tabId,
            expectedProfile, expectedOrigin, expectedUrl))
            return new(WindowInspectCode.INVALID_ARGUMENT, [], Detail: ArgumentError(windowId, maxDepth,
                maxElements, startPath, startElementId, offset, includeDetails, nameContains,
                roleFilter, waitMs, scope, tabId, expectedProfile, expectedOrigin, expectedUrl));
        if (scope != WindowInspectScope.WINDOW)
            return await WindowChromeBroker.InspectAsync(new(windowId, maxDepth, maxElements,
                startPath, startElementId, offset, includeDetails, nameContains, roleFilter,
                waitMs, scope, tabId, expectedProfile, expectedOrigin, expectedUrl), cancellationToken);
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return new(WindowInspectCode.WORKER_FAILED, []);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("window-inspect-worker");
        start.Environment.Remove("CONTROL_PLANE_API_KEY");
        start.Environment.Remove("OPENAI_API_KEY");
        using var process = new Process { StartInfo = start };
        bool launched = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (!process.Start()) return new(WindowInspectCode.WORKER_FAILED, []);
            launched = true;
            var request = new WindowInspectRequest(windowId, maxDepth, maxElements, startPath,
                startElementId, offset, includeDetails, nameContains, roleFilter, waitMs, scope, tabId,
                expectedProfile, expectedOrigin, expectedUrl);
            string payload = JsonSerializer.Serialize(request, PipeWire.Json);
            await process.StandardInput.WriteAsync(payload.AsMemory(), deadline.Token);
            await process.StandardInput.FlushAsync(deadline.Token);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            string json = await output;
            if (json.Length > 256000) return new(WindowInspectCode.WORKER_FAILED, [], Detail: "OUTPUT_LIMIT");
            return JsonSerializer.Deserialize<WindowInspectResult>(json, PipeWire.Json) ??
                new(WindowInspectCode.WORKER_FAILED, [], Detail: "EMPTY_RESULT");
        }
        catch (OperationCanceledException)
        {
            StopWorker(process, launched);
            return new(cancellationToken.IsCancellationRequested ? WindowInspectCode.WORKER_FAILED : WindowInspectCode.TIMEOUT, []);
        }
        catch (Exception e) when (e is Win32Exception or IOException or JsonException or InvalidOperationException)
        {
            StopWorker(process, launched);
            return new(WindowInspectCode.WORKER_FAILED, [], Detail: e.GetType().Name);
        }
    }

    private static void StopWorker(Process process, bool launched)
    {
        if (!launched) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
    }

    private static string Bounded(string? value, int limit = 128)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var bounded = value.Length > limit ? value[..limit] : value;
        return bounded.Replace('\r', ' ').Replace('\n', ' ');
    }
}
