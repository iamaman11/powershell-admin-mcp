# WindowsAdminMCP

Windows PowerShell 7 MCP stdio server for **explicitly authorized elevated administrator sessions** via the official OpenAI secure MCP tunnel.

## Source of truth

- Repository: `iamaman11/powershell-admin-mcp`
- MCP server: `windows-admin-mcp.ps1` (v0.3.0)
- Launcher: `start-windows-admin-mcp.ps1`
- Offline acceptance: `Test-WindowsAdminMCP.ps1`
- Official tunnel runtime (external dependency): `openai/tunnel-client` v0.0.19, Windows x64.
- Do **not** commit or publish API keys, private tunnel IDs, logs, private configuration or the runtime binary.

## Security

`powershell_exec` can execute arbitrary PowerShell under the **Windows administrator token**. The launcher requires an intentionally elevated PowerShell 7 process, local `ENABLE` consent and the OpenAI tunnel credential. Consent enables the session, **not approval of every command**. Only use with trusted users, trusted connectors and an appropriate OpenAI account/workspace access policy.

The installation directory must be writable **only by Administrators and SYSTEM**. Verify effective write access for sandbox/non-admin users, not only ACL text. Production logs contain command hashes, status, duration and correlation IDs, not commands. Command output may itself contain sensitive material and is returned to the MCP caller.

## Installation and test

Follow `README.md` and the protected installation script after the initial source commit. Do **not** run the MCP server from a user-writable directory. The supplied launcher expects the official runtime EXE in `client/tunnel-client-runtime.exe` and checks both the EXE and server SHA-256.

The launcher asks for `CONTROL_PLANE_TUNNEL_ID` when it is not supplied in the environment, and asks for the tunnel API key securely at startup. Credentials are not kept in the repository. The local installed launcher may use a locally customized tunnel ID, and is intentionally **not byte-for-byte identical** to the public portable launcher.

## Acceptance scope

The included tests check protocol errors, Unicode, stdout/stderr bounds, execution errors, timeout, status/ping responsiveness and audit fail-closed behavior. A successful offline test is **not** proof that all OpenAI control-plane authorization and network security controls are correctly configured.

## Restore and operations

Run `Test-WindowsAdminMCP.ps1` in the protected installation. Restart the **runtime** from the protected launcher when you can re-enter the tunnel key. Never embed the key in a scheduled task or committed source file. Validate `127.0.0.1:8080/readyz` and confirm the running runtime EXE path is under Program Files.

