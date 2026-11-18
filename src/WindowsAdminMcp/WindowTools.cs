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
}

internal readonly record struct WindowIdentity(long Handle, int ProcessId, long StartFileTimeUtc, int SessionId);

internal static class WindowCatalog
{
    internal static string FormatId(WindowIdentity id) =>
        $"w1:{id.Handle:x}:{id.ProcessId:x}:{id.StartFileTimeUtc:x}:{id.SessionId:x}";

    internal static bool TryParseId(string? text, out WindowIdentity identity)
    {
        identity = default;
        if (text is null) return false;
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
            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid > int.MaxValue) return true;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (process.SessionId != session) return true;
                var identity = new WindowIdentity(hwnd.ToInt64(), process.Id,
                    process.StartTime.ToFileTimeUtc(), session);
                windows.Add(new(FormatId(identity), process.Id, process.ProcessName,
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
    private static extern IntPtr GetForegroundWindow();
}
