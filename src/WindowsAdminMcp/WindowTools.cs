using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

[JsonConverter(typeof(JsonStringEnumConverter<WindowListCode>))]
public enum WindowListCode
{
    COMPLETED, INVALID_ARGUMENT, SERVICE_UNAVAILABLE, USER_CONTEXT_UNAVAILABLE,
    DESKTOP_UNAVAILABLE, ENUMERATION_FAILED
}

public sealed record WindowSummary(
    [property: JsonPropertyName("window_id")] string WindowId,
    [property: JsonPropertyName("hwnd")] long Handle,
    [property: JsonPropertyName("process_id")] int ProcessId,
    [property: JsonPropertyName("process_name")] string ProcessName,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("session_id")] int SessionId,
    [property: JsonPropertyName("is_foreground")] bool IsForeground);

public sealed record WindowListResult(
    [property: JsonPropertyName("status")] WindowListCode Status,
    [property: JsonPropertyName("windows")] WindowSummary[] Windows,
    [property: JsonPropertyName("session_id")] int? SessionId = null,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("elapsed_ms")] long? ElapsedMs = null);

[McpServerToolType]
public sealed class WindowTools
{
    [McpServerTool(Name = "windows.list_windows", UseStructuredContent = true),
     Description("List named top-level windows in the current authorized interactive USER desktop. Includes occluded windows, never changes focus or accesses other sessions. Maximum 100 results.")]
    public static async Task<WindowListResult> ListWindows(int max_windows = 50,
        CancellationToken cancellationToken = default)
    {
        if (max_windows is < 1 or > 100)
            return new(WindowListCode.INVALID_ARGUMENT, []);

        var status = await AdminTools.Status(cancellationToken);
        if (status.Status != StatusCode.OK)
            return new(WindowListCode.SERVICE_UNAVAILABLE, [], Detail: status.Status.ToString());

        using var self = Process.GetCurrentProcess();
        if (status.ExecutionContext != CommandContext.USER ||
            status.UserContextAvailable != true || status.Frontend?.Elevated != true ||
            status.Frontend?.SessionId != self.SessionId)
            return new(WindowListCode.USER_CONTEXT_UNAVAILABLE, [], SessionId: self.SessionId);

        var desktop = DesktopCapture.ObserveDesktop(self.SessionId);
        if (desktop.Code != CaptureCode.COMPLETED)
            return new(WindowListCode.DESKTOP_UNAVAILABLE, [], self.SessionId, desktop.Code.ToString());

        cancellationToken.ThrowIfCancellationRequested();
        return WindowCatalog.List(max_windows, self.SessionId);
    }

    [McpServerTool(Name = "windows.inspect_window", UseStructuredContent = true),
     Description("Read up to 100 UIA elements without focus or input values. Optional start_path + fresh start_element_id selects a verified subtree; offset pages through it (next_offset). Paths never authorize actions alone. Bounded 5-second USER worker.")]
    public static async Task<WindowInspectResult> InspectWindow(string window_id, int max_depth = 12,
        int max_elements = 100, string? start_path = null, string? start_element_id = null,
        int offset = 0, CancellationToken cancellationToken = default)
    {
        if (!WindowInspector.ValidArguments(window_id, max_depth, max_elements,
            start_path, start_element_id, offset))
            return new(WindowInspectCode.INVALID_ARGUMENT, []);
        var status = await AdminTools.Status(cancellationToken);
        if (status.Status != StatusCode.OK)
            return new(WindowInspectCode.SERVICE_UNAVAILABLE, [], Detail: status.Status.ToString());
        using var self = Process.GetCurrentProcess();
        if (status.ExecutionContext != CommandContext.USER ||
            status.UserContextAvailable != true || status.Frontend?.Elevated != true ||
            status.Frontend?.SessionId != self.SessionId)
            return new(WindowInspectCode.USER_CONTEXT_UNAVAILABLE, []);
        var desktop = DesktopCapture.ObserveDesktop(self.SessionId);
        if (desktop.Code != CaptureCode.COMPLETED)
            return new(WindowInspectCode.DESKTOP_UNAVAILABLE, [], Detail: desktop.Code.ToString());
        var resolved = WindowCatalog.ResolveId(window_id, self.SessionId, out _);
        if (resolved != WindowResolveCode.OK)
            return new(resolved == WindowResolveCode.ACCESS_DENIED ? WindowInspectCode.ACCESS_DENIED :
                resolved == WindowResolveCode.WRONG_SESSION ? WindowInspectCode.USER_CONTEXT_UNAVAILABLE :
                WindowInspectCode.STALE_WINDOW, [], Detail: resolved.ToString());
        return await WindowInspector.RunIsolatedAsync(window_id, max_depth, max_elements,
            cancellationToken, start_path, start_element_id, offset);
    }

    [McpServerTool(Name = "windows.capture_window", UseStructuredContent = true),
     Description("Capture pixels of a selected window_id using Windows Graphics Capture, even when occluded. Only authorized foreground USER desktop, never changes focus. Minimized/protected windows may be unavailable. PNG max 2 MB and max_width 256..1920. No desktop screenshot fallback.")]
    public static async Task<ModelContextProtocol.Protocol.CallToolResult> CaptureWindow(string window_id,
        int max_width = 1280, CancellationToken cancellationToken = default)
    {
        if (!WindowCapture.Valid(window_id, max_width))
            return WindowCapture.Response(new(WindowCaptureCode.INVALID_ARGUMENT));
        var status = await AdminTools.Status(cancellationToken);
        if (status.Status != StatusCode.OK)
            return WindowCapture.Response(new(WindowCaptureCode.SERVICE_UNAVAILABLE, Detail: status.Status.ToString()));
        using var self = Process.GetCurrentProcess();
        if (status.ExecutionContext != CommandContext.USER ||
            status.UserContextAvailable != true || status.Frontend?.Elevated != true ||
            status.Frontend?.SessionId != self.SessionId)
            return WindowCapture.Response(new(WindowCaptureCode.USER_CONTEXT_UNAVAILABLE));
        var desktop = DesktopCapture.ObserveDesktop(self.SessionId);
        if (desktop.Code != CaptureCode.COMPLETED)
            return WindowCapture.Response(new(WindowCaptureCode.DESKTOP_UNAVAILABLE, Detail: desktop.Code.ToString()));
        var resolved = WindowCatalog.ResolveId(window_id, self.SessionId, out _);
        if (resolved != WindowResolveCode.OK)
            return WindowCapture.Response(new(WindowCapture.MapResolve(resolved), Detail: resolved.ToString()));
        return await WindowCapture.RunIsolatedAsync(window_id, max_width, cancellationToken);
    }

    [McpServerTool(Name = "windows.invoke_element", UseStructuredContent = true),
     Description("Act on a fresh element_id (30 seconds) from windows.inspect_window. Optional element_path addresses nodes beyond the first 100, always fingerprint-checked. Requires confirm=true and authorized USER desktop. UIA2 disables automatic focus but providers may still change it (foreground_changed). No synthetic input or auto-retry after unknown effect.")]
    public static async Task<WindowActionResult> InvokeElement(string window_id, string element_id,
        WindowActionKind action, string? value = null, bool confirm = false,
        string? element_path = null,
        CancellationToken cancellationToken = default)
    {
        var request = new WindowActionRequest(window_id, element_id, action, value, element_path);
        if (!WindowAction.Valid(request)) return new(WindowActionCode.INVALID_ARGUMENT);
        if (!confirm) return new(WindowActionCode.CONFIRMATION_REQUIRED);

        var status = await AdminTools.Status(cancellationToken);
        if (status.Status != StatusCode.OK)
            return new(WindowActionCode.SERVICE_UNAVAILABLE, Detail: status.Status.ToString());
        using var self = Process.GetCurrentProcess();
        if (status.ExecutionContext != CommandContext.USER ||
            status.UserContextAvailable != true || status.Frontend?.Elevated != true ||
            status.Frontend?.SessionId != self.SessionId)
            return new(WindowActionCode.USER_CONTEXT_UNAVAILABLE);
        var desktop = DesktopCapture.ObserveDesktop(self.SessionId);
        if (desktop.Code != CaptureCode.COMPLETED)
            return new(WindowActionCode.DESKTOP_UNAVAILABLE, Detail: desktop.Code.ToString());
        var resolved = WindowCatalog.ResolveId(window_id, self.SessionId, out _);
        if (resolved != WindowResolveCode.OK)
            return new(resolved == WindowResolveCode.ACCESS_DENIED ? WindowActionCode.ACCESS_DENIED :
                resolved == WindowResolveCode.WRONG_SESSION ? WindowActionCode.USER_CONTEXT_UNAVAILABLE :
                WindowActionCode.STALE_WINDOW, Detail: resolved.ToString());
        return await WindowAction.RunIsolatedAsync(request, cancellationToken);
    }
}

internal readonly record struct WindowIdentity(long Handle, int ProcessId, long StartFileTimeUtc, int SessionId);
internal enum WindowResolveCode { OK, INVALID_ARGUMENT, WRONG_SESSION, STALE_WINDOW, ACCESS_DENIED }

internal static class WindowCatalog
{
    internal static string FormatId(WindowIdentity id) =>
        $"w1:{id.Handle:x}:{id.ProcessId:x}:{id.StartFileTimeUtc:x}:{id.SessionId:x}";

    internal static bool TryParseId(string? text, out WindowIdentity identity)
    {
        identity = default;
        if (text is null || text.Length > 128) return false;
        var fields = text.Split(':');
        if (fields.Length != 5 || fields[0] != "w1" ||
            !long.TryParse(fields[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hwnd) ||
            !int.TryParse(fields[2], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var pid) ||
            !long.TryParse(fields[3], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var started) ||
            !int.TryParse(fields[4], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var session) ||
            hwnd <= 0 || pid <= 0 || started <= 0 || session <= 0)
            return false;

        identity = new(hwnd, pid, started, session);
        return true;
    }

    // Recheck the actual HWND owner before any future inspection or UI action.
    // A handle alone is not an identity and can be reused after a window closes.
    internal static WindowResolveCode ResolveId(string? token, int session, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        if (!TryParseId(token, out var id)) return WindowResolveCode.INVALID_ARGUMENT;
        if (session <= 0 || id.SessionId != session) return WindowResolveCode.WRONG_SESSION;

        var candidate = new IntPtr(id.Handle);
        if (!IsWindow(candidate)) return WindowResolveCode.STALE_WINDOW;
        GetWindowThreadProcessId(candidate, out uint pid);
        if (pid == 0 || pid != id.ProcessId) return WindowResolveCode.STALE_WINDOW;
        try
        {
            using var process = Process.GetProcessById(id.ProcessId);
            if (process.SessionId != session || process.StartTime.ToFileTimeUtc() != id.StartFileTimeUtc)
                return WindowResolveCode.STALE_WINDOW;
        }
        catch (Win32Exception) { return WindowResolveCode.ACCESS_DENIED; }
        catch (System.Security.SecurityException) { return WindowResolveCode.ACCESS_DENIED; }
        catch (ArgumentException) { return WindowResolveCode.STALE_WINDOW; }
        catch (InvalidOperationException) { return WindowResolveCode.STALE_WINDOW; }

        // Recheck after process readback to reduce close/reuse races.
        if (!IsWindow(candidate)) return WindowResolveCode.STALE_WINDOW;
        GetWindowThreadProcessId(candidate, out pid);
        if (pid != id.ProcessId) return WindowResolveCode.STALE_WINDOW;
        hwnd = candidate;
        return WindowResolveCode.OK;
    }

    internal static WindowListResult List(int limit, int session)
    {
        var clock = Stopwatch.StartNew();
        var windows = new List<WindowSummary>(limit);
        var foreground = GetForegroundWindow();
        bool stoppedByLimit = false;
        EnumWindowsCallback callback = (hwnd, _) =>
        {
            if (windows.Count >= limit)
            {
                stoppedByLimit = true;
                return false;
            }
            if (!IsWindowVisible(hwnd)) return true;
            var titleLength = GetWindowTextLengthW(hwnd);
            if (titleLength <= 0) return true;
            var title = new StringBuilder(Math.Min(titleLength + 1, 1024));
            if (GetWindowTextW(hwnd, title, title.Capacity) == 0) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid > int.MaxValue) return true;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (process.SessionId != session) return true;
                var identity = new WindowIdentity(hwnd.ToInt64(), process.Id,
                    process.StartTime.ToFileTimeUtc(), session);
                windows.Add(new(FormatId(identity), identity.Handle, process.Id, process.ProcessName,
                    title.ToString(), session, hwnd == foreground));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or
                                      Win32Exception or System.Security.SecurityException)
            {
                // Window/process exited or cannot be inspected; never return an unverifiable ID.
            }
            return true;
        };
        bool enumerated = EnumWindows(callback, IntPtr.Zero);
        return new(enumerated || stoppedByLimit ? WindowListCode.COMPLETED : WindowListCode.ENUMERATION_FAILED,
            windows.ToArray(), session, ElapsedMs: clock.ElapsedMilliseconds);
    }

    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
