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
| Read processes, services, adapters, TCP connections with owning PID, Windows events, registry metadata or scheduled tasks with native typed fields | `windows.system_query` |
| Read TCP IPv4/IPv6 connections without the broad system-query discriminator | `windows.tcp_connections` |
| Diagnose a file or directory ACL without changing it | `windows.file_acl` |
| Inspect native Windows process wait chains | `windows.process_wait_chain` |
| Actively test DNS, TCP, TLS or bounded HTTP against an explicit host | `windows.network_probe` |
| Start/stop a service or enable/disable a task with guarded native operations | `windows.admin_action` |
| Find a window | `windows.list_windows` |
| Inspect window controls or Chrome tabs | `windows.inspect_window` |
| Capture a single covered window | `windows.capture_window` |
| Act on a freshly inspected element | `windows.invoke_element` |
| Find one element and act without a preceding inspection | `windows.invoke_locator` |
| Activate, move, resize, minimize, or close an exact window | `windows.manage_window` |
| Find and connect a Chrome profile through its own consent dialog | `windows.chrome_connection` |
| List, open, navigate, reload or close exact Chrome tabs | `windows.chrome_tab` |

These 23 tools share native Windows identities and typed results. There is no
arbitrary path, UI-role or administrator-cmdlet allowlist. Windows ACLs, desktop
sessions, Chrome's permission dialog, and exact resource identity still apply.

Installation backup maintenance uses the existing `powershell_update` tool:
`PREVIEW_BACKUPS` only counts eligible historical archives and returns a SHA-256
plan plus reclaimable bytes; `CLEANUP_BACKUPS` requires that exact
`plan_sha256` and `expected_commit`. Its Scheduler result is read with `STATUS`.
The current files and `.rollback` are never cleanup candidates; the nearest
trial chain and most recent accepted backup are retained. The cleanup does not
restart WindowsAdminMCP or disturb Chrome, and is never automatic.

`windows.system_query(kind=PROCESSES|SERVICES|NETWORK_INTERFACES|TCP_CONNECTIONS|EVENT_LOG|REGISTRY|SCHEDULED_TASKS)`
is read-only and uses the MCP frontend's real Windows token. Process ID and
service name select exact subjects; lists are paged (up to 50 entries per call).
`windows.tcp_connections(pid?, page_size?, offset?)` reads the same native TCP
table but has its **own narrow result schema**. It is the preferred entry point
when a published client has an older cached `windows.system_query` kind enum.
The remote connector must still publish the refreshed tool catalog before a
client can invoke it; the server cannot unilaterally refresh client metadata.
For `TCP_CONNECTIONS`, `pid` optionally selects the owning process. Each
IPv4/IPv6 row exposes local/remote endpoint, state and PID; listening endpoints
omit the meaningless remote address and port. A TCP snapshot is not a stable
transaction across separate `offset` pages; retry the read-only query if the
socket table changes materially. No network connection is initiated.
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
   For common `CLICK` actions, supply only `window_id`, fresh `element_id` and
   `action=CLICK` to `windows.invoke_element`. For text, also supply `value` with
   `action=SET_VALUE`. Advanced typed options remain available when needed;
   unused optional fields need not be supplied.
3. Choose `INVOKE`, `CLICK`, `DOUBLE_CLICK`, `RIGHT_CLICK`, `SCROLL`, `HOVER`,
   `DRAG`, `KEY_PRESS` or an available semantic pattern. Provide an expected
   postcondition for verification. `input_mode=BACKGROUND_ONLY` is the default:
   it prohibits **our** OS mouse/keyboard injection and calls to activate a
   window. The UIA provider of another application can nevertheless briefly
   raise its window. `foreground_changed=false` compares snapshots; it is not
   proof that no transient activation happened. For consented Chrome web
   controls, use `input_mode=CDP_ONLY` to prohibit both native UIA actions and
   physical input; unsupported controls then return `CDP_ONLY_UNAVAILABLE`
   without dispatching an effect. A physical-input-only action in the default
   mode returns `FOREGROUND_INPUT_DISALLOWED` with `effect_state=NO_EFFECT`.
   Explicit `input_mode=ALLOW_FOREGROUND_INPUT` permits the verified native
   pointer/keyboard path, subject to Windows foreground restrictions.
   For a Chrome web page, `input_mode=CDP_ONLY` also supports `DRAG` between
   two **fresh** `c1` DOM references using `target_element_id`; neither endpoint
   is a screen coordinate. `SET_FILES` selects 1–16 existing local files in an
   HTML `<input type=file>` using `file_paths=["C:\\path\\file.txt"]`. It is
   file selection only: submitting the form and transferring data to a site
   requires another explicit action. Multi-file selection requires the HTML
   control's native `multiple` attribute. The result reports
   `selected_file_count` only after the DOM's file list was verified. Neither
   operation activates Chrome or uses OS pointer/keyboard injection.
4. `windows.manage_window(action=CLOSE)` sends graceful `WM_CLOSE`. A successful
   message enqueue is **not** success: `COMPLETED` requires the exact HWND to
   disappear. Applications may show a save prompt; then the effect is unknown.

### Understanding an action result

For navigation (for example, opening a profile list), first inspect the
current window and identify an element **unique to the destination screen**.
Invoke the navigation control once, supplying that destination as
`expected_name` and `expected_change=APPEARS`; optionally set
`expected_kind=UIA_ELEMENT` (default) and `observation_timeout_ms` (100..15000).
Never use the navigation button's name as the destination check if it was
already visible before the click.

The following fields have separate meanings:

| Field | Interpretation |
| --- | --- |
| `status=COMPLETED`, `provider_outcome=ACKNOWLEDGED` | UIA/CDP accepted dispatch; **not** proof that navigation happened |
| `effect_state=EFFECT_VERIFIED`, `effect_evidence=POSTCONDITION_VERIFIED` | The requested transition was independently observed |
| `effect_state=EFFECT_UNKNOWN`, `effect_evidence=PROVIDER_ACK_ONLY` | No independently verified effect; do a fresh **read-only** inspection |
| `effect_evidence=POSTCONDITION_NOT_OBSERVED` | A requested postcondition was checked but not seen; still no blind retry |
| `safe_to_retry=false` | Do not re-dispatch the same state-changing action without independent evidence |
| `next_step` | Typed guidance such as `INSPECT_AND_SUPPLY_POSTCONDITION` or `INSPECT_STATE_DO_NOT_RETRY` |
| `focus_behavior=UIA_PROVIDER_MAY_ACTIVATE` | Even with `BACKGROUND_ONLY`, a third-party UIA provider may momentarily change foreground |
| `focus_behavior=UNKNOWN_METHOD_MAY_ACTIVATE` | The action outcome/method is uncertain; do not infer that focus stayed unchanged |

`windows.chrome_tab` and `windows.inspect_window(scope=TABS|TAB)` intentionally
omit query strings and fragments from displayed URLs. A tab with omitted
components reports `url_components_redacted=true`. Do not reuse such a display
URL as an exact `expected_url` for a web action: use the fresh `c1` identity and
`expected_origin`, or an independently known complete URL. With several Chrome
windows, use the existing `chrome_tab(scope=BROWSER)` interface and its exact
`browser_window_id`; opening a tab into a particular native HWND cannot be
guaranteed through CDP alone. `windows.chrome_tab(OPEN, scope=BROWSER)` reports
`destination=CHROME_SELECTED_UNVERIFIED` for a Chrome-chosen window and returns
its actual `browser_window_id`. This does not promise the original profile.

After `INSPECT_AND_SUPPLY_POSTCONDITION`, supply the discovered destination
postcondition on a **future** action. Do not repeat an action whose effect is
still unknown merely to add the missing expected-state fields.

`CDP_ONLY` does not imply that arbitrary website JavaScript can never call
focus APIs; it guarantees only that the MCP tool does not fall back to native
UIA dispatch, OS foreground activation or synthetic keyboard/mouse input.

For Chrome, first use `windows.chrome_connection` with the exact profile and
window. `REQUEST` does not raise Chrome by default: an already foreground
window can display the native consent dialog; otherwise it returns
`CONSENT_WINDOW_UNAVAILABLE` with `SHOW_CHROME_FOR_CONSENT`. Optional
`activate_window_for_consent=true` explicitly allows a foreground activation
attempt *for the permission prompt only*. After user consent, inspect
`scope=TABS` and `scope=TAB`; ordinary CDP actions remain background-only.
The application never silently approves Chrome permissions.

### Two supported Chrome connection modes

- `mode=USER_BROWSER` (default) attaches to an already running Chrome 144+.
  Enable `chrome://inspect/#remote-debugging` and approve Chrome's native
  per-session prompt. The permission cannot be bypassed.
- `mode=MANAGED_BROWSER` connects to an already launched separate Chrome with
  a persistent data directory. Its `managed_profile_id` contains 1..40 ASCII
  letters, digits, `_` or `-`; the directory is
  `%LOCALAPPDATA%\WindowsAdminMCP\ChromeProfiles\<managed_profile_id>`.
  No personal profile is copied and no elevated Chrome is launched by MCP.

To set up a managed browser, start Chrome once from a **non-elevated** Windows
user terminal (replace `work` with your managed profile ID):

```powershell
$profilePath = Join-Path $env:LOCALAPPDATA 'WindowsAdminMCP\ChromeProfiles\work'
& "$env:ProgramFiles\Google\Chrome\Application\chrome.exe" `
  "--user-data-dir=$profilePath" '--remote-debugging-port=0'
```

Call `windows.chrome_connection(action=LIST)` to discover the exact new
`window_id` and `expected_profile`, then call
`windows.chrome_connection(action=REQUEST, mode=MANAGED_BROWSER,
managed_profile_id=work, window_id=..., expected_profile=...)`.
`CONNECTING` means wait for `STATUS`; `CONNECTED` proves a usable CDP socket.
The separate managed profile needs no native consent prompt on every connection.
The connection-only result has `target_ready=null` because no particular tab
was probed. This is **not** a failed target check. Verify an actual tab with
`windows.chrome_tab(action=LIST)` or `windows.inspect_window(scope=TAB)`;
their typed results describe target resolution and readiness.

After connecting either mode, use `windows.chrome_tab`:

1. `scope=WINDOW` (default) restricts `LIST` and operations to the verified
   native `window_id` and `expected_profile`. A mapping problem returns typed
   `cdp_mapping_failure`, `cdp_title_candidates`,
   `cdp_fingerprint_candidates` and `next_step=LIST_BROWSER_TABS`.
2. If native Chrome windows overlap or have incomparable bounds, call
   `action=LIST, scope=BROWSER` using the same consented connection. This lists
   tabs in **all profiles of that CDP browser** with their exact `tab_id`,
   `browser_window_id`, URL and `next_offset` for pagination. `BROWSER` is an
   explicit browser-wide authority, **not** a claim that CDP identity matches
   the originally selected native window. `browser_window_id` optionally
   filters `LIST` to one CDP window.
3. For `scope=BROWSER`, pass the exact `browser_window_id` and `tab_id` from
   `LIST` to `NAVIGATE`, `BACK`, `FORWARD`, `RELOAD` or `CLOSE`. `scope=WINDOW`
   accepts an exact `tab_id` without the CDP window ID.
4. `OPEN` takes an HTTP(S) URL and defaults to `background=true`. The default
   `WINDOW` mode requires one matching native Chrome window; explicit
   `BROWSER` mode allows Chrome itself to select the destination and reports
   the actual `browser_window_id` in the verified result. Use `LIST` if you
   need to inspect that destination before further actions.
5. Only independently observed outcomes return `effect_state=EFFECT_VERIFIED`.
   `EFFECT_UNKNOWN` means inspect current state using a read-only `LIST`,
   never automatically replay the effectful command. Transient readback
   rejection after navigation retries the read-only observation, not the action.

For example, if `scope=WINDOW, action=LIST` reports `TARGET_AMBIGUOUS`, do
`scope=BROWSER, action=LIST, offset=0, page_size=100`; choose the exact tab
and its `browser_window_id`, then call `scope=BROWSER, action=NAVIGATE,
tab_id=<listed tab>, browser_window_id=<listed window>, url=https://example.com/,
confirm=true`. The tool always validates the target's current CDP window
before dispatch. `next_offset` means another read-only page is available.

For a DOM button in a browser tab whose native HWND mapping is ambiguous,
the same explicit browser window identity now works for inspection and
element actions (without a second CDP connection):

1. Use `windows.chrome_tab(action=LIST, scope=BROWSER, window_id=<anchor>,
   expected_profile=<anchor profile>)` and select the intended exact
   `browser_window_id` and `tab_id`. The list covers **all profiles** of the
   consented Chrome process; verify your choice, including the unredacted
   origin from a trusted context. Displayed URLs can omit query/fragment.
2. Use `windows.inspect_window(window_id=<anchor>, scope=TAB,
   browser_window_id=<selected window>, tab_id=<selected tab>,
   expected_profile=<anchor profile>, expected_origin=https://example.com)`
   to get fresh DOM `c1` element references. This opt-in route verifies the
   exact CDP window/tab pair, not native window geometry or a tab title.
3. Use `windows.invoke_element(window_id=<anchor>,
   element_id=<fresh c1 reference>, action=CLICK,
   browser_window_id=<selected window>, input_mode=CDP_ONLY,
   expected_profile=<anchor profile>, expected_origin=https://example.com,
   expected_name=<known control>, expected_change=...)`, with `confirm=true`
   when required. Each dispatch rechecks the current native anchor profile,
   tab ownership, URL and loader. Background-only CDP input never steals OS
   pointer/keyboard focus or falls back to physical input.

Here, `expected_profile` verifies the **native anchor** used to authorize
the connection; it does **not** attest to the destination tab's Chrome profile.
If a native window-to-tab mapping is required, keep the default route without
`browser_window_id`. With an uncertain effect, perform a read-only check;
do not automatically repeat the action.

Internal Chrome pages, OS file pickers and privileged prompts may still require
the existing native UIA/foreground consent policy rather than a CDP action.

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
