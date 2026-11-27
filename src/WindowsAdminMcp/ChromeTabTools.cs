using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class ChromeTabTools
{
    [McpServerTool(Name = "windows.chrome_tab", UseStructuredContent = true),
     Description("Chrome tabs: LIST/OPEN/NAVIGATE/BACK/FORWARD/RELOAD/CLOSE through the consented CDP owner. WINDOW verifies the native profile; BROWSER is browser-wide and requires browser_window_id plus fresh tab_id for exact targets. BROWSER OPEN lets Chrome choose the window. No OS input. Mutations require confirm=true; never retry EFFECT_UNKNOWN blindly.")]
    public static async Task<ChromeTabResult> ChromeTab(string window_id, string expected_profile,
        ChromeTabAction action, string? tab_id = null, string? url = null,
        bool background = true, bool confirm = true,
        ChromeTabScope scope = ChromeTabScope.WINDOW, int? browser_window_id = null,
        int offset = 0, int page_size = 100,
        CancellationToken cancellationToken = default)
    {
        var request = new ChromeTabRequest(window_id, expected_profile, action,
            tab_id, url, background, scope, browser_window_id, offset, page_size);
        if (!WindowChromeCdp.ValidTabRequest(request))
            return new(ChromeTabCode.INVALID_ARGUMENT, action);
        if (!confirm && action != ChromeTabAction.LIST)
            return new(ChromeTabCode.CONFIRMATION_REQUIRED, action);
        var status = await AdminTools.Status(cancellationToken: cancellationToken);
        if (status.Status != StatusCode.OK)
            return new(ChromeTabCode.SERVICE_UNAVAILABLE, action);
        using var self = Process.GetCurrentProcess();
        if (status.ExecutionContext != CommandContext.USER ||
            status.UserContextAvailable != true || status.Frontend?.Elevated != true ||
            status.Frontend?.SessionId != self.SessionId)
            return new(ChromeTabCode.USER_CONTEXT_UNAVAILABLE, action);
        var desktop = DesktopCapture.ObserveDesktop(self.SessionId);
        if (desktop.Code != CaptureCode.COMPLETED)
            return new(ChromeTabCode.DESKTOP_UNAVAILABLE, action);
        if (WindowCatalog.ResolveId(window_id, self.SessionId, out _) != WindowResolveCode.OK)
            return new(ChromeTabCode.STALE_WINDOW, action);
        string auditId = Guid.NewGuid().ToString("N");
        if (action != ChromeTabAction.LIST && !WriteAudit(auditId, "INTENT", request, null))
            return new(ChromeTabCode.AUDIT_UNAVAILABLE, action);
        var result = await WindowChromeBroker.ManageTabAsync(request, cancellationToken, auditId);
        if (action != ChromeTabAction.LIST && !WriteAudit(auditId, "RESULT", request, result))
            return new(ChromeTabCode.EFFECT_UNKNOWN, action, AuditId: auditId,
                Detail: "AUDIT_RESULT_UNAVAILABLE");
        return result with { AuditId = action == ChromeTabAction.LIST ? null : auditId };
    }

    // No user URL, profile name, or browsing data is written to the audit log.
    // A failed pre-dispatch intent blocks the mutating command.
    private static bool WriteAudit(string id, string phase, ChromeTabRequest request,
        ChromeTabResult? result)
    {
        try
        {
            string windowHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(request.WindowId)).AsSpan(0, 12));
            var entry = JsonSerializer.Serialize(new
            {
                component = "WindowsAdminMCP.chrome_tab", audit_id = id, phase,
                window_hash = windowHash, action = request.Action.ToString(),
                tab_id = request.TabId, browser_window_id = request.BrowserWindowId,
                scope = request.Scope.ToString(), result = result?.Status.ToString(),
                effect_state = result?.EffectState.ToString(),
                safe_to_retry = result?.SafeToRetry, utc = DateTimeOffset.UtcNow
            }, PipeWire.Json);
            EventLog.WriteEntry("WindowsAdminMCP", entry, EventLogEntryType.Information, 8102);
            return true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or
                                 System.Security.SecurityException or ArgumentException)
        { return false; }
    }
}
