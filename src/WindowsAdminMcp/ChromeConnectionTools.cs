using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Automation;
using ModelContextProtocol.Server;

[JsonConverter(typeof(JsonStringEnumConverter<ChromeConnectionAction>))]
public enum ChromeConnectionAction { LIST, REQUEST, STATUS, DISCONNECT }

[JsonConverter(typeof(JsonStringEnumConverter<ChromeConnectionCode>))]
public enum ChromeConnectionCode
{
    OK, INVALID_ARGUMENT, SERVICE_UNAVAILABLE, USER_CONTEXT_UNAVAILABLE,
    DESKTOP_UNAVAILABLE, STALE_WINDOW, PROFILE_MISMATCH, PROFILE_NOT_OPEN,
    PROFILE_AMBIGUOUS, WORKER_FAILED
}

[JsonConverter(typeof(JsonStringEnumConverter<ChromeConnectionState>))]
public enum ChromeConnectionState
{
    NOT_REQUESTED, DEBUGGING_UNAVAILABLE, CONSENT_PENDING, CONNECTED,
    CONSENT_NOT_CONFIRMED, CONSENT_TIMED_OUT, CONNECTION_FAILED,
    CONSENT_WINDOW_UNAVAILABLE, DISCONNECTED, CONNECTION_LOST
}

[JsonConverter(typeof(JsonStringEnumConverter<ChromeConnectionFailureReason>))]
public enum ChromeConnectionFailureReason
{
    SOCKET_CLOSED, TRANSPORT_ERROR, TIMEOUT, PROTOCOL_ERROR, BROKER_EXITED,
    WINDOW_HIDDEN, WINDOW_MINIMIZED, FOREGROUND_DENIED,
    CONSENT_TIMEOUT, HANDSHAKE_FAILED
}

[JsonConverter(typeof(JsonStringEnumConverter<ChromeConnectionNextAction>))]
public enum ChromeConnectionNextAction
{
    NONE, SELECT_PROFILE, ENABLE_CHROME_DEBUGGING, ALLOW_CHROME_PROMPT, RETRY_CONNECTION
}

[JsonConverter(typeof(JsonStringEnumConverter<ChromeProfileEmailSource>))]
public enum ChromeProfileEmailSource { SIGNED_IN_ACCOUNT, PROFILE_NAME, NATIVE_AVATAR, NOT_AVAILABLE }

public sealed record ChromeProfileInfo(
    [property: JsonPropertyName("window_id")] string? WindowId,
    [property: JsonPropertyName("profile_id")] string? ProfileId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("email_source")] ChromeProfileEmailSource EmailSource,
    [property: JsonPropertyName("process_id")] int? ProcessId,
    [property: JsonPropertyName("session_id")] int? SessionId);

internal sealed record ChromeProfileMetadata(string Id, string Name, string? Email,
    ChromeProfileEmailSource Source);

public sealed record ChromeConnectionResult(
    [property: JsonPropertyName("status")] ChromeConnectionCode Status,
    [property: JsonPropertyName("state")] ChromeConnectionState State,
    [property: JsonPropertyName("next_action")] ChromeConnectionNextAction NextAction = ChromeConnectionNextAction.NONE,
    [property: JsonPropertyName("profile"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ChromeProfileInfo? Profile = null,
    [property: JsonPropertyName("profiles"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ChromeProfileInfo[]? Profiles = null,
    [property: JsonPropertyName("detail"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Detail = null,
    [property: JsonPropertyName("failure_reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ChromeConnectionFailureReason? FailureReason = null,
    [property: JsonPropertyName("last_loss_reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ChromeConnectionFailureReason? LastLossReason = null);

internal sealed record ChromeConnectionRequest(string WindowId, string ExpectedProfile,
    ChromeConnectionAction Action);

[McpServerToolType]
public sealed class ChromeConnectionTools
{
    [McpServerTool(Name = "windows.chrome_connection", UseStructuredContent = true),
     Description("Discover Chrome profiles and request or check native user-approved CDP connection. " +
         "A unique open profile may be selected by email without window_id; ambiguous windows require window_id.")]
    public static async Task<ChromeConnectionResult> Connection(ChromeConnectionAction action,
        string? window_id = null, string? expected_profile = null,
        CancellationToken cancellationToken = default)
    {
        if (!ValidArguments(action, window_id, expected_profile))
            return new(ChromeConnectionCode.INVALID_ARGUMENT, ChromeConnectionState.NOT_REQUESTED);
        var status = await AdminTools.Status(cancellationToken: cancellationToken);
        if (status.Status != StatusCode.OK)
            return new(ChromeConnectionCode.SERVICE_UNAVAILABLE, ChromeConnectionState.NOT_REQUESTED);
        using var self = Process.GetCurrentProcess();
        if (status.ExecutionContext != CommandContext.USER || status.UserContextAvailable != true ||
            status.Frontend?.Elevated != true || status.Frontend?.SessionId != self.SessionId)
            return new(ChromeConnectionCode.USER_CONTEXT_UNAVAILABLE, ChromeConnectionState.NOT_REQUESTED);
        var desktop = DesktopCapture.ObserveDesktop(self.SessionId);
        if (desktop.Code != CaptureCode.COMPLETED)
            return new(ChromeConnectionCode.DESKTOP_UNAVAILABLE, ChromeConnectionState.NOT_REQUESTED);
        if (action == ChromeConnectionAction.LIST)
            return await ChromeProfileCatalog.ListIsolatedAsync(cancellationToken);

        if (window_id is null)
        {
            var discovered = await ChromeProfileCatalog.ListIsolatedAsync(cancellationToken);
            if (discovered.Status != ChromeConnectionCode.OK) return discovered;
            var matches = ChromeProfileCatalog.OpenMatches(discovered.Profiles ?? [], expected_profile!);
            if (matches.Length != 1)
                return new(matches.Length == 0 ? ChromeConnectionCode.PROFILE_NOT_OPEN :
                    ChromeConnectionCode.PROFILE_AMBIGUOUS, ChromeConnectionState.NOT_REQUESTED,
                    ChromeConnectionNextAction.SELECT_PROFILE,
                    Profiles: matches.Length > 1 ? matches : null);
            window_id = matches[0].WindowId;
        }

        var resolved = WindowCatalog.ResolveId(window_id, self.SessionId, out _);
        if (resolved != WindowResolveCode.OK)
            return new(ChromeConnectionCode.STALE_WINDOW, ChromeConnectionState.NOT_REQUESTED,
                Detail: resolved.ToString());
        return await WindowChromeBroker.ConnectionAsync(
            new ChromeConnectionRequest(window_id!, expected_profile!, action), cancellationToken);
    }

    internal static bool ValidArguments(ChromeConnectionAction action, string? windowId, string? profile) =>
        action == ChromeConnectionAction.LIST ? windowId is null && profile is null :
        (action is ChromeConnectionAction.REQUEST or ChromeConnectionAction.STATUS or
            ChromeConnectionAction.DISCONNECT) &&
            (windowId is null || WindowCatalog.TryParseId(windowId, out _)) &&
            profile is { Length: >= 1 and <= 100 } && !string.IsNullOrWhiteSpace(profile) &&
            !profile.Contains('\0');
}

// The Chrome native avatar button is UIA, independent of CDP permission.
// Perform these third-party UIA calls in a bounded disposable USER child.
internal static class ChromeProfileCatalog
{
    private const string AvatarAutomationId = "view_1018";

    internal static bool IsEmail(string? value)
    {
        if (value is not { Length: >= 3 and <= 200 }) return false;
        int at = value.IndexOf('@');
        return at > 0 && at < value.Length - 2 && value[(at + 1)..].Contains('.') &&
            !value.Any(char.IsWhiteSpace) && !value.Contains('(') && !value.Contains(')');
    }

    internal static ChromeProfileMetadata[] ParseInfoCache(JsonElement root)
    {
        if (!root.TryGetProperty("profile", out var profiles) ||
            !profiles.TryGetProperty("info_cache", out var cache) ||
            cache.ValueKind != JsonValueKind.Object) return [];
        var result = new List<ChromeProfileMetadata>();
        foreach (var entry in cache.EnumerateObject())
        {
            if (result.Count == 200) break;
            string id = entry.Name;
            if (id.Length is < 1 or > 60 || id.IndexOfAny(['/', '\\', ':', '\0']) >= 0 ||
                entry.Value.ValueKind != JsonValueKind.Object ||
                !entry.Value.TryGetProperty("name", out var nameValue) ||
                nameValue.ValueKind != JsonValueKind.String) continue;
            string? name = nameValue.GetString();
            if (name is not { Length: >= 1 and <= 200 }) continue;
            string? user = entry.Value.TryGetProperty("user_name", out var userValue) &&
                userValue.ValueKind == JsonValueKind.String ? userValue.GetString() : null;
            bool signedIn = IsEmail(user);
            bool emailName = IsEmail(name);
            result.Add(new(id, name, signedIn ? user : emailName ? name : null,
                signedIn ? ChromeProfileEmailSource.SIGNED_IN_ACCOUNT :
                emailName ? ChromeProfileEmailSource.PROFILE_NAME : ChromeProfileEmailSource.NOT_AVAILABLE));
        }
        return [.. result];
    }

    internal static ChromeProfileMetadata[] ReadKnownProfiles()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string path = Path.Combine(local, "Google", "Chrome", "User Data", "Local State");
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > 8_388_608) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var data = JsonDocument.Parse(stream);
            return ParseInfoCache(data.RootElement);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or System.Security.SecurityException) { return []; }
    }

    // Bind a real Chrome toolbar avatar to one and only one profile in Chrome's
    // own Local State. Never infer an email from a title or a non-unique name.
    internal static ChromeProfileMetadata? ResolveMetadata(string avatarName, string? avatarAccount,
        IReadOnlyList<ChromeProfileMetadata> known)
    {
        var matches = known.Where(p => avatarAccount is not null &&
            string.Equals(p.Email, avatarAccount, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (avatarAccount is not null) return matches.Length == 1 ? matches[0] : null;
        matches = known.Where(p => string.Equals(p.Name, avatarName,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static (string Name, string? Account)? ParseAvatar(string? label)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > 200 || label.Contains('\0')) return null;
        string name = label.Trim();
        string? account = null;
        int open = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (open > 0 && name.EndsWith(')'))
        {
            string candidate = name[(open + 2)..^1];
            if (IsEmail(candidate))
            {
                account = candidate;
                name = name[..open];
            }
        }
        return name.Length is >= 1 and <= 100 ? (name, account) : null;
    }

    internal static ChromeProfileInfo? ReadWindowProfile(string windowId, int session,
        IReadOnlyList<ChromeProfileMetadata>? known = null)
    {
        if (WindowCatalog.ResolveId(windowId, session, out var hwnd) != WindowResolveCode.OK ||
            !WindowCatalog.TryParseId(windowId, out var identity)) return null;
        try
        {
            using var owner = Process.GetProcessById(identity.ProcessId);
            if (!owner.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase)) return null;
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return null;
            var avatars = root.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.AutomationIdProperty, AvatarAutomationId)));
            if (avatars.Count != 1 || WindowChromeCdp.IsInsideDocument(avatars[0])) return null;
            var parsed = ParseAvatar(avatars[0].Current.Name);
            if (parsed is not { } profile) return null;
            var match = ResolveMetadata(profile.Name, profile.Account, known ?? ReadKnownProfiles());
            return new(windowId, match?.Id, profile.Name,
                match?.Email ?? profile.Account,
                match?.Source ?? (profile.Account is null ? ChromeProfileEmailSource.NOT_AVAILABLE :
                    ChromeProfileEmailSource.NATIVE_AVATAR), identity.ProcessId, identity.SessionId);
        }
        catch (Exception e) when (e is COMException or ElementNotAvailableException or
            InvalidOperationException or ArgumentException or Win32Exception or OverflowException ||
            e is System.Security.SecurityException) { return null; }
    }

    internal static bool MatchesSelector(ChromeProfileInfo profile, string selector) =>
        string.Equals(profile.DisplayName, selector, StringComparison.OrdinalIgnoreCase) ||
        profile.Email is not null &&
            string.Equals(profile.Email, selector, StringComparison.OrdinalIgnoreCase) ||
        profile.ProfileId is not null &&
            string.Equals(profile.ProfileId, selector, StringComparison.OrdinalIgnoreCase);

    internal static ChromeProfileInfo[] OpenMatches(IEnumerable<ChromeProfileInfo> profiles, string selector) =>
        [.. profiles.Where(p => p.WindowId is not null && MatchesSelector(p, selector))];

    internal static ChromeConnectionResult ListInWorker()
    {
        using var self = Process.GetCurrentProcess();
        var discovered = WindowCatalog.List(100, self.SessionId);
        if (discovered.Status != WindowListCode.COMPLETED)
            return new(ChromeConnectionCode.WORKER_FAILED, ChromeConnectionState.NOT_REQUESTED);
        var known = ReadKnownProfiles();
        var profiles = known.Select(p => new ChromeProfileInfo(null, p.Id, p.Name, p.Email,
            p.Source, null, null)).ToList();
        foreach (var window in discovered.Windows.Where(w => w.ProcessName.Equals("chrome",
            StringComparison.OrdinalIgnoreCase)).Take(20))
        {
            var observed = ReadWindowProfile(window.WindowId, self.SessionId, known);
            if (observed is null) continue;
            int index = profiles.FindIndex(p => p.ProfileId == observed.ProfileId &&
                p.WindowId is null);
            if (index >= 0 && observed.ProfileId is not null) profiles[index] = observed;
            else profiles.Add(observed);
        }
        return new(ChromeConnectionCode.OK, ChromeConnectionState.NOT_REQUESTED,
            ChromeConnectionNextAction.SELECT_PROFILE, Profiles: [.. profiles]);
    }

    internal static async Task<ChromeConnectionResult> ListIsolatedAsync(CancellationToken cancellationToken)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
            return new(ChromeConnectionCode.WORKER_FAILED, ChromeConnectionState.NOT_REQUESTED);
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("window-chrome-profiles-worker");
        start.Environment.Remove("CONTROL_PLANE_API_KEY");
        start.Environment.Remove("OPENAI_API_KEY");
        using var worker = new Process { StartInfo = start };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            if (!worker.Start()) throw new Win32Exception("PROFILE_WORKER_START_FAILED");
            var output = worker.StandardOutput.ReadToEndAsync(deadline.Token);
            await worker.WaitForExitAsync(deadline.Token);
            string json = await output;
            if (worker.ExitCode != 0 || json.Length > 16000) throw new JsonException("PROFILE_WORKER_OUTPUT_INVALID");
            return JsonSerializer.Deserialize<ChromeConnectionResult>(json, PipeWire.Json) ??
                new(ChromeConnectionCode.WORKER_FAILED, ChromeConnectionState.NOT_REQUESTED);
        }
        catch (Exception e) when (e is OperationCanceledException or Win32Exception or IOException or JsonException)
        {
            try { if (!worker.HasExited) worker.Kill(true); }
            catch (Exception x) when (x is InvalidOperationException or Win32Exception) { }
            return new(ChromeConnectionCode.WORKER_FAILED, ChromeConnectionState.NOT_REQUESTED,
                Detail: e.GetType().Name);
        }
    }
}
