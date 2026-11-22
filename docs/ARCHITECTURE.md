# WindowsAdminMCP architecture and acceptance boundaries

This document is a short code map; the detailed executable specification and
acceptance matrix live in [Issue #56](https://github.com/iamaman11/powershell-admin-mcp/issues/56).

## One owner per effect

| Owner | Responsibility | Implementation |
| --- | --- | --- |
| MCP frontend | Typed tool schemas, validation, dispatch and JSON results | `AdminTools`, `WindowTools`, `ChromeConnectionTools`, `UpdateTools` |
| SYSTEM service | SQLite admission, encrypted durable PowerShell queue, idempotency, cancellation and recovery | `AdminService`, `JobCoordinator`, `StoreSchema` |
| USER worker | UIA, Win32, WGC, user desktop and session-bound operations | `UserWorker`, `WindowInspector`, `WindowAction`, `WindowCapture` |
| Chrome child | Native consented CDP connection, browser context/window/profile binding and background tab actions | `WindowChromeBroker`, `WindowChromeCdp` |
| Native operator | Verified package/SCM/Task Scheduler maintenance, exact SHA, trial and rollback | `installer/*`, `PackageIntegrity`, `Doctor` |

There is **one** SCM service, **one** SQLite job store, the existing elevated
interactive USER frontend and the official tunnel runtime. A disposable UIA
worker isolates providers that can block. The CDP child is owned by the USER
frontend, not a separate permanent browser manager. No extra HTTP API.

## Identity, effect and retry rules

1. Service-bound PowerShell jobs are durable by operation key. A timeout after
   dispatch can be `NEEDS_RECONCILIATION`; never resubmit automatically.
2. UIA and CDP actions require fresh native window identity, session, target
   and (for Chrome) profile/context/origin validation. A successful dispatch
   does not prove the postcondition.
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
