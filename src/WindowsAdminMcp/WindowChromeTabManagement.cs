using System.ComponentModel;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<ChromeTabAction>))]
public enum ChromeTabAction { LIST, OPEN, NAVIGATE, BACK, FORWARD, RELOAD, CLOSE }

[JsonConverter(typeof(JsonStringEnumConverter<ChromeTabCode>))]
public enum ChromeTabCode
{
    COMPLETED, INVALID_ARGUMENT, CONFIRMATION_REQUIRED, SERVICE_UNAVAILABLE,
    USER_CONTEXT_UNAVAILABLE, DESKTOP_UNAVAILABLE, STALE_WINDOW, PROFILE_MISMATCH,
    CDP_UNAVAILABLE, CONSENT_PENDING, TARGET_NOT_FOUND, TARGET_AMBIGUOUS,
    HISTORY_UNAVAILABLE, ALREADY_AT_URL, AUDIT_UNAVAILABLE, EFFECT_UNKNOWN
}

[JsonConverter(typeof(JsonStringEnumConverter<ChromeTabNextStep>))]
public enum ChromeTabNextStep
{
    NONE, FIX_ARGUMENTS, CONNECT_BROWSER, REINSPECT_TAB, INSPECT_STATE_DO_NOT_RETRY
}

internal sealed record ChromeTabRequest(
    [property: JsonPropertyName("window_id")] string WindowId,
    [property: JsonPropertyName("expected_profile")] string ExpectedProfile,
    [property: JsonPropertyName("action")] ChromeTabAction Action,
    [property: JsonPropertyName("tab_id")] string? TabId = null,
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("background")] bool Background = true);

public sealed record ChromeTabResult(
    [property: JsonPropertyName("status")] ChromeTabCode Status,
    [property: JsonPropertyName("action")] ChromeTabAction Action,
    [property: JsonPropertyName("tab_id")] string? TabId = null,
    [property: JsonPropertyName("tab")] WindowTab? Tab = null,
    [property: JsonPropertyName("tabs")] WindowTab[]? Tabs = null,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("audit_id")] string? AuditId = null,
    [property: JsonPropertyName("broker_owner_pid")] int? BrokerOwnerPid = null,
    [property: JsonPropertyName("session_generation")] string? SessionGeneration = null,
    [property: JsonPropertyName("trace_id")] string? TraceId = null)
{
    [JsonPropertyName("effect_state")]
    public WindowEffectState EffectState => Status == ChromeTabCode.COMPLETED && Action != ChromeTabAction.LIST
        ? WindowEffectState.EFFECT_VERIFIED : Status == ChromeTabCode.EFFECT_UNKNOWN
            ? WindowEffectState.EFFECT_UNKNOWN : WindowEffectState.NO_EFFECT;

    [JsonPropertyName("safe_to_retry")]
    public bool SafeToRetry => EffectState == WindowEffectState.NO_EFFECT && Status is
        ChromeTabCode.INVALID_ARGUMENT or ChromeTabCode.CONFIRMATION_REQUIRED or
        ChromeTabCode.STALE_WINDOW or ChromeTabCode.PROFILE_MISMATCH or
        ChromeTabCode.TARGET_NOT_FOUND or ChromeTabCode.TARGET_AMBIGUOUS or
        ChromeTabCode.HISTORY_UNAVAILABLE or ChromeTabCode.ALREADY_AT_URL or
        ChromeTabCode.AUDIT_UNAVAILABLE;

    [JsonPropertyName("next_step")]
    public ChromeTabNextStep NextStep => Status switch
    {
        ChromeTabCode.EFFECT_UNKNOWN => ChromeTabNextStep.INSPECT_STATE_DO_NOT_RETRY,
        ChromeTabCode.CDP_UNAVAILABLE or ChromeTabCode.CONSENT_PENDING =>
            ChromeTabNextStep.CONNECT_BROWSER,
        ChromeTabCode.TARGET_NOT_FOUND or ChromeTabCode.TARGET_AMBIGUOUS or
            ChromeTabCode.STALE_WINDOW or ChromeTabCode.PROFILE_MISMATCH =>
            ChromeTabNextStep.REINSPECT_TAB,
        ChromeTabCode.INVALID_ARGUMENT => ChromeTabNextStep.FIX_ARGUMENTS,
        _ => ChromeTabNextStep.NONE
    };
}

// Typed navigation is routed through the same per-session Chrome broker as
// inspect/actions. No new CDP socket and no Windows desktop input.
internal static partial class WindowChromeCdp
{
    internal static bool ValidTabRequest(ChromeTabRequest request) =>
        WindowCatalog.TryParseId(request.WindowId, out _) &&
        request.ExpectedProfile is {Length: >= 1 and <= 100} &&
        !string.IsNullOrWhiteSpace(request.ExpectedProfile) &&
        !request.ExpectedProfile.Contains('\0') && Enum.IsDefined(request.Action) &&
        (request.Action is ChromeTabAction.LIST or ChromeTabAction.OPEN
            ? request.TabId is null : ValidTargetId(request.TabId)) &&
        (request.Action is ChromeTabAction.OPEN or ChromeTabAction.NAVIGATE
            ? ValidNavigationUrl(request.Url) : request.Url is null) &&
        (request.Action == ChromeTabAction.OPEN || request.Background);

    internal static bool ValidNavigationUrl(string? url) =>
        url is {Length: >= 1 and <= 2048} &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0;

    private static WindowTab TabInfo(TabTarget tab)
    {
        var uri = new Uri(tab.Url);
        return new(tab.Id, tab.Title, uri.GetLeftPart(UriPartial.Path),
            uri.GetLeftPart(UriPartial.Authority));
    }

    internal static ChromeTabResult ManageTabInWorker(ChromeTabRequest request)
    {
        if (!ValidTabRequest(request)) return new(ChromeTabCode.INVALID_ARGUMENT, request.Action);
        using var self = Process.GetCurrentProcess();
        if (WindowCatalog.ResolveId(request.WindowId, self.SessionId, out var hwnd) != WindowResolveCode.OK)
            return new(ChromeTabCode.STALE_WINDOW, request.Action);
        if (!VerifiedChromeProfile(hwnd, request.ExpectedProfile, out var reason))
            return new(ChromeTabCode.PROFILE_MISMATCH, request.Action, Detail: reason.ToString());
        if (!TryEndpoint(out var endpoint, out var endpointFailure))
            return new(ChromeTabCode.CDP_UNAVAILABLE, request.Action, Detail: endpointFailure);
        var connection = ConnectionState(endpoint!, request: false, hwnd);
        if (connection == ChromeConnectionState.CONSENT_PENDING)
            return new(ChromeTabCode.CONSENT_PENDING, request.Action);
        if (connection != ChromeConnectionState.CONNECTED)
            return new(ChromeTabCode.CDP_UNAVAILABLE, request.Action, Detail: connection.ToString());
        try
        {
            return ManageTabAsync(request, hwnd, endpoint!).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is WebSocketException or IOException or JsonException or
                                   OperationCanceledException or InvalidOperationException or KeyNotFoundException)
        {
            ObserveCdpFailure(e);
            // A call may have reached Chrome before the response was lost.
            // A read-only LIST is the only operation safe to classify as no effect.
            return new(request.Action == ChromeTabAction.LIST ? ChromeTabCode.CDP_UNAVAILABLE :
                ChromeTabCode.EFFECT_UNKNOWN, request.Action, Detail: e.GetType().Name);
        }
    }

    private static async Task<ChromeTabResult> ManageTabAsync(ChromeTabRequest request,
        IntPtr hwnd, Uri endpoint)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(16));
        Session cdp;
        List<TabTarget> tabs;
        try
        {
            cdp = await TabSessionAsync(endpoint, deadline.Token);
            tabs = await TargetsForWindow(cdp, hwnd, request.WindowId, deadline.Token,
                allowFallback: true);
        }
        catch (CdpWindowMappingException e)
        {
            return new(e.State == CdpWindowMappingState.NOT_FOUND
                ? ChromeTabCode.TARGET_NOT_FOUND : ChromeTabCode.TARGET_AMBIGUOUS,
                request.Action, Detail: e.Message);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or
                                   IOException or JsonException or InvalidOperationException)
        {
            ObserveCdpFailure(e);
            // Native/CDP preflight failed before any tab command was sent.
            return new(ChromeTabCode.CDP_UNAVAILABLE, request.Action,
                Detail: e is OperationCanceledException ? "PREFLIGHT_TIMEOUT" :
                    "PREFLIGHT_" + e.GetType().Name);
        }
        if (request.Action == ChromeTabAction.LIST)
            return new(ChromeTabCode.COMPLETED, request.Action, Tabs: tabs.Take(100).Select(TabInfo).ToArray());

        var current = tabs.SingleOrDefault(t => t.Id == request.TabId);
        if (request.Action != ChromeTabAction.OPEN && current is null)
            return new(ChromeTabCode.TARGET_NOT_FOUND, request.Action);
        string targetId = current?.Id ?? "";
        if (request.Action == ChromeTabAction.NAVIGATE &&
            string.Equals(current?.Url, new Uri(request.Url!).AbsoluteUri, StringComparison.Ordinal))
            return new(ChromeTabCode.ALREADY_AT_URL, request.Action, targetId);
        bool dispatched = false;
        int? expectedHistoryIndex = null;
        string? historySessionId = null;
        string stage = "PRE_DISPATCH";
        try
        {
            if (request.Action == ChromeTabAction.OPEN)
            {
                // Target.createTarget has no native window selector. Restrict
                // OPEN to one Chrome HWND owned by this process, and verify
                // the resulting target's exact CDP window before success.
                WindowCatalog.TryParseId(request.WindowId, out var identity);
                if (WindowCatalog.List(100, identity.SessionId).Windows.Count(w =>
                    w.ProcessId == identity.ProcessId) != 1 || tabs.Count == 0)
                    return new(ChromeTabCode.TARGET_AMBIGUOUS, request.Action,
                        Detail: "OPEN_REQUIRES_ONE_CHROME_WINDOW");
                dispatched = true;
                stage = "CREATE_TARGET";
                var opened = await cdp.Call("Target.createTarget", new
                    {url = request.Url, background = request.Background});
                targetId = opened.GetProperty("targetId").GetString() ?? "";
                if (!ValidTargetId(targetId)) return new(ChromeTabCode.EFFECT_UNKNOWN, request.Action);
            }
            else if (request.Action == ChromeTabAction.CLOSE)
            {
                dispatched = true;
                stage = "CLOSE_TARGET";
                _ = await cdp.Call("Target.closeTarget", new {targetId});
                AttachedTabs.Remove(targetId);
            }
            else
            {
                string sessionId = await Attach(cdp, targetId);
                historySessionId = sessionId;
                if (request.Action == ChromeTabAction.NAVIGATE)
                {
                    dispatched = true;
                    stage = "NAVIGATE";
                    _ = await cdp.Call("Page.navigate", new {url = request.Url}, sessionId);
                }
                else if (request.Action == ChromeTabAction.RELOAD)
                {
                    string oldLoader = await ReadFrameLoaderAsync(cdp, sessionId, deadline.Token);
                    dispatched = true;
                    stage = "RELOAD";
                    _ = await cdp.Call("Page.reload", new {ignoreCache = false}, sessionId);
                    while (!deadline.IsCancellationRequested)
                    {
                        if (await ReadFrameLoaderAsync(cdp, sessionId, deadline.Token) != oldLoader)
                            return new(ChromeTabCode.COMPLETED, request.Action, targetId);
                        await Task.Delay(150, deadline.Token);
                    }
                    return new(ChromeTabCode.EFFECT_UNKNOWN, request.Action, targetId);
                }
                else
                {
                    var history = await cdp.Call("Page.getNavigationHistory", new {}, sessionId);
                    int index = history.GetProperty("currentIndex").GetInt32() +
                        (request.Action == ChromeTabAction.BACK ? -1 : 1);
                    var entries = history.GetProperty("entries");
                    if (index < 0 || index >= entries.GetArrayLength())
                        return new(ChromeTabCode.HISTORY_UNAVAILABLE, request.Action, targetId);
                    int entryId = entries[index].GetProperty("id").GetInt32();
                    expectedHistoryIndex = index;
                    dispatched = true;
                    stage = "NAVIGATE_HISTORY_ENTRY";
                    _ = await cdp.Call("Page.navigateToHistoryEntry", new {entryId}, sessionId);
                    request = request with {Url = entries[index].GetProperty("url").GetString()};
                }
            }

            // An ACK is not proof of navigation. Check target state independently.
            stage = "VERIFY_POSTCONDITION";
            while (!deadline.IsCancellationRequested)
            {
                if (expectedHistoryIndex is { } desiredIndex)
                {
                    stage = "VERIFY_HISTORY_INDEX";
                    var history = await cdp.Call("Page.getNavigationHistory", new {}, historySessionId);
                    if (history.GetProperty("currentIndex").GetInt32() == desiredIndex)
                    {
                        var uri = new Uri(request.Url!);
                        stage = "VERIFY_HISTORY_TARGET_URL";
                        var targetInfo = await cdp.Call("Target.getTargetInfo", new {targetId});
                        string? actualUrl = targetInfo.GetProperty("targetInfo")
                            .GetProperty("url").GetString();
                        if (Uri.TryCreate(actualUrl, UriKind.Absolute, out var actual) &&
                            string.Equals(actual.AbsoluteUri, uri.AbsoluteUri,
                                StringComparison.Ordinal))
                            return new(ChromeTabCode.COMPLETED, request.Action, targetId,
                                new(targetId, current?.Title ?? "", uri.AbsoluteUri,
                                    uri.GetLeftPart(UriPartial.Authority)));
                    }
                    await Task.Delay(150, deadline.Token);
                    continue;
                }
                stage = "VERIFY_TARGET_URL";
                var listed = await cdp.Call("Target.getTargets", new {});
                var entry = listed.GetProperty("targetInfos").EnumerateArray()
                    .FirstOrDefault(t => t.TryGetProperty("targetId", out var id) &&
                        id.GetString() == targetId);
                if (request.Action == ChromeTabAction.CLOSE && entry.ValueKind == JsonValueKind.Undefined)
                    return new(ChromeTabCode.COMPLETED, request.Action, targetId);
                if (request.Action is ChromeTabAction.OPEN or ChromeTabAction.NAVIGATE &&
                    entry.ValueKind == JsonValueKind.Object &&
                    entry.TryGetProperty("url", out var currentUrl) &&
                    Uri.TryCreate(currentUrl.GetString(), UriKind.Absolute, out var pageUri) &&
                    string.Equals(pageUri!.AbsoluteUri, new Uri(request.Url!).AbsoluteUri,
                        StringComparison.Ordinal))
                {
                    if (request.Action != ChromeTabAction.OPEN)
                    {
                        // Target.url can update before the navigation commits a
                        // history entry. BACK immediately after a URL-only ACK
                        // would then race the still-pending navigation.
                        stage = "VERIFY_COMMITTED_NAVIGATION";
                        var history = await cdp.Call("Page.getNavigationHistory", new {},
                            historySessionId);
                        int index = history.GetProperty("currentIndex").GetInt32();
                        var entries = history.GetProperty("entries");
                        if (index < 0 || index >= entries.GetArrayLength() ||
                            !Uri.TryCreate(entries[index].GetProperty("url").GetString(),
                                UriKind.Absolute, out var committed) ||
                            !string.Equals(committed.AbsoluteUri, new Uri(request.Url!).AbsoluteUri,
                                StringComparison.Ordinal))
                        {
                            await Task.Delay(150, deadline.Token);
                            continue;
                        }
                        return new(ChromeTabCode.COMPLETED, request.Action, targetId,
                            new(targetId, entry.GetProperty("title").GetString() ?? "",
                                request.Url!, new Uri(request.Url!).GetLeftPart(UriPartial.Authority)));
                    }
                    var owner = await cdp.Call("Browser.getWindowForTarget", new {targetId});
                    var firstOwner = await cdp.Call("Browser.getWindowForTarget", new
                        {targetId = tabs[0].Id});
                    if (owner.GetProperty("windowId").GetInt32() ==
                        firstOwner.GetProperty("windowId").GetInt32())
                        return new(ChromeTabCode.COMPLETED, request.Action, targetId,
                            new(targetId, entry.GetProperty("title").GetString() ?? "",
                                request.Url!, new Uri(request.Url!).GetLeftPart(UriPartial.Authority)));
                    return new(ChromeTabCode.EFFECT_UNKNOWN, request.Action, targetId,
                        Detail:"OPENED_IN_DIFFERENT_WINDOW");
                }
                await Task.Delay(150, deadline.Token);
            }
            return new(ChromeTabCode.EFFECT_UNKNOWN, request.Action, targetId,
                Detail: "POSTCONDITION_NOT_OBSERVED");
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or
                                   IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            ObserveCdpFailure(e);
            return new(dispatched ? ChromeTabCode.EFFECT_UNKNOWN : ChromeTabCode.CDP_UNAVAILABLE,
                request.Action, targetId.Length == 0 ? null : targetId,
                Detail: stage + ":" + (e is OperationCanceledException ? "CDP_TIMEOUT" :
                    e is InvalidOperationException && e.Message.StartsWith("CDP_",
                        StringComparison.Ordinal) ? e.Message : e.GetType().Name));
        }
    }
}
