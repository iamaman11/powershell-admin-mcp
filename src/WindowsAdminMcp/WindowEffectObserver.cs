using System.Diagnostics;
using System.Windows.Automation;

// Read-only postcondition checks live in the disposable UIA worker. The
// dispatch path runs at most once, even when the provider reports an error.
internal sealed record WindowEffectBaseline(string[] Identities);

internal static class WindowEffectObserver
{
    internal static WindowEffectBaseline Capture(WindowActionRequest request)
    {
        if (request.ExpectedName is null) return new([]);
        return request.ExpectedKind switch
        {
            WindowExpectedKind.PROCESS_STARTED => new(ProcessIdentities(request.ExpectedName)),
            WindowExpectedKind.WINDOW_CREATED => new(WindowIdentities(request.ExpectedName)),
            _ => new([])
        };
    }

    internal static bool Check(WindowActionRequest request, WindowEffectBaseline baseline)
    {
        if (request.ExpectedKind is WindowExpectedKind.PROCESS_STARTED or WindowExpectedKind.WINDOW_CREATED)
        {
            string[] current = request.ExpectedKind == WindowExpectedKind.PROCESS_STARTED
                ? ProcessIdentities(request.ExpectedName!) : WindowIdentities(request.ExpectedName!);
            return current.Except(baseline.Identities, StringComparer.Ordinal).Any();
        }
        if (request.ExpectedKind == WindowExpectedKind.WINDOW_TITLE)
            return WindowAction.WindowTitlePresent(request.WindowId, request.ExpectedName!);
        if (request.ExpectedKind == WindowExpectedKind.PROCESS_NAME)
            return WindowAction.ExternalStatePresent(request.ExpectedKind, request.ExpectedName!);
        using var currentProcess = Process.GetCurrentProcess();
        if (WindowCatalog.ResolveId(request.WindowId, currentProcess.SessionId, out var hwnd) != WindowResolveCode.OK)
            return false;
        var root = AutomationElement.FromHandle(hwnd);
        return root is not null && WindowAction.ExpectedStatePresent(root, request);
    }

    internal static bool EnabledState(string windowId, string name, bool enabled)
    {
        using var current = Process.GetCurrentProcess();
        if (WindowCatalog.ResolveId(windowId, current.SessionId, out var hwnd) != WindowResolveCode.OK)
            return false;
        var root = AutomationElement.FromHandle(hwnd);
        return root is not null && WindowAction.ExpectedEnabledPresent(root, name, enabled);
    }

    private static string[] WindowIdentities(string title)
    {
        using var current = Process.GetCurrentProcess();
        return WindowCatalog.List(100, current.SessionId).Windows
            .Where(w => string.Equals(w.Title, title, StringComparison.Ordinal))
            .Select(w => w.WindowId).ToArray();
    }

    private static string[] ProcessIdentities(string name)
    {
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        using var current = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName(name);
        try
        {
            var result = new List<string>();
            foreach (var process in processes)
            {
                try
                {
                    if (process.SessionId == current.SessionId)
                        result.Add($"{process.Id}:{process.StartTime.ToFileTimeUtc()}");
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                { /* A process exited or is inaccessible; it cannot confirm an effect. */ }
            }
            return result.ToArray();
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
