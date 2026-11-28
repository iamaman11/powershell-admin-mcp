# WindowsAdminMCP architecture and acceptance boundaries

This document is a short code map; the detailed executable specification and
acceptance matrix live in [Issue #56](https://github.com/iamaman11/powershell-admin-mcp/issues/56).

## One owner per effect

| Owner | Responsibility | Implementation |
| --- | --- | --- |
| MCP frontend | Typed tool schemas, validation, dispatch and JSON results | `AdminTools`, `WindowTools`, `ChromeConnectionTools`, `ChromeTabTools`, `ChromeProtocolTools`, `FileTools`, `SystemTools`, `TcpConnectionTools`, `ExpertDiagnosticsTools`, `NetworkProbeTools`, `NativeAdminTools`, `UpdateTools` |
| SYSTEM service | SQLite admission, encrypted durable PowerShell queue, idempotency, cancellation and recovery | `AdminService`, `JobCoordinator`, `StoreSchema` |
| USER worker | UIA, Win32, WGC, user desktop and session-bound operations | `UserWorker`, `WindowInspector`, `WindowAction`, `WindowCapture` |
| Chrome child | Native consented CDP connection, browser context/window/profile binding and background tab actions | `WindowChromeBroker`, `WindowChromeCdp` and its partial-class modules |
| Native file transaction | Exact USER/SYSTEM token, same-volume stage plus bounded journal, SHA-256 and atomic replacement | `NativeFileWriter`, `FileWriteContracts`, `FileTools` |
| Native operator | Verified package/SCM/Task Scheduler maintenance, exact SHA, trial and rollback | `installer/*`, `PackageIntegrity`, `Doctor` |

There is **one** SCM service, **one** SQLite job store, **one** elevated interactive
USER worker and **one supervisor** for N independently owned official tunnel
runtime children. Each distinct tunnel ID uses a separate encrypted key, local
health port, vendor Job Object and MCP stdio child; their SYSTEM job store is
shared, without a second daemon or database. A disposable UIA
worker isolates providers that can block. The CDP child is owned by the USER
frontend, not a separate permanent browser manager. No extra HTTP API.

## Code boundaries and public schemas

The registered MCP surface lives only in `Program.cs` (`stdio`): **24 tools**.
Each tool declares typed arguments and a result DTO. `powershell_screenshot`
deliberately returns image content; the other 23 tools expose structured
output schemas. CI reads the *actual* SDK-generated `tools/list` and checks
the exact inventory, structured outputs and a bounded 80-KiB catalog.

Keep these responsibilities separate when adding functionality:

| Source module | Single responsibility |
| --- | --- |
| `SystemContracts.cs` | Native system-query enum and immutable public DTOs |
| `SystemSnapshot.cs` | Argument gate and read-only OS-backed query dispatch |
| `SystemProcessQueries.cs`, `SystemExtraQueries.cs`, `SystemTcpQueries.cs` | Subject-specific native API calls |
| `SystemTools.cs` | Legacy `windows.system_query` MCP entry point |
| `TcpConnectionTools.cs` | Narrow `windows.tcp_connections` contract and snapshot projection |
| `WindowChromeConnection.cs` | One authorized socket owner, Chrome consent and profile/endpoint checks |
| `ChromeManagedProfile.cs` | Strict managed profile identifiers and per-user data-directory boundary |
| `ChromeTabTools.cs`, `WindowChromeTabManagement.cs` | Typed tab lifecycle, audited intent, independent effect verification |
| `ChromeProtocolTools.cs`, `WindowChromeProtocol.cs` | Version-aware generic CDP schema discovery, JSON schema checks, browser/tab scope, explicit focus authority, audited command dispatch, bounded subscription results |
| `WindowChromeTabMapping.cs` | Exact native window-to-CDP target association |
| `WindowChromeTabInspection.cs` | Read-only accessible document/tab snapshots |
| `WindowChromeTabActions.cs` | Pre-dispatch identity checks, one effectful input and postcondition observation |
| `WindowChromeUiaBridge.cs` | Optional semantic UIA-to-background-CDP bridge |
| `WindowChromeCdp.cs` | Shared WebSocket reader, pending command map, per-call deadlines, late-reply discard and bounded event subscription queues |

`WindowChromeCdp` remains a *single partial class*, not multiple CDP owners.
Splitting source files does not create another socket, process, state store or
permission path. A timed-out individual `Page.getFrameTree` call cancels its
waiter; the WebSocket reader stays alive for other tabs. The legacy
`USER_BROWSER` mode uses Chrome's native per-session consent; `MANAGED_BROWSER`
selects only an explicitly launched, non-elevated Chrome with a dedicated
user-data-dir under `%LOCALAPPDATA%\WindowsAdminMCP\ChromeProfiles`.
Both modes use the existing one-process/one-WebSocket broker and require
fresh native window and profile identity for each operation. An explicit
mode switch is rejected while the prior connection is active. `OPEN` has no
CDP-native target-window selector, so it requires one Chrome window and
verifies the new target's browser window identity after creation.

The generic `windows.chrome_cdp` entry point prefers the running browser's
`/json/protocol` schema. Approval-based Chrome may return HTTP 404 for `/json/*`
despite a fully working consented WebSocket. For `DESCRIBE`, this mode uses an
embedded, upstream-commit-pinned official CDP reference (browser and JavaScript
domains), verified against SHA-256 digests at load time and clearly labeled
`BUNDLED_REFERENCE_SCHEMA_NOT_RUNTIME_VERIFIED`. This reference describes
possible commands, events and parameter types, **not the exact methods
implemented by the connected Chrome**. `CALL` never uses the bundled reference
as a whitelist or parameter validator: Chrome itself validates commands and
JSON on the existing consented WebSocket. If the reference is unavailable,
`Schema.getDomains` on the same WebSocket supplies `DOMAINS_ONLY` metadata when
available. Runtime metadata is cached per broker generation, while the pinned
reference is shared across generations; no additional socket or debugging
endpoint is created. `DESCRIBE`, `TARGETS`, `CALL`, `SUBSCRIBE`, `EVENTS`
and `UNSUBSCRIBE` remain one typed contract, and arguments are bounded JSON
objects regardless of the metadata source. By default target selection requires a
unique native window mapping. An explicit browser-scoped opt-in may select
an exact `(browser_window_id, tab_id)` pair through CDP even when native HWND
geometry is ambiguous; the verified native window/profile remains the
**anchor**, not an assertion about a destination tab's different profile.
Focus-changing commands require separate `allow_foreground` authority. The
same ReceiveLoop handles subscription events with bounded queues, paging and
overflow indicators. A CDP command acknowledgement is not external site-state
verification; dropped or unknown effects are never automatically replayed.

The legacy `windows.system_query(kind=TCP_CONNECTIONS)` wire result is unchanged. The
dedicated `windows.tcp_connections` result projects exactly seven fields and
its `kind` enum admits only `TCP_CONNECTIONS`; adding unrelated fields to its
output is a contract regression.

## Identity, effect and retry rules

1. Service-bound PowerShell jobs are durable by operation key. A timeout after
   dispatch can be `NEEDS_RECONCILIATION`; never resubmit automatically.
2. UIA and CDP actions require fresh native window identity, session, target
   and (for Chrome) profile/origin validation. CDP `TargetInfo.browserContextId`
   is optional: a missing field is preserved as an explicit null context,
   never treated as proof that the page belongs to a different profile. A
   browser target still needs a unique native HWND title/bounds to
   `Browser.windowId` mapping. Multiple contexts within that **same verified
   browser window** do not make its window identity ambiguous; tabs are
   selected by `Browser.getWindowForTarget` window ID, never by URL or context
   alone. When overlapping/maximized Chrome windows contain the same titled
   page, an on-demand **isolated and bounded** UIA snapshot of the selected
   window's real tab-strip siblings must supply a page title belonging to
   exactly one candidate CDP browser window. Mixed, truncated, absent or
   contradictory evidence fails closed; no page contents or title fingerprints
   are logged. Zero matching window IDs yield `CDP_TARGET_NOT_FOUND`; unresolved
   multiple window IDs yield `CDP_TARGET_AMBIGUOUS`. Neither condition dispatches a
   UI effect, invents a foreground/profile match, or revokes native Chrome
   consent. Just before `Input.dispatch*` or the potentially effectful
   `DOM.focus`, a second CDP checkpoint checks native HWND/PID/session,
   profile, target ID/URL, browser window ID and main-frame loader ID. A
   changed snapshot fails closed without input; a failure after `DOM.focus`
   is an unknown effect and cannot be replayed. This narrows the race window,
   but does not claim that navigation and remote dispatch are atomic. For
   native UIA, `0x80040200` after a fresh enabled snapshot is reported as
   `UIA_ELEMENTNOTENABLED_UNKNOWN_EFFECT`, including the HRESULT and method;
   it is not mistaken for a verified disabled control or a retryable failure.
   A successful dispatch does not prove the postcondition.
3. `EFFECT_UNKNOWN` is not retryable. A read-only probe or a command demonstrably
   rejected **before dispatch** can be retried within a bounded budget.
4. An update is an exact CI artifact tied to a commit. Trial installation keeps
   rollback; only explicit remote acceptance retires that journal.
5. Native administrative permissions are bounded by the real Windows security
   boundaries and Chrome's consent. There is no artificial ControlType/AX-role
   whitelist.

## Trace examples

- **PowerShell job:** MCP `powershell_job` → SYSTEM `JobCoordinator` → USER
  session runner or SYSTEM execution → durable result/audit. Interrupted
  execution is never silently replayed.
- **Background Chrome action:** MCP `windows.invoke_element` → exact HWND/PID/
  session/profile validation → existing CDP broker → DOM event → observed
  effect with `execution_method=CHROME_CDP`. An unrelated foreground window
  remains unaffected when CDP supports the action.
- **Desktop input policy:** `BACKGROUND_ONLY` is the default for both UI
  action tools. Only consented, addressed CDP effects can dispatch; native
  UIA patterns and OS mouse/keyboard input are rejected before dispatch because
  third-party UIA providers can activate windows even with `AutoSetFocus=false`.
  Native UIA and OS input require explicit `ALLOW_FOREGROUND_INPUT`; this is a
  risk opt-in, not a promise that the operation will need to activate a window.
  Initial Chrome permission requests likewise do not activate a window without
  separate opt-in; consent is still granted only by Chrome/the user.
- **Native file inspection (P0-A read-only):** MCP `windows.file_query` or
  `windows.file_read_chunk` → authenticated real elevated USER context, or
  existing SYSTEM SCM IPC with a bounded 30-second deadline → Win32 file
  handle with no final-link following → identity/version and bounded readback.
  No PowerShell child, second database, permanent daemon, or side-effect retry.
  Directory pagination fingerprints the observed directory and returns
  `CHANGED_SINCE_LIST` rather than continuing from an invalidated cursor.
- **Native file write (P0-A):** MCP `windows.file_write` receives one durable
  `operation_id` with typed `BEGIN/APPEND/COMMIT/ABORT/STATUS`. The existing
  SYSTEM `JobCoordinator` now owns its immutable operation/actor/target-hash
  SQLite row and PREPARE/OBSERVE state transition for USER and SYSTEM tools.
  The global 100,000-row/512-MiB admission cap covers jobs **and** file receipts;
  no second SQLite writer or separate service exists. Actual USER/SYSTEM
  filesystem effects retain their bounded (1024 per directory) same-volume
  sidecar journal and stage. Both SQLite intent and a local `COMMITTING`
  receipt are flushed before atomic rename. On a lost answer, `STATUS` performs
  native readback and reconciles SQLite. Unknown effects block an unverified
  second COMMIT. Physical service-kill/power-loss tests remain unaccepted.
- **Native file and directory lifecycle (P0-A):** MCP `windows.file_manage`
  exposes native MKDIR, COPY, same-volume MOVE and DELETE for USER/SYSTEM.
  Its immutable action/source/destination/CAS/recursive spec is SHA-256-bound
  to a GUID in that same bounded `file_receipts` SQLite table. The service
  persists a COMMITTING intent before any filesystem mutation and an observed
  outcome afterward; lost replies yield a read-only status query and never
  trigger a blind destructive replay. COPY stages on the destination volume,
  followed by one visible rename; directory traversal rejects reparse points.
  No schema migration, new service, subprocess or administrative path policy.
  Unobserved crash windows remain `EFFECT_UNKNOWN` pending manual verification.
- **Window operations (P0-B):** MCP `windows.manage_window` resolves a fresh
  HWND/process/session, calls Win32 once and compares geometry/foreground
  readback. UIA actions accept bounded `SCROLL`, `HOVER`, `DRAG` and native
  key combinations. `windows.invoke_locator` finds exactly one element within
  a bounded worker and fails closed on ambiguous or incomplete searches.
- **Legacy cleanup (#56, one time):** `powershell_update(FORGET_LEGACY)` →
  existing verified interactive Task Scheduler operator → drain and stop SCM →
  offline `LegacyUnknownCleanup` plan/fingerprint → transactional deletion of
  exactly 17 old unknown entries → SQLite integrity/readback → restart SCM/
  tunnel. The installed queue is never edited through a second connection to
  a running service. No old command is executed by cleanup.

## SQLite lifecycle and bounded storage

The SYSTEM `JobCoordinator` remains the **only** writer. It retains exact
`operation_key` and payload hash identity indefinitely because a generic
PowerShell command can have an irreversible effect; deleting the key would
make a later accidental replay indistinguishable from a new request.

Two bounded phases reclaim expensive history: after 30 days, terminal output
is compacted to hashes/lengths; after 90 days the same SQLite owner removes
terminal `job_steps` and encrypted working directory, leaving a durable,
small job tombstone. Unknown, active and waiting effects never qualify.
The permanent keys are limited by a typed total-row/DB-byte safety budget.
If it is exhausted, **new distinct jobs fail closed** while status, readback
and existing-key idempotency stay available. An indefinite workload cannot
both erase arbitrary keys and promise indefinite exactly-once behaviour;
any future key-epoch protocol must be explicit and versioned. All pruning
uses one transaction/FK owner, no external writer or auto-replay.

## Current version and test references

Baseline before #56: `605abdbe`; first accepted cleanup commit: `d656b7b`, SDK ModelContextProtocol `2.4.0`,
.NET 10 LTS (build SDK pinned to `10.0.404`), tunnel runtime `0.0.21`, PowerShell 7.6 LTS.
Version provenance and explicit upgrade/rollback rules live in [`DEPENDENCIES.md`](DEPENDENCIES.md).
CI workflow: `.github/workflows/windows-acceptance.yml`. Exact installed
package/physical acceptance is separate from Windows CI. `NOT_TESTED` must not
be rendered as PASS or FAIL. The 24-hour soak is outside the required scope;
bounded smoke and injected failure checks remain mandatory.

Issue #56 CI fixtures exercise binary exactness (5 MiB + tail), zero bytes,
stale version CAS, duplicate chunks, missing staging state, unique UIA locator,
Win32 geometry, and 20,000 SQLite WAL updates plus 100,000 metadata rows,
`integrity_check`, receipt crash-recovery and reopen. They do not substitute for remote installed package or real service
kill/power-loss evidence.
