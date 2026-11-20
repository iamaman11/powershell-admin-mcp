# WindowsAdminMCP — native Windows administration

Canonical source: [iamaman11/powershell-admin-mcp](https://github.com/iamaman11/powershell-admin-mcp).
Candidate native MCP 0.8.1-dev; .NET 10; official OpenAI tunnel-client 0.0.21; SQLite schema 4. The installed commit and version must be read back from the actual service. CI success is not remote acceptance.

## Architecture and execution context

One Windows service (SYSTEM) owns the durable SQLite queue, state transitions, encrypted job data and concurrent system execution. One existing interactive elevated tunnel host runs the same native PowerShellRunner for USER jobs in its own Windows logon. The MCP frontend uses the existing authenticated local pipe. The service derives the SID, logon SID and session from Windows rather than accepting arbitrary user IDs. No password/token duplication, separate tunnel, new service, or automatic user impersonation for execution.

`powershell_exec` and `powershell_job submit` default to USER for non-SYSTEM clients; explicitly set execution_context=SYSTEM for service operations. USER uses the existing administrator profile, HKCU, environment and user network/application permissions. Unavailable matching runner returns USER_CONTEXT_UNAVAILABLE; never silently switch to SYSTEM. A persisted USER job stays bound to its original logon/sid/session; after logoff a different logon cannot claim/resume it. Old queued USER work can be cancelled; interrupted work needs reconciliation. Reboot checkpoints require explicit SYSTEM. Local admins are trusted administrators, not a security sandbox; physical multi-user fairness and session transitions still require acceptance.

INTERNET owns networking. Tunnel startup intentionally requires the current supported INTERNET deployment: adapter internet-tun Up and exactly one running C:\internet\sing-box.exe. The app reads these prerequisites and does not change routes or start INTERNET. RDP, locked or non-console desktops do not capture the physical desktop; typed failure explains the check. WSL, Docker, browser and mapped drive support depends on the actual caller's profile and privileges; the CLI's presence is not proof that a daemon or distribution is running.

## One operator entry point

Run locally in elevated PowerShell 7 (Windows x64) from the authorized interactive logon:

```powershell
$admin = "$env:ProgramFiles\WindowsAdminMCP\native\WindowsAdminMCP.ps1"
& $admin status
& $admin start
& $admin doctor
& $admin stop
```

Stop disconnects the interactive tunnel but does not stop the durable system service. For deliberate offline maintenance use an independent local console. The canonical updater drains work itself after package verification; callers do not need to clear the queue manually. The old root PowerShell MCP and v0.7 installer/launch path are retired; do not run old copies left outside the native package. The source root installer forwards to installer/Install-Native.ps1. Credential configuration is local-only and one-time; existing protected DPAPI credentials are reused for updates.

## Install, update, trial and rollback

For an existing installation **that contains the updater helpers** (included in native packages from this change onward), the standard command from **any PowerShell directory**, using an interactive elevated PowerShell 7 console, is:

```powershell
& "$env:ProgramFiles\WindowsAdminMCP\native\WindowsAdminMCP.ps1" -Action update
```

The installed operator checks its own manifest-verified updater scripts, then verifies the exact main commit and matching successful CI and downloads and verifies the official package. **Only after package verification**, it stops the tunnel ingress, closes its same-session disposable `WindowsAdminMCP.exe stdio` frontends, atomically blocks new job admission and requests cancellation of queued/running jobs through the service's audited native API. The updater waits up to 60 seconds for the queue to become idle, then replaces files offline, configures **Automatic SCM service startup and an enabled interactive logon tunnel task even for the unaccepted trial**, and starts the service and trial tunnel. External acceptance remains separate: the `.rollback` journal is retained until live MCP evidence is confirmed. Uncertain effects are retained durably as `NEEDS_RECONCILIATION`, never replayed or silently marked successful. A verified candidate binary uses the legacy typed list/cancel API on the first upgrade from an old service without the atomic gate; this happens only after ingress is closed, and the idle readback must still pass. If a verified direct launcher keeps recreating blocked frontends, the updater also closes that launcher (including Codex or PowerShell if necessary). It never stops the console running the update, foreign user sessions, protected Windows processes or the independent INTERNET dataplane. Unconfirmed process identity or ownership, foreign-session clients, repeatedly reappearing frontends or an unresponsive queue stop the update safely. A pre-swap failure resumes admission and restores transport where possible. No local Git checkout, directory lookup or Git command is needed after the one-time transition. The native tunnel host itself checks `internet-tun` Up and exactly one `C:\internet\sing-box.exe`, waits for TUN if absent, and automatically starts the vendor tunnel only when ready. The result reports `TRIAL_LOCAL_READY`, `TRIAL_WAITING_FOR_TUN`, or `TRIAL_NOT_READY`, with the TUN state, `startup_ready` readback and counts of closed local clients, launchers and cancelled jobs. It intentionally does **not** accept the candidate remotely; exercise all eight MCP tools before explicit acceptance.

The artifact acquisition phase now shows downloaded bytes every five seconds and stops an attempt after 240 seconds total or 30 seconds without incoming data. It retries one failed transfer, deleting partial files, and extracts only the named package and SHA-256 receipt from GitHub's outer artifact ZIP. Verification still happens before maintenance, and download errors do not stop the running service or tunnel.

**One-time transition from an older installed package** that lacks `native\installer\Update-Native.ps1`: run the following single command in the same independent elevated PowerShell 7 console. It fetches a fresh, clean `main` into a unique temporary directory automatically; no knowledge of existing Git checkouts is required. Git and GitHub CLI are required for this one-time route, and the temporary checkout is retained for recovery if the trial needs rollback.

```powershell
& { $p=Join-Path $env:TEMP ('WindowsAdminMCP-bootstrap-'+[guid]::NewGuid().ToString('N')); git clone --quiet --depth 1 --branch main https://github.com/iamaman11/powershell-admin-mcp.git $p; if($LASTEXITCODE -ne 0){throw 'BOOTSTRAP_CLONE_FAILED'}; & (Join-Path $p 'WindowsAdminMCP.ps1') -Action update }
```

After that, use only the normal installed command above. A rollback to a legacy package without updater helpers must use a trusted checkout: the installed updater refuses that rollback **before** stopping the service.

If an older installed updater still reports `UPDATE_ACTIVE_JOBS`, its script cannot retroactively acquire this fix because the protected installed helper is manifest-pinned. Run the one-time trusted `main` bootstrap command above after the new version has passed CI; the new verified candidate CLI drains the old service through its existing typed job API. Once installed, the normal `-Action update` command handles busy queues automatically. No manual job cancellation, extra Chrome profile, extension or CDP setup is required.

Manual initial-install and recovery entry points remain available. Use the CI package ZIP and its published SHA-256 from the exact accepted commit, not source-script installation. From a trusted checkout:

```powershell
.\Install-WindowsAdminMCP.ps1 -Action Verify -PackagePath <zip> -ExpectedSha256 <sha256>
.\Install-WindowsAdminMCP.ps1 -Action Install -PackagePath <zip> -ExpectedSha256 <sha256>
.\Install-WindowsAdminMCP.ps1 -Action Prepare -ExpectedCommit <commit>
```

For an existing install, use the independent local console to stop the tunnel and service intentionally and run `Maintenance -ExpectedCommit <installed-commit>` before file replacement. Close existing local stdio clients after finishing their jobs; an idle frontend can hold native DLLs and blocks replacement with `ACTIVE_FRONTEND_MAINTENANCE_REQUIRED`. The installer never kills clients. Prepare enables the existing interactive logon task and Automatic SCM service startup **before** remote acceptance, and configures SCM recovery: restart after 5/15/60 seconds, then stop retrying, with a 24-hour failure-count reset and non-crash failure handling. The host waits for TUN restoration while it is running; no Task Scheduler trigger is added for TUN changes. It never reboots Windows or runs a recovery command. Native recovery readback is included in startup readiness; configuring non-crash handling does not prove its behavior before a future Windows restart. Start via the native operator; verify all eight tools externally, refresh the existing connector catalog, check actual service and vendor, then run `Accept -ExpectedCommit <commit> -RemoteAcceptanceConfirmed` only after real client acceptance. **Accept records evidence and retires the rollback journal; it is no longer required for baseline autostart.** The installer never performs live replacement. `Rollback` is offline only and refuses an older binary that cannot read the current store. Schema 2/3 is backed up before transactional migration to 4; **never restore stale database backups automatically**. Same-schema rollback must preserve jobs and idempotency keys.

## Tools

If physical acceptance rejects an unaccepted schema-4 candidate, use offline `Install -ReplaceUnaccepted -PackagePath <verified-zip> -ExpectedSha256 <digest>` from a trusted checkout to repair it forward. This retains its prior file backup under `.trial-backup-<candidate-commit>` and creates the normal `.rollback` journal for the superseded candidate. Offline Install does not Accept, start services, restore a database or permit a schema downgrade; Prepare later configures normal autostart for the verified replacement without falsely accepting it. A failed swap restores the prior trial journal when rollback succeeds; interrupted repairs stay offline and retain backups for explicit recovery. Finish jobs, stop the existing tunnel/service and close stdio clients first.

- powershell_status: service identity (SYSTEM), authenticated frontend identity, USER runner availability, actual assembly/MCP/.NET/store versions, pinned expected tunnel version and local timings.
- powershell_exec: durable single step, execution timeout 1–120 s, synchronous wait capped at 90 s from entry. PENDING carries job_id; inspect it, don't resubmit with a different key.
- powershell_job: submit (1–8 steps, each timeout 1–3600 s), status by job_id or operation_key, logs (step_index **1-based**, 1..total_steps), list with state_filter/page_size (1–100)/before_seq cursor, cancel, resume, reconcile, prune.
- powershell_screenshot: PNG ImageContent; bounded region and image size. Requires authorized default physical console; never unlocks or switches sessions. Returns typed reason and local timing on failure.
- windows.list_windows: lists up to 100 titled, visible top-level windows on the authorized physical USER desktop, including covered windows. Each `window_id` binds HWND, PID, process start and session. Never changes focus.
- windows.inspect_window: reads a bounded UI Automation control tree without activating elements; depth 12 / 100 returned nodes by default, hard cap depth 16 / 100 returned nodes, with at most 2,000 traversed elements per call. `offset` (default 0) and `next_offset` allow bounded pages when Chrome has many tabs or the page has many controls. Optional `name_contains` (case-insensitive accessible name, 1–128 chars), typed `role_filter` (e.g. `BUTTON`, `TEXT`, `CHECK_BOX`) and `wait_ms` (0–1500, only with a filter) find and wait for dynamic controls; no match within the requested wait yields `STATE_NOT_OBSERVED`. Optional `start_path` **together with** a fresh `start_element_id` scopes inspection to a fingerprint-verified subtree. The `path` is never authority by itself. Optional `include_details=true` (max 10 nodes) returns bounds, keyboard focusability/focus and a provider-reported clickable point without changing focus. Names of password, Edit and Document fields are redacted, never searched by name, and input **values are never returned**. Each `element_id` is valid for 30 seconds. Provider access runs in a disposable 5-second worker. Pages are live snapshots, not an atomic cross-page snapshot; always use the fresh identity when acting.
- windows.capture_window: captures **only** the selected HWND with Windows Graphics Capture, including when the window is covered. Requires `window_id` and authorized USER desktop. Output is a PNG ImageContent with structured status, max width 256–1920 (default 1280), max 2 MB and 1080 pixel height. GPU access runs in a 10-second killable worker. Minimized, capture-protected, locked/remote or unsupported windows can fail explicitly. There is no silent desktop-screenshot fallback.
- windows.invoke_element: one typed action on a fresh UIA or CDP element reference: `INVOKE`, `CLICK`, `SELECT`, `TOGGLE`, `SET_VALUE` (Edit or ComboBox), `TYPE_TEXT` (replace text in Edit/ComboBox), or `KEY_PRESS` (`ENTER|SPACE|ESCAPE`). The `confirm` argument defaults to true for authorized operations; explicit false declines execution. UIA uses native AutoSetFocus=false when supported, whereas physical input verifies exact foreground window, element fingerprint and keyboard focus. Each action has `effect_state=NO_EFFECT|EFFECT_VERIFIED|EFFECT_UNKNOWN` and never replays an uncertain effect. `expected_name`, `expected_change`, and typed `expected_kind=UIA_ELEMENT|UIA_VALUE|WINDOW_TITLE|PROCESS_NAME` support optional observable postconditions; `UIA_VALUE` also requires `expected_value` and verifies a named control's ValuePattern (or CDP AX value). `APPEARS` means the value becomes equal; `DISAPPEARS` means it ceases to equal. Text values are bounded to 4096 characters and omitted from audit logs. Failed UIA calls preserve HRESULT and stage; success is never inferred solely from dispatch.

For background Chrome content, `windows.inspect_window(scope=TABS)` enumerates tabs in the exact verified Chrome window, returning unique `tab_id`, title, URL (without query) and `origin`. Optional `expected_profile` checks the **native Chrome toolbar profile button** (e.g. `Valer`); optional `expected_origin` and `expected_url` filter tabs to the requested origin or exact full URL. `windows.inspect_window(scope=TAB, tab_id=...)` reads that tab's CDP accessibility tree even if it is not active, returning short-lived `c1` element references bound to the tab, document loader, element identity, window and full URL. `windows.invoke_element` uses that reference for background `CLICK`, `KEY_PRESS` or `TYPE_TEXT` without stealing desktop focus; the same `expected_profile`, `expected_origin` and `expected_url` can be supplied again to verify the native browser context immediately before dispatch. Same-named tabs remain distinct. A context mismatch is a typed result, never a fallback to another tab. In-page overlays and disabled controls are not bypassed; `TARGET_OCCLUDED` is explicit. Legacy `scope=WINDOW` continues to use UIAutomation, and Chrome toolbar/native apps retain safe physical fallback. Chrome 144+ must explicitly allow Remote Debugging at `chrome://inspect/#remote-debugging`; no extension, second service, browser profile copying, or arbitrary scripting interface is introduced. Full live background acceptance requires a real authorized Chrome session and cannot be proven by synthetic CI fixtures.

All four window tools reject SYSTEM, other sessions, non-default/unavailable desktop access and stale window identities. Not every Windows application implements UI Automation or permits capture; never interpret an unsupported action as a successful click. No Chrome extension or external Node.js runtime is required, and Chrome CDP is optional. The exact eight-tool `tools/list` contract (including typed semantic filters, bounded wait and key selection) is CI-tested within a 24 KiB JSON budget (24,500 bytes maximum, leaving transport headroom). If the external connector omits these parameters, its catalog is stale; do not invent untyped argument encodings or bypass the bounds.

All commands are trusted administrative PowerShell, not sandboxed. Timeout/cancel may leave side effects. A disconnected request does not grant permission to replay. USER worker loss records unknown effect; jobs are not automatically retried.

## Unknown effects and history

Use logs to inspect the interrupted step. Original source is encrypted for new interrupted jobs; `interrupted_source_available` discloses availability without exposing text. `include_interrupted_command=true` is an explicit operator-authorized read only for the unresolved step and may reveal sensitive script text; never request it automatically. Some legacy v3 commands were already erased and cannot be recovered from the store.

Use reconcile with decision EFFECT_CONFIRMED, NO_EFFECT_CONFIRMED or UNKNOWN and concise evidence. The service stores the immutable decision, time, correlation and authenticated local caller SID; the original execution status and key remain. RECONCILED never means successful execution. Repeating an identical decision is idempotent, contradictory changes conflict. Resume cannot replay interrupted work.

Independent jobs start individual native PowerShell processes on demand, with no fixed worker count. Admission requires both free physical memory and available commit of at least max(1 GiB, 5% of installed RAM) plus 256 MiB for each running and newly admitted execution; the store remains the queue. This is a startup safety reserve, not a hard memory limit on PowerShell. Memory observations/active reservations are diagnostic estimates and not per-script hard resource limits. A named resource_key serializes active steps using that same name; '*' is globally exclusive while active and a queued exclusive step drains existing work. Reboot plans use global exclusivity. Callers must declare conflicts: arbitrary scripts cannot be analysed reliably for shared effects. Locks cover execution of a step, not an entire paused plan or unknown external effects.

The store keeps at most 10,000 detailed job histories and warns above 80%. Terminal output is compacted after 30 days in bounded batches (explicit prune, retain_days 0–30, default 30, up to 8 per call). NEEDS_RECONCILIATION and active jobs are excluded. Original command/result hashes, output digests and lengths, state, audit metadata, operator evidence and operation keys remain. Compacted logs return HISTORY_PRUNED, not a new execution. If capacity is exhausted by protected/recent records there is a typed STORE_FULL; use supported reconcile/prune, never edit SQLite manually. Indefinite idempotency metadata still requires disk capacity monitoring. audit_persisted means a stored step result or retained audit summary, never mere admission and never an independent external audit claim.

## Diagnostics and evidence

Timing fields include UTC start, frontend tool_ms, scm_ms, ipc_ms, service_ms, store_wait_ms, store_ms, capture_ms when relevant. Durably stored results include queue_wait_ms, step start/end and runner elapsed_ms when known. Timings overlap (IPC includes service); don't add them indiscriminately. Service warnings correlate slow local IPC. The official runtime owns cloud polling and response delivery; local timing boundaries do not identify cloud enqueue/delivery/client latency on their own. Match remote call IDs and UTC against supported tunnel evidence. Do not report unrelated latency samples as one trace.

Doctor is read-only. Without external evidence Remote/Desktop remain NOT_TESTED. To incorporate a real external acceptance result, supply a bounded JSON receipt to `& $admin doctor -EvidencePath <receipt.json>`. Its scope is operator-attested external evidence, not a live cloud or desktop probe. It must match current commit, service and tunnel generation and age <=24h; doctor independently checks durable short/long/cancelled job records. Screenshot is an operator-attested PNG receipt; never save the actual image. Example fields (camelCase, replace with actual observed values):

```json
{
  "schema": 1,
  "commit": "<exact commit>",
  "runId": "<unique run id>",
  "observedUtc": "<UTC>",
  "servicePid": 123,
  "serviceStartedUtc": "<UTC from status>",
  "tunnelPid": 456,
  "tunnelStartedUtc": "<UTC from doctor>",
  "statusCorrelationId": "<32 hex>",
  "shortJobId": "<32 hex>",
  "longJobId": "<32 hex>",
  "cancelledJobId": "<32 hex>",
  "screenshotCorrelationId": "<32 hex>",
  "screenshotSessionId": 1,
  "screenshotWidth": 16,
  "screenshotHeight": 16,
  "screenshotPngBytes": 148,
  "screenshotMime": "image/png"
}
```

Run short command Write-Output 'WADMIN-ACCEPT-<runId>-SHORT' with key <runId>-exec; long command Start-Sleep -Seconds 130; Write-Output 'WADMIN-ACCEPT-<runId>-LONG' with key <runId>-long; submit the long and queued steps with the same resource_key accept-<runId>; cancel the harmless queued step with key <runId>-queue before long completes. Independently submit a short command while the long job is active to confirm actual concurrent dispatch. Record actual UUIDs, client-side latencies and exact success/error status. Screenshot fields are optional if not tested.

For physical smoke acceptance, use the trusted checkout's `tests/Test-InstalledCandidate.ps1 -Exe <installed-native-exe>` from the authorized elevated user session after trial startup. It checks actual USER profile/HKCU/elevation, two concurrent USER jobs with independent SYSTEM execution, and distinct audited output. Opt in to `-CheckUserResources` on the target workstation to verify existing GitHub CLI authorization and read-only WSL/Docker-context queries; no secret or screen image is saved. This is local SDK evidence, not external tunnel acceptance.

## Acceptance boundaries

Windows CI exercises isolated service crashes, durable jobs, SQLite migration, checkpoint/reboot guards, package upgrade and compatible rollback, SDK contracts, queue/timeout/cancel/idempotency, and local latency fixtures. Real Windows reboot is excluded by owner instruction; forced physical power loss, live tunnel interruption, actual 24h soak, RDP/locked/logoff scenarios, real administrators, actual installed upgrade and external PNG acceptance must be separately evidenced. No green CI or local readiness response alone proves these.

Reference: [Issue #2](https://github.com/iamaman11/powershell-admin-mcp/issues/2).
