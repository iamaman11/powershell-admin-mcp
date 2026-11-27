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

// Exclusive Chrome socket, consent and target identity.
internal static partial class WindowChromeCdp
{
    private sealed record TabTarget(string Id, string Title, string Url, int BrowserWindowId);
    private sealed record TabElementRef(long Stamp, string TargetId, int BackendId, string LoaderId,
        string Digest);

    // A single-threaded USER broker owns these across requests. Each request
    // replaces only its command deadline, not the socket reader or attachment.
    private static ClientWebSocket? _tabSocket;
    private static Session? _tabSession;
    private static Uri? _tabEndpoint;
    // Retain only the exact endpoint of a live, previously approved session.
    // A managed Chrome may reconnect to it without a new browser consent.
    // USER_BROWSER is intentionally excluded: Chrome owns every new prompt.
    private static Uri? _lastConnectedEndpoint;
    private static ChromeConnectionMode _lastConnectedMode = ChromeConnectionMode.USER_BROWSER;
    private static string? _lastConnectedManagedProfileId;
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
    private static ChromeConnectionMode _sourceMode = ChromeConnectionMode.USER_BROWSER;
    private static string? _managedProfileId;

    internal static ChromeConnectionState MissingConnectionState(bool hadConnection) =>
        hadConnection ? ChromeConnectionState.CONNECTION_LOST : ChromeConnectionState.NOT_REQUESTED;

    internal static ChromeConnectionNextAction ConnectionNextAction(ChromeConnectionState state,
        ChromeConnectionFailureReason? reason) => state switch
        {
            ChromeConnectionState.CONNECTED or ChromeConnectionState.DISCONNECTED =>
                ChromeConnectionNextAction.NONE,
            ChromeConnectionState.CONSENT_PENDING => ChromeConnectionNextAction.ALLOW_CHROME_PROMPT,
            ChromeConnectionState.CONSENT_WINDOW_UNAVAILABLE =>
                ChromeConnectionNextAction.SHOW_CHROME_FOR_CONSENT,
            _ when reason is ChromeConnectionFailureReason.FOREGROUND_DENIED or
                ChromeConnectionFailureReason.WINDOW_MINIMIZED or
                ChromeConnectionFailureReason.WINDOW_HIDDEN =>
                ChromeConnectionNextAction.SHOW_CHROME_FOR_CONSENT,
            _ => ChromeConnectionNextAction.RETRY_CONNECTION
        };

    internal static bool MayReconnectManagedBrowser(ChromeConnectionMode mode,
        bool hadConnection, bool sameManagedProfile,
        Uri? lastEndpoint, Uri? currentEndpoint) =>
        mode == ChromeConnectionMode.MANAGED_BROWSER && hadConnection &&
        sameManagedProfile && lastEndpoint is not null && lastEndpoint == currentEndpoint;

    internal static ChromeConnectionNextAction RecoveryNextAction(ChromeConnectionState state,
        ChromeConnectionFailureReason? reason, ChromeConnectionMode mode, bool consentWindowReady) =>
        mode == ChromeConnectionMode.USER_BROWSER && !consentWindowReady &&
        state is ChromeConnectionState.NOT_REQUESTED or ChromeConnectionState.CONNECTION_LOST
            ? ChromeConnectionNextAction.SHOW_CHROME_FOR_CONSENT
            : ConnectionNextAction(state, reason);

    private static bool ConsentWindowReady(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && IsWindowVisible(hwnd) && !IsIconic(hwnd) &&
        GetForegroundWindow() == hwnd;

    internal static bool CdpTransportFailure(Exception e) => e is
        WebSocketException or IOException or JsonException ||
        e is InvalidOperationException { Message: "CDP_SOCKET_CLOSED" };

    private static void ObserveCdpFailure(Exception e)
    {
        if (e is InvalidOperationException { Message: "CDP_TARGET_DETACHED" })
            AttachedTabs.Clear();
        if (!CdpTransportFailure(e)) return;
        _lastFailure = e switch
        {
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
        ClearSessionWindowBindings();
        AttachedTabs.Clear();
        _tabSession?.Dispose();
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
    private static bool PrepareConsentWindow(IntPtr hwnd, bool allowActivation,
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
            if (!allowActivation)
            {
                failure = ChromeConnectionFailureReason.WINDOW_MINIMIZED;
                return false;
            }
            _ = ShowWindowAsync(hwnd, 9); // SW_RESTORE on Chrome's own UI thread
            if (!SpinWait.SpinUntil(() => !IsIconic(hwnd), 1500))
            {
                failure = ChromeConnectionFailureReason.WINDOW_MINIMIZED;
                return false;
            }
        }
        if (GetForegroundWindow() != hwnd)
        {
            if (!allowActivation) return false;
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
        IntPtr consentWindow, bool retry = false, bool activateWindowForConsent = false)
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
                _tabSession = new Session(socket);
                _sessionGeneration = Guid.NewGuid().ToString("N");
                _hadConnection = true;
                _lastConnectedEndpoint = endpoint;
                _lastConnectedMode = _sourceMode;
                _lastConnectedManagedProfileId = _managedProfileId;
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
        ChromeConnectionFailureReason activationFailure = ChromeConnectionFailureReason.FOREGROUND_DENIED;
        if (_sourceMode == ChromeConnectionMode.USER_BROWSER &&
            !PrepareConsentWindow(consentWindow, activateWindowForConsent, out activationFailure))
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
        var profile = ChromeProfileCatalog.ReadWindowProfileStable(request.WindowId,
            self.SessionId, out var profileRead);
        if (profile is null || !ChromeProfileCatalog.MatchesSelector(profile, request.ExpectedProfile))
            return new(ChromeConnectionCode.PROFILE_MISMATCH,
                ChromeConnectionState.NOT_REQUESTED, ChromeConnectionNextAction.SELECT_PROFILE,
                Detail: $"PROFILE_{(profile is null ? profileRead :
                    ChromeProfileCatalog.ChromeProfileReadCode.PROFILE_MISMATCH)}");

        if (!Enum.IsDefined(request.Mode) || request.Mode == ChromeConnectionMode.MANAGED_BROWSER &&
            !ChromeManagedProfile.ValidId(request.ManagedProfileId) ||
            request.Mode == ChromeConnectionMode.USER_BROWSER && request.ManagedProfileId is not null)
            return new(ChromeConnectionCode.INVALID_ARGUMENT, ChromeConnectionState.NOT_REQUESTED);
        if (request.Action == ChromeConnectionAction.REQUEST)
        {
            if (TabConnected && (_sourceMode != request.Mode ||
                _managedProfileId != request.ManagedProfileId))
                return new(ChromeConnectionCode.PROFILE_MISMATCH, ChromeConnectionState.CONNECTED,
                    ChromeConnectionNextAction.SELECT_PROFILE,
                    Detail: "DISCONNECT_BEFORE_SWITCHING_BROWSER_MODE");
            _sourceMode = request.Mode;
            _managedProfileId = request.ManagedProfileId;
        }
        else if (_sourceMode != request.Mode || _managedProfileId != request.ManagedProfileId)
            return new(ChromeConnectionCode.OK, ChromeConnectionState.NOT_REQUESTED,
                ChromeConnectionNextAction.RETRY_CONNECTION, profile,
                Detail: "BROWSER_MODE_NOT_SELECTED", Mode: request.Mode,
                ManagedProfileId: request.ManagedProfileId);

        if (request.Action == ChromeConnectionAction.STATUS)
        {
            ProbeTabConnection();
            if (_pendingConnect is null && !TabConnected &&
                !MayReconnectManagedBrowser(request.Mode, _hadConnection,
                    _lastConnectedMode == request.Mode &&
                    _lastConnectedManagedProfileId == request.ManagedProfileId,
                    _lastConnectedEndpoint, _lastConnectedEndpoint))
            {
                if (_hadConnection && _lastLossReason is null)
                    _lastLossReason = ChromeConnectionFailureReason.SOCKET_CLOSED;
                var missing = MissingConnectionState(_hadConnection);
                bool consentWindowReady = request.Mode != ChromeConnectionMode.USER_BROWSER ||
                    WindowCatalog.ResolveId(request.WindowId, self.SessionId, out var window) ==
                    WindowResolveCode.OK && ConsentWindowReady(window);
                return new(ChromeConnectionCode.OK, missing,
                    RecoveryNextAction(missing, _lastFailure, request.Mode, consentWindowReady), profile,
                    FailureReason: _lastFailure ?? (_hadConnection
                        ? ChromeConnectionFailureReason.SOCKET_CLOSED : null),
                    LastLossReason: _lastLossReason);
            }
        }
        if (!TryEndpoint(out var endpoint, out string failure))
            return new(ChromeConnectionCode.OK, ChromeConnectionState.DEBUGGING_UNAVAILABLE,
                request.Mode == ChromeConnectionMode.MANAGED_BROWSER
                    ? ChromeConnectionNextAction.START_MANAGED_BROWSER
                    : ChromeConnectionNextAction.ENABLE_CHROME_DEBUGGING,
                profile, Detail: failure, Mode: request.Mode,
                ManagedProfileId: request.ManagedProfileId);
        if (WindowCatalog.ResolveId(request.WindowId, self.SessionId, out var hwnd) != WindowResolveCode.OK)
            return new(ChromeConnectionCode.STALE_WINDOW, ChromeConnectionState.NOT_REQUESTED);
        bool autoManagedReconnect = request.Action == ChromeConnectionAction.STATUS &&
            _pendingConnect is null && !TabConnected &&
            MayReconnectManagedBrowser(request.Mode, _hadConnection,
                _lastConnectedMode == request.Mode &&
                _lastConnectedManagedProfileId == request.ManagedProfileId,
                _lastConnectedEndpoint, endpoint);
        var state = ConnectionState(endpoint!, request.Action == ChromeConnectionAction.REQUEST ||
            autoManagedReconnect,
            hwnd, retry: request.Action == ChromeConnectionAction.REQUEST,
            activateWindowForConsent: request.ActivateWindowForConsent);
        var reportedState = request.Mode == ChromeConnectionMode.MANAGED_BROWSER &&
            state == ChromeConnectionState.CONSENT_PENDING ? ChromeConnectionState.CONNECTING : state;
        var next = request.Mode == ChromeConnectionMode.MANAGED_BROWSER &&
            reportedState == ChromeConnectionState.CONNECTING
                ? ChromeConnectionNextAction.WAIT_FOR_CONNECTION
                : ConnectionNextAction(reportedState, _lastFailure);
        return new(ChromeConnectionCode.OK, reportedState, next, profile,
            FailureReason: reportedState == ChromeConnectionState.CONNECTED ? null : _lastFailure,
            LastLossReason: _lastLossReason,
            SessionGeneration: reportedState == ChromeConnectionState.CONNECTED ? _sessionGeneration : null,
            Mode: request.Mode, ManagedProfileId: request.ManagedProfileId);
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

    // Target.getTargetInfo is a second, just-before-dispatch readback. A page
    // may navigate between the AX snapshot and a DOM hit-test/focus request.
    // The original URL is part of the element fingerprint, even when the
    // caller intentionally selected by origin rather than by exact URL.
    internal static bool MatchesDispatchTarget(JsonElement response, string targetId,
        string snapshotUrl, string? expectedOrigin, string? expectedUrl)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("targetInfo", out var info) ||
            info.ValueKind != JsonValueKind.Object ||
            !info.TryGetProperty("targetId", out var id) || id.ValueKind != JsonValueKind.String ||
            !info.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
            !info.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String)
            return false;
        string? actualUrl = url.GetString();
        return id.GetString() == targetId && type.GetString() == "page" &&
            string.Equals(actualUrl, snapshotUrl, StringComparison.Ordinal) &&
            MatchesContext(actualUrl!, expectedOrigin, expectedUrl);
    }

    // Resolve the native Chrome window using its active title *before* this
    // predicate. Afterwards only address the requested tab or URL; mapping
    // every unrelated HTTP tab with Browser.getWindowForTarget was O(all tabs)
    // for even a single exact-origin action and could exhaust the CDP budget.
    // For an opaque exact tab ID, preserve the later URL-mismatch diagnostic.
    internal static bool IsRequestedTabCandidate(string id, string url, string? tabId,
        string? expectedOrigin, string? expectedUrl) =>
        tabId is not null ? string.Equals(id, tabId, StringComparison.Ordinal) &&
            TryHttpUri(url, out _) : MatchesContext(url, expectedOrigin, expectedUrl);

    // A live browser socket does not mean a newly navigated page already has a
    // responsive AX tree. Allow cold address-only targets to settle without
    // treating their first slow inspection as lost Chrome consent.
    internal static int InspectTimeoutMs(bool connected, bool exactTabId) =>
        !connected ? 24000 : exactTabId ? 12000 : 18000;

    internal static string CdpTimeoutDetail(OperationCanceledException failure) =>
        failure.Data["cdp_method"] is string method && method is
            ("Target.getTargets" or "Browser.getWindowForTarget" or
             "Browser.getWindowBounds" or "Target.attachToTarget" or
             "Page.getFrameTree" or "Page.getResourceTree" or
             "Accessibility.getFullAXTree" or
             "Browser.getVersion" or "DOM.getBoxModel")
            ? "CDP_TIMEOUT_" + method : "CDP_TIMEOUT";

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

    internal static bool VerifiedChromeProfile(IntPtr hwnd, string expected) =>
        VerifiedChromeProfile(hwnd, expected, out _);

    internal static bool VerifiedChromeProfile(IntPtr hwnd, string expected,
        out ChromeProfileCatalog.ChromeProfileReadCode reason)
    {
        reason = ChromeProfileCatalog.ChromeProfileReadCode.STALE_WINDOW;
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
            var profile = ChromeProfileCatalog.ReadWindowProfileStable(id, self.SessionId,
                out reason);
            if (profile is null) return false;
            bool matches = ChromeProfileCatalog.MatchesSelector(profile, expected);
            if (!matches) reason = ChromeProfileCatalog.ChromeProfileReadCode.PROFILE_MISMATCH;
            return matches;
        }
        catch (Exception e) when (e is COMException or ElementNotAvailableException or
            InvalidOperationException or ArgumentException or Win32Exception or OverflowException)
        {
            reason = ChromeProfileCatalog.ChromeProfileReadCode.PROVIDER_ERROR;
            return false;
        }
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
            string profileRoot = _sourceMode == ChromeConnectionMode.USER_BROWSER
                ? Path.Combine(data, "Google", "Chrome", "User Data")
                : ChromeManagedProfile.DataDirectory(_managedProfileId) ?? "";
            if (!TryReadEndpoint(File.ReadAllText(Path.Combine(profileRoot,
                "DevToolsActivePort")), out endpoint))
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
}
