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
    [McpServerTool(Name = "windows.manage_window",UseStructuredContent = true),
     Description("Native Win32 ACTIVATE/RESTORE/MINIMIZE/MAXIMIZE/MOVE/RESIZE/CLOSE; exact HWND/process/session identity. CLOSE posts WM_CLOSE (no forced process termination), verified only when the exact window disappears.")]
    public static async Task<WindowManageResult> ManageWindow(string window_id,WindowManageAction action,
        int? x=null,int? y=null,int? width=null,int? height=null,CancellationToken cancellationToken=default)
    {
        if(!WindowCatalog.TryParseId(window_id,out _) || !WindowManager.Valid(action,x,y,width,height))
            return new(WindowManageCode.INVALID_ARGUMENT,window_id,action);
        var status=await AdminTools.Status(cancellationToken:cancellationToken);
        if(status.Status!=StatusCode.OK)return new(WindowManageCode.SERVICE_UNAVAILABLE,window_id,action);
        using var self=Process.GetCurrentProcess();
        if(status.ExecutionContext!=CommandContext.USER || status.UserContextAvailable!=true ||
            status.Frontend?.Elevated!=true || status.Frontend.SessionId!=self.SessionId)
            return new(WindowManageCode.USER_CONTEXT_UNAVAILABLE,window_id,action);
        var desktop=DesktopCapture.ObserveDesktop(self.SessionId);
        if(desktop.Code!=CaptureCode.COMPLETED)return new(WindowManageCode.DESKTOP_UNAVAILABLE,window_id,action);
        cancellationToken.ThrowIfCancellationRequested();
        return WindowManager.ExecuteAudited(window_id,action,x,y,width,height,self.SessionId);
    }

    [McpServerTool(Name = "windows.list_windows", UseStructuredContent = true),
     Description("List USER windows.")]
    public static async Task<WindowListResult> ListWindows(int max_windows = 50,
        CancellationToken cancellationToken = default)
    {
        if (max_windows is < 1 or > 100)
            return new(WindowListCode.INVALID_ARGUMENT, []);

        var status = await AdminTools.Status(cancellationToken: cancellationToken);
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
     Description("Inspect UIA or Chrome tabs.")]
    public static async Task<WindowInspectResult> InspectWindow(string window_id, int max_depth = 12,
        int max_elements = 100, string? start_path = null, string? start_element_id = null,
        int offset = 0, bool include_details = false, string? name_contains = null,
        WindowRoleFilter? role_filter = null, int wait_ms = 0,
        WindowInspectScope scope = WindowInspectScope.WINDOW, string? tab_id = null,
        string? expected_profile = null, string? expected_origin = null, string? expected_url = null,
        int? browser_window_id = null,
        CancellationToken cancellationToken = default)
    {
        if (!WindowInspector.ValidArguments(window_id, max_depth, max_elements,
            start_path, start_element_id, offset, include_details,
            name_contains, role_filter, wait_ms, scope, tab_id,
            expected_profile, expected_origin, expected_url, browser_window_id))
            return new(WindowInspectCode.INVALID_ARGUMENT, [], Detail: WindowInspector.ArgumentError(
                window_id, max_depth, max_elements, start_path, start_element_id, offset,
                include_details, name_contains, role_filter, wait_ms, scope, tab_id,
                expected_profile, expected_origin, expected_url, browser_window_id));
        var status = await AdminTools.Status(cancellationToken: cancellationToken);
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
            cancellationToken, start_path, start_element_id, offset, include_details,
            name_contains, role_filter, wait_ms, scope, tab_id,
            expected_profile, expected_origin, expected_url, browser_window_id);
    }

    [McpServerTool(Name = "windows.capture_window", UseStructuredContent = true),
     Description("Window PNG, no focus change.")]
    public static async Task<ModelContextProtocol.Protocol.CallToolResult> CaptureWindow(string window_id,
        int max_width = 1280, CancellationToken cancellationToken = default)
    {
        if (!WindowCapture.Valid(window_id, max_width))
            return WindowCapture.Response(new(WindowCaptureCode.INVALID_ARGUMENT));
        var status = await AdminTools.Status(cancellationToken: cancellationToken);
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
     Description("Act on a fresh UIA/Chrome element. Supply expected_name and expected_change for independently verified navigation. CDP_ONLY supports DRAG between two fresh DOM element IDs (target_element_id) and SET_FILES on an HTML file input (file_paths, 1..16 local absolute paths); these never use OS input. BACKGROUND_ONLY forbids our own OS input/activation but a UIA provider may briefly focus its window. CDP_ONLY requires consented Chrome and never falls back to UIA/SendInput. ALLOW_FOREGROUND_INPUT explicitly permits native pointer/keyboard input. EFFECT_UNKNOWN means inspect state, never replay blindly; inspect effect_evidence, next_step and focus_behavior.")]
    public static async Task<WindowActionResult> InvokeElement(string window_id, string element_id,
        WindowActionKind action, string? value = null, bool confirm = true,
        string? element_path = null, string? expected_name = null,
        WindowExpectedChange? expected_change = null, WindowKey? key = null,
        WindowExpectedKind expected_kind = WindowExpectedKind.UIA_ELEMENT,
        string? expected_value = null, string? expected_profile = null,
        string? expected_origin = null, string? expected_url = null,
        int observation_timeout_ms = 8000,
        string? expected_enabled_name = null, bool expected_enabled = true,
        int? virtual_key = null, bool ctrl = false, bool alt = false, bool shift = false, bool win = false,
        WindowScrollDirection? scroll_direction = null, int scroll_units = 1,
        int? target_x = null, int? target_y = null,
        string? locator_automation_id = null, string? locator_name = null,
        WindowRoleFilter? locator_role = null,
        WindowInputMode input_mode = WindowInputMode.BACKGROUND_ONLY,
        string? target_element_id = null, string[]? file_paths = null,
        int? browser_window_id = null,
        CancellationToken cancellationToken = default)
    {
        var request = new WindowActionRequest(window_id, element_id, action, value,
            element_path, expected_name, expected_change, key, expected_kind,
            expected_value, expected_profile, expected_origin, expected_url, observation_timeout_ms,
            expected_enabled_name, expected_enabled, virtual_key, ctrl, alt, shift, win,
            scroll_direction, scroll_units, target_x, target_y,
            locator_automation_id, locator_name, locator_role,
            InputMode: input_mode, TargetElementId: target_element_id, FilePaths: file_paths,
            BrowserWindowId: browser_window_id);
        if (!WindowAction.Valid(request)) return new(WindowAction.InvalidCode(request));
        if (!confirm) return new(WindowActionCode.CONFIRMATION_REQUIRED);

        var status = await AdminTools.Status(cancellationToken: cancellationToken);
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

    [McpServerTool(Name="windows.invoke_locator",UseStructuredContent=true),
     Description("Find one exact accessible element and act once. For navigation set expected_name/expected_change to the new screen's observable state. BACKGROUND_ONLY may trigger provider-owned UIA focus despite no OS input; CDP_ONLY forbids native UIA actions, while ALLOW_FOREGROUND_INPUT permits explicit pointer/keyboard input. Read effect_evidence, next_step, focus_behavior; never replay EFFECT_UNKNOWN.")]
    public static Task<WindowActionResult> InvokeLocator(string window_id,WindowActionKind action,
        string? automation_id=null,string? name=null,WindowRoleFilter? role=null,
        string? value=null,bool confirm=true,
        string? expected_name=null,WindowExpectedChange? expected_change=null,
        WindowKey? key=null,WindowExpectedKind expected_kind=WindowExpectedKind.UIA_ELEMENT,
        string? expected_value=null,string? expected_profile=null,
        int observation_timeout_ms=8000,int? virtual_key=null,
        bool ctrl=false,bool alt=false,bool shift=false,bool win=false,
        WindowScrollDirection? scroll_direction=null,int scroll_units=1,
        int? target_x=null,int? target_y=null,
        WindowInputMode input_mode=WindowInputMode.BACKGROUND_ONLY,
        string? target_element_id=null,string[]? file_paths=null,
        CancellationToken cancellationToken=default)=>
        InvokeElement(window_id,"locator",action,value,confirm,
            expected_name:expected_name,expected_change:expected_change,key:key,
            expected_kind:expected_kind,expected_value:expected_value,
            expected_profile:expected_profile,observation_timeout_ms:observation_timeout_ms,
            virtual_key:virtual_key,ctrl:ctrl,alt:alt,shift:shift,win:win,
            scroll_direction:scroll_direction,scroll_units:scroll_units,
            target_x:target_x,target_y:target_y,
            locator_automation_id:automation_id,locator_name:name,locator_role:role,
            input_mode:input_mode,target_element_id:target_element_id,file_paths:file_paths,
            cancellationToken:cancellationToken);
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

    // Poll the title of the *same* verified HWND without enumerating unrelated
    // windows. A tab switch must not be confirmed by another browser window.
    internal static bool TryReadTitle(string windowId, int session, out string title)
    {
        title = string.Empty;
        if (ResolveId(windowId, session, out var hwnd) != WindowResolveCode.OK)
            return false;
        int length = GetWindowTextLengthW(hwnd);
        if (length <= 0) return false;
        var buffer = new StringBuilder(Math.Min(length + 1, 1024));
        if (GetWindowTextW(hwnd, buffer, buffer.Capacity) <= 0) return false;
        title = buffer.ToString();
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
