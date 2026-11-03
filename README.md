# WindowsAdminMCP

This repository is the **canonical source** of the Windows PowerShell 7 MCP server used with the official OpenAI secure MCP tunnel.

**Current:** WindowsAdminMCP `0.4.0`. Official external transport: `openai/tunnel-client v0.0.19` (the vendor-maintained EXE is **not** copied into this repository).

## Components

| File | Purpose |
| --- | --- |
| `windows-admin-mcp.ps1` | MCP stdio server with JSON-RPC validation, bounded output, Unicode support, execution error handling and fail-closed audit |
| `start-windows-admin-mcp.ps1` | Explicit elevated session consent, hash checks, prompt for tunnel credentials and localhost health binding |
| `Test-WindowsAdminMCP.ps1` | Offline Windows acceptance suite (41 scenarios) |
| `Install-WindowsAdminMCP.ps1` | Deploy source and verified vendor runtime into protected `Program Files` directory |
| `.github/workflows/windows-acceptance.yml` | Windows CI using the same protected installation and offline acceptance suite |

## Installation

On **Windows 11 / PowerShell 7**, from an intentionally elevated PowerShell terminal:

```powershell
& .\Install-WindowsAdminMCP.ps1
& 'C:\Program Files\WindowsAdminMCP\Test-WindowsAdminMCP.ps1'
& 'C:\Program Files\WindowsAdminMCP\start-windows-admin-mcp.ps1'
```

The installer checks its own elevation, installs the upstream v0.0.19 Windows x64 runtime with vendor-published archive and EXE SHA-256 verification, and restricts installation directory ACLs to Administrators/SYSTEM. If the exact verified executable already exists, it is reused. It never writes tunnel secrets to disk.

The launcher requires `ENABLE` **for this session** and a securely entered tunnel runtime API key. Set `CONTROL_PLANE_TUNNEL_ID` in the launching shell or supply the ID when prompted. The launcher validates the exact SHA-256 of the installed MCP server and official runtime. The tunnel ID is intentionally **not stored in this public repository**.

### Safety properties and limitations

- The `powershell_exec` tool executes **arbitrary elevated Windows PowerShell**. `ENABLE` approves the *session*, not each command.
- Only trust clients and accounts authorized to access the control plane. These scripts are **not** a replacement for OpenAI account/workspace security.
- Only Administrators and SYSTEM may write code, launcher, executable and logs. Verify effective sandbox user access after deployment.
- Writes to `logs/commands.jsonl` record a hashed command, a correlation ID and status; a failed admission log write blocks execution. **Command output returned to callers can contain secrets.**
- PowerShell processes execute serially; `ping` and `powershell_status` can respond during a long command at the local stdio layer. A remote tunnel/client may serialize requests independently.
- A bounded output buffer prevents full-output accumulation; a timeout is reported as `TIMEOUT_UNKNOWN_EFFECT` because prior effects cannot be undone by terminating the process.

### Do not commit

API keys, actual tunnel IDs, `CONTROL_PLANE_API_KEY`, `OPENAI_API_KEY`, `logs/`, `client/`, downloaded zip files, local configuration, or backup files.

### Verification and release readiness

The offline acceptance suite includes error codes for unknown methods/tools, malformed requests, Unicode output, nonterminating\/terminating PowerShell errors, native exit status, handled exceptions, structured warnings, plain diagnostic stderr, output of 2 million characters, timeout behavior, concurrent status\/ping, active and queued cancellation, queue-inclusive timeouts, progress notifications, and audit fail-closed behavior. **Passing local tests does not prove full cloud-path parallelism, reboot recovery, or non-admin ACL denial tests.** Those are separate physical acceptance gates.

The installed OpenAI tunnel runtime may be kept running independently of the source upload. Switching the transport EXE path requires restarting the runtime and re-entering the credential, not moving a live EXE.


## v0.4.0 behavior

- Nonzero native process exits are considered tool failures even when PowerShell continues afterward. Caught/handled PowerShell errors do not automatically fail the request.
- Warnings are returned in `structuredContent.warnings`. Diagnostic `stderr` text is retained but does not alone mark a successful command as failed; check `status`, `isError` and `exit_code`.
- `notifications/cancelled` cancels queued or running commands. A command already started reports `CANCELLED_UNKNOWN_EFFECT`: cancellation cannot undo effects already produced.
- `timeout_seconds` is counted from request admission, including queue time. A queued timeout reports `TIMEOUT_BEFORE_EXECUTION`.
- Optional MCP `_meta.progressToken` receives `notifications/progress` on queue, start and finish. Progress is not a reliable substitute for a durable job record.
- The server still does **not** persist a resumable job database or enforce OS-level protection against dangerous administrative commands. Treat autonomous destructive operations as outside this release's acceptance scope.
