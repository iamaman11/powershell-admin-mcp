using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Automation;

[JsonConverter(typeof(JsonStringEnumConverter<WindowActionKind>))]
public enum WindowActionKind { INVOKE, SET_VALUE, SELECT, TOGGLE }

[JsonConverter(typeof(JsonStringEnumConverter<WindowActionCode>))]
public enum WindowActionCode
{
    COMPLETED, INVALID_ARGUMENT, CONFIRMATION_REQUIRED, SERVICE_UNAVAILABLE,
    USER_CONTEXT_UNAVAILABLE, DESKTOP_UNAVAILABLE, STALE_WINDOW, STALE_ELEMENT,
    ACCESS_DENIED, DISABLED, PASSWORD_DENIED, UNSUPPORTED_ACTION,
    UIA_UNAVAILABLE, AUDIT_UNAVAILABLE, AUDIT_RESULT_FAILED,
    TIMEOUT_UNKNOWN_EFFECT, ACTION_FAILED_UNKNOWN_EFFECT, WORKER_FAILED_UNKNOWN_EFFECT
}

internal sealed record WindowActionRequest(
    [property: JsonPropertyName("window_id")] string WindowId,
    [property: JsonPropertyName("element_id")] string ElementId,
    [property: JsonPropertyName("action")] WindowActionKind Action,
    [property: JsonPropertyName("value")] string? Value);

public sealed record WindowActionResult(
    [property: JsonPropertyName("status")] WindowActionCode Status,
    [property: JsonPropertyName("audit_id")] string? AuditId = null,
    [property: JsonPropertyName("effect_unknown")] bool EffectUnknown = false,
    [property: JsonPropertyName("foreground_changed")] bool? ForegroundChanged = null,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("elapsed_ms")] long? ElapsedMs = null);

// The disposable child uses native UIA2 with AutoSetFocus disabled. An untrusted
// provider can still change focus on its own; report the observed result.
internal static class WindowAction
{
    internal static bool Valid(WindowActionRequest request) =>
        WindowCatalog.TryParseId(request.WindowId, out _) &&
        WindowInspector.TryParseElementRef(request.ElementId, out _) &&
        Enum.IsDefined(request.Action) &&
        (request.Action == WindowActionKind.SET_VALUE ?
            request.Value is { Length: <= 4096 } && request.Value?.Contains('\0') == false :
            request.Value is null);

    internal static WindowActionResult InvokeInWorker(WindowActionRequest request)
    {
        if (!Valid(request)) return new(WindowActionCode.INVALID_ARGUMENT);
        using var self = Process.GetCurrentProcess();
        var desktop = DesktopCapture.ObserveDesktop(self.SessionId);
        if (desktop.Code != CaptureCode.COMPLETED)
            return new(WindowActionCode.DESKTOP_UNAVAILABLE, Detail: desktop.Code.ToString());
        var resolved = WindowCatalog.ResolveId(request.WindowId, self.SessionId, out var hwnd);
        if (resolved != WindowResolveCode.OK)
            return new(resolved == WindowResolveCode.ACCESS_DENIED ? WindowActionCode.ACCESS_DENIED :
                resolved == WindowResolveCode.WRONG_SESSION ? WindowActionCode.USER_CONTEXT_UNAVAILABLE :
                WindowActionCode.STALE_WINDOW, Detail: resolved.ToString());
        return InvokeCore(hwnd, request);
    }

    internal static WindowActionResult InvokeCore(IntPtr hwnd, WindowActionRequest request)
    {
        var clock = Stopwatch.StartNew();
        if (hwnd == IntPtr.Zero || !Valid(request))
            return new(WindowActionCode.INVALID_ARGUMENT, ElapsedMs: clock.ElapsedMilliseconds);
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return new(WindowActionCode.UIA_UNAVAILABLE);
            var walker = TreeWalker.ControlViewWalker;
            var stack = new Stack<(AutomationElement Element, int Depth, int[] Path)>();
            stack.Push((root, 0, []));
            WindowInspector.TryParseElementRef(request.ElementId, out long snapshotMs);
            int inspected = 0;
            while (stack.Count > 0 && inspected < 100)
            {
                var (element, depth, path) = stack.Pop();
                inspected++;
                AutomationElement.AutomationElementInformation current;
                try { current = element.Current; }
                catch (ElementNotAvailableException) { continue; }
                string candidate;
                try { candidate = WindowInspector.ElementRef(element, request.WindowId, snapshotMs); }
                catch (ElementNotAvailableException) { continue; }
                if (string.Equals(candidate, request.ElementId, StringComparison.Ordinal))
                {
                    if (current.IsPassword) return new(WindowActionCode.PASSWORD_DENIED);
                    if (!current.IsEnabled) return new(WindowActionCode.DISABLED);
                    if (Pattern(element, current.ControlType, request.Action) is null)
                        return new(WindowActionCode.UNSUPPORTED_ACTION);
                    // WinForms radios may report Select() success without checking
                    // the button when AutoSetFocus is off. Invoke is the equivalent
                    // accessible activation when the radio exposes that pattern.
                    bool radioInvoke = request.Action == WindowActionKind.SELECT &&
                        current.ControlType == ControlType.RadioButton &&
                        element.TryGetCurrentPattern(InvokePattern.Pattern, out _);
                    // Match the exact runtime ID again in the native UIA2 view before acting.
                    // Never fall back to managed Invoke/SetValue: those default to setting focus.
                    return WindowActionNoFocus.Invoke(hwnd, path, element.GetRuntimeId(), request,
                        clock, radioInvoke);
                }
                if (depth >= WindowInspector.MaxDepth) continue;
                var children = new List<AutomationElement>();
                try
                {
                    var child = walker.GetFirstChild(element);
                    int remaining = Math.Max(0, 100 - inspected - stack.Count);
                    while (child is not null && children.Count < remaining)
                    {
                        children.Add(child);
                        child = walker.GetNextSibling(child);
                    }
                }
                catch (ElementNotAvailableException) { continue; }
                for (int i = children.Count - 1; i >= 0; i--)
                    stack.Push((children[i], depth + 1, [.. path, i]));
            }
            return new(WindowActionCode.STALE_ELEMENT, ElapsedMs: clock.ElapsedMilliseconds);
        }
        catch (ElementNotAvailableException) { return new(WindowActionCode.STALE_ELEMENT); }
        catch (COMException e) { return new(WindowActionCode.UIA_UNAVAILABLE, Detail: e.HResult.ToString("X8")); }
        catch (InvalidOperationException) { return new(WindowActionCode.UIA_UNAVAILABLE); }
    }

    private static object? Pattern(AutomationElement element, ControlType role, WindowActionKind kind)
    {
        AutomationPattern? id = kind switch
        {
            WindowActionKind.INVOKE when role == ControlType.Button ||
                role == ControlType.Hyperlink || role == ControlType.MenuItem => InvokePattern.Pattern,
            WindowActionKind.SET_VALUE when role == ControlType.Edit => ValuePattern.Pattern,
            WindowActionKind.SELECT when role == ControlType.ListItem ||
                role == ControlType.TabItem || role == ControlType.RadioButton => SelectionItemPattern.Pattern,
            WindowActionKind.TOGGLE when role == ControlType.CheckBox => TogglePattern.Pattern,
            _ => null
        };
        return id is not null && element.TryGetCurrentPattern(id, out var value) ? value : null;
    }

    internal static async Task<WindowActionResult> RunIsolatedAsync(WindowActionRequest request,
        CancellationToken cancellationToken)
    {
        if (!Valid(request)) return new(WindowActionCode.INVALID_ARGUMENT);
        var auditId = Guid.NewGuid().ToString("N");
        if (!WriteAudit(auditId, "INTENT", request, null))
            return new(WindowActionCode.AUDIT_UNAVAILABLE, AuditId: auditId);
        WindowActionResult result;
        bool launched = false;
        using var process = new Process();
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            result = new(WindowActionCode.WORKER_FAILED_UNKNOWN_EFFECT, EffectUnknown: true);
        else
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardInput = true, RedirectStandardError = false, CreateNoWindow = true
            };
            start.ArgumentList.Add("window-action-worker");
            start.Environment.Remove("CONTROL_PLANE_API_KEY");
            start.Environment.Remove("OPENAI_API_KEY");
            process.StartInfo = start;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                if (!process.Start()) throw new Win32Exception("Worker launch failed");
                launched = true;
                string payload = JsonSerializer.Serialize(request, PipeWire.Json);
                await process.StandardInput.WriteAsync(payload.AsMemory(), deadline.Token);
                await process.StandardInput.FlushAsync(deadline.Token);
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
                string json = await output;
                if (json.Length > 2048) throw new JsonException("OUTPUT_LIMIT");
                result = JsonSerializer.Deserialize<WindowActionResult>(json, PipeWire.Json) ??
                    new(WindowActionCode.WORKER_FAILED_UNKNOWN_EFFECT, EffectUnknown: true);
            }
            catch (OperationCanceledException)
            {
                Stop(process, launched);
                result = new(WindowActionCode.TIMEOUT_UNKNOWN_EFFECT, EffectUnknown: true);
            }
            catch (Exception e) when (e is Win32Exception or IOException or JsonException or InvalidOperationException)
            {
                Stop(process, launched);
                result = new(WindowActionCode.WORKER_FAILED_UNKNOWN_EFFECT, EffectUnknown: launched,
                    Detail: e.GetType().Name);
            }
        }
        if (!WriteAudit(auditId, "RESULT", request, result.Status))
            return new(WindowActionCode.AUDIT_RESULT_FAILED, AuditId: auditId, EffectUnknown: true);
        return result with { AuditId = auditId };
    }

    private static void Stop(Process process, bool launched)
    {
        if (!launched) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
    }

    // Two Windows Application event records, never the supplied field value.
    // Audit failure before dispatch blocks action. No new log service or DB.
    private static bool WriteAudit(string id, string phase, WindowActionRequest req, WindowActionCode? status)
    {
        try
        {
            string windowHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(req.WindowId)).AsSpan(0, 12));
            string record = JsonSerializer.Serialize(new
            {
                component = "WindowsAdminMCP.window_action", audit_id = id, phase,
                window_hash = windowHash, element_id = req.ElementId,
                action = req.Action.ToString(), result = status?.ToString(),
                utc = DateTimeOffset.UtcNow
            }, PipeWire.Json);
            EventLog.WriteEntry("WindowsAdminMCP", record, EventLogEntryType.Information, 8101);
            return true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or
                                 System.Security.SecurityException or ArgumentException)
        { return false; }
    }

}
