# WindowsAdminMCP — one-command start

**WindowsAdminMCP 0.6.0.** Canonical source: iamaman11/powershell-admin-mcp.
One official OpenAI Secure MCP tunnel runtime v0.0.19; one Windows PowerShell MCP stdio server; two tools: `powershell_status` and `powershell_exec`. No extra daemon, database, service, scheduler, mode selector or per-command approval.

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
