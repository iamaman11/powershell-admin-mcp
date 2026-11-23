# WindowsAdminMCP architecture and acceptance boundaries

This document is a short code map; the detailed executable specification and
acceptance matrix live in [Issue #56](https://github.com/iamaman11/powershell-admin-mcp/issues/56).

## One owner per effect

| Owner | Responsibility | Implementation |
| --- | --- | --- |
| MCP frontend | Typed tool schemas, validation, dispatch and JSON results | `AdminTools`, `WindowTools`, `ChromeConnectionTools`, `FileTools`, `UpdateTools` |
| SYSTEM service | SQLite admission, encrypted durable PowerShell queue, idempotency, cancellation and recovery | `AdminService`, `JobCoordinator`, `StoreSchema` |
| USER worker | UIA, Win32, WGC, user desktop and session-bound operations | `UserWorker`, `WindowInspector`, `WindowAction`, `WindowCapture` |
| Chrome child | Native consented CDP connection, browser context/window/profile binding and background tab actions | `WindowChromeBroker`, `WindowChromeCdp` |
| Native file transaction | Exact USER/SYSTEM token, same-volume stage plus bounded journal, SHA-256 and atomic replacement | `NativeFileWriter`, `FileWriteContracts`, `FileTools` |
| Native operator | Verified package/SCM/Task Scheduler maintenance, exact SHA, trial and rollback | `installer/*`, `PackageIntegrity`, `Doctor` |

There is **one** SCM service, **one** SQLite job store, **one** elevated interactive
USER worker and **one supervisor** for N independently owned official tunnel
runtime children. Each distinct tunnel ID uses a separate encrypted key, local
health port, vendor Job Object and MCP stdio child; their SYSTEM job store is
shared, without a second daemon or database. A disposable UIA
worker isolates providers that can block. The CDP child is owned by the USER
frontend, not a separate permanent browser manager. No extra HTTP API.

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
   consent. A successful dispatch does not prove the postcondition.
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
