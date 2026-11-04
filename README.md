# WindowsAdminMCP — one-command start

**WindowsAdminMCP 0.7.0.** Canonical source: iamaman11/powershell-admin-mcp.
One official OpenAI Secure MCP tunnel runtime v0.0.19; one Windows PowerShell MCP stdio server; three tools: `powershell_status`, `powershell_exec`, and `powershell_screenshot`. No extra daemon, database, service, scheduler, mode selector or per-command approval.

## Everyday use

In an **elevated PowerShell 7** window:

```powershell
& "$env:ProgramFiles\WindowsAdminMCP\WindowsAdminMCP.ps1" start
```

This starts **both tunnel and MCP** with full administrator PowerShell access. **No `ENABLE`, mode selection or runtime API key input on every launch.** Running twice while healthy is idempotent (`ALREADY_RUNNING`). Stop intentionally from an elevated local console via the same entrypoint with `stop`.

```powershell
& "$env:ProgramFiles\WindowsAdminMCP\WindowsAdminMCP.ps1" status
& "$env:ProgramFiles\WindowsAdminMCP\WindowsAdminMCP.ps1" test
& "$env:ProgramFiles\WindowsAdminMCP\WindowsAdminMCP.ps1" stop
```

## Initial credential setup — only once per Windows machine

**The official OpenAI tunnel still requires its runtime API key** to authenticate; this project does not remove or bypass authentication. To stop asking on every run, configure it once in the interactive elevated Windows console:

```powershell
& "$env:ProgramFiles\WindowsAdminMCP\WindowsAdminMCP.ps1" configure
```

Enter the runtime API key **once**. The tunnel ID is discovered automatically from the active tunnel, current environment, or a protected v0.4/v0.5 launcher backup on this Windows installation; it is never requested interactively. If it cannot be recovered, `configure` reports `TUNNEL_ID_AUTODISCOVERY_FAILED` without modifying credentials. After configuration, the identifier is stored with the encrypted runtime key and is never requested again. The complete credential is encrypted with **Windows DPAPI LocalMachine** in `%ProgramData%\WindowsAdminMCP\tunnel-credential.dpapi`, a directory restricted to Administrators and SYSTEM. It is never committed to GitHub or echoed. **If this encrypted configuration is absent, start fails explicitly and does not ask for a key.** Rotation: rerun `configure`.

Use `install` on the same entrypoint from an authorized checkout to install or update. The installer checks fixed SHA-256 of the MCP server and the official OpenAI tunnel executable.

## Safety and known limits

- `powershell_exec` provides **unrestricted elevated Windows PowerShell** after the locally configured tunnel is connected. Treat access to the tunnel and the host as administrator-level authorization.
- Code and runtime are hash-pinned; commands are serialized, bounded, timed out, cancellable and audited. Timeout or cancel of a started command does not undo side effects.
- No one-shot approval files, SAFE / APPROVAL / SUPERVISED modes, interactive ENABLE step, or persisted raw secrets.
- DPAPI LocalMachine keeps credentials tied to Windows. Backup/restore onto a different computer requires configuring again.
- Persisted resumable jobs, power-loss recovery, effective non-admin ACL denial and end-to-end tunnel concurrency remain unaccepted; see Issue #1. CI passing does not prove these.

## Intermittent initialization conflict (409)

The official OpenAI tunnel-client **v0.0.19** rejects legacy `tools/call` when the stdio child has not completed both `initialize` and `notifications/initialized`, returning HTTP 409 `mcp_initialization_required` **before the PowerShell MCP server receives the call**. The standard `start` launcher enables the vendor-supported `MCP_STDIO_SEND_INITIALIZED_NOTIFICATION=true` shim: after a successful `initialize`, the runtime sends the initialized notification, suppressing a duplicate from the remote caller. This requires restarting the single tunnel-client process to take effect; updating files without restarting does not change the current runtime process.

Check `http://127.0.0.1:8080/metrics` for `command_end_to_end_latency_milliseconds_count` with `request_method="tools/call"` and `tunnel_service_status="409"`. A 409 is a **tunnel/client lifecycle rejection**, not a Windows command failure. `/readyz` = 200 checks runtime readiness, not the readiness of every legacy MCP client session. If a 409 recurs after the shim is active, the caller must complete `initialize` before retrying; **never automatically re-run a side-effecting `powershell_exec` whose outcome is unknown**. No extra process, scheduler, or second tunnel is required.

## MCP 2026-07-28 and direct screen capture

This single stdio MCP server now supports **modern MCP 2026-07-28** and existing **legacy MCP 2025-06-18** clients. Modern requests include `params._meta["io.modelcontextprotocol/protocolVersion"] = "2026-07-28"` and `params._meta["io.modelcontextprotocol/clientCapabilities"] = {}`; a prior `initialize` is not required. `server/discover` advertises 2026-07-28; modern replies include `resultType: "complete"`, unsupported versions return `-32022`, missing capabilities return `-32602`. Legacy `initialize` still selects 2025-06-18, with the official tunnel's initialized notification shim retained.

**`powershell_screenshot`** takes an optional zero-based `monitor`, screen-relative `x, y, width, height`, and `max_width` (256..1920; default 1280). A screenshot is captured from the existing Windows interactive desktop and returned **directly as PNG ImageContent** with base64 image data. There is no persisted PNG, new service, background process, or second tunnel. Images may contain personal data or passwords visible on screen. Capture is bounded to 16 million pixels and a 2 MB PNG; oversized results are downscaled. If the interactive desktop is unavailable or locked, the result is a typed tool error with next action; CI/headless runs test failure paths without pretending to capture the screen.

**End-to-end acceptance** additionally requires this tool to be exposed by the ChatGPT connector and the image to arrive in the model's tool result. Offline and on-host PNG checks alone cannot prove the connector has refreshed its tool schema. When the connector exposes only the previous two tools, refresh/reconnect its tool discovery before claiming visual functionality.
