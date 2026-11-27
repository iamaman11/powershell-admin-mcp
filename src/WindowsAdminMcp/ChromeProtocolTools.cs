using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

// One public MCP entry point for the runtime CDP schema; all operations still
// route through the existing USER-session Chrome broker and its consented socket.
[McpServerToolType]
public sealed class ChromeProtocolTools
{
    [McpServerTool(Name = "windows.chrome_cdp", UseStructuredContent = true),
     Description("Consented CDP: DESCRIBE/TARGETS/CALL/SUBSCRIBE/EVENTS/UNSUBSCRIBE. Live schema, exact identity, explicit browser/focus opt-ins. No effect replay.")]
    public static async Task<ChromeProtocolResult> ChromeCdp(string window_id,
        string expected_profile, ChromeProtocolAction action,
        string? method = null, string? parameters_json = null, string? tab_id = null,
        int? browser_window_id = null, string? expected_origin = null,
        string? expected_url = null, string? domain = null,
        int offset = 0, int page_size = 50, string? subscription_id = null,
        long after_sequence = 0, bool allow_foreground = false,
        bool allow_browser_scope = false, bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        var request = new ChromeProtocolRequest(window_id, expected_profile, action,
            method, parameters_json, tab_id, browser_window_id, expected_origin,
            expected_url, domain, offset, page_size, subscription_id,
            after_sequence, allow_foreground, allow_browser_scope);
        if (!WindowChromeCdp.ValidProtocolRequest(request))
            return new(ChromeProtocolCode.INVALID_ARGUMENT, action);
        bool dispatchMayChangeState = action is ChromeProtocolAction.CALL or
            ChromeProtocolAction.SUBSCRIBE or ChromeProtocolAction.UNSUBSCRIBE;
        if (dispatchMayChangeState && !confirm)
            return new(ChromeProtocolCode.CONFIRMATION_REQUIRED, action);

        var status = await AdminTools.Status(cancellationToken: cancellationToken);
        if (status.Status != StatusCode.OK)
            return new(ChromeProtocolCode.SERVICE_UNAVAILABLE, action);
        using var self = Process.GetCurrentProcess();
        if (status.ExecutionContext != CommandContext.USER ||
            status.UserContextAvailable != true || status.Frontend?.Elevated != true ||
            status.Frontend?.SessionId != self.SessionId)
            return new(ChromeProtocolCode.USER_CONTEXT_UNAVAILABLE, action);
        // CDP does not need a captureable desktop or a foreground window once
        // Chrome consent is confirmed. Keep the exact HWND/process/session
        // identity check without imposing native UIA/desktop prerequisites.
        var resolved = WindowCatalog.ResolveId(window_id, self.SessionId, out _);
        if (resolved != WindowResolveCode.OK)
            return new(ChromeProtocolCode.STALE_WINDOW, action,
                Detail: resolved.ToString());

        string auditId = Guid.NewGuid().ToString("N");
        if (dispatchMayChangeState && !WriteAudit(auditId, "INTENT", request, null))
            return new(ChromeProtocolCode.AUDIT_UNAVAILABLE, action);
        var result = await WindowChromeBroker.ProtocolAsync(request, cancellationToken, auditId);
        if (dispatchMayChangeState && !WriteAudit(auditId, "RESULT", request, result))
            return new(ChromeProtocolCode.EFFECT_UNKNOWN, action,
                Detail: "AUDIT_RESULT_UNAVAILABLE", TraceId: auditId);
        return result with { TraceId = result.TraceId ?? auditId };
    }

    // Do not log CDP parameters, JavaScript, URLs, cookies, response payloads
    // or profile/account metadata. A failed INTENT log prevents dispatch.
    private static bool WriteAudit(string id, string phase, ChromeProtocolRequest request,
        ChromeProtocolResult? result)
    {
        try
        {
            string windowHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(request.WindowId)).AsSpan(0, 12));
            string? tabHash = request.TabId is null ? null : Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(request.TabId)).AsSpan(0, 12));
            string entry = JsonSerializer.Serialize(new
            {
                component = "WindowsAdminMCP.chrome_cdp", audit_id = id, phase,
                window_hash = windowHash, tab_hash = tabHash,
                browser_window_id = request.BrowserWindowId,
                action = request.Action.ToString(),
                method = request.Method, result = result?.Status.ToString(),
                utc = DateTimeOffset.UtcNow
            }, PipeWire.Json);
            EventLog.WriteEntry("WindowsAdminMCP", entry, EventLogEntryType.Information, 8103);
            return true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or
                                 System.Security.SecurityException or ArgumentException)
        { return false; }
    }
}
