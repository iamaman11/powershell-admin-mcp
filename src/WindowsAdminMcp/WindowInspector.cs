using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Automation;

[JsonConverter(typeof(JsonStringEnumConverter<WindowInspectCode>))]
public enum WindowInspectCode
{
    COMPLETED, INVALID_ARGUMENT, SERVICE_UNAVAILABLE, USER_CONTEXT_UNAVAILABLE, DESKTOP_UNAVAILABLE,
    STALE_WINDOW, STALE_ELEMENT, ACCESS_DENIED, UIA_UNAVAILABLE, TIMEOUT, WORKER_FAILED
}

public sealed record WindowElement(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("parent_index")] int? ParentIndex,
    [property: JsonPropertyName("depth")] int Depth,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("automation_id")] string AutomationId,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("offscreen")] bool Offscreen,
    [property: JsonPropertyName("password")] bool Password,
    [property: JsonPropertyName("patterns")] string[] Patterns,
    [property: JsonPropertyName("element_id")] string ElementId);

public sealed record WindowInspectResult(
    [property: JsonPropertyName("status")] WindowInspectCode Status,
    [property: JsonPropertyName("elements")] WindowElement[] Elements,
    [property: JsonPropertyName("truncated")] bool Truncated = false,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("elapsed_ms")] long? ElapsedMs = null,
    [property: JsonPropertyName("element_ref_expires_utc")] DateTimeOffset? ElementRefExpiresUtc = null,
    [property: JsonPropertyName("next_offset")] int? NextOffset = null);

// UI Automation can block inside a third-party provider. Run it in a bounded,
// on-demand copy of this executable, never on the MCP request/USER worker thread.
internal static class WindowInspector
{
    internal const int ElementRefSeconds = 30;
    // Chromium's page document can be nested well below its browser chrome.
    // Keep node count and the isolated worker deadline unchanged.
    internal const int MaxDepth = 16;
    internal const int MaxTraversal = 2000;
    private const long FutureSkewMs = 5000;

    // Runtime IDs are ephemeral. A fresh token also binds the window identity
    // and control metadata. It is an identity check, never an authorization.
    internal static string ElementRef(AutomationElement element, string windowId, long unixMs)
    {
        int[] runtimeId = element.GetRuntimeId();
        var current = element.Current;
        string fingerprint = windowId + "|" + string.Join(",", runtimeId) + "|" +
            current.ControlType.ProgrammaticName + "|" + current.AutomationId + "|" + current.Name;
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint));
        return "e1:" + unixMs.ToString("x", CultureInfo.InvariantCulture) + ":" +
            Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }

    internal static bool TryParseElementRef(string? text, out long unixMs)
    {
        unixMs = 0;
        if (text is null || text.Length > 80) return false;
        var fields = text.Split(':');
        if (fields.Length != 3 || fields[0] != "e1" || fields[2].Length != 32 ||
            !fields[2].All(Uri.IsHexDigit) ||
            !long.TryParse(fields[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out unixMs))
            return false;
        long age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - unixMs;
        return age >= -FutureSkewMs && age <= ElementRefSeconds * 1000L;
    }

    internal static bool TryParsePath(string? path, out int[] indices)
    {
        indices = [];
        if (string.IsNullOrEmpty(path) || path.Length > 96) return false;
        var pieces = path.Split('.');
        if (pieces.Length is < 1 or > MaxDepth + 1 || pieces[0] != "0") return false;
        var result = new int[pieces.Length - 1];
        for (int i = 1; i < pieces.Length; i++)
        {
            if (!int.TryParse(pieces[i], NumberStyles.None, CultureInfo.InvariantCulture, out int index) ||
                index is < 0 or > MaxTraversal ||
                index.ToString(CultureInfo.InvariantCulture) != pieces[i]) return false;
            result[i - 1] = index;
        }
        indices = result;
        return true;
    }

    internal static bool ValidArguments(string? windowId, int maxDepth, int maxElements,
        string? startPath = null, string? startElementId = null, int offset = 0) =>
        WindowCatalog.TryParseId(windowId, out _) && maxDepth is >= 0 and <= MaxDepth &&
        maxElements is >= 1 and <= 100 && offset >= 0 && offset + maxElements <= MaxTraversal &&
        (startPath is null && startElementId is null ||
         startPath is not null && startElementId is not null &&
         TryParsePath(startPath, out var indices) && indices.Length <= maxDepth &&
         TryParseElementRef(startElementId, out _));

    internal static WindowInspectResult InspectInWorker(string windowId, int maxDepth, int maxElements,
        string? startPath = null, string? startElementId = null, int offset = 0)
    {
        if (!ValidArguments(windowId, maxDepth, maxElements, startPath, startElementId, offset))
            return new(WindowInspectCode.INVALID_ARGUMENT, []);
        using var self = Process.GetCurrentProcess();
        var desktop = DesktopCapture.ObserveDesktop(self.SessionId);
        if (desktop.Code != CaptureCode.COMPLETED)
            return new(WindowInspectCode.DESKTOP_UNAVAILABLE, [], Detail: desktop.Code.ToString());
        var resolved = WindowCatalog.ResolveId(windowId, self.SessionId, out var hwnd);
        if (resolved != WindowResolveCode.OK)
            return new(resolved == WindowResolveCode.ACCESS_DENIED ? WindowInspectCode.ACCESS_DENIED :
                resolved == WindowResolveCode.WRONG_SESSION ? WindowInspectCode.USER_CONTEXT_UNAVAILABLE :
                WindowInspectCode.STALE_WINDOW, [], Detail: resolved.ToString());
        return InspectCore(hwnd, maxDepth, maxElements, windowId, startPath, startElementId, offset);
    }

    // Paths are never authority on their own. Callers must also provide the
    // fresh fingerprint returned for that exact node by a previous inspection.
    internal static AutomationElement? ResolvePath(AutomationElement root,
        TreeWalker walker, int[] indices)
    {
        var current = root;
        foreach (int index in indices)
        {
            var child = walker.GetFirstChild(current);
            for (int i = 0; i < index && child is not null; i++)
                child = walker.GetNextSibling(child);
            if (child is null) return null;
            current = child;
        }
        return current;
    }

    internal static WindowInspectResult InspectCore(IntPtr hwnd, int maxDepth, int maxElements,
        string? windowId = null, string? startPath = null, string? startElementId = null, int offset = 0)
    {
        var clock = Stopwatch.StartNew();
        if (hwnd == IntPtr.Zero || maxDepth is < 0 or > MaxDepth || maxElements is < 1 or > 100 ||
            offset < 0 || offset + maxElements > MaxTraversal ||
            (startPath is not null || startElementId is not null) &&
            (windowId is null || !ValidArguments(windowId, maxDepth, maxElements,
                startPath, startElementId, offset)))
            return new(WindowInspectCode.INVALID_ARGUMENT, [], ElapsedMs: clock.ElapsedMilliseconds);
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return new(WindowInspectCode.UIA_UNAVAILABLE, [], ElapsedMs: clock.ElapsedMilliseconds);
            var walker = TreeWalker.ControlViewWalker;
            string rootPath = "0";
            int rootDepth = 0;
            if (startPath is not null)
            {
                TryParsePath(startPath, out var path);
                var selected = ResolvePath(root, walker, path);
                if (selected is null) return new(WindowInspectCode.STALE_ELEMENT, []);
                TryParseElementRef(startElementId, out long previousSnapshot);
                if (!string.Equals(ElementRef(selected, windowId!, previousSnapshot), startElementId,
                    StringComparison.Ordinal)) return new(WindowInspectCode.STALE_ELEMENT, []);
                root = selected;
                rootPath = startPath;
                rootDepth = path.Length;
            }
            long snapshotMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var nodes = new List<WindowElement>(maxElements);
            var indices = new Dictionary<string, int>(StringComparer.Ordinal);
            var stack = new Stack<(AutomationElement Element, string Path, int Depth, string? ParentPath)>();
            stack.Push((root, rootPath, rootDepth, null));
            bool truncated = false;
            int visited = 0;
            while (stack.Count != 0 && nodes.Count < maxElements && visited < MaxTraversal)
            {
                var item = stack.Pop();
                AutomationElement.AutomationElementInformation current;
                try { current = item.Element.Current; }
                catch (ElementNotAvailableException) { continue; }
                if (visited++ >= offset)
                {
                    bool password = current.IsPassword;
                    bool valueBearing = current.ControlType == ControlType.Edit ||
                        current.ControlType == ControlType.Document;
                    string name = password || valueBearing ? "[redacted]" : Bounded(current.Name);
                    string role = Bounded(current.ControlType?.ProgrammaticName);
                    string automationId = password ? "" : Bounded(current.AutomationId);
                    string[] patterns;
                    try { patterns = item.Element.GetSupportedPatterns().Take(12)
                        .Select(p => Bounded(p.ProgrammaticName, 64)).ToArray(); }
                    catch (ElementNotAvailableException) { patterns = []; }
                    string elementId = "";
                    if (windowId is not null)
                    {
                        try { elementId = ElementRef(item.Element, windowId, snapshotMs); }
                        catch (ElementNotAvailableException) { continue; }
                    }
                    int? parentIndex = item.ParentPath is not null &&
                        indices.TryGetValue(item.ParentPath, out int found) ? found : null;
                    indices[item.Path] = nodes.Count;
                    nodes.Add(new(item.Path, parentIndex, item.Depth, role, name, automationId,
                        current.IsEnabled, current.IsOffscreen, password, patterns, elementId));
                }
                if (item.Depth >= maxDepth) continue;

                var children = new List<AutomationElement>();
                try
                {
                    var child = walker.GetFirstChild(item.Element);
                    // No page can consume more than offset + maxElements
                    // siblings at a single level. Avoid probing thousands of
                    // Chrome tabs just to return the first 100 controls.
                    while (child is not null && children.Count < offset + maxElements)
                    {
                        children.Add(child);
                        child = walker.GetNextSibling(child);
                    }
                    if (child is not null) truncated = true;
                }
                catch (ElementNotAvailableException) { continue; }
                for (int i = children.Count - 1; i >= 0; i--)
                    stack.Push((children[i], item.Path + "." + i, item.Depth + 1, item.Path));
            }
            return new(WindowInspectCode.COMPLETED, nodes.ToArray(), truncated || stack.Count != 0,
                ElapsedMs: clock.ElapsedMilliseconds,
                ElementRefExpiresUtc:windowId is null ? null :
                    DateTimeOffset.FromUnixTimeMilliseconds(snapshotMs).AddSeconds(ElementRefSeconds),
                NextOffset: (truncated || stack.Count != 0) && nodes.Count > 0 ? offset + nodes.Count : null);
        }
        catch (ElementNotAvailableException) { return new(WindowInspectCode.STALE_WINDOW, [], ElapsedMs: clock.ElapsedMilliseconds); }
        catch (COMException e) { return new(WindowInspectCode.UIA_UNAVAILABLE, [], Detail: e.HResult.ToString("X8"), ElapsedMs: clock.ElapsedMilliseconds); }
        catch (InvalidOperationException) { return new(WindowInspectCode.UIA_UNAVAILABLE, [], ElapsedMs: clock.ElapsedMilliseconds); }
    }

    internal static async Task<WindowInspectResult> RunIsolatedAsync(string windowId, int maxDepth, int maxElements,
        CancellationToken cancellationToken, string? startPath = null, string? startElementId = null, int offset = 0)
    {
        if (!ValidArguments(windowId, maxDepth, maxElements, startPath, startElementId, offset))
            return new(WindowInspectCode.INVALID_ARGUMENT, []);
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return new(WindowInspectCode.WORKER_FAILED, []);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("window-inspect-worker");
        start.ArgumentList.Add(windowId);
        start.ArgumentList.Add(maxDepth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(maxElements.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(startPath ?? "-");
        start.ArgumentList.Add(startElementId ?? "-");
        start.ArgumentList.Add(offset.ToString(CultureInfo.InvariantCulture));
        start.Environment.Remove("CONTROL_PLANE_API_KEY");
        start.Environment.Remove("OPENAI_API_KEY");
        using var process = new Process { StartInfo = start };
        bool launched = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (!process.Start()) return new(WindowInspectCode.WORKER_FAILED, []);
            launched = true;
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            string json = await output;
            if (json.Length > 256000) return new(WindowInspectCode.WORKER_FAILED, [], Detail: "OUTPUT_LIMIT");
            return JsonSerializer.Deserialize<WindowInspectResult>(json, PipeWire.Json) ??
                new(WindowInspectCode.WORKER_FAILED, [], Detail: "EMPTY_RESULT");
        }
        catch (OperationCanceledException)
        {
            StopWorker(process, launched);
            return new(cancellationToken.IsCancellationRequested ? WindowInspectCode.WORKER_FAILED : WindowInspectCode.TIMEOUT, []);
        }
        catch (Exception e) when (e is Win32Exception or IOException or JsonException or InvalidOperationException)
        {
            StopWorker(process, launched);
            return new(WindowInspectCode.WORKER_FAILED, [], Detail: e.GetType().Name);
        }
    }

    private static void StopWorker(Process process, bool launched)
    {
        if (!launched) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
    }

    private static string Bounded(string? value, int limit = 128)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var bounded = value.Length > limit ? value[..limit] : value;
        return bounded.Replace('\r', ' ').Replace('\n', ' ');
    }
}
