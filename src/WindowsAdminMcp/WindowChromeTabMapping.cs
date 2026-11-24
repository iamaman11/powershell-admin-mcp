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

// Match a specific native Chrome window to its CDP tabs.
internal static partial class WindowChromeCdp
{
    private const int BoundsTolerance = 40;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private static bool MatchesBounds(WindowBounds native, WindowBounds chrome, double dpi)
    {
        static bool Close(int a, double b) => Math.Abs(a - b) <= BoundsTolerance;
        bool At(double scale) => Close(native.X, chrome.X * scale) &&
            Close(native.Y, chrome.Y * scale) && Close(native.Width, chrome.Width * scale) &&
            Close(native.Height, chrome.Height * scale);
        return chrome.Width > 0 && chrome.Height > 0 && (At(1) || dpi is >= 1 and <= 4 && At(dpi));
    }

    internal enum CdpWindowMappingState { NOT_FOUND, AMBIGUOUS }

    private sealed class CdpWindowMappingException(CdpWindowMappingState state,
        WindowCdpMappingFailure failure, int titleCandidates, int fingerprintCandidates)
        : InvalidOperationException(state == CdpWindowMappingState.NOT_FOUND
            ? "CDP_WINDOW_NO_MATCH" : "CDP_WINDOW_NOT_UNIQUE")
    {
        internal CdpWindowMappingState State { get; } = state;
        internal WindowCdpMappingFailure Failure { get; } = failure;
        internal int TitleCandidates { get; } = titleCandidates;
        internal int FingerprintCandidates { get; } = fingerprintCandidates;
    }

    internal static WindowInspectCode InspectMappingCode(CdpWindowMappingState state) =>
        state == CdpWindowMappingState.NOT_FOUND ? WindowInspectCode.CDP_TARGET_NOT_FOUND :
        WindowInspectCode.CDP_TARGET_AMBIGUOUS;

    internal static bool TryTargetContextId(JsonElement target, out string? contextId)
    {
        contextId = null;
        if (!target.TryGetProperty("browserContextId", out var value)) return true;
        if (value.ValueKind != JsonValueKind.String) return false;
        contextId = value.GetString();
        return contextId is { Length: >= 1 and <= 512 } &&
            !string.IsNullOrWhiteSpace(contextId);
    }

    // Browser.getWindowForTarget yields the Chrome window identity. Targets in
    // one window may expose different optional browserContextId values; the
    // context is not the HWND identity and cannot make one window ambiguous.
    // Reject duplicate *window IDs* after native title + bounds validation.
    internal static int? UniqueWindowId(
        IEnumerable<(int WindowId, string? ContextId)> candidates) =>
        UniqueWindowId(candidates, out _);

    internal static int? UniqueWindowId(
        IEnumerable<(int WindowId, string? ContextId)> candidates, out int distinctCount)
    {
        var unique = candidates.Where(c => c.WindowId > 0 &&
                (c.ContextId is null || c.ContextId is { Length: >= 1 and <= 512 } &&
                    !string.IsNullOrWhiteSpace(c.ContextId)))
            .Select(c => c.WindowId).Distinct().Take(2).ToArray();
        distinctCount = unique.Length;
        return unique.Length == 1 ? unique[0] : null;
    }

    // Chrome accessibility labels can decorate a tab title with its memory
    // state or selected state. Extract only the exact quoted title; never do
    // fuzzy substring matching against arbitrary web page controls.
    internal static string? ChromeTabLabel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (string prefix in new[] { "Вкладка \"", "Tab \"" })
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            int end = name.IndexOf('"', prefix.Length);
            return end > prefix.Length ? name[prefix.Length..end] : null;
        }
        return name;
    }

    // A page can contain ARIA tab items. Only one sibling tab-strip group
    // containing the native window's *active tab title* can be evidence.
    internal static string[]? NativeTabStripTitles(
        IEnumerable<(string Path, string Name)> tabItems, string nativeWindowTitle)
    {
        const string suffix = " - Google Chrome";
        if (!nativeWindowTitle.EndsWith(suffix, StringComparison.Ordinal)) return null;
        string activeTitle = nativeWindowTitle[..^suffix.Length];
        var groups = tabItems.Select(t =>
        {
            int dot = t.Path.LastIndexOf('.');
            return (Parent: dot > 0 ? t.Path[..dot] : "", Title: ChromeTabLabel(t.Name));
        }).Where(t => t.Parent.Length > 0 && t.Title is not null)
          .GroupBy(t => t.Parent, StringComparer.Ordinal)
          .Where(g => g.Any(t => string.Equals(t.Title, activeTitle, StringComparison.Ordinal)))
          .Take(2).ToArray();
        return groups.Length == 1 ? groups[0].Select(t => t.Title!)
            .Distinct(StringComparer.Ordinal).ToArray() : null;
    }

    // An overlapping/maximized second Chrome window can have the SAME active
    // page title. Disambiguate only when a native tab-strip title belongs to
    // exactly one candidate CDP window; competing exclusive matches fail shut.
    internal static int? UniqueTabFingerprintWindow(IEnumerable<int> candidateWindowIds,
        IEnumerable<(int WindowId, string Title)> pageTitles, IEnumerable<string> nativeTabTitles)
    {
        var candidates = candidateWindowIds.Where(id => id > 0).ToHashSet();
        var native = nativeTabTitles.ToHashSet(StringComparer.Ordinal);
        var owners = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var page in pageTitles)
        {
            if (!candidates.Contains(page.WindowId) || !native.Contains(page.Title)) continue;
            if (!owners.TryGetValue(page.Title, out var windows))
                owners.Add(page.Title, windows = []);
            windows.Add(page.WindowId);
        }
        var unique = owners.Values.Where(w => w.Count == 1).Select(w => w.Single())
            .Distinct().Take(2).ToArray();
        return unique.Length == 1 ? unique[0] : null;
    }

    // A missing/stale active-page title is not authority to guess by geometry.
    // Accept a fallback only when >=2 different native tab titles exclusively
    // corroborate the SAME bounds-matching CDP window, with all but at most
    // one native tab represented in that window and no conflicting owner.
    internal static int? UniqueStrongTabFingerprintWindow(IEnumerable<int> candidateWindowIds,
        IEnumerable<(int WindowId, string Title)> pageTitles, IEnumerable<string> nativeTabTitles)
    {
        var candidates = candidateWindowIds.Where(id => id > 0).ToHashSet();
        var native = nativeTabTitles.Where(t => !string.IsNullOrWhiteSpace(t))
            .ToHashSet(StringComparer.Ordinal);
        if (native.Count < 2 || candidates.Count == 0) return null;
        var owners = pageTitles.Where(p => candidates.Contains(p.WindowId) && native.Contains(p.Title))
            .GroupBy(p => p.Title, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(p => p.WindowId).Distinct().ToArray(),
                StringComparer.Ordinal);
        var exclusive = owners.Values.Where(w => w.Length == 1).Select(w => w[0])
            .GroupBy(id => id).Take(2).ToArray();
        if (exclusive.Length != 1 || exclusive[0].Count() < 2) return null;
        int selected = exclusive[0].Key;
        int represented = owners.Values.Count(w => w.Contains(selected));
        return represented >= native.Count - 1 ? selected : null;
    }

    private static async Task<List<TabTarget>> TargetsForWindow(Session cdp, IntPtr hwnd,
        string windowId, CancellationToken cancellationToken, string? expectedOrigin = null,
        string? expectedUrl = null, string? exactTabId = null, bool allowFallback = false)
    {
        if (!GetWindowRect(hwnd, out var rect)) return [];
        string nativeTitle = GetTitle(hwnd);
        var native = new WindowBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        uint reportedDpi = GetDpiForWindow(hwnd);
        double dpi = reportedDpi == 0 ? 1 : reportedDpi / 96.0;
        var targets = await cdp.Call("Target.getTargets", new { });
        var pages = new List<(string Id, string Title, string Url, string? ContextId)>();
        foreach (var info in targets.GetProperty("targetInfos").EnumerateArray())
        {
            if (info.GetProperty("type").GetString() != "page" ||
                !info.TryGetProperty("url", out var address) ||
                address.ValueKind != JsonValueKind.String) continue;
            if (!TryTargetContextId(info, out string? contextId)) continue;
            string id = info.GetProperty("targetId").GetString() ?? "";
            if (!ValidTargetId(id)) continue;
            pages.Add((id, info.GetProperty("title").GetString() ?? "",
                address.GetString() ?? "", contextId));
        }

        var boundCache = new Dictionary<int, bool>();
        var ownerCache = new Dictionary<string, int>(StringComparer.Ordinal);
        async Task<int> WindowId(string targetId)
        {
            if (ownerCache.TryGetValue(targetId, out int cached)) return cached;
            var owner = await cdp.Call("Browser.getWindowForTarget", new { targetId });
            int windowId = owner.GetProperty("windowId").GetInt32();
            ownerCache.Add(targetId, windowId);
            return windowId;
        }

        async Task<bool> BoundsMatch(int browserWindowId)
        {
            if (boundCache.TryGetValue(browserWindowId, out bool cached)) return cached;
            var response = await cdp.Call("Browser.getWindowBounds", new { windowId = browserWindowId });
            var b = response.GetProperty("bounds");
            bool match = b.TryGetProperty("left", out var left) &&
                b.TryGetProperty("top", out var top) && b.TryGetProperty("width", out var width) &&
                b.TryGetProperty("height", out var height) &&
                MatchesBounds(native, new(left.GetInt32(), top.GetInt32(),
                    width.GetInt32(), height.GetInt32()), dpi);
            boundCache.Add(browserWindowId, match);
            return match;
        }

        var titleMatches = new List<(int WindowId, string? ContextId)>();
        var titleWindowIds = new HashSet<int>();
        var titlePages = pages.Where(p => nativeTitle.StartsWith(p.Title + " - Google Chrome",
            StringComparison.Ordinal)).ToArray();
        foreach (var page in titlePages)
        {
            int browserWindowId = await WindowId(page.Id);
            titleWindowIds.Add(browserWindowId);
            if (await BoundsMatch(browserWindowId))
                titleMatches.Add((browserWindowId, page.ContextId));
        }

        // A native, profile-verified HWND must match exactly one CDP window.
        // Distinct TargetInfo contexts are allowed *within that same window*;
        // multiple CDP window IDs (even at identical bounds) are not guessed.
        var selected = UniqueWindowId(titleMatches, out int distinctCount);
        var failure = titlePages.Length == 0 ? WindowCdpMappingFailure.TITLE_NOT_FOUND :
            distinctCount == 0 ? WindowCdpMappingFailure.BOUNDS_MISMATCH :
            WindowCdpMappingFailure.FINGERPRINT_AMBIGUOUS;
        int fingerprintCandidates = 0;
        if (selected is null && (distinctCount > 1 || allowFallback))
        {
            // Reuse the existing bounded, isolated UIA inspector. Never run a
            // potentially hung Chrome accessibility provider in the CDP broker.
            var ui = await WindowInspector.RunIsolatedAsync(windowId,
                WindowInspector.MaxDepth, 100, cancellationToken,
                roleFilter: WindowRoleFilter.TAB_ITEM);
            if (ui.Status == WindowInspectCode.COMPLETED && !ui.Truncated)
            {
                var tabTitles = NativeTabStripTitles(
                    ui.Elements.Select(e => (e.Path, e.Name)), nativeTitle);
                if (tabTitles is not null)
                {
                    var titles = new List<(int WindowId, string Title)>();
                    var nativeLabels = tabTitles.ToHashSet(StringComparer.Ordinal);
                    var candidates = titleMatches.Select(x => x.WindowId).ToHashSet();
                    var matchingPages = pages.Where(p => nativeLabels.Contains(p.Title) &&
                        (distinctCount > 1 || MatchesContext(p.Url, null, null))).ToArray();
                    fingerprintCandidates = matchingPages.Length;
                    if (fingerprintCandidates > 64)
                        failure = WindowCdpMappingFailure.FINGERPRINT_LIMIT;
                    else
                    {
                        // Maximized Chrome windows can report bounds in a different
                        // coordinate space from Win32. Geometry alone cannot
                        // distinguish overlapping HWNDs anyway. With an exact
                        // active title and a unique native tab strip, permit only
                        // the stronger multi-tab fingerprint to resolve them.
                        // Never admit a window without that active-title match.
                        if (distinctCount == 0 && titlePages.Length > 0)
                            candidates.UnionWith(titleWindowIds);
                        foreach (var page in matchingPages)
                        {
                            int id = await WindowId(page.Id);
                            if (distinctCount == 0 && titlePages.Length == 0 &&
                                await BoundsMatch(id)) candidates.Add(id);
                            if (candidates.Contains(id)) titles.Add((id, page.Title));
                        }
                        selected = distinctCount > 1
                            ? UniqueTabFingerprintWindow(candidates, titles, tabTitles)
                            : UniqueStrongTabFingerprintWindow(candidates, titles, tabTitles);
                        if (selected is null)
                            failure = fingerprintCandidates == 0
                                ? WindowCdpMappingFailure.FINGERPRINT_NOT_FOUND :
                                candidates.Count == 0 ? WindowCdpMappingFailure.BOUNDS_MISMATCH :
                                WindowCdpMappingFailure.FINGERPRINT_AMBIGUOUS;
                    }
                }
                else failure = WindowCdpMappingFailure.TAB_STRIP_UNAVAILABLE;
            }
            else failure = WindowCdpMappingFailure.TAB_STRIP_UNAVAILABLE;
        }
        if (selected is null)
            throw new CdpWindowMappingException(distinctCount == 0 ?
                CdpWindowMappingState.NOT_FOUND : CdpWindowMappingState.AMBIGUOUS,
                failure, titlePages.Length, fingerprintCandidates);

        var found = new List<TabTarget>();
        foreach (var page in pages.Where(p => IsRequestedTabCandidate(p.Id, p.Url,
                     exactTabId, expectedOrigin, expectedUrl)))
        {
            if (await WindowId(page.Id) == selected.Value)
                found.Add(new(page.Id, page.Title, page.Url, selected.Value));
        }
        return found;
    }
}
