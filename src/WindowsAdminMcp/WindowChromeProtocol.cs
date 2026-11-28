using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// A deliberately small, schema-driven bridge into the ONE consented Chrome CDP
// session owned by the existing USER broker. There is no second DevTools socket.
[JsonConverter(typeof(JsonStringEnumConverter<ChromeProtocolAction>))]
public enum ChromeProtocolAction { DESCRIBE, TARGETS, CALL, SUBSCRIBE, EVENTS, UNSUBSCRIBE }

[JsonConverter(typeof(JsonStringEnumConverter<ChromeProtocolCode>))]
public enum ChromeProtocolCode
{
    COMPLETED, INVALID_ARGUMENT, CONFIRMATION_REQUIRED, SERVICE_UNAVAILABLE,
    USER_CONTEXT_UNAVAILABLE, DESKTOP_UNAVAILABLE, AUDIT_UNAVAILABLE,
    STALE_WINDOW, PROFILE_MISMATCH, CDP_UNAVAILABLE, CONSENT_PENDING,
    TARGET_NOT_FOUND, TARGET_AMBIGUOUS, CONTEXT_MISMATCH,
    METHOD_NOT_SUPPORTED, PARAMETERS_INVALID, FOREGROUND_NOT_ALLOWED,
    BROWSER_SCOPE_NOT_ALLOWED, RESULT_TOO_LARGE, EFFECT_UNKNOWN
}

internal sealed record ChromeProtocolRequest(
    [property: JsonPropertyName("window_id")] string WindowId,
    [property: JsonPropertyName("expected_profile")] string ExpectedProfile,
    [property: JsonPropertyName("action")] ChromeProtocolAction Action,
    [property: JsonPropertyName("method")] string? Method = null,
    [property: JsonPropertyName("parameters_json")] string? ParametersJson = null,
    [property: JsonPropertyName("tab_id")] string? TabId = null,
    [property: JsonPropertyName("browser_window_id")] int? BrowserWindowId = null,
    [property: JsonPropertyName("expected_origin")] string? ExpectedOrigin = null,
    [property: JsonPropertyName("expected_url")] string? ExpectedUrl = null,
    [property: JsonPropertyName("domain")] string? Domain = null,
    [property: JsonPropertyName("offset")] int Offset = 0,
    [property: JsonPropertyName("page_size")] int PageSize = 50,
    [property: JsonPropertyName("subscription_id")] string? SubscriptionId = null,
    [property: JsonPropertyName("after_sequence")] long AfterSequence = 0,
    [property: JsonPropertyName("allow_foreground")] bool AllowForeground = false,
    [property: JsonPropertyName("allow_browser_scope")] bool AllowBrowserScope = false);

public sealed record ChromeProtocolEvent(
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("parameters")] JsonElement? Parameters);

public sealed record ChromeProtocolResult(
    [property: JsonPropertyName("status")] ChromeProtocolCode Status,
    [property: JsonPropertyName("action")] ChromeProtocolAction Action,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("method")] string? Method = null,
    [property: JsonPropertyName("data")] JsonElement? Data = null,
    [property: JsonPropertyName("events")] ChromeProtocolEvent[]? Events = null,
    [property: JsonPropertyName("subscription_id")] string? SubscriptionId = null,
    [property: JsonPropertyName("last_sequence")] long? LastSequence = null,
    [property: JsonPropertyName("dropped")] bool? Dropped = null,
    [property: JsonPropertyName("broker_owner_pid")] int? BrokerOwnerPid = null,
    [property: JsonPropertyName("trace_id")] string? TraceId = null,
    [property: JsonPropertyName("session_generation")] string? SessionGeneration = null,
    [property: JsonPropertyName("destination")] ChromeTabDestination? Destination = null)
{
    // A successful CDP command reply is only an acknowledgement by Chrome. It
    // is not proof of an external application's persisted state change.
    [JsonPropertyName("effect_state")]
    public string EffectState => Action == ChromeProtocolAction.CALL && Status is
        ChromeProtocolCode.COMPLETED or ChromeProtocolCode.RESULT_TOO_LARGE
        ? "PROVIDER_ACK_ONLY" : Status == ChromeProtocolCode.EFFECT_UNKNOWN
            ? "EFFECT_UNKNOWN" : "NO_EFFECT";

    [JsonPropertyName("safe_to_retry")]
    public bool SafeToRetry => Status is ChromeProtocolCode.INVALID_ARGUMENT or
        ChromeProtocolCode.CONFIRMATION_REQUIRED or ChromeProtocolCode.STALE_WINDOW or
        ChromeProtocolCode.PROFILE_MISMATCH or ChromeProtocolCode.TARGET_NOT_FOUND or
        ChromeProtocolCode.TARGET_AMBIGUOUS or ChromeProtocolCode.CONTEXT_MISMATCH or
        ChromeProtocolCode.METHOD_NOT_SUPPORTED or ChromeProtocolCode.PARAMETERS_INVALID or
        ChromeProtocolCode.FOREGROUND_NOT_ALLOWED or ChromeProtocolCode.BROWSER_SCOPE_NOT_ALLOWED;
}

internal static partial class WindowChromeCdp
{
    private const int ProtocolMaxParametersBytes = 16 * 1024;
    private const int ProtocolMaxResultBytes = 64 * 1024;
    private const int ProtocolMaxSchemaBytes = 8 * 1024 * 1024;
    private static string? _protocolSchemaGeneration;
    private static Uri? _protocolSchemaEndpoint;
    private static JsonElement _protocolSchema;
    private static string? _protocolMetadataGeneration;
    private static Uri? _protocolMetadataEndpoint;
    private static ProtocolMetadata? _protocolMetadata;
    private static JsonElement _referenceProtocolSchema;
    internal sealed record ProtocolMetadata(JsonElement? FullSchema, JsonElement? LiveDomains,
        JsonElement? ReferenceSchema = null)
    {
        internal bool HasFullSchema => FullSchema is not null;
    }
    private sealed record ProtocolSubscriptionBinding(string WindowId, string ExpectedProfile,
        string? TabId, int? BrowserWindowId, string? ExpectedOrigin, string? ExpectedUrl,
        string? SessionGeneration, bool BrowserScope);
    private static readonly Dictionary<string, ProtocolSubscriptionBinding> ProtocolSubscriptions =
        new(StringComparer.Ordinal);
    private static string? _protocolSubscriptionsGeneration;

    internal static bool ValidProtocolRequest(ChromeProtocolRequest request) =>
        WindowCatalog.TryParseId(request.WindowId, out _) &&
        request.ExpectedProfile is { Length: >= 1 and <= 100 } &&
        !string.IsNullOrWhiteSpace(request.ExpectedProfile) &&
        !request.ExpectedProfile.Contains('\0') && Enum.IsDefined(request.Action) &&
        request.Method is null or { Length: >= 3 and <= 128 } &&
        (request.Method is null || ValidProtocolMethod(request.Method)) &&
        request.Domain is null or { Length: >= 1 and <= 100 } &&
        (request.Domain is null || request.Domain.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) &&
        (request.ParametersJson is null || Encoding.UTF8.GetByteCount(request.ParametersJson) <= ProtocolMaxParametersBytes) &&
        (request.TabId is null || ValidTargetId(request.TabId)) &&
        request.BrowserWindowId is null or > 0 &&
        ContextArgumentError(request.ExpectedProfile, request.ExpectedOrigin, request.ExpectedUrl) is null &&
        request.Offset is >= 0 and <= 10000 && request.PageSize is >= 1 and <= 100 &&
        (request.Action is not (ChromeProtocolAction.SUBSCRIBE or ChromeProtocolAction.EVENTS) ||
            request.PageSize <= 64) &&
        request.AfterSequence >= 0 &&
        (request.SubscriptionId is null || request.SubscriptionId is { Length: >= 1 and <= 128 } &&
            request.SubscriptionId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) &&
        (request.Action switch
        {
            ChromeProtocolAction.DESCRIBE => request.ParametersJson is null &&
                request.TabId is null && request.SubscriptionId is null,
            ChromeProtocolAction.TARGETS => request.Method is null && request.ParametersJson is null &&
                request.SubscriptionId is null && request.Domain is null,
            ChromeProtocolAction.CALL => request.Method is not null && request.SubscriptionId is null &&
                request.Domain is null,
            ChromeProtocolAction.SUBSCRIBE => (request.Method is not null ^ request.Domain is not null) &&
                request.SubscriptionId is null && request.ParametersJson is null,
            ChromeProtocolAction.EVENTS or ChromeProtocolAction.UNSUBSCRIBE =>
                request.SubscriptionId is not null && request.Method is null &&
                request.ParametersJson is null && request.TabId is null && request.Domain is null,
            _ => false
        });

    private static bool ValidProtocolMethod(string method)
    {
        int dot = method.IndexOf('.');
        return dot is > 0 and < 100 && dot < method.Length - 1 &&
            method.LastIndexOf('.') == dot &&
            method.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_');
    }

    internal static ChromeProtocolResult ProtocolInWorker(ChromeProtocolRequest request)
    {
        if (!ValidProtocolRequest(request))
            return new(ChromeProtocolCode.INVALID_ARGUMENT, request.Action);
        using var self = Process.GetCurrentProcess();
        if (WindowCatalog.ResolveId(request.WindowId, self.SessionId, out var hwnd) != WindowResolveCode.OK)
            return new(ChromeProtocolCode.STALE_WINDOW, request.Action);
        if (!VerifiedChromeProfile(hwnd, request.ExpectedProfile, out var reason))
            return new(ChromeProtocolCode.PROFILE_MISMATCH, request.Action, Detail: reason.ToString());
        if (!TryEndpoint(out var endpoint, out string failure))
            return new(ChromeProtocolCode.CDP_UNAVAILABLE, request.Action, Detail: failure);
        var connection = ConnectionState(endpoint!, request: false, hwnd);
        if (connection == ChromeConnectionState.CONSENT_PENDING)
            return new(ChromeProtocolCode.CONSENT_PENDING, request.Action);
        if (connection != ChromeConnectionState.CONNECTED)
            return new(ChromeProtocolCode.CDP_UNAVAILABLE, request.Action, Detail: connection.ToString());
        try
        {
            return ProtocolAsync(request, hwnd, endpoint!).GetAwaiter().GetResult() with
            { SessionGeneration = CurrentGeneration };
        }
        catch (CdpWindowMappingException error)
        {
            return new(error.State == CdpWindowMappingState.AMBIGUOUS
                ? ChromeProtocolCode.TARGET_AMBIGUOUS : ChromeProtocolCode.TARGET_NOT_FOUND,
                request.Action, Detail: error.Failure.ToString());
        }
        catch (Exception error) when (error is WebSocketException or IOException or JsonException or
            HttpRequestException or OperationCanceledException or InvalidOperationException or KeyNotFoundException)
        {
            ObserveCdpFailure(error);
            // Even a canceled waiter can have sent the command. Never replay it.
            return new(request.Action == ChromeProtocolAction.CALL
                ? ChromeProtocolCode.EFFECT_UNKNOWN : ChromeProtocolCode.CDP_UNAVAILABLE,
                request.Action, Detail: error.GetType().Name, SessionGeneration: CurrentGeneration);
        }
    }

    private static async Task<ChromeProtocolResult> ProtocolAsync(ChromeProtocolRequest request,
        IntPtr hwnd, Uri endpoint)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var cdp = await TabSessionAsync(endpoint, timeout.Token);
        if (_protocolSubscriptionsGeneration != CurrentGeneration)
        {
            ProtocolSubscriptions.Clear();
            _protocolSubscriptionsGeneration = CurrentGeneration;
        }
        if (request.Action is ChromeProtocolAction.EVENTS or ChromeProtocolAction.UNSUBSCRIBE)
        {
            if (!ProtocolSubscriptions.TryGetValue(request.SubscriptionId!, out var binding))
                return new(ChromeProtocolCode.TARGET_NOT_FOUND, request.Action,
                    Detail: "SUBSCRIPTION_NOT_IN_CURRENT_BROWSER_SESSION");
            if (binding.WindowId != request.WindowId || binding.ExpectedProfile != request.ExpectedProfile ||
                binding.SessionGeneration != CurrentGeneration ||
                request.BrowserWindowId is { } explicitWindow &&
                binding.BrowserWindowId != explicitWindow)
                return new(ChromeProtocolCode.CONTEXT_MISMATCH, request.Action,
                    Detail: "SUBSCRIPTION_OWNER_MISMATCH");
            if (request.Action == ChromeProtocolAction.EVENTS && binding.BrowserScope &&
                !request.AllowBrowserScope)
                return new(ChromeProtocolCode.BROWSER_SCOPE_NOT_ALLOWED, request.Action,
                    Detail: "EXPLICIT_ALLOW_BROWSER_SCOPE_REQUIRED");
            if (binding.TabId is not null && request.Action == ChromeProtocolAction.EVENTS)
            {
                // Re-check authority on every event read, not just subscribe.
                // This prevents leaking queued events after a tab has moved to
                // another native/DevTools window or profile.
                var actual = binding.BrowserScope
                    ? await BrowserTargets(cdp, binding.BrowserWindowId, binding.TabId)
                    : await TargetsForWindow(cdp, hwnd, request.WindowId, timeout.Token,
                        binding.ExpectedOrigin, binding.ExpectedUrl, binding.TabId);
                if (actual.Count != 1 ||
                    actual[0].BrowserWindowId != binding.BrowserWindowId ||
                    !MatchesContextIfSpecified(actual[0].Url, binding.ExpectedOrigin, binding.ExpectedUrl))
                {
                    cdp.Unsubscribe(request.SubscriptionId!);
                    ProtocolSubscriptions.Remove(request.SubscriptionId!);
                    return new(ChromeProtocolCode.CONTEXT_MISMATCH, request.Action,
                        Detail: "SUBSCRIBED_TARGET_CHANGED");
                }
            }
            if (request.Action == ChromeProtocolAction.UNSUBSCRIBE)
            {
                ProtocolSubscriptions.Remove(request.SubscriptionId!);
                return new(cdp.Unsubscribe(request.SubscriptionId!) ? ChromeProtocolCode.COMPLETED :
                    ChromeProtocolCode.TARGET_NOT_FOUND, request.Action);
            }
            var events = cdp.ReadEvents(request.SubscriptionId!, request.AfterSequence, request.PageSize);
            return Bounded(new(ChromeProtocolCode.COMPLETED, request.Action,
                Events: events.Events, LastSequence: events.LastSequence, Dropped: events.Dropped,
                Destination: binding.BrowserScope ? ChromeTabDestination.CHROME_SELECTED_UNVERIFIED :
                    ChromeTabDestination.VERIFIED_WINDOW));
        }

        // Authorization does not depend on optional HTTP metadata. Reject
        // browser-wide/focus-changing operations before any schema discovery.
        if (request.Action is ChromeProtocolAction.CALL or ChromeProtocolAction.SUBSCRIBE &&
            request.TabId is null && !request.AllowBrowserScope)
            return new(ChromeProtocolCode.BROWSER_SCOPE_NOT_ALLOWED, request.Action,
                Detail: "EXPLICIT_ALLOW_BROWSER_SCOPE_REQUIRED");
        if (request.Action == ChromeProtocolAction.CALL && !request.AllowForeground &&
            (MayActivateForeground(request.Method!) || ProtocolCanRunArbitraryCode(request.Method!)))
            return new(ChromeProtocolCode.FOREGROUND_NOT_ALLOWED, request.Action,
                Detail: "METHOD_MAY_ACTIVATE_WINDOW_OR_ARBITRARY_CODE");

        ProtocolMetadata? metadata = null;
        if (request.Action is ChromeProtocolAction.DESCRIBE or ChromeProtocolAction.CALL or
            ChromeProtocolAction.SUBSCRIBE)
            metadata = await ResolveProtocolMetadataAsync(endpoint, cdp, timeout.Token);
        JsonElement? schema = metadata?.FullSchema;

        if (request.Action == ChromeProtocolAction.DESCRIBE)
        {
            if (schema is { } full) return DescribeProtocol(full, request);
            if (metadata!.ReferenceSchema is { } reference)
            {
                var described = DescribeProtocol(reference, request);
                if (described.Status == ChromeProtocolCode.COMPLETED)
                    return described with { Detail = "BUNDLED_REFERENCE_SCHEMA_NOT_RUNTIME_VERIFIED" };
            }
            return DescribeLiveDomains(metadata.LiveDomains, request);
        }

        if (request.Action == ChromeProtocolAction.TARGETS)
        {
            var targets = request.AllowBrowserScope
                ? await BrowserTargets(cdp, request.BrowserWindowId, request.TabId)
                : await TargetsForWindow(cdp, hwnd, request.WindowId, timeout.Token,
                    request.ExpectedOrigin, request.ExpectedUrl, request.TabId);
            targets = targets.Where(t => MatchesContextIfSpecified(t.Url,
                request.ExpectedOrigin, request.ExpectedUrl)).ToList();
            if (request.BrowserWindowId is { } expected && targets.Any(t => t.BrowserWindowId != expected))
                return new(ChromeProtocolCode.CONTEXT_MISMATCH, request.Action);
            var page = targets.Skip(request.Offset).Take(request.PageSize)
                .Select(t => new { tab_id = t.Id, title = t.Title, url = t.Url,
                    browser_window_id = t.BrowserWindowId });
            return Bounded(new(ChromeProtocolCode.COMPLETED, request.Action,
                Data: JsonSerializer.SerializeToElement(new { tabs = page,
                    next_offset = targets.Count > request.Offset + request.PageSize
                        ? request.Offset + request.PageSize : (int?)null }),
                Destination: request.AllowBrowserScope ? ChromeTabDestination.CHROME_SELECTED_UNVERIFIED :
                    ChromeTabDestination.VERIFIED_WINDOW));
        }

        bool isEvent = request.Action == ChromeProtocolAction.SUBSCRIBE;
        string eventMethod = isEvent && request.Domain is not null
            ? request.Domain + ".*" : request.Method!;
        JsonElement command = default;
        if (schema is { } verifiedSchema &&
            (isEvent ? !EventSubscriptionExists(verifiedSchema, eventMethod) :
            !TryFindCommand(verifiedSchema, request.Method!, out command)))
            return new(ChromeProtocolCode.METHOD_NOT_SUPPORTED, request.Action,
                Detail: isEvent ? "EVENT_NOT_IN_CONNECTED_CHROME_PROTOCOL" :
                    "NOT_IN_CONNECTED_CHROME_PROTOCOL", Method: eventMethod);

        // A target-session CALL is always bound to one proven HWND and exact tab.
        // Browser-scoped commands can modify other profiles, so require an
        // explicit opt-in except clearly observational queries.
        string? sessionId = null;
        string? selectedUrl = null;
        int? selectedWindow = request.BrowserWindowId;
        if (request.TabId is not null)
        {
            if (request.AllowBrowserScope && request.BrowserWindowId is null)
                return new(ChromeProtocolCode.INVALID_ARGUMENT, request.Action,
                    Detail: "BROWSER_SCOPE_TARGET_REQUIRES_EXACT_BROWSER_WINDOW_ID");
            var targets = request.AllowBrowserScope
                ? await BrowserTargets(cdp, request.BrowserWindowId, request.TabId)
                : await TargetsForWindow(cdp, hwnd, request.WindowId, timeout.Token,
                    request.ExpectedOrigin, request.ExpectedUrl, request.TabId);
            if (targets.Count != 1)
                return new(targets.Count == 0 ? ChromeProtocolCode.TARGET_NOT_FOUND :
                    ChromeProtocolCode.TARGET_AMBIGUOUS, request.Action);
            if (request.BrowserWindowId is { } expected && targets[0].BrowserWindowId != expected)
                return new(ChromeProtocolCode.CONTEXT_MISMATCH, request.Action);
            if (!MatchesContextIfSpecified(targets[0].Url, request.ExpectedOrigin, request.ExpectedUrl))
                return new(ChromeProtocolCode.CONTEXT_MISMATCH, request.Action);
            selectedUrl = targets[0].Url;
            selectedWindow = targets[0].BrowserWindowId;
            // Target identity and URL are re-read before attaching. This is
            // observation only; never retry a possibly dispatched effect.
            var snapshot = await cdp.Call("Target.getTargetInfo", new { targetId = request.TabId });
            if (!SameProtocolTarget(snapshot, request.TabId, selectedUrl,
                request.ExpectedOrigin, request.ExpectedUrl))
                return new(ChromeProtocolCode.CONTEXT_MISMATCH, request.Action,
                    Detail: "TARGET_CHANGED_BEFORE_DISPATCH");
            sessionId = await Attach(cdp, request.TabId);
        }
        else if (!request.AllowBrowserScope)
            return new(ChromeProtocolCode.BROWSER_SCOPE_NOT_ALLOWED, request.Action,
                Detail: "EXPLICIT_ALLOW_BROWSER_SCOPE_REQUIRED");

        if (request.Action == ChromeProtocolAction.SUBSCRIBE)
        {
            if (ProtocolSubscriptions.Count >= 16)
                return new(ChromeProtocolCode.INVALID_ARGUMENT, request.Action,
                    Detail: "SUBSCRIPTION_LIMIT_REACHED");
            string id = cdp.Subscribe(eventMethod, sessionId, request.PageSize);
            ProtocolSubscriptions[id] = new(request.WindowId, request.ExpectedProfile,
                request.TabId, selectedWindow, request.ExpectedOrigin, request.ExpectedUrl,
                CurrentGeneration, request.AllowBrowserScope);
            return new(ChromeProtocolCode.COMPLETED, request.Action,
                Method: eventMethod, SubscriptionId: id,
                Detail: schema is null ? "EVENT_FILTER_REGISTERED_WITHOUT_HTTP_SCHEMA" : null,
                Destination: request.AllowBrowserScope ? ChromeTabDestination.CHROME_SELECTED_UNVERIFIED :
                    ChromeTabDestination.VERIFIED_WINDOW);
        }

        JsonElement parametersFromSchema;
        string? schemaError;
        bool validParameters = schema is { } typedSchema
            ? TryParseAndValidateParameters(typedSchema, command, request.Method!,
                request.ParametersJson, out parametersFromSchema, out schemaError)
            : TryParseProtocolObject(request.ParametersJson, out parametersFromSchema, out schemaError);
        if (!validParameters)
            return new(ChromeProtocolCode.PARAMETERS_INVALID, request.Action,
                Detail: schemaError, Method: request.Method);

        // CDP dynamic commands may change focus indirectly (e.g. arbitrary
        // JavaScript). There is no honest universal foreground guarantee.
        // Caller explicitly authorizes such operations with AllowForeground.
        JsonElement response;
        try
        {
            response = await cdp.Call(request.Method!, parametersFromSchema, sessionId);
        }
        catch (InvalidOperationException rejected) when (rejected.Message == "CDP_COMMAND_REJECTED")
        {
            if (rejected.Data["cdp_error_code"] is int code && code == -32601)
                return new(ChromeProtocolCode.METHOD_NOT_SUPPORTED, request.Action,
                    Detail: "CHROME_REJECTED_UNKNOWN_METHOD", Method: request.Method);
            // Chrome rejected a *dispatched* request. The generic bridge cannot
            // prove whether any side effect preceded that rejection.
            return new(ChromeProtocolCode.EFFECT_UNKNOWN, request.Action,
                Detail: "CDP_COMMAND_REJECTED", Method: request.Method);
        }
        return Bounded(new(ChromeProtocolCode.COMPLETED, request.Action,
            Detail: schema is null ? "CDP_ACK_CHROME_VALIDATED_PARAMETERS_NO_HTTP_SCHEMA" :
                "CDP_ACK_NOT_APPLICATION_VERIFICATION", Method: request.Method,
            Data: response.Clone(),
            Destination: request.AllowBrowserScope ? ChromeTabDestination.CHROME_SELECTED_UNVERIFIED :
                ChromeTabDestination.VERIFIED_WINDOW));
    }

    private static bool MatchesContextIfSpecified(string url, string? origin, string? expectedUrl) =>
        origin is null && expectedUrl is null || MatchesContext(url, origin, expectedUrl);

    private static bool SameProtocolTarget(JsonElement response, string id,
        string expectedSnapshotUrl, string? expectedOrigin, string? expectedUrl)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("targetInfo", out var info) ||
            info.ValueKind != JsonValueKind.Object ||
            StringProperty(info, "targetId") != id || StringProperty(info, "type") != "page")
            return false;
        string? url = StringProperty(info, "url");
        return url is not null && string.Equals(url, expectedSnapshotUrl, StringComparison.Ordinal) &&
            MatchesContextIfSpecified(url, expectedOrigin, expectedUrl);
    }

    // Newer approval-based Chrome sessions expose the consented browser WebSocket
    // but return HTTP 404 for every /json/* endpoint. HTTP metadata is optional:
    // querying it must never disable commands on an otherwise healthy CDP socket.
    // Both discovery paths reuse the ONE already connected Session.
    internal static async Task<ProtocolMetadata> ResolveProtocolMetadataAsync(Uri endpoint,
        Session session, CancellationToken cancellation)
    {
        if (_protocolMetadataGeneration == CurrentGeneration &&
            _protocolMetadataEndpoint == endpoint && _protocolMetadata is { } cached)
            return cached;

        JsonElement? full = await RuntimeProtocolSchema(endpoint, cancellation);
        JsonElement? liveDomains = null;
        if (full is null)
        {
            try
            {
                var observed = await session.Call("Schema.getDomains", new { });
                if (observed.ValueKind == JsonValueKind.Object &&
                    observed.TryGetProperty("domains", out var domains) &&
                    domains.ValueKind == JsonValueKind.Array)
                    liveDomains = observed.Clone();
            }
            catch (InvalidOperationException error) when (error.Message == "CDP_COMMAND_REJECTED")
            {
                // Schema is an optional CDP domain, even on a healthy browser.
                // In that case Chrome itself remains authoritative for CALL.
            }
        }
        var metadata = new ProtocolMetadata(full, liveDomains,
            full is null ? BundledReferenceProtocolSchema() : null);
        _protocolMetadataGeneration = CurrentGeneration;
        _protocolMetadataEndpoint = endpoint;
        _protocolMetadata = metadata;
        return metadata;
    }

    // Official ChromeDevTools protocol JSON is pinned in protocol.lock.json.
    // It is an OFFLINE REFERENCE for DESCRIBE, not a claim about the exact
    // running Chrome build and never a command/argument allowlist. Only a
    // successful /json/protocol response is authoritative for prevalidation.
    internal static JsonElement? BundledReferenceProtocolSchema()
    {
        if (_referenceProtocolSchema.ValueKind == JsonValueKind.Object)
            return _referenceProtocolSchema;
        var files = new[]
        {
            (Name: "browser_protocol.json.gz",
             Sha: "604da8bb8bc470f663ae3ca42052af8779e551018e3770c1713518cf3f3da543"),
            (Name: "js_protocol.json.gz",
             Sha: "cd77c2c629bd3ca2901ea6634ba8a29fdfd4fff35d0ec63d6f21904007b5a0a6")
        };
        var domains = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            using var stream = typeof(WindowChromeCdp).Assembly.GetManifestResourceStream(
                "WindowsAdminMCP.ProtocolSchemas." + file.Name);
            if (stream is null || stream.Length is < 1 or > ProtocolMaxSchemaBytes)
                return null;
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var buffer = new MemoryStream();
            var chunk = new byte[32768];
            while (true)
            {
                int received = gzip.Read(chunk, 0, chunk.Length);
                if (received == 0) break;
                if (buffer.Length + received > ProtocolMaxSchemaBytes) return null;
                buffer.Write(chunk, 0, received);
            }
            byte[] contents = buffer.ToArray();
            if (!SHA256.HashData(contents).AsSpan().SequenceEqual(Convert.FromHexString(file.Sha)))
                return null;
            using var parsed = JsonDocument.Parse(contents);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object ||
                !parsed.RootElement.TryGetProperty("domains", out var source) ||
                source.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var domain in source.EnumerateArray())
            {
                string? name = StringProperty(domain, "domain");
                if (string.IsNullOrEmpty(name) || !seen.Add(name)) return null;
                domains.Add(domain.Clone());
            }
        }
        _referenceProtocolSchema = JsonSerializer.SerializeToElement(new
        {
            version = new { major = "1", minor = "3" }, domains
        });
        return _referenceProtocolSchema;
    }

    // Standard remote debugging offers full metadata. Approval-based debugging
    // may return 404 here while its WebSocket remains fully operational.
    private static async Task<JsonElement?> RuntimeProtocolSchema(Uri endpoint, CancellationToken cancellation)
    {
        if (_protocolSchemaGeneration == CurrentGeneration && _protocolSchemaEndpoint == endpoint &&
            _protocolSchema.ValueKind == JsonValueKind.Object)
            return _protocolSchema;
        var uri = new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), endpoint.Port,
            "/json/protocol").Uri;
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        probe.CancelAfter(TimeSpan.FromSeconds(8));
        // A short-lived HTTP transport is used only to fetch the protocol
        // schema. Disposing its entire handler after reading guarantees that
        // neither Chrome nor .NET keeps a second established pooled socket.
        using var httpHandler = new HttpClientHandler { UseProxy = false };
        using var httpClient = new HttpClient(httpHandler);
        using var schemaRequest = new HttpRequestMessage(HttpMethod.Get, uri);
        schemaRequest.Headers.ConnectionClose = true;
        try
        {
            using var response = await httpClient.SendAsync(schemaRequest,
                HttpCompletionOption.ResponseHeadersRead, probe.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > ProtocolMaxSchemaBytes)
                return null;
            await using var stream = await response.Content.ReadAsStreamAsync(probe.Token);
            using var memory = new MemoryStream();
            var buffer = new byte[32768];
            while (true)
            {
                int received = await stream.ReadAsync(buffer, probe.Token);
                if (received == 0) break;
                if (memory.Length + received > ProtocolMaxSchemaBytes) return null;
                memory.Write(buffer, 0, received);
            }
            using var parsed = JsonDocument.Parse(memory.ToArray());
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("domains", out var domains) || domains.ValueKind != JsonValueKind.Array)
                return null;
            _protocolSchema = root.Clone();
            _protocolSchemaGeneration = CurrentGeneration;
            _protocolSchemaEndpoint = endpoint;
            return _protocolSchema;
        }
        catch (HttpRequestException) { return null; }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return null;
        }
    }

    private static ChromeProtocolResult DescribeLiveDomains(JsonElement? live,
        ChromeProtocolRequest request)
    {
        if (live is not { } schema || schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("domains", out var domains) ||
            domains.ValueKind != JsonValueKind.Array)
            return new(ChromeProtocolCode.CDP_UNAVAILABLE, request.Action,
                Detail: "PROTOCOL_METADATA_UNAVAILABLE_CALL_STILL_SUPPORTED");

        var available = domains.EnumerateArray().ToArray();
        if (request.Method is { } method)
        {
            string domain = method[..method.IndexOf('.')];
            if (!available.Any(x => StringProperty(x, "name") == domain))
                return new(ChromeProtocolCode.METHOD_NOT_SUPPORTED, request.Action,
                    Detail: "DOMAIN_NOT_IN_CONNECTED_CHROME_PROTOCOL");
            return new(ChromeProtocolCode.COMPLETED, request.Action,
                Detail: "WEBSOCKET_DOMAINS_ONLY_METHOD_SIGNATURE_UNAVAILABLE",
                Method: method, Data: JsonSerializer.SerializeToElement(new
                {
                    method, domain, schema_level = "DOMAINS_ONLY",
                    method_support_verified = false, parameters_schema_available = false
                }));
        }
        if (request.Domain is { } domainName)
        {
            var matched = available.FirstOrDefault(x => StringProperty(x, "name") == domainName);
            if (matched.ValueKind == JsonValueKind.Undefined)
                return new(ChromeProtocolCode.METHOD_NOT_SUPPORTED, request.Action,
                    Detail: "DOMAIN_NOT_IN_CONNECTED_CHROME_PROTOCOL");
            return new(ChromeProtocolCode.COMPLETED, request.Action,
                Detail: "WEBSOCKET_DOMAINS_ONLY_METHOD_SIGNATURE_UNAVAILABLE",
                Data: JsonSerializer.SerializeToElement(new
                {
                    domain = domainName, version = StringProperty(matched, "version"),
                    schema_level = "DOMAINS_ONLY", commands = (object?)null,
                    events = (object?)null, method_signatures_available = false
                }));
        }
        return Bounded(new(ChromeProtocolCode.COMPLETED, request.Action,
            Detail: "WEBSOCKET_DOMAINS_ONLY_METHOD_SIGNATURE_UNAVAILABLE",
            Data: JsonSerializer.SerializeToElement(new
            {
                schema_level = "DOMAINS_ONLY",
                domains = available.Skip(request.Offset).Take(request.PageSize)
                    .Select(x => new { name = StringProperty(x, "name"),
                        version = StringProperty(x, "version") }),
                next_offset = available.Length > request.Offset + request.PageSize
                    ? request.Offset + request.PageSize : (int?)null
            })));
    }

    private static ChromeProtocolResult DescribeProtocol(JsonElement schema, ChromeProtocolRequest request)
    {
        var domains = schema.GetProperty("domains").EnumerateArray();
        if (request.Method is not null)
        {
            if (!TryFindCommand(schema, request.Method, out var command))
                return new(ChromeProtocolCode.METHOD_NOT_SUPPORTED, request.Action);
            return Bounded(new(ChromeProtocolCode.COMPLETED, request.Action,
                Method: request.Method, Data: command.Clone()));
        }
        if (request.Domain is not null)
        {
            foreach (var domain in domains)
            {
                if (StringProperty(domain, "domain") != request.Domain) continue;
                var commands = domain.TryGetProperty("commands", out var list) &&
                    list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToArray() : [];
                var events = domain.TryGetProperty("events", out var evts) &&
                    evts.ValueKind == JsonValueKind.Array ? evts.EnumerateArray().ToArray() : [];
                return Bounded(new(ChromeProtocolCode.COMPLETED, request.Action,
                    Data: JsonSerializer.SerializeToElement(new
                    {
                        domain = request.Domain, version = StringProperty(domain, "version"),
                        commands = commands.Skip(request.Offset).Take(request.PageSize)
                            .Select(c => new { name = StringProperty(c, "name"),
                                experimental = BooleanProperty(c, "experimental"),
                                deprecated = BooleanProperty(c, "deprecated") }),
                        events = events.Skip(request.Offset).Take(request.PageSize)
                            .Select(c => new { name = StringProperty(c, "name") }),
                        next_offset = commands.Length > request.Offset + request.PageSize ||
                            events.Length > request.Offset + request.PageSize
                                ? request.Offset + request.PageSize : (int?)null
                    })));
            }
            return new(ChromeProtocolCode.METHOD_NOT_SUPPORTED, request.Action,
                Detail: "DOMAIN_NOT_IN_CONNECTED_CHROME_PROTOCOL");
        }
        var all = domains.ToArray();
        return Bounded(new(ChromeProtocolCode.COMPLETED, request.Action,
            Data: JsonSerializer.SerializeToElement(new
            {
                version = schema.TryGetProperty("version", out var version) ? version.Clone() :
                    JsonSerializer.SerializeToElement(new { major = "unknown", minor = "unknown" }),
                domains = all.Skip(request.Offset).Take(request.PageSize).Select(d => new
                {
                    name = StringProperty(d, "domain"), version = StringProperty(d, "version"),
                    experimental = BooleanProperty(d, "experimental"),
                    deprecated = BooleanProperty(d, "deprecated")
                }),
                next_offset = all.Length > request.Offset + request.PageSize
                    ? request.Offset + request.PageSize : (int?)null
            })));
    }

    private static string? StringProperty(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) &&
        item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    private static bool BooleanProperty(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) &&
        item.ValueKind == JsonValueKind.True;

    internal static bool TryFindCommand(JsonElement schema, string method, out JsonElement command) =>
        FindProtocolDefinition(schema, method, "commands", out command);

    internal static bool TryFindEvent(JsonElement schema, string method) =>
        FindProtocolDefinition(schema, method, "events", out _);

    private static bool EventSubscriptionExists(JsonElement schema, string method)
    {
        if (!method.EndsWith(".*", StringComparison.Ordinal))
            return TryFindEvent(schema, method);
        string domainName = method[..^2];
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("domains", out var domains)) return false;
        foreach (var domain in domains.EnumerateArray())
            if (StringProperty(domain, "domain") == domainName &&
                domain.TryGetProperty("events", out var events) &&
                events.ValueKind == JsonValueKind.Array && events.GetArrayLength() > 0)
                return true;
        return false;
    }

    private static bool FindProtocolDefinition(JsonElement schema, string method,
        string field, out JsonElement definition)
    {
        definition = default;
        if (!ValidProtocolMethod(method) || schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("domains", out var domains) ||
            domains.ValueKind != JsonValueKind.Array) return false;
        string domainName = method[..method.IndexOf('.')];
        string memberName = method[(method.IndexOf('.') + 1)..];
        foreach (var domain in domains.EnumerateArray())
        {
            if (StringProperty(domain, "domain") != domainName ||
                !domain.TryGetProperty(field, out var members) ||
                members.ValueKind != JsonValueKind.Array) continue;
            foreach (var member in members.EnumerateArray())
            {
                if (StringProperty(member, "name") != memberName) continue;
                definition = member;
                return true;
            }
        }
        return false;
    }

    private static bool TryParseAndValidateParameters(JsonElement schema, JsonElement command,
        string method, string? parametersJson, out JsonElement parameters, out string? error)
    {
        parameters = default;
        error = null;
        try
        {
            using var document = JsonDocument.Parse(parametersJson ?? "{}", new JsonDocumentOptions
            { MaxDepth = 32 });
            var input = document.RootElement;
            if (input.ValueKind != JsonValueKind.Object)
            {
                error = "PARAMETERS_MUST_BE_JSON_OBJECT";
                return false;
            }
            var domain = method[..method.IndexOf('.')];
            if (!ValidateProtocolObject(schema, domain, command, input, "parameters", 0, out error))
                return false;
            parameters = input.Clone();
            return true;
        }
        catch (JsonException)
        {
            error = "PARAMETERS_JSON_INVALID";
            return false;
        }
    }

    // On approval-based Chrome, the HTTP method schema is deliberately absent.
    // Preserve bounded/typed JSON input; Chrome's actual CDP dispatcher checks
    // the method and its arguments. Do not invent a stale local allowlist.
    internal static bool TryParseProtocolObject(string? parametersJson,
        out JsonElement parameters, out string? error)
    {
        parameters = default;
        error = null;
        try
        {
            using var document = JsonDocument.Parse(parametersJson ?? "{}",
                new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "PARAMETERS_MUST_BE_JSON_OBJECT";
                return false;
            }
            parameters = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            error = "PARAMETERS_JSON_INVALID";
            return false;
        }
    }

    private static bool ValidateProtocolObject(JsonElement schema, string domain,
        JsonElement definition, JsonElement input, string field, int depth, out string? error)
    {
        error = null;
        if (depth > 16 || input.ValueKind != JsonValueKind.Object)
        {
            error = "PARAMETER_OBJECT_INVALID";
            return false;
        }
        JsonElement[] members = definition.TryGetProperty(field, out var specs) &&
            specs.ValueKind == JsonValueKind.Array ? specs.EnumerateArray().ToArray() : [];
        foreach (var required in members.Where(m => !BooleanProperty(m, "optional")))
        {
            string? name = StringProperty(required, "name");
            if (name is null || input.TryGetProperty(name, out _)) continue;
            error = "MISSING_PARAMETER:" + name;
            return false;
        }
        foreach (var provided in input.EnumerateObject())
        {
            var definitionMember = members.FirstOrDefault(m => StringProperty(m, "name") == provided.Name);
            if (definitionMember.ValueKind == JsonValueKind.Undefined)
            {
                error = "UNKNOWN_PARAMETER:" + provided.Name;
                return false;
            }
            if (!ValidateProtocolValue(schema, domain, definitionMember,
                provided.Value, depth + 1, out error))
            {
                error = provided.Name + ":" + error;
                return false;
            }
        }
        return true;
    }

    private static bool ValidateProtocolValue(JsonElement schema, string domain,
        JsonElement spec, JsonElement value, int depth, out string? error)
    {
        error = null;
        if (depth > 16) { error = "PARAMETER_NESTING_TOO_DEEP"; return false; }
        if (StringProperty(spec, "$ref") is { } reference)
        {
            string referenceDomain = domain;
            if (reference.Contains('.'))
            {
                int dot = reference.IndexOf('.');
                referenceDomain = reference[..dot];
                reference = reference[(dot + 1)..];
            }
            foreach (var domainSpec in schema.GetProperty("domains").EnumerateArray())
            {
                if (StringProperty(domainSpec, "domain") != referenceDomain ||
                    !domainSpec.TryGetProperty("types", out var types) ||
                    types.ValueKind != JsonValueKind.Array) continue;
                foreach (var candidate in types.EnumerateArray())
                    if (StringProperty(candidate, "id") == reference)
                        return ValidateProtocolValue(schema, referenceDomain,
                            candidate, value, depth + 1, out error);
            }
            error = "UNRESOLVED_PROTOCOL_REFERENCE";
            return false;
        }
        string? type = StringProperty(spec, "type");
        bool correct = type switch
        {
            null or "any" => true,
            "string" or "binary" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            _ => false
        };
        if (!correct) { error = "PARAMETER_TYPE_MISMATCH:" + type; return false; }
        if (spec.TryGetProperty("enum", out var choices) && choices.ValueKind == JsonValueKind.Array &&
            !choices.EnumerateArray().Any(choice => choice.ToString() == value.ToString()))
        {
            error = "PARAMETER_ENUM_MISMATCH";
            return false;
        }
        if (type == "array" && spec.TryGetProperty("items", out var items))
            foreach (var item in value.EnumerateArray())
                if (!ValidateProtocolValue(schema, domain, items, item, depth + 1, out error))
                    return false;
        if (type == "object" && spec.TryGetProperty("properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Array)
            return ValidateProtocolObject(schema, domain, spec, value, "properties", depth + 1,
                out error);
        return true;
    }

    private static bool MayActivateForeground(string method) => method is
        "Target.activateTarget" or "Page.bringToFront" or "Browser.setWindowBounds" or
        "Target.createTarget" or "Target.closeTarget" or "Browser.close" or
        "Browser.crash" or "DOM.focus" or
        "Emulation.setFocusEmulationEnabled" or "Input.dispatchKeyEvent" or
        "Input.dispatchMouseEvent" or "Input.insertText" or "Input.dispatchTouchEvent" or
        "Input.dispatchDragEvent" or "Input.imeSetComposition" or "Input.synthesizeScrollGesture";

    private static bool ProtocolCanRunArbitraryCode(string method) => method is
        "Runtime.evaluate" or "Runtime.callFunctionOn" or "Page.addScriptToEvaluateOnNewDocument" or
        "Page.createIsolatedWorld" or "Runtime.runScript" or "Debugger.evaluateOnCallFrame" or
        "Runtime.compileScript";

    private static ChromeProtocolResult Bounded(ChromeProtocolResult result)
    {
        // The existing USER broker's transport is intentionally bounded. An
        // oversized CDP answer must not tear down its sole approved session.
        if (JsonSerializer.SerializeToUtf8Bytes(result).Length <= ProtocolMaxResultBytes)
            return result;
        return new(ChromeProtocolCode.RESULT_TOO_LARGE, result.Action,
            Detail: "CDP_RESULT_EXCEEDS_64_KIB", Method: result.Method);
    }
}
