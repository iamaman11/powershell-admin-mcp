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
| Create, copy, move or delete files and folders | `windows.file_manage` |
| Read processes, services, adapters, Windows events, registry metadata or scheduled tasks with native typed fields | `windows.system_query` |
| Find a window | `windows.list_windows` |
| Inspect window controls or Chrome tabs | `windows.inspect_window` |
| Capture a single covered window | `windows.capture_window` |
| Act on a freshly inspected element | `windows.invoke_element` |
| Find one element and act without a preceding inspection | `windows.invoke_locator` |
| Activate, move, resize, minimize, or close an exact window | `windows.manage_window` |
| Find and connect a Chrome profile through its own consent dialog | `windows.chrome_connection` |

These 17 tools share native Windows identities and typed results. There is no
arbitrary path, UI-role or administrator-cmdlet allowlist. Windows ACLs, desktop
sessions, Chrome's permission dialog, and exact resource identity still apply.

`windows.system_query(kind=PROCESSES|SERVICES|NETWORK_INTERFACES|EVENT_LOG|REGISTRY|SCHEDULED_TASKS)`
is read-only and uses the MCP frontend's real Windows token. Process ID and
service name select exact subjects; lists are paged (up to 50 entries per call).
For `EVENT_LOG`, select `event_log=APPLICATION|SYSTEM|SECURITY` and optionally
`event_id` (0–65535); results expose event identity, provider, level and time,
not event messages. The newest events come first and offsets are bounded to 200.
For `REGISTRY`, specify `registry_hive=LOCAL_MACHINE|CURRENT_USER|USERS|CLASSES_ROOT|CURRENT_CONFIG`
and a hive-relative `registry_path`. It lists immediate subkeys/value names;
`registry_view=DEFAULT|REGISTRY32|REGISTRY64` selects the view. Value content
is omitted unless explicitly requested with `include_value=true` and is
truncated or hashed in bounded result fields. This may reveal sensitive
configuration to the authorized caller. For `SCHEDULED_TASKS`, optionally
select `task_folder` (default `\`); results list tasks in that exact folder,
including hidden tasks allowed by ACLs, but never execution arguments.
No query elevates a token, modifies state, or bypasses ACLs. Unavailable details
are explicitly marked rather than silently turned into zero. For privileged
modifications or other diagnostics, `powershell_exec` continues to support
USER and explicit SYSTEM execution.

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

## Files and folders: native management

Use `windows.file_manage(action=MKDIR|COPY|MOVE|DELETE)` with a fully qualified
`path`, a fresh stable 32-character GUID (`operation_id`), and `destination`
for COPY/MOVE. Choose `execution_context=USER` or `SYSTEM` explicitly. The
operation is executed natively under that Windows identity, with OS ACLs; the
service is the only SQLite receipt owner. COPY stages on the destination
volume, then atomically renames into place. MOVE is an atomic rename on one
volume: `CROSS_VOLUME` means use COPY and then an *explicit separate* DELETE.
Existing destinations are never overwritten. The destination parent must
exist (create it first with MKDIR).

Directory COPY and DELETE require `recursive=true` (nonempty DELETE without
it reports `NOT_EMPTY`). For a file's optimistic change guard, obtain both
`expected_version` and `expected_sha256` with `windows.file_query(STAT,
include_sha256=true)` and supply them together. Junctions/symlinks are not
traversed by the manager; no arbitrary path or administrative capability
allowlist is imposed.

```json
{
  "action": "MOVE",
  "path": "C:\\Temp\\report.txt",
  "destination": "C:\\Archive\\report.txt",
  "operation_id": "2cb13a6d2b7a4abda83ca2acd686228c",
  "execution_context": "SYSTEM"
}
```

When an acknowledgement is lost, repeat the **same complete request** with
`read_only=true` to check the durable receipt without performing the operation.
`COMPLETED` proves the native effect was observed and persisted. `READY` means
the operation had no recorded commit effect yet. `EFFECT_UNKNOWN` means the
service may have dispatched it but did not persist a conclusive outcome; it
must not be retried automatically, even after restart. Inspect source and
destination and use a separate explicit recovery decision. A **new** GUID
always means a new action. File receipts share the same 100,000-row and
512-MiB global SQLite metadata budget as jobs and atomic writes.

## Windows UI and Chrome

1. `windows.list_windows` returns an exact, reusable *window* identity.
2. `windows.inspect_window` returns fresh UIA element IDs; `windows.invoke_locator`
   can resolve one exact locator and act in a single request.
3. Choose `INVOKE`, `CLICK`, `DOUBLE_CLICK`, `RIGHT_CLICK`, `SCROLL`, `HOVER`,
   `DRAG`, `KEY_PRESS` or an available semantic pattern. Provide an expected
   postcondition for verification when possible. `input_mode=BACKGROUND_ONLY`
   is the default: CDP and native UIA patterns do not inject OS mouse/keyboard
   events. A physical-input-only action returns `FOREGROUND_INPUT_DISALLOWED`
   with `effect_state=NO_EFFECT`. Explicit `input_mode=ALLOW_FOREGROUND_INPUT`
   permits the existing verified pointer/keyboard path, including its Windows
   foreground restrictions.
4. `windows.manage_window(action=CLOSE)` sends graceful `WM_CLOSE`. A successful
   message enqueue is **not** success: `COMPLETED` requires the exact HWND to
   disappear. Applications may show a save prompt; then the effect is unknown.

For Chrome, first use `windows.chrome_connection` with the exact profile and
window. `REQUEST` does not raise Chrome by default: an already foreground
window can display the native consent dialog; otherwise it returns
`CONSENT_WINDOW_UNAVAILABLE` with `SHOW_CHROME_FOR_CONSENT`. Optional
`activate_window_for_consent=true` explicitly allows a foreground activation
attempt *for the permission prompt only*. After user consent, inspect
`scope=TABS` and `scope=TAB`; ordinary CDP actions remain background-only.
The application never silently approves Chrome permissions.

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
