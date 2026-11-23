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

// Address individual Chrome tabs without using foreground focus. This is a
// bounded CDP adapter, not a second browser manager or arbitrary script tool.
internal static partial class WindowChromeCdp
{
    private sealed record TabTarget(string Id, string Title, string Url, int BrowserWindowId);
    private sealed record TabElementRef(long Stamp, string TargetId, int BackendId, string LoaderId,
        string Digest);

    // A single-threaded USER broker owns these across requests. Each request
    // replaces the deadline, not the consented socket or tab attachment.
    private static ClientWebSocket? _tabSocket;
    private static Session? _tabSession;
    private static Uri? _tabEndpoint;
    private static ClientWebSocket? _pendingSocket;
    private static Task? _pendingConnect;
    private static CancellationTokenSource? _pendingDeadline;
    private static Uri? _pendingEndpoint;
    internal static readonly TimeSpan ConsentTimeout = TimeSpan.FromMinutes(5);
    internal const int ReconnectQuickWaitMs = 1200;
    private static readonly Dictionary<string, string> AttachedTabs = new(StringComparer.Ordinal);
    private static bool TabConnected => _tabSocket?.State == WebSocketState.Open && _tabSession is not null;
    private static bool _hadConnection;
    private static string? _sessionGeneration;
    internal static string? CurrentGeneration => _sessionGeneration;
    private static ChromeConnectionFailureReason? _lastFailure;
    private static ChromeConnectionFailureReason? _lastLossReason;

    internal static ChromeConnectionState MissingConnectionState(bool hadConnection) =>
        hadConnection ? ChromeConnectionState.CONNECTION_LOST : ChromeConnectionState.NOT_REQUESTED;

    internal static bool CdpTransportFailure(Exception e) => e is
        WebSocketException or IOException or OperationCanceledException or JsonException ||
        e is InvalidOperationException { Message: "CDP_SOCKET_CLOSED" };

    private static void ObserveCdpFailure(Exception e)
    {
        if (e is InvalidOperationException { Message: "CDP_TARGET_DETACHED" })
            AttachedTabs.Clear();
        if (!CdpTransportFailure(e)) return;
        _lastFailure = e switch
        {
            OperationCanceledException => ChromeConnectionFailureReason.TIMEOUT,
            JsonException => ChromeConnectionFailureReason.PROTOCOL_ERROR,
            IOException { Message: "CDP_SOCKET_CLOSED" } or
                InvalidOperationException { Message: "CDP_SOCKET_CLOSED" } =>
                ChromeConnectionFailureReason.SOCKET_CLOSED,
            _ => ChromeConnectionFailureReason.TRANSPORT_ERROR
        };
        _lastLossReason = _lastFailure;
        CloseTabConnection();
    }

    private static Task<Session> TabSessionAsync(Uri endpoint, CancellationToken deadline)
    {
        if (TabConnected && _tabEndpoint == endpoint)
        {
            _tabSession!.SetDeadline(deadline);
            return Task.FromResult(_tabSession!);
        }
        // Reconnect only through ConnectionState, which verifies the selected
        // native Chrome window before any consent handshake can begin.
        throw new IOException("CDP_SOCKET_CLOSED");
    }

    internal static void CloseTabConnection()
    {
        CancelPendingConnection();
        AttachedTabs.Clear();
        _tabSession = null;
        _tabEndpoint = null;
        _tabSocket?.Dispose();
        _tabSocket = null;
        _sessionGeneration = null;
    }

    private static void CancelPendingConnection()
    {
        _pendingDeadline?.Cancel();
        _pendingSocket?.Dispose();
        _pendingDeadline?.Dispose();
        _pendingDeadline = null;
        _pendingSocket = null;
        _pendingConnect = null;
        _pendingEndpoint = null;
    }

    // Chrome's approval-only server does not expose its current permission
    // state. A live authorized socket proves consent; an outstanding handshake
    // means only that consent has NOT YET been confirmed (not that it was denied).
    // Run one handshake independently of the short-lived MCP request deadline.
    // Chrome displays the per-connection dialog in its last-active window.
    // Select the verified native window immediately before the handshake;
    // once authorized, all tab operations remain background-only.
    private static bool PrepareConsentWindow(IntPtr hwnd,
        out ChromeConnectionFailureReason failure)
    {
        failure = ChromeConnectionFailureReason.FOREGROUND_DENIED;
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd))
        {
            failure = ChromeConnectionFailureReason.WINDOW_HIDDEN;
            return false;
        }
        if (IsIconic(hwnd))
        {
            _ = ShowWindowAsync(hwnd, 9); // SW_RESTORE on Chrome's own UI thread
            if (!SpinWait.SpinUntil(() => !IsIconic(hwnd), 1500))
            {
                failure = ChromeConnectionFailureReason.WINDOW_MINIMIZED;
                return false;
            }
        }
        if (GetForegroundWindow() != hwnd)
        {
            _ = SetForegroundWindow(hwnd);
            if (!SpinWait.SpinUntil(() => GetForegroundWindow() == hwnd, 750))
            {
                // A native UIA focus request sometimes succeeds when Windows
                // refuses SetForegroundWindow. Never send synthetic ALT keys.
                try
                {
                    var root = AutomationElement.FromHandle(hwnd);
                    if (root?.Current.IsKeyboardFocusable == true) root.SetFocus();
                }
                catch (Exception e) when (e is COMException or ElementNotAvailableException or
                    InvalidOperationException) { }
                if (!SpinWait.SpinUntil(() => GetForegroundWindow() == hwnd, 750)) return false;
            }
        }
        Thread.Sleep(150); // Let Chrome record its newly active window.
        return GetForegroundWindow() == hwnd;
    }

    private static void ProbeTabConnection()
    {
        if (!TabConnected) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            _tabSession!.SetDeadline(timeout.Token);
            _ = _tabSession.Call("Browser.getVersion", new { }).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is WebSocketException or IOException or JsonException or
            OperationCanceledException or InvalidOperationException or KeyNotFoundException)
        { ObserveCdpFailure(e); }
    }

    private static ChromeConnectionState ConnectionState(Uri endpoint, bool request,
        IntPtr consentWindow, bool retry = false)
    {
        if (!WindowChromeBroker.IsDedicatedWorker)
            return ChromeConnectionState.CONNECTION_FAILED;
        if (!TabConnected && _tabSession is not null)
        {
            _lastFailure ??= ChromeConnectionFailureReason.SOCKET_CLOSED;
            _lastLossReason = _lastFailure;
            CloseTabConnection();
        }
        if (TabConnected && _tabEndpoint == endpoint) return ChromeConnectionState.CONNECTED;
        if (_pendingConnect is not null && _pendingEndpoint == endpoint)
        {
            if (!_pendingConnect.IsCompleted) return ChromeConnectionState.CONSENT_PENDING;
            if (_pendingConnect.IsCompletedSuccessfully && _pendingSocket?.State == WebSocketState.Open)
            {
                var socket = _pendingSocket!;
                _pendingSocket = null;
                _pendingConnect = null;
                _pendingEndpoint = null;
                _pendingDeadline?.Dispose();
                _pendingDeadline = null;
                CloseTabConnection();
                _tabSocket = socket;
                _tabEndpoint = endpoint;
                _tabSession = new Session(socket, CancellationToken.None);
                _sessionGeneration = Guid.NewGuid().ToString("N");
                _hadConnection = true;
                _lastFailure = null;
                return ChromeConnectionState.CONNECTED;
            }
            ChromeConnectionState failure = _pendingConnect.IsCanceled
                ? ChromeConnectionState.CONSENT_TIMED_OUT
                : ChromeConnectionState.CONNECTION_FAILED;
            _lastFailure = _pendingConnect.IsCanceled
                ? ChromeConnectionFailureReason.CONSENT_TIMEOUT
                : ChromeConnectionFailureReason.HANDSHAKE_FAILED;
            if (!retry) return failure;
        }
        if (!request) return MissingConnectionState(_hadConnection);
        if (!PrepareConsentWindow(consentWindow, out var activationFailure))
        {
            _lastFailure = activationFailure;
            return ChromeConnectionState.CONSENT_WINDOW_UNAVAILABLE;
        }
        CloseTabConnection();
        _lastFailure = null; // A new pending handshake is not a failed attempt.
        _pendingSocket = new ClientWebSocket();
        _pendingEndpoint = endpoint;
        _pendingDeadline = new CancellationTokenSource(ConsentTimeout);
        _pendingConnect = _pendingSocket.ConnectAsync(endpoint, _pendingDeadline.Token);
        // The task is inspected on STATUS/poll. Observe faults if a new
        // selection kills this worker before the approval dialog completes.
        _ = _pendingConnect.ContinueWith(t => { _ = t.Exception; },
            TaskContinuationOptions.OnlyOnFaulted);
        // A previous user-approved Chrome session commonly reconnects without
        // another dialog. Let that handshake complete during this same call.
        var pending = _pendingConnect!;
        if (_hadConnection && SpinWait.SpinUntil(() => pending.IsCompleted,
            ReconnectQuickWaitMs))
            return ConnectionState(endpoint, request: false, consentWindow: consentWindow);
        return ChromeConnectionState.CONSENT_PENDING;
    }

    internal static ChromeConnectionResult ConnectInWorker(ChromeConnectionRequest request)
    {
        using var self = Process.GetCurrentProcess();
        var profile = ChromeProfileCatalog.ReadWindowProfile(request.WindowId, self.SessionId);
        if (profile is null || !ChromeProfileCatalog.MatchesSelector(profile, request.ExpectedProfile))
            return new(ChromeConnectionCode.PROFILE_MISMATCH,
                ChromeConnectionState.NOT_REQUESTED, ChromeConnectionNextAction.SELECT_PROFILE);

        if (request.Action == ChromeConnectionAction.STATUS)
        {
            ProbeTabConnection();
            if (_pendingConnect is null && !TabConnected)
            {
                if (_hadConnection && _lastLossReason is null)
                    _lastLossReason = ChromeConnectionFailureReason.SOCKET_CLOSED;
                return new(ChromeConnectionCode.OK, MissingConnectionState(_hadConnection),
                    ChromeConnectionNextAction.RETRY_CONNECTION, profile,
                    FailureReason: _lastFailure ?? (_hadConnection
                        ? ChromeConnectionFailureReason.SOCKET_CLOSED : null),
                    LastLossReason: _lastLossReason);
            }
        }
        if (!TryEndpoint(out var endpoint, out string failure))
            return new(ChromeConnectionCode.OK, ChromeConnectionState.DEBUGGING_UNAVAILABLE,
                ChromeConnectionNextAction.ENABLE_CHROME_DEBUGGING, profile, Detail: failure);
        if (WindowCatalog.ResolveId(request.WindowId, self.SessionId, out var hwnd) != WindowResolveCode.OK)
            return new(ChromeConnectionCode.STALE_WINDOW, ChromeConnectionState.NOT_REQUESTED);
        var state = ConnectionState(endpoint!, request.Action == ChromeConnectionAction.REQUEST,
            hwnd, retry: request.Action == ChromeConnectionAction.REQUEST);
        var next = state switch
        {
            ChromeConnectionState.CONSENT_PENDING => ChromeConnectionNextAction.ALLOW_CHROME_PROMPT,
            ChromeConnectionState.CONNECTED => ChromeConnectionNextAction.NONE,
            _ => ChromeConnectionNextAction.RETRY_CONNECTION
        };
        return new(ChromeConnectionCode.OK, state, next, profile,
            FailureReason: state == ChromeConnectionState.CONNECTED ? null : _lastFailure,
            LastLossReason: _lastLossReason,
            SessionGeneration: state == ChromeConnectionState.CONNECTED ? _sessionGeneration : null);
    }

    internal static bool ValidTargetId(string? id) => id is { Length: >= 16 and <= 64 } &&
        id.All(Uri.IsHexDigit);

    internal static string? ContextArgumentError(string? profile, string? origin, string? url)
    {
        if (profile is not null && (profile.Length is < 1 or > 100 ||
            string.IsNullOrWhiteSpace(profile) || profile.Contains('\0')))
            return "expected_profile: expected 1..100 non-whitespace characters without NUL";
        if (origin is not null && (origin.Length > 2048 || !TryHttpUri(origin, out var parsedOrigin) ||
            !string.Equals(origin.TrimEnd('/'), parsedOrigin!.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase)))
            return "expected_origin: expected an HTTP(S) origin (scheme, host and optional port)";
        if (url is not null && (url.Length > 2048 || !TryHttpUri(url, out _)))
            return "expected_url: expected an absolute HTTP(S) URL up to 2048 characters";
        if (origin is not null && url is not null && !MatchesContext(url, origin, null))
            return "expected_origin: does not match expected_url";
        return null;
    }

    private static bool TryHttpUri(string value, out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri) &&
        uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0;

    internal static bool MatchesContext(string actualUrl, string? expectedOrigin, string? expectedUrl)
    {
        if (!TryHttpUri(actualUrl, out var actual)) return false;
        return (expectedOrigin is null || string.Equals(actual!.GetLeftPart(UriPartial.Authority),
                expectedOrigin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) &&
            (expectedUrl is null || TryHttpUri(expectedUrl, out var expected) &&
                string.Equals(actual!.AbsoluteUri, expected!.AbsoluteUri, StringComparison.Ordinal));
    }

    // Verify the *native Chrome toolbar* profile button, never page content or
    // an inferred profile from a tab title. A missing/ambiguous button fails closed.
    // Chrome's native accessibility label often appends account details. A
    // delimited exact profile name is acceptable; a substring or prefix of
    // another profile (Valer vs Valery) is not.
    internal static bool ProfileLabelMatches(string label, string expected)
    {
        if (string.Equals(label, expected, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (string prefix in new[] { "", "Profile: ", "Profile " })
        {
            if (!label.StartsWith(prefix + expected, StringComparison.OrdinalIgnoreCase)) continue;
            string rest = label[(prefix.Length + expected.Length)..].TrimStart(' ');
            if (rest.Length > 0 && rest[0] is ',' or ';' or '(' or '\n' or '\r') return true;
        }
        return false;
    }

    internal static bool VerifiedChromeProfile(IntPtr hwnd, string expected)
    {
        try
        {
            if (!GetWindowRect(hwnd, out _)) return false;
            GetWindowThreadProcessId(hwnd, out uint ownerPid);
            if (ownerPid == 0) return false;
            using var owner = Process.GetProcessById(checked((int)ownerPid));
            if (!owner.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase)) return false;
            using var self = Process.GetCurrentProcess();
            var id = WindowCatalog.FormatId(new WindowIdentity(hwnd.ToInt64(), owner.Id,
                owner.StartTime.ToFileTimeUtc(), self.SessionId));
            var profile = ChromeProfileCatalog.ReadWindowProfile(id, self.SessionId);
            return profile is not null && ChromeProfileCatalog.MatchesSelector(profile, expected);
        }
        catch (Exception e) when (e is COMException or ElementNotAvailableException or
            InvalidOperationException or ArgumentException or Win32Exception or OverflowException) { return false; }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    private static bool TryParseTabElementRef(string? token, out TabElementRef? result,
        bool requireFresh = true)
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
        if (requireFresh && (age is < -5000 or > 30000)) return false;
        result = new(stamp, fields[2], backendId, fields[4], fields[5]);
        return true;
    }

    internal static bool ValidTabElementRef(string? token) => TryParseTabElementRef(token, out _);
    internal static bool WellFormedTabElementRef(string? token) =>
        TryParseTabElementRef(token, out _, requireFresh: false);

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

    // A DevToolsActivePort file can survive an earlier Chrome debugging session.
    // A stale file is not evidence of an active, authorized CDP connection.
    internal static bool LoopbackPortOpen(int port)
    {
        if (port is < 1 or > 65535) return false;
        try
        {
            using var client = new TcpClient();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
            client.ConnectAsync(IPAddress.Loopback, port, deadline.Token).GetAwaiter().GetResult();
            return client.Connected;
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException)
        { return false; }
    }

    private static bool TryEndpoint(out Uri? endpoint, out string failure)
    {
        endpoint = null;
        failure = "CHROME_REMOTE_DEBUGGING_NOT_ENABLED";
        string data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(data)) return false;
        try
        {
            if (!TryReadEndpoint(File.ReadAllText(Path.Combine(data, "Google", "Chrome",
                "User Data", "DevToolsActivePort")), out endpoint))
            {
                failure = "CHROME_DEBUG_ENDPOINT_INVALID";
                return false;
            }
            if (!TabConnected || _tabEndpoint != endpoint)
            {
                if (!LoopbackPortOpen(endpoint!.Port))
                {
                    failure = "CHROME_DEBUG_PORT_CLOSED";
                    return false;
                }
            }
            failure = "";
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (UnauthorizedAccessException)
        {
            failure = "CHROME_DEBUG_ENDPOINT_ACCESS_DENIED";
            return false;
        }
        catch (IOException)
        {
            failure = "CHROME_DEBUG_ENDPOINT_READ_FAILED";
            return false;
        }
    }

    private static bool MatchesBounds(WindowBounds native, WindowBounds chrome, double dpi)
    {
        static bool Close(int a, double b) => Math.Abs(a - b) <= BoundsTolerance;
        bool At(double scale) => Close(native.X, chrome.X * scale) &&
            Close(native.Y, chrome.Y * scale) && Close(native.Width, chrome.Width * scale) &&
            Close(native.Height, chrome.Height * scale);
        return chrome.Width > 0 && chrome.Height > 0 && (At(1) || dpi is >= 1 and <= 4 && At(dpi));
    }

    internal enum CdpWindowMappingState { NOT_FOUND, AMBIGUOUS }

    private sealed class CdpWindowMappingException(CdpWindowMappingState state)
        : InvalidOperationException(state == CdpWindowMappingState.NOT_FOUND
            ? "CDP_WINDOW_NO_MATCH" : "CDP_WINDOW_NOT_UNIQUE")
    {
        internal CdpWindowMappingState State { get; } = state;
    }

    internal static WindowInspectCode InspectMappingCode(CdpWindowMappingState state) =>
        state == CdpWindowMappingState.NOT_FOUND ? WindowInspectCode.CDP_TARGET_NOT_FOUND :
        WindowInspectCode.CDP_TARGET_AMBIGUOUS;

    internal static bool TryTargetContextId(JsonElement target, out string? contextId)
    {
        contextId = null;
        if (!target.TryGetProperty("browserContextId", out var value)) return true;
        if (value.ValueKind != JsonValueKind.String) return false;
        contextId = value.GetString();
        return contextId is { Length: >= 1 and <= 512 } &&
            !string.IsNullOrWhiteSpace(contextId);
    }

    // Browser.getWindowForTarget yields the Chrome window identity. Targets in
    // one window may expose different optional browserContextId values; the
    // context is not the HWND identity and cannot make one window ambiguous.
    // Reject duplicate *window IDs* after native title + bounds validation.
    internal static int? UniqueWindowId(
        IEnumerable<(int WindowId, string? ContextId)> candidates) =>
        UniqueWindowId(candidates, out _);

    internal static int? UniqueWindowId(
        IEnumerable<(int WindowId, string? ContextId)> candidates, out int distinctCount)
    {
        var unique = candidates.Where(c => c.WindowId > 0 &&
                (c.ContextId is null || c.ContextId is { Length: >= 1 and <= 512 } &&
                    !string.IsNullOrWhiteSpace(c.ContextId)))
            .Select(c => c.WindowId).Distinct().Take(2).ToArray();
        distinctCount = unique.Length;
        return unique.Length == 1 ? unique[0] : null;
    }

    // Chrome accessibility labels can decorate a tab title with its memory
    // state or selected state. Extract only the exact quoted title; never do
    // fuzzy substring matching against arbitrary web page controls.
    internal static string? ChromeTabLabel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (string prefix in new[] { "Вкладка \"", "Tab \"" })
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            int end = name.IndexOf('"', prefix.Length);
            return end > prefix.Length ? name[prefix.Length..end] : null;
        }
        return name;
    }

    // A page can contain ARIA tab items. Only one sibling tab-strip group
    // containing the native window's *active tab title* can be evidence.
    internal static string[]? NativeTabStripTitles(
        IEnumerable<(string Path, string Name)> tabItems, string nativeWindowTitle)
    {
        const string suffix = " - Google Chrome";
        if (!nativeWindowTitle.EndsWith(suffix, StringComparison.Ordinal)) return null;
        string activeTitle = nativeWindowTitle[..^suffix.Length];
        var groups = tabItems.Select(t =>
        {
            int dot = t.Path.LastIndexOf('.');
            return (Parent: dot > 0 ? t.Path[..dot] : "", Title: ChromeTabLabel(t.Name));
        }).Where(t => t.Parent.Length > 0 && t.Title is not null)
          .GroupBy(t => t.Parent, StringComparer.Ordinal)
          .Where(g => g.Any(t => string.Equals(t.Title, activeTitle, StringComparison.Ordinal)))
          .Take(2).ToArray();
        return groups.Length == 1 ? groups[0].Select(t => t.Title!)
            .Distinct(StringComparer.Ordinal).ToArray() : null;
    }

    // An overlapping/maximized second Chrome window can have the SAME active
    // page title. Disambiguate only when a native tab-strip title belongs to
    // exactly one candidate CDP window; competing exclusive matches fail shut.
    internal static int? UniqueTabFingerprintWindow(IEnumerable<int> candidateWindowIds,
        IEnumerable<(int WindowId, string Title)> pageTitles, IEnumerable<string> nativeTabTitles)
    {
        var candidates = candidateWindowIds.Where(id => id > 0).ToHashSet();
        var native = nativeTabTitles.ToHashSet(StringComparer.Ordinal);
        var owners = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var page in pageTitles)
        {
            if (!candidates.Contains(page.WindowId) || !native.Contains(page.Title)) continue;
            if (!owners.TryGetValue(page.Title, out var windows))
                owners.Add(page.Title, windows = []);
            windows.Add(page.WindowId);
        }
        var unique = owners.Values.Where(w => w.Count == 1).Select(w => w.Single())
            .Distinct().Take(2).ToArray();
        return unique.Length == 1 ? unique[0] : null;
    }

    private static async Task<List<TabTarget>> TargetsForWindow(Session cdp, IntPtr hwnd,
        string windowId, CancellationToken cancellationToken)
    {
        if (!GetWindowRect(hwnd, out var rect)) return [];
        string nativeTitle = GetTitle(hwnd);
        var native = new WindowBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        uint reportedDpi = GetDpiForWindow(hwnd);
        double dpi = reportedDpi == 0 ? 1 : reportedDpi / 96.0;
        var targets = await cdp.Call("Target.getTargets", new { });
        var pages = new List<(string Id, string Title, string Url, string? ContextId)>();
        foreach (var info in targets.GetProperty("targetInfos").EnumerateArray())
        {
            if (info.GetProperty("type").GetString() != "page" ||
                !info.TryGetProperty("url", out var address) ||
                address.ValueKind != JsonValueKind.String) continue;
            if (!TryTargetContextId(info, out string? contextId)) continue;
            string id = info.GetProperty("targetId").GetString() ?? "";
            if (!ValidTargetId(id)) continue;
            pages.Add((id, info.GetProperty("title").GetString() ?? "",
                address.GetString() ?? "", contextId));
        }

        var boundCache = new Dictionary<int, bool>();
        var ownerCache = new Dictionary<string, int>(StringComparer.Ordinal);
        async Task<int> WindowId(string targetId)
        {
            if (ownerCache.TryGetValue(targetId, out int cached)) return cached;
            var owner = await cdp.Call("Browser.getWindowForTarget", new { targetId });
            int windowId = owner.GetProperty("windowId").GetInt32();
            ownerCache.Add(targetId, windowId);
            return windowId;
        }

        var titleMatches = new List<(int WindowId, string? ContextId)>();
        foreach (var page in pages.Where(p => nativeTitle.StartsWith(p.Title + " - Google Chrome",
                     StringComparison.Ordinal)))
        {
            int browserWindowId = await WindowId(page.Id);
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
            if (boundsMatch) titleMatches.Add((browserWindowId, page.ContextId));
        }

        // A native, profile-verified HWND must match exactly one CDP window.
        // Distinct TargetInfo contexts are allowed *within that same window*;
        // multiple CDP window IDs (even at identical bounds) are not guessed.
        var selected = UniqueWindowId(titleMatches, out int distinctCount);
        if (selected is null && distinctCount > 1)
        {
            // Reuse the existing bounded, isolated UIA inspector. Never run a
            // potentially hung Chrome accessibility provider in the CDP broker.
            var ui = await WindowInspector.RunIsolatedAsync(windowId,
                WindowInspector.MaxDepth, 100, cancellationToken,
                roleFilter: WindowRoleFilter.TAB_ITEM);
            if (ui.Status == WindowInspectCode.COMPLETED && !ui.Truncated)
            {
                var tabTitles = NativeTabStripTitles(
                    ui.Elements.Select(e => (e.Path, e.Name)), nativeTitle);
                if (tabTitles is not null)
                {
                    var titles = new List<(int WindowId, string Title)>();
                    var nativeLabels = tabTitles.ToHashSet(StringComparer.Ordinal);
                    var candidates = titleMatches.Select(x => x.WindowId).ToHashSet();
                    foreach (var page in pages.Where(p => nativeLabels.Contains(p.Title)))
                    {
                        int id = await WindowId(page.Id);
                        if (candidates.Contains(id)) titles.Add((id, page.Title));
                    }
                    selected = UniqueTabFingerprintWindow(candidates, titles, tabTitles);
                }
            }
        }
        if (selected is null)
            throw new CdpWindowMappingException(distinctCount == 0 ?
                CdpWindowMappingState.NOT_FOUND : CdpWindowMappingState.AMBIGUOUS);

        var found = new List<TabTarget>();
        foreach (var page in pages.Where(p => TryHttpUri(p.Url, out _)))
        {
            if (await WindowId(page.Id) == selected.Value)
                found.Add(new(page.Id, page.Title, page.Url, selected.Value));
        }
        return found;
    }

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
            if (expectedProfile is not null && !VerifiedChromeProfile(hwnd, expectedProfile))
                return new(WindowInspectCode.CDP_CONTEXT_MISMATCH, [], Detail: "PROFILE_NOT_VERIFIED");
            for (int attempt = 0; attempt < 2; attempt++)
            {
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
                    // No target operation was dispatched. This is a mapping
                    // failure, not CDP transport loss: keep native consent.
                    return new(InspectMappingCode(e.State), [], Detail:e.Message);
                }
                catch (Exception e) when (e is WebSocketException or IOException or JsonException or
                    OperationCanceledException or InvalidOperationException or KeyNotFoundException)
                {
                    bool transportLost = CdpTransportFailure(e);
                    ObserveCdpFailure(e);
                    if (transportLost && attempt == 0) continue; // Read-only retry.
                    return new(WindowInspectCode.CDP_UNAVAILABLE, [],
                        Detail: e is InvalidOperationException ? e.Message :
                            transportLost ? "CDP_TRANSPORT_LOST" : e.GetType().Name);
                }
            }
            return new(WindowInspectCode.CDP_UNAVAILABLE, [], Detail: "CDP_RECONNECT_FAILED");
        }
        catch (Exception e) when (e is WebSocketException or IOException or JsonException or
            OperationCanceledException or InvalidOperationException or KeyNotFoundException or
            ArgumentException or Win32Exception)
        {
            ObserveCdpFailure(e);
            return new(WindowInspectCode.CDP_UNAVAILABLE,
                [], Detail: e is InvalidOperationException ? e.Message : e is WebSocketException ws
                    ? $"CDP_WEBSOCKET_{ws.WebSocketErrorCode}" : e.GetType().Name);
        }
    }

    private static async Task<WindowInspectResult> InspectTabAsync(Uri endpoint, IntPtr hwnd,
        string windowId, WindowInspectScope scope, string? tabId, int maxElements, int offset,
        string? nameFilter, WindowRoleFilter? roleFilter,
        string? expectedProfile, string? expectedOrigin, string? expectedUrl)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(
            TabConnected ? 7500 : 24000));
        var cdp = await TabSessionAsync(endpoint, timeout.Token);
        var tabs = await TargetsForWindow(cdp, hwnd, windowId, timeout.Token);
        if (scope == WindowInspectScope.TABS)
        {
            tabs = tabs.Where(t => MatchesContext(t.Url, expectedOrigin, expectedUrl)).ToList();
            var page = tabs.Skip(offset).Take(maxElements).Select(t =>
            {
                var uri = new Uri(t.Url);
                return new WindowTab(t.Id, t.Title, uri.GetLeftPart(UriPartial.Path),
                    uri.GetLeftPart(UriPartial.Authority));
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
                selectedAddress.GetLeftPart(UriPartial.Authority))]);
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

    // AX roles describe accessibility, not the browser's ability to receive
    // pointer input. In particular a clickable <summary>, custom widget or
    // editable field may be exposed as "generic" or "textbox". Route every
    // pointer-like action through the same verified DOM hit test.
    internal static bool PointerAction(WindowActionKind action) => action is
        WindowActionKind.CLICK or WindowActionKind.DOUBLE_CLICK or WindowActionKind.RIGHT_CLICK or
        WindowActionKind.INVOKE or WindowActionKind.SELECT or
        WindowActionKind.TOGGLE or WindowActionKind.EXPAND or WindowActionKind.COLLAPSE;

    internal static bool? AxActionState(JsonElement node, WindowActionKind action)
    {
        string? property = action switch
        {
            WindowActionKind.EXPAND or WindowActionKind.COLLAPSE => "expanded",
            WindowActionKind.SELECT => "selected",
            WindowActionKind.TOGGLE => "checked",
            _ => null
        };
        if (property is null || !node.TryGetProperty("properties", out var properties)) return null;
        foreach (var item in properties.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out var name) ||
                name.GetString() != property &&
                !(action == WindowActionKind.TOGGLE && name.GetString() == "pressed") ||
                !item.TryGetProperty("value", out var value) ||
                !value.TryGetProperty("value", out var state)) continue;
            if (state.ValueKind == JsonValueKind.True) return true;
            if (state.ValueKind == JsonValueKind.False) return false;
            if (state.ValueKind == JsonValueKind.String &&
                bool.TryParse(state.GetString(), out bool parsed)) return parsed;
        }
        return null;
    }

    private static async Task<bool> EditableDomNode(Session cdp, string sessionId, int backendNodeId)
    {
        var resolved = await cdp.Call("DOM.resolveNode", new { backendNodeId }, sessionId);
        string objectId = resolved.GetProperty("object").GetProperty("objectId").GetString()!;
        var editable = await cdp.Call("Runtime.callFunctionOn", new
        {
            objectId,
            functionDeclaration = "function(){const e=this;return e instanceof Element && !e.closest('[inert]') && (e.isContentEditable || (e instanceof HTMLInputElement && !['button','checkbox','color','file','hidden','image','radio','range','reset','submit'].includes(e.type) && !e.readOnly && !e.disabled) || (e instanceof HTMLTextAreaElement && !e.readOnly && !e.disabled));}",
            returnByValue = true, silent = true
        }, sessionId);
        return editable.GetProperty("result").TryGetProperty("value", out var value) &&
            value.ValueKind == JsonValueKind.True;
    }

    private static async Task ReplaceFocusedText(Session cdp, string sessionId, string value)
    {
        // Chrome receives the same replacement gesture for foreground and
        // background tabs. Only the focused tab target receives these events.
        await cdp.Call("Input.dispatchKeyEvent", new { type = "keyDown", key = "Control",
            code = "ControlLeft", windowsVirtualKeyCode = 17 }, sessionId);
        await cdp.Call("Input.dispatchKeyEvent", new { type = "keyDown", key = "a",
            code = "KeyA", windowsVirtualKeyCode = 65, modifiers = 2 }, sessionId);
        await cdp.Call("Input.dispatchKeyEvent", new { type = "keyUp", key = "a",
            code = "KeyA", windowsVirtualKeyCode = 65, modifiers = 2 }, sessionId);
        await cdp.Call("Input.dispatchKeyEvent", new { type = "keyUp", key = "Control",
            code = "ControlLeft", windowsVirtualKeyCode = 17 }, sessionId);
        if (value.Length > 0) await cdp.Call("Input.insertText", new { text = value }, sessionId);
        else
        {
            await cdp.Call("Input.dispatchKeyEvent", new { type = "keyDown", key = "Backspace",
                code = "Backspace", windowsVirtualKeyCode = 8 }, sessionId);
            await cdp.Call("Input.dispatchKeyEvent", new { type = "keyUp", key = "Backspace",
                code = "Backspace", windowsVirtualKeyCode = 8 }, sessionId);
        }
    }

    private static async Task<bool> ReadTextValue(Session cdp, string sessionId, int backendNodeId,
        string expectedValue)
    {
        var resolved = await cdp.Call("DOM.resolveNode", new { backendNodeId }, sessionId);
        string objectId = resolved.GetProperty("object").GetProperty("objectId").GetString()!;
        var readback = await cdp.Call("Runtime.callFunctionOn", new
        {
            objectId,
            // Only a boolean leaves the page, including for password inputs.
            functionDeclaration = "function(expected){return ('value' in this ? this.value : this.textContent) === expected;}",
            arguments = new[] { new { value = expectedValue } },
            returnByValue = true, silent = true
        }, sessionId);
        return readback.GetProperty("result").TryGetProperty("value", out var value) &&
            value.ValueKind == JsonValueKind.True;
    }

    internal static WindowActionResult InvokeTab(IntPtr hwnd, WindowActionRequest request,
        Stopwatch clock)
    {
        if (!TryParseTabElementRef(request.ElementId, out var target) || target is null)
            return new(WindowActionCode.STALE_ELEMENT);
        if (!TryEndpoint(out var endpoint, out string endpointFailure))
            return new(WindowActionCode.CDP_UNAVAILABLE, FailureStage: WindowFailureStage.CDP_CONNECT,
                Detail: endpointFailure);
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var connection = ConnectionState(endpoint!, request: false, hwnd);
                if (connection == ChromeConnectionState.CONSENT_PENDING)
                    return new(WindowActionCode.CDP_CONSENT_PENDING,
                        FailureStage: WindowFailureStage.CDP_CONNECT, Detail: "ALLOW_CHROME_PROMPT");
                if (connection != ChromeConnectionState.CONNECTED)
                    return new(WindowActionCode.CDP_UNAVAILABLE,
                        FailureStage: WindowFailureStage.CDP_CONNECT, Detail: connection.ToString());
                var result = InvokeTabAsync(endpoint!, hwnd, request, target, clock)
                    .GetAwaiter().GetResult();
                // Only a proven pre-dispatch transport failure may be retried.
                // Never replay a click after its effect could be unknown.
                if (attempt == 0 && CanRetryCdpAction(result, TabConnected)) continue;
                return result;
            }
            return new(WindowActionCode.CDP_UNAVAILABLE, FailureStage: WindowFailureStage.CDP_CONNECT);
        }
        catch (Exception e) when (e is WebSocketException or IOException or JsonException or
            OperationCanceledException or InvalidOperationException or KeyNotFoundException)
        {
            ObserveCdpFailure(e);
            return new(WindowActionCode.CDP_UNAVAILABLE, FailureStage: WindowFailureStage.CDP_CONNECT,
                ExceptionType: e.GetType().Name,
                Detail: e is WebSocketException ws ? $"CDP_WEBSOCKET_{ws.WebSocketErrorCode}" : null,
                ElapsedMs: clock.ElapsedMilliseconds);
        }
    }

    internal static bool CanRetryCdpAction(WindowActionResult result, bool connected) =>
        result.Status == WindowActionCode.CDP_UNAVAILABLE && !result.EffectUnknown &&
        result.ExecutionMethod is null && !connected;

    private static async Task<WindowActionResult> InvokeTabAsync(Uri endpoint, IntPtr hwnd,
        WindowActionRequest request, TabElementRef target, Stopwatch clock)
    {
        bool dispatched = false;
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(request.ObservationTimeoutMs + (TabConnected ? 5500 : 24000)));
        try
        {
            var cdp = await TabSessionAsync(endpoint, timeout.Token);
            var tabs = await TargetsForWindow(cdp, hwnd, request.WindowId, timeout.Token);
            var selected = tabs.SingleOrDefault(t => t.Id == target.TargetId);
            if (selected is null) return Rejected(WindowActionCode.CDP_TARGET_AMBIGUOUS);
            if (!MatchesContext(selected.Url, request.ExpectedOrigin, request.ExpectedUrl) ||
                request.ExpectedProfile is not null && !VerifiedChromeProfile(hwnd, request.ExpectedProfile))
                return Rejected(WindowActionCode.CDP_CONTEXT_MISMATCH);
            string sessionId = await Attach(cdp, selected.Id);
            string loader = await Loader(cdp, sessionId);
            if (loader != target.LoaderId) return Rejected(WindowActionCode.STALE_ELEMENT);
            var ax = await cdp.Call("Accessibility.getFullAXTree", new { }, sessionId);
            var nodeMatches = ax.GetProperty("nodes").EnumerateArray().Where(node =>
                node.TryGetProperty("backendDOMNodeId", out var backend) &&
                backend.ValueKind == JsonValueKind.Number && backend.GetInt32() == target.BackendId &&
                !(node.TryGetProperty("ignored", out var ignored) && ignored.GetBoolean())).ToArray();
            if (nodeMatches.Length != 1) return Rejected(WindowActionCode.STALE_ELEMENT);
            // Protected fields remain redacted in inspection, but an explicit
            // action on their fresh element reference may still enter text.
            if (AxDisabled(nodeMatches[0])) return Rejected(WindowActionCode.DISABLED);
            string role = AxValue(nodeMatches[0], "role"), name = AxValue(nodeMatches[0], "name");
            if (!string.Equals(ElementDigest(request.WindowId, selected.Id, target.BackendId,
                loader, selected.Url, role, name), target.Digest, StringComparison.OrdinalIgnoreCase))
                return Rejected(WindowActionCode.STALE_ELEMENT);
            if (request.ExpectedName is not null)
            {
                bool before = request.ExpectedKind is WindowExpectedKind.UIA_ELEMENT or
                    WindowExpectedKind.UIA_VALUE or WindowExpectedKind.UIA_ENABLED
                    ? ExpectedAxState(ax, request)
                    : WindowAction.ExternalStatePresent(request.ExpectedKind, request.ExpectedName);
                if (before == (request.ExpectedChange == WindowExpectedChange.APPEARS) &&
                    (request.ExpectedEnabledName is null ||
                     HasAxEnabled(ax, request.ExpectedEnabledName, request.ExpectedEnabled)))
                    return Rejected(WindowActionCode.EXPECTED_STATE_ALREADY_TRUE);
            }
            bool? stateBefore = AxActionState(nodeMatches[0], request.Action);
            bool? desiredState = request.Action switch
            {
                WindowActionKind.EXPAND => true,
                WindowActionKind.COLLAPSE => false,
                WindowActionKind.SELECT => true,
                WindowActionKind.TOGGLE when stateBefore is not null => !stateBefore.Value,
                _ => null
            };
            if (desiredState is not null && stateBefore == desiredState)
                return Rejected(WindowActionCode.EXPECTED_STATE_ALREADY_TRUE);
            string provider = "CDP_BACKGROUND";
            if (PointerAction(request.Action))
            {
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
                string button=request.Action==WindowActionKind.RIGHT_CLICK?"right":"left";
                int repeats=request.Action==WindowActionKind.DOUBLE_CLICK?2:1;
                for(int click=1;click<=repeats;click++)
                {
                    await cdp.Call("Input.dispatchMouseEvent", new { type = "mousePressed", x, y,
                        button, clickCount = click }, sessionId);
                    await cdp.Call("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y,
                        button, clickCount = click }, sessionId);
                }
            }
            else if (request.Action is WindowActionKind.KEY_PRESS or WindowActionKind.TYPE_TEXT or
                     WindowActionKind.SET_VALUE)
            {
                if ((request.Action is WindowActionKind.TYPE_TEXT or WindowActionKind.SET_VALUE) &&
                    !await EditableDomNode(cdp, sessionId, target.BackendId))
                    return Rejected(WindowActionCode.UNSUPPORTED_ACTION);
                await cdp.Call("DOM.focus", new { backendNodeId = target.BackendId }, sessionId);
                if (request.Action is WindowActionKind.TYPE_TEXT or WindowActionKind.SET_VALUE)
                {
                    dispatched = true;
                    await ReplaceFocusedText(cdp, sessionId, request.Value!);
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
            bool? enabledObserved = null;
            if (request.ExpectedName is not null || request.ExpectedEnabledName is not null ||
                desiredState is not null ||
                (request.Action is WindowActionKind.TYPE_TEXT or WindowActionKind.SET_VALUE))
            {
                var observation = Stopwatch.StartNew();
                do
                {
                    bool needsAx = desiredState is not null || request.ExpectedEnabledName is not null ||
                        (request.ExpectedName is not null && request.ExpectedKind is
                            (WindowExpectedKind.UIA_ELEMENT or WindowExpectedKind.UIA_VALUE or
                                WindowExpectedKind.UIA_ENABLED));
                    var latestAx = needsAx
                        ? await cdp.Call("Accessibility.getFullAXTree", new { }, sessionId)
                        : default;
                    bool primary = request.ExpectedName is not null
                        ? (request.ExpectedKind is WindowExpectedKind.UIA_ELEMENT or
                            WindowExpectedKind.UIA_VALUE or WindowExpectedKind.UIA_ENABLED
                            ? ExpectedAxState(latestAx, request)
                            : WindowAction.ExternalStatePresent(request.ExpectedKind, request.ExpectedName)) ==
                            (request.ExpectedChange == WindowExpectedChange.APPEARS)
                        : (request.Action is WindowActionKind.TYPE_TEXT or WindowActionKind.SET_VALUE)
                            ? await ReadTextValue(cdp, sessionId, target.BackendId, request.Value!)
                            : true;
                    enabledObserved = request.ExpectedEnabledName is null ? null :
                        HasAxEnabled(latestAx, request.ExpectedEnabledName, request.ExpectedEnabled);
                    bool semanticObserved = desiredState is null ||
                        latestAx.GetProperty("nodes").EnumerateArray().Any(node =>
                            node.TryGetProperty("backendDOMNodeId", out var backend) &&
                            backend.ValueKind == JsonValueKind.Number &&
                            backend.GetInt32() == target.BackendId &&
                            AxActionState(node, request.Action) == desiredState);
                    effect = primary && semanticObserved && enabledObserved != false;
                    if (effect == true) break;
                    await Task.Delay(100, timeout.Token);
                } while (observation.ElapsedMilliseconds < request.ObservationTimeoutMs);
            }
            return new(WindowActionCode.COMPLETED, TargetVerified: true,
                TargetIdentity: request.ElementId, ProviderResponse: provider,
                ExpectedStateObserved: effect,
                ObservationState: effect is null ? WindowObservationState.NOT_REQUESTED :
                    effect == true ? WindowObservationState.OBSERVED : WindowObservationState.NOT_OBSERVED,
                ExpectedEnabledObserved: enabledObserved, ElapsedMs: clock.ElapsedMilliseconds,
                ExecutionMethod: WindowExecutionMethod.CHROME_CDP);
        }
        catch (CdpWindowMappingException) when (!dispatched)
        {
            // Preserve the authorized socket when only the window lookup failed.
            return Rejected(WindowActionCode.CDP_TARGET_AMBIGUOUS);
        }
        catch (Exception e) when (e is WebSocketException or IOException or JsonException or
            OperationCanceledException or InvalidOperationException or KeyNotFoundException)
        {
            ObserveCdpFailure(e);
            return new(dispatched ? WindowActionCode.CDP_FAILED_UNKNOWN_EFFECT : WindowActionCode.CDP_UNAVAILABLE,
                EffectUnknown: dispatched, FailureStage: dispatched ? WindowFailureStage.DISPATCH_INPUT :
                    WindowFailureStage.CDP_TARGET, ExceptionType: e.GetType().Name,
                Detail: e is InvalidOperationException { Message: "CDP_COMMAND_REJECTED" } or
                    InvalidOperationException { Message: "CDP_TARGET_DETACHED" } ? e.Message : null,
                ElapsedMs: clock.ElapsedMilliseconds,
                ExecutionMethod: dispatched ? WindowExecutionMethod.CHROME_CDP : null);
        }
        WindowActionResult Rejected(WindowActionCode status) => new(status,
            TargetVerified: status == WindowActionCode.EXPECTED_STATE_ALREADY_TRUE,
            FailureStage: WindowFailureStage.CDP_TARGET, ElapsedMs: clock.ElapsedMilliseconds);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hwnd, int command);
}
