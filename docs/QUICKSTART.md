# WindowsAdminMCP — everyday use

One elevated Windows installation; one SYSTEM service; one authenticated USER
frontend; one SQLite store. No separate scripts, daemons, or browser extension
are needed for the native operations below.

## Choose the tool by task

| Need | Tool |
| --- | --- |
| Is the service working? | `powershell_status` (`include_diagnostics=true` for a detailed check) |
| Run any allowed administrator PowerShell command | `powershell_exec` (USER by default, SYSTEM explicitly) |
| Submit, inspect, cancel or reconcile a durable command | `powershell_job` |
| See the user's desktop | `powershell_screenshot` |
| Check, request, or explicitly accept an update | `powershell_update` |
| Check a file or list a directory | `windows.file_query` |
| Read binary, text, or a large file exactly | `windows.file_read_chunk` |
| Create or replace a file atomically | `windows.file_write` |
| Find a window | `windows.list_windows` |
| Inspect window controls or Chrome tabs | `windows.inspect_window` |
| Capture a single covered window | `windows.capture_window` |
| Act on a freshly inspected element | `windows.invoke_element` |
| Find one element and act without a preceding inspection | `windows.invoke_locator` |
| Activate, move, resize, minimize, or close an exact window | `windows.manage_window` |
| Find and connect a Chrome profile through its own consent dialog | `windows.chrome_connection` |

These 15 tools share native Windows identities and typed results. There is no
arbitrary path, UI-role or administrator-cmdlet allowlist. Windows ACLs, desktop
sessions, Chrome's permission dialog, and exact resource identity still apply.

## Small file: one call

Call `windows.file_write` with:

```json
{
  "action": "WRITE_ATOMIC",
  "path": "C:\\Temp\\example.txt",
  "operation_id": "e7a3c7ee38d74ce8b8dcd2302f60ca12",
  "data_base64": "SGVsbG8=",
  "overwrite_policy": "FAIL_IF_EXISTS",
  "execution_context": "USER"
}
```

`COMMITTED` and `effect_state=EFFECT_VERIFIED` mean the exact bytes are on
disk. A new `operation_id` is an entirely new action. Generate and retain a
stable ID **before** dispatch; if the response disappears, query the same
path and ID using `action=STATUS`. Never blindly try a new ID. Inline uploads
are bounded to 16 KiB; use `BEGIN`, `APPEND`, `COMMIT` for larger files.

Replacing an existing file requires `REPLACE_IF_MATCH` and the version and
SHA-256 obtained by `windows.file_query(STAT, include_sha256=true)`. If the
original changed, replacement fails with `CONFLICT` and no overwrite.

## Windows UI and Chrome

1. `windows.list_windows` returns an exact, reusable *window* identity.
2. `windows.inspect_window` returns fresh UIA element IDs; `windows.invoke_locator`
   can resolve one exact locator and act in a single request.
3. Choose `INVOKE`, `CLICK`, `DOUBLE_CLICK`, `RIGHT_CLICK`, `SCROLL`, `HOVER`,
   `DRAG`, `KEY_PRESS` or an available semantic pattern. Provide an expected
   postcondition for verification when possible. The physical pointer is only
   sent when the actual window and hit target are verified.
4. `windows.manage_window(action=CLOSE)` sends graceful `WM_CLOSE`. A successful
   message enqueue is **not** success: `COMPLETED` requires the exact HWND to
   disappear. Applications may show a save prompt; then the effect is unknown.

For Chrome, first use `windows.chrome_connection` with the exact profile and
window, approve the browser's native dialog, then inspect `scope=TABS` and
`scope=TAB`. Native CDP works with a covered Chrome window without forcing it
to the foreground. It does not bypass browser consent.

## Updates and errors

`powershell_update(action=STATUS)` reports two different time horizons:

- `stage` is the *historical* outcome of the last update attempt, which may
  remain `trial_not_ready` even when the service has recovered since.
- `current_service_status`, `current_local_operational_ready`, and
  `current_observed_utc` are a separate live readback. A null readiness means
  it was not proved; it does not mean the external connector is ready.

`rollback_pending=true` means the installed trial is **not accepted**. Do not
auto-accept it. Use explicit acceptance only after external smoke tests.

For any mutating tool, check `status`, `effect_state` and `safe_to_retry`.
`EFFECT_UNKNOWN` never permits an automatic replay. Inspect the relevant
status/readback first. Arbitrary PowerShell may have unknowable side effects;
the tool records the execution result but does not pretend to verify each
possible Windows effect.

For full recovery, deployment, limitations, and CI evidence, see
[README](../README.md), [architecture](ARCHITECTURE.md) and
[Issue #56](https://github.com/iamaman11/powershell-admin-mcp/issues/56).
