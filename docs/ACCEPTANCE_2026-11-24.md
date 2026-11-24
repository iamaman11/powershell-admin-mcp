# WindowsAdminMCP acceptance evidence — 2026-11-24

Scope: the installed Windows application, not unrelated websites or projects.
`PASS` describes only an observed case; `NOT_TESTED` is not a failure or a pass.
The parent acceptance requirements remain [#56](https://github.com/iamaman11/powershell-admin-mcp/issues/56)
and [#72](https://github.com/iamaman11/powershell-admin-mcp/issues/72).

## Verified installed baseline

- Exact installed source: `4f0b5bef0a91a89be5f0629f45b851c83e967ad3`,
  `0.8.1-dev`, .NET `10.0.15`.
- Windows service, vendor, PowerShell, startup, package, boot and local Tunnel:
  `OK`; local operational readiness and USER worker readiness: true.
- Doctor: `DEGRADED`, **only because two historic USER jobs require
  reconciliation**. Both effects are unknown; neither was replayed, reconciled,
  pruned or modified.
- Remote and interactive desktop recovery: `NOT_TESTED` by doctor.
- Update: `COMPLETED/trial_local_ready`; rollback remains available, **not ACCEPTED**.

## Chrome/CDP evidence

- CI [PR #94](https://github.com/iamaman11/powershell-admin-mcp/actions/runs/45333761560):
  Windows + Release `PASS`; fresh owned Chrome profile exercised 100 status →
  inspect → action → effect cycles, including **50 verified clicks and 50 text
  inputs**. Concurrent independent 2/4/8 clients shared **one** worker and
  **one** physical CDP socket. Synthetic delayed/late/out-of-order responses,
  detachment and malformed protocol also passed.
- Exact-main [run](https://github.com/iamaman11/powershell-admin-mcp/actions/runs/45334379709):
  Windows + Release `PASS`, including offline install/rollback fixture.
- On the actual installed Windows: Valer (`Profile 5`) `CONNECTED`, one broker
  PID `16888`, generation `96f984b588fe47b6955d3c0950c5ab01`.
  `scope=TABS` read 24 tabs; a background exact-tab query read 40 GitHub
  buttons. A different inactive GitHub tab returned
  `CDP_TIMEOUT_Page.getFrameTree` after 12.2 s **without disconnecting**;
  the next TABS and TAB calls succeeded under the *same* generation.
- Four subsequent TABS reads each returned all 24 tabs. One earlier transient
  `BOUNDS_MISMATCH` on the previous installed build was not reproduced by
  these four checks. This does **not** prove the geometric mapping flawless.
- No synthetic OS mouse/keyboard input and no web-site mutation on the user's
  live browser. Remaining: reliable per-tab operation on the unresponsive tab;
  native locator, stale identity, multi-profile and consent-loss fault matrix
  in the *installed* configuration: `NOT_TESTED`.

## Native Windows and file evidence

- `powershell_status`: typed SCM identity, service PID and detailed doctor:
  `PASS`.
- `powershell_update`: exact installed SHA and rollback receipt: `PASS` for
  `STATUS`; final `ACCEPT` and physical rollback of the user's install:
  `NOT_TESTED`.
- `powershell_job`: two `NEEDS_RECONCILIATION` USER jobs enumerated: `PASS`.
  Unknown-effect recovery decision: `NOT_TESTED`; never guess an effect.
- `powershell_exec`: read-only USER `%TEMP%` and SYSTEM `whoami` both returned
  `COMPLETED/exit_code=0`: `PASS` for their execution identities.
- `windows.system_query`: process by PID, SCM service, network interfaces,
  Application Event Log, HKLM Registry and Task Scheduler all returned typed
  `COMPLETED`: `PASS` for those six read-only kinds. Native TCP connections exist
  in source, but the currently exposed client tool schema does not advertise
  `TCP_CONNECTIONS`: live MCP acceptance `NOT_TESTED`.
- `windows.list_windows`: 16 native USER windows enumerated: `PASS`.
- `windows.inspect_window`: Notepad background UIA enumeration yielded 16
  buttons without activation: `PASS`. Chrome evidence is separate above.
- `windows.file_manage`, `windows.file_write`, `windows.file_query` and
  `windows.file_read_chunk`: under a new unique USER Temp folder, MKDIR,
  9-byte `WRITE_ATOMIC`, STAT+owner+SHA, exact-byte read, COPY, MOVE and
  recursive DELETE returned verified effects; independent readback matched;
  original source of MOVE was `NOT_FOUND` and entire fixture directory was
  `NOT_FOUND` after cleanup: `PASS` for those concrete operations.
- One bounded negative test: old `WRITE_ATOMIC` with a matching
  `expected_bytes=9` returned `INVALID_ARGUMENT/NO_EFFECT`, although its
  public request contract exposed this optional integrity field. This PR
  makes a matching size valid and an incorrect size fail **before effect**.
- Desktop screenshots/captures, physical window management, live native
  UIA mutations, SYSTEM file mutations, cross-volume/locked-file behavior,
  reboot, offline network recovery, process/service/task administration,
  ACLs, wait chains and ETW/WER traces: `NOT_TESTED` in this pass. UIA and
  browser effectful scenarios have separate owned-fixture CI evidence.

## Release boundary

The preceding evidence is **not** full P0/P1/P2 GO. Do not call the app
fully accepted merely because one browser timeout was isolated. In particular:

1. Prove all declared administrative capability families and denied/stale/
   unknown-effect outcomes on disposable targets, including USER/SYSTEM when
   applicable, without making unbounded changes to the user's environment.
2. Investigate the inactive tab's `Page.getFrameTree` delay without bypassing
   target identity, native window bounds, consent or effect verification.
3. Resolve the two historical unknown-effect jobs only from independent
   evidence. Do not silently replay them or mistake them for code regressions.
4. Do not invoke updater `ACCEPT` until the exact installed commit has passed
   the agreed live acceptance and the rollback decision is explicit.

## Recovery matrix for the unified completion slice

The new isolated [lost-acknowledgement fixture](../tests/Test-LostAcknowledgement.ps1)
is scheduled after the existing real-service acceptance suite on full Windows
CI runs. Its expected assertions are listed here as **planned**, not as
observed passes until that specific SHA's CI finishes successfully. The
fixture runs on a clean disposable GitHub Windows runner only; it must never
be used against a user's installed service or historical jobs.

| Failure / recovery boundary | Executable evidence | Remaining limitation |
| --- | --- | --- |
| Frontend disconnect while an operation runs | Existing `Test-NativeStatus.ps1` three-client fixture kills one frontend and checks independent completion | No remote vendor network partition yet |
| Reply lost after durable job admission | New `Test-LostAcknowledgement.ps1` waits until a second client independently sees the admitted key, kills the first frontend *without reading its reply*, resubmits the identical key, checks single append + conflicting payload refusal, and repeats after SCM restart | Initial CI execution pending; local transport loss is not an Internet outage |
| Process/service crash with unknown effect | Existing `Test-NativeStatus.ps1` kills the real SCM worker after a physical append, verifies `NEEDS_RECONCILIATION`, and denies same-key replay/resume | Does not identify or resolve two historical USER jobs |
| Reboot-gated plan | Existing `Test-NativeStatus.ps1` commits a checkpoint, restarts SCM, proves `SAME_BOOT` cannot release the next step; `RunnerTests` tests full-boot event classification | Genuine *new* kernel boot is **NOT_TESTED** |
| Two interactive Windows users | Native service authorizes local pipe tokens and durable USER jobs are SID/logon-scoped in code | Real distinct-token, simultaneous-session refusal and ownership transition are **NOT_TESTED** |
| Exact installed rollback and recovery | Offline package fixtures test file swap, crash window, SCM policies, durable results, incompatible schema downgrade refusal | Physical rollback and post-reboot remote/desktop recovery on the user's installed computer are **NOT_TESTED** |

### Safe outstanding physical acceptance

Perform this on a separately provisioned, recoverable Windows test machine
with an operator controlling the console. Before any physical reboot or
connectivity disruption, record the exact installed commit, the rollback
receipt, a fresh doctor report, actual Windows session/user IDs, kernel boot
event IDs and a uniquely named test job/marker. Only isolated, newly created
fixtures may be executed. The two historical USER
`NEEDS_RECONCILIATION` entries must be left unchanged.

1. Establish two real interactive Windows sessions with different SIDs. Verify
   both can independently query their authorized data and that USER jobs and
   window/browser session handles cannot cross session boundaries. Probe
   negative cases; do not perform administrator actions on unrelated sessions.
2. For a deliberately created reboot-gated SYSTEM plan, verify the first
   checkpoint is durable, perform a real **full** reboot under the operator's
   control, independently read the old and new kernel event records, and
   verify the postcondition before allowing the next step. Hybrid shutdown,
   cleared/rotated logs or unverifiable boot history must remain blocked.
3. Drop and restore the *test machine's* remote tunnel while a disposable
   idempotency-keyed job runs; compare the durable server receipt and an
   independent marker after reconnect. Unknown-effect jobs must remain frozen.
4. Verify the exact installed rollback path only after authorized maintenance
   and captured recovery evidence. Recheck boot, startup, USER context, remote
   routing and desktop session after the transition.

Until these tests have observed outputs, the verdict remains **NO FULL GO**;
neither a clean CI result nor successful local failover alone substitutes for
physical reboot, distinct-user and remote recovery evidence.
