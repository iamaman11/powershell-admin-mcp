# MCP tool acceptance ledger (2026-11-24)

This ledger distinguishes a successful *specific test* from acceptance of a whole
tool. It supplements [#56](https://github.com/iamaman11/powershell-admin-mcp/issues/56)
and [#72](https://github.com/iamaman11/powershell-admin-mcp/issues/72), not a
replacement for their requirements. `PASS` means an observed result; `NOT_TESTED`
means no qualifying end-to-end result. A typed validation rejection does not
prove that a modifying operation works.

## Baselines

- Source `main`: `eb2391408c482dc077d47bbfc1fe6b69e2ffcad2`.
- PR #89 [Windows + Release PASS](https://github.com/iamaman11/powershell-admin-mcp/actions/runs/45314481689),
  exact-main [Windows + Release PASS](https://github.com/iamaman11/powershell-admin-mcp/actions/runs/45315467325).
- Connected USER Windows frontend: `0.8.1-dev+443e70f0efd6d43e19a40eceb822cb24ccc90d51`.
  The update is still a **trial** (`rollback_pending=true`); it is not ACCEPTED.
- Local SCM/status `OK`; doctor `DEGRADED` because of **two**
  `NEEDS_RECONCILIATION` USER jobs. Their effects are **unknown**. Do not
  resubmit or reconcile them without independent evidence.

## Published tools (17)

All 17 methods are present in the source tool catalog. Unless specified,
`PASS` below refers to the installed Windows trial, not exact-main deployment.

| MCP tool | Observed test outcome | Remaining acceptance boundary |
| --- | --- | --- |
| `powershell_status` | **PASS** SCM identity, USER context, doctor and typed Tunnel metrics | Independent remote/desktop recovery evidence **NOT_TESTED** |
| `powershell_update` | **PASS** read-only STATUS returned the exact pending trial and rollback | New verified install, acceptance and physical rollback **NOT_TESTED**; do not start while trial pending |
| `powershell_job` | **PASS** read-only LIST/STATUS identified both unknown-effect jobs | USER/SYSTEM submission, cancellation, interrupted-job recovery and multi-client fairness **NOT_TESTED** in this trial |
| `powershell_exec` | **PASS** bounded read-only `Get-Date` and temp-root query under USER | Timeout/lost-response, SYSTEM effect, cancellation and no-replay physical tests **NOT_TESTED** |
| `powershell_screenshot` | **NOT_TESTED** image acquisition on this desktop | Privacy-safe isolated visual capture still required |
| `windows.list_windows` | **PASS** bounded native USER window enumeration without activation | Multi-session and stale identity matrix **NOT_TESTED** |
| `windows.inspect_window` | **PASS** bounded real UIA inspection of a native utility window | Chrome per-tab targeting, stale tab and ambiguous profile matrix **NOT_TESTED** |
| `windows.capture_window` | **PASS** invalid ID rejected without capture | Successful capture/occluded window test **NOT_TESTED** in this trial |
| `windows.manage_window` | **PASS** invalid ID rejected before effect | MOVE/RESIZE/CLOSE native readback **NOT_TESTED** on this desktop |
| `windows.invoke_element` | **PASS** invalid ID rejected before effect; #89 CI verified disabled/stale UIA safety | Live enabled UIA/CDP effects on installed package **NOT_TESTED** |
| `windows.invoke_locator` | **PASS** invalid locator window rejected; #88 CI exercised single-owner CDP | Independent live multi-client action routing **NOT_TESTED** |
| `windows.chrome_connection` | **PASS** LIST and scoped STATUS: approved Chrome CDP connected with broker generation | `target_ready=false`; no per-tab action effect or disconnect/recovery accepted |
| `windows.file_query` | **PASS** STAT and LIST in an isolated temp fixture, typed version/hash | ACL/owner metadata and adversarial path tests **NOT_TESTED** in this trial |
| `windows.file_read_chunk` | **PASS** bounded read returned `COMPLETED` for a newly written 4-byte fixture and installed manifest | Independent byte-for-byte read comparison, large/locked/changed-version fixtures **NOT_TESTED** in this trial |
| `windows.file_write` | **PASS** `WRITE_ATOMIC` returned `COMMITTED/EFFECT_VERIFIED`; independent SHA-256 matched | Lost ACK, crash window and replacement CAS **NOT_TESTED** on installed trial |
| `windows.file_manage` | **PASS** MKDIR, COPY, MOVE, DELETE and independent readback in isolated temp fixture | SYSTEM, cross-volume and interrupted effects **NOT_TESTED** on installed trial |
| `windows.system_query` | **PASS** processes, services, interfaces, Event Log, registry and scheduled tasks | TCP by PID exists in source `main`, not in the older installed schema; live TCP **NOT_TESTED** |

File fixture cleanup was independently verified `NOT_FOUND` after recursive
DELETE. A nonrecursive directory DELETE first returned `NOT_EMPTY/NO_EFFECT`
because `file_write` had retained its `.wmcp-*.journal` sidecar; the journal
was the only remaining entry. This is a **reproduced usability limitation**,
not evidence of an unsafe duplicate write. Durable receipts are safety-critical:
do not remove or relocate them without crash/recovery tests.

## Gaps across the tool catalog

The native `windows.system_query` covers seven read-only domains in `main`
(including new TCP connections), but not yet typed process trees/wait chains,
service and task mutations, WER/ETW tracing, active DNS/TLS/HTTP checks or file
ACL inspection. Arbitrary PowerShell is possible with administrative context,
but is not proof that these **native typed** capabilities exist. A second
administrative service, broker or overlapping MCP method is not justified.

The following are **NOT_TESTED** for full product acceptance: all 17 methods
under both applicable USER/SYSTEM contexts; 1/2/4/8 actual remote MCP clients;
resource-independent concurrency and cancellation; lost-ACK safety; UAC/Chrome
consent transitions; worker kill/reconnect; cold/warm runtime performance;
package install and rollback on the user machine. #88/#89 isolated CI is
valuable evidence only for the cases it actually executed.

## CI time evidence and revised ownership

From #89 PR Windows job logs (elapsed per major step, rounded): Runner tests
**135 s**, native service/MCP tests **53 s**, full offline package/rollback
**211 s**. The full package pass was by far the largest PR cost. The existing
exact-main run used smoke package/rollback in roughly **27 s** and skipped
Runner after a verified PR.

The workflow now assigns tests by event:

| Event | Runner + service | Offline package | Experimental probes |
| --- | --- | --- | --- |
| Normal non-draft PR | Full | Smoke | Skipped |
| Main push with matching accepted PR | Service smoke | **Full** | Skipped |
| Direct/unproven main push | Full | **Full** | Skipped |
| Scheduled/manual | Full | **Full** | Full |

Each run publishes the exact-SHA package **once**; Release only verifies the
downloaded artifact. Unused research dependency graphs are no longer restored
on normal PR/main runs. This moves (does not delete) the costly offline
acceptance to the exact commit that can actually be installed. The desired
1–5 minute ordinary-PR/normal-main duration is an **unproven target** until
new Windows run logs demonstrate it. Direct/unproven pushes and weekly full
maintenance are allowed to take longer; their additional coverage is explicit.
