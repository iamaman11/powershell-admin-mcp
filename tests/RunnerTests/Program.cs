using System.Diagnostics;

static void Check(bool ok, string label)
{
    if (!ok) throw new InvalidOperationException("FAIL " + label);
    Console.WriteLine("PASS " + label);
}
static async Task<ExecutionResult> Run(string command, int timeout = 10, string? cwd = null, CancellationToken ct = default)
{
    var result = await PowerShellRunner.RunAsync(command, timeout, cwd, ct);
    Console.WriteLine($"runner status={result.Status} exit={result.ExitCode} stage={result.Stage} native_error={result.NativeError} elapsed_ms={result.ElapsedMs}");
    return result;
}
if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
    throw new InvalidOperationException("Isolated Windows CI only");
Check(File.Exists(PowerShellRunner.Executable), "r2.machine_wide_pwsh");
Check(AdminTools.ExecWaitBudget(120)==TimeSpan.FromSeconds(90) && AdminTools.ExecWaitBudget(85)==TimeSpan.FromSeconds(90) &&
    AdminTools.ExecWaitBudget(10)==TimeSpan.FromSeconds(15), "transport.exec_wait_budget");
var simple = await Run("Write-Output 'hello'");
Check(simple.Status == ExecutionState.COMPLETED && simple.ExitCode == 0 && simple.Stdout.Trim() == "hello", "r2.stdout_exit");
string prefix = "Write-Output 'Проверка Unicode 🚀'\n#";
var unicode = await Run(prefix + new string('x', 11982 - prefix.Length));
Check(unicode.Status == ExecutionState.COMPLETED && unicode.Stdout.Trim() == "Проверка Unicode 🚀", "r2.long_unicode_stdin");
var explicitExit = await Run("exit 7");
Check(explicitExit.Status == ExecutionState.COMMAND_ERROR && explicitExit.ExitCode == 7, "r2.explicit_exit");
var thrown = await Run("throw 'EXPECTED_PROBE_FAILURE'");
Check(thrown.Status == ExecutionState.COMMAND_ERROR && thrown.Stderr.Contains("EXPECTED_PROBE_FAILURE"), "r2.exception");
var warning = await Run("Write-Warning 'expected warning'; Write-Output 'after'");
Check(warning.Status == ExecutionState.COMPLETED && warning.Warnings?.Contains("expected warning") == true &&
    warning.Stdout.Trim() == "after", "r2.warning_stream");
var nonTerminating = await Run("Write-Error 'expected error' -ErrorAction Continue; Write-Output 'after'");
Check(nonTerminating.Status == ExecutionState.COMMAND_ERROR && nonTerminating.Stderr.Contains("expected error"), "r2.nonterminating_error");
var large = await Run("[Console]::Out.Write(('a' * 100000)); [Console]::Error.Write(('b' * 100000))");
Check(large.Status == ExecutionState.COMPLETED && large.OutputTruncated && large.Stdout.Length == 16000 &&
    large.Stderr.TrimEnd().Length == 16000, "r2.bounded_concurrent_drain");
var cwd = await Run("(Get-Location).Path", cwd: Path.GetTempPath());
Check(cwd.Status == ExecutionState.COMPLETED && Path.TrimEndingDirectorySeparator(cwd.Stdout.Trim()) ==
    Path.TrimEndingDirectorySeparator(Path.GetTempPath()), "r2.working_directory");
var timeout = await Run("Start-Sleep -Seconds 30", 1);
Check(timeout.Status == ExecutionState.TIMEOUT_UNKNOWN_EFFECT && timeout.ElapsedMs < 10000, "r2.timeout");
using (var cancel = new CancellationTokenSource())
{
    cancel.Cancel();
    var before = await Run("Write-Output 'must not execute'", ct: cancel.Token);
    Check(before.Status == ExecutionState.CANCELLED_BEFORE_EXECUTION, "r2.cancel_before_launch");
}
using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
{
    var running = await Run("Start-Sleep -Seconds 30", ct: cancel.Token);
    Check(running.Status == ExecutionState.CANCELLED_UNKNOWN_EFFECT, "r2.cancel_running");
}
// No remote services or arbitrary processes: only a known inert local child.
var tree = await Run("$p=Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\\ping.exe') -ArgumentList '-n','60','127.0.0.1' -PassThru; [Console]::Out.WriteLine($p.Id); Start-Sleep -Seconds 30", 4);
Check(tree.Status == ExecutionState.TIMEOUT_UNKNOWN_EFFECT && int.TryParse(tree.Stdout.Trim(), out _), "r2.child_pid_observed");
int pid = int.Parse(tree.Stdout.Trim(), System.Globalization.CultureInfo.InvariantCulture);
bool gone;
try { using var child = Process.GetProcessById(pid); gone = child.WaitForExit(5000); }
catch (ArgumentException) { gone = true; }
Check(gone, "r2.job_terminated_child");
Console.WriteLine("R2_POWERSHELL_RUNNER=PASS (actual candidate runner, direct CI; not exposed through MCP yet)");

using(var bitmap=new System.Drawing.Bitmap(2400,1800))
{
    using(var graphics=System.Drawing.Graphics.FromImage(bitmap))graphics.Clear(System.Drawing.Color.SteelBlue);
    var png=DesktopCapture.Encode(bitmap,1280);
    Check(png is not null && png.Width==1280 && png.Height==960 &&
        png.Png.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),"r5.synthetic_png_dimensions_and_signature");
}
Check(!DesktopCapture.ValidArguments(0,0,0,10,null,1280),"r5.invalid_region_pair");

var fixtureCredential=new TunnelHost.Credential(1,"tunnel_"+new string('a',32),"inert-test-key");
Check(TunnelHost.Valid(fixtureCredential)&&!TunnelHost.Valid(fixtureCredential with {Version=2})&&
    !TunnelHost.Valid(fixtureCredential with {RuntimeKey="bad\nkey"}),"r6.typed_tunnel_credential");
var launch=TunnelHost.StartInfo(@"C:\Program Files\WindowsAdminMCP\client\tunnel-client-runtime.exe",
    @"C:\Program Files\WindowsAdminMCP\native\WindowsAdminMCP.exe",fixtureCredential);
Check(launch.FileName.EndsWith(@"\client\tunnel-client-runtime.exe",StringComparison.Ordinal)&&
    launch.ArgumentList.Contains("command=\"C:/Program Files/WindowsAdminMCP/native/WindowsAdminMCP.exe\" stdio,channel=main")&&
    launch.ArgumentList.Contains("env:CONTROL_PLANE_API_KEY")&&
    !launch.ArgumentList.Any(a=>a.Contains("inert-test-key",StringComparison.Ordinal))&&
    launch.Environment["CONTROL_PLANE_API_KEY"]=="inert-test-key","r6.stable_vendor_binding_no_key_in_argv");

var up=System.Net.NetworkInformation.OperationalStatus.Up;
var down=System.Net.NetworkInformation.OperationalStatus.Down;
Check(TunDependency.Classify([up],1)==TunReadiness.READY,"r6.tun_ready");
Check(TunDependency.Classify([],1)==TunReadiness.ADAPTER_MISSING,"r6.tun_missing");
Check(TunDependency.Classify([down],1)==TunReadiness.ADAPTER_DOWN,"r6.tun_down");
Check(TunDependency.Classify([up],0)==TunReadiness.DATAPLANE_NOT_RUNNING,"r6.tun_stale_adapter");
Check(TunDependency.Classify([up,up],1)==TunReadiness.ADAPTER_AMBIGUOUS,"r6.tun_duplicate_adapter");
Check(TunDependency.Classify([up],2)==TunReadiness.DATAPLANE_AMBIGUOUS,"r6.tun_duplicate_process");

using(var denied=System.Text.Json.JsonDocument.Parse("""{"schema_version":1,"component":"control-plane","details":{"http_status":403,"consecutive_failures":1}}"""))
    Check(TunnelLifetime.IsAccessDenied(denied.RootElement),"r6.authorization_denial_terminal");
using(var stale=System.Text.Json.JsonDocument.Parse("""{"schema_version":1,"component":"control-plane","details":{"http_status":403,"consecutive_failures":0}}"""))
    Check(!TunnelLifetime.IsAccessDenied(stale.RootElement),"r6.no_stale_denial");
using(var other=System.Text.Json.JsonDocument.Parse("""{"schema_version":1,"component":"control-plane","details":{"http_status":503,"consecutive_failures":2}}"""))
    Check(!TunnelLifetime.IsAccessDenied(other.RootElement),"r6.transient_error_not_auth_denial");
ProcessStartInfo InertChild()
{
    var info=new ProcessStartInfo(PowerShellRunner.Executable){UseShellExecute=false,WorkingDirectory=Environment.SystemDirectory};
    foreach(string arg in new[]{"-NoLogo","-NoProfile","-NonInteractive","-Command","Start-Sleep -Seconds 60"})info.ArgumentList.Add(arg);
    return info;
}
using(var fixture=NativeChildProcess.Start(InertChild()))
{
    fixture.Input.Close();
    int checks=0;
    var end=await TunnelLifetime.WatchAsync(fixture.Process,()=>++checks==1?TunReadiness.READY:TunReadiness.ADAPTER_DOWN,
        ()=>Task.FromResult(false),TimeSpan.FromMilliseconds(50));
    Check(end==TunnelEnd.TUN_LOST && !fixture.Process.HasExited,"r6.tun_loss_detected_on_real_child");
    fixture.StopTree();
    Check(fixture.Process.WaitForExit(5000),"r6.owned_child_terminated");
}
using(var fixture=NativeChildProcess.Start(InertChild()))
{
    fixture.Input.Close();
    var end=await TunnelLifetime.WatchAsync(fixture.Process,()=>TunReadiness.READY,
        ()=>Task.FromResult(true),TimeSpan.FromMilliseconds(50));
    Check(end==TunnelEnd.ACCESS_DENIED,"r6.real_child_auth_stop");
    fixture.StopTree();
    Check(fixture.Process.WaitForExit(5000),"r6.auth_denied_child_terminated");
}

var startUtc=new DateTimeOffset(2026,1,1,12,0,0,TimeSpan.Zero);
var priorBoot=new BootStamp(1000,startUtc,1008,startUtc.AddMilliseconds(1),0);
// A wall clock adjustment between boots must not defeat chronological event evidence.
var nextBoot=new BootStamp(2000,startUtc.AddHours(-1),2008,startUtc.AddHours(-1),0);
Check(BootEvidence.Classify(priorBoot,priorBoot,priorBoot)==BootEvidenceCode.SAME_BOOT,"r4.boot_same_kernel_session");
Check(BootEvidence.Classify(priorBoot,priorBoot,nextBoot)==BootEvidenceCode.NEW_FULL_BOOT_OBSERVED,"r4.boot_new_full_fixture");
Check(BootEvidence.Classify(priorBoot,null,nextBoot)==BootEvidenceCode.AMBIGUOUS,"r4.boot_log_cleared_or_rotated");
Check(BootEvidence.Classify(priorBoot,priorBoot with {TypeUtc=startUtc.AddSeconds(2)},nextBoot)==BootEvidenceCode.AMBIGUOUS,"r4.boot_record_not_same");
Check(BootEvidence.Classify(priorBoot,priorBoot,nextBoot with {BootType=1})==BootEvidenceCode.AMBIGUOUS,"r4.boot_hybrid_not_accepted");
Check(BootEvidence.Classify(priorBoot,priorBoot,nextBoot with {TypeUtc=startUtc})==BootEvidenceCode.AMBIGUOUS,"r4.boot_unpaired_records_rejected");
Check(BootEvidence.Classify(priorBoot,priorBoot,nextBoot with {StartRecordId=999,TypeRecordId=1001})==BootEvidenceCode.AMBIGUOUS,"r4.boot_reused_record_ids_rejected");
var actualBoot=BootEvidence.Observe();
if(actualBoot.Status==BootEvidenceCode.FULL_BOOT_OBSERVED && actualBoot.Stamp is {} stamp)
{
    Check(BootEvidence.After(stamp).Status==BootEvidenceCode.SAME_BOOT,"r4.boot_same_session_native_event_readback");
}
else Console.WriteLine($"NOT_TESTED r4.boot_native_event_readback status={actualBoot.Status}");
Console.WriteLine("R4_BOOT_EVIDENCE_PROBE_COMPLETE (no actual OS reboot; not wired to job transitions)");

// Exact v2 schema from merged main, actual SQLite + real DPAPI ciphertext.
// No service or production directory is used.
string migrationRoot=Path.Combine(Path.GetTempPath(),"WindowsAdminMCP-migration-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(migrationRoot);
try
{
    string database=Path.Combine(migrationRoot,"jobs.db");
    string id=Guid.NewGuid().ToString("N"), key="v2-completed-fixture";
    string command="Write-Output 'v2 preserved'", v2Cwd=Environment.SystemDirectory;
    string Hash(string text)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    // Same anonymous object/property order and defaults as schema v2.
    string payload=Hash(System.Text.Json.JsonSerializer.Serialize(new {steps=new[] {new {command,timeout_seconds=30,pause_after=false}},cwd=v2Cwd}));
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+database+";Pooling=False"))
    {
        db.Open();
        using var create=db.CreateCommand();
        create.CommandText="""
                CREATE TABLE IF NOT EXISTS jobs(
                  seq INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL UNIQUE, op_key TEXT NOT NULL UNIQUE,
                  payload_hash TEXT NOT NULL, command_hash TEXT NOT NULL, cwd BLOB NOT NULL,
                  state TEXT NOT NULL CHECK(state IN ('QUEUED','RUNNING','CANCEL_REQUESTED','WAITING_FOR_RESUME','COMPLETED','FAILED','CANCELLED','NEEDS_RECONCILIATION')),
                  created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL,
                  completed_steps INTEGER NOT NULL DEFAULT 0, step_count INTEGER NOT NULL CHECK(step_count BETWEEN 1 AND 8),
                  CHECK(completed_steps BETWEEN 0 AND step_count));
                CREATE TABLE IF NOT EXISTS job_steps(
                  job_id TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE CASCADE,
                  step_index INTEGER NOT NULL CHECK(step_index BETWEEN 1 AND 8),
                  command BLOB, timeout_seconds INTEGER NOT NULL CHECK(timeout_seconds BETWEEN 1 AND 3600),
                  pause_after INTEGER NOT NULL CHECK(pause_after IN (0,1)), result BLOB,
                  PRIMARY KEY(job_id,step_index));
                PRAGMA user_version=2;
                """;
        create.ExecuteNonQuery();
        using var row=db.CreateCommand();
        row.CommandText="""
            INSERT INTO jobs(job_id,op_key,payload_hash,command_hash,cwd,state,created_utc,updated_utc,completed_steps,step_count)
            VALUES($id,$key,$payload,$hash,$cwd,'COMPLETED',$now,$now,1,1);
            INSERT INTO job_steps(job_id,step_index,command,timeout_seconds,pause_after,result)
            VALUES($id,1,NULL,30,0,$result);
            """;
        row.Parameters.AddWithValue("$id",id);
        row.Parameters.AddWithValue("$key",key);
        row.Parameters.AddWithValue("$payload",payload);
        row.Parameters.AddWithValue("$hash",Hash(command));
        row.Parameters.AddWithValue("$cwd",JobDataProtection.Protect(v2Cwd,id,"cwd"));
        row.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        row.Parameters.AddWithValue("$result",JobDataProtection.Protect(System.Text.Json.JsonSerializer.Serialize(
            new ExecutionResult(ExecutionState.COMPLETED,Hash(command),12,0,"v2 preserved"),PipeWire.Json),id,"step/1/result"));
        row.ExecuteNonQuery();
    }
    using(var store=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
    {
        store.Initialize(database);
        var saved=store.Dispatch(new(AdminOperation.Job,JobAction.logs,JobId:id),"migration");
        Check(saved.Status==StatusCode.OK && saved.Job?.State==JobState.COMPLETED &&
            saved.Job.Execution is {Stdout:"v2 preserved",AuditPersisted:true},"r4.real_v2_migration_preserves_encrypted_result_and_audit");
        var duplicate=store.Dispatch(new(AdminOperation.Job,JobAction.submit,Key:key,Command:command,WorkingDirectory:v2Cwd),"migration");
        Check(duplicate.Status==StatusCode.OK && duplicate.Job?.JobId==id,"r4.v2_payload_hash_unchanged");
        var counts=store.Summary();
        Check(counts.SchemaVersion==4 && counts.Total==1 && counts.Completed==1,"r4.migration_store_counts");
    }
    Check(StoreSchema.ObserveFile(database) is {Status:StoreSchemaCode.OBSERVED,Version:4},"r6.offline_store_schema_v4");
    Check(StoreSchema.ObserveFile(Path.Combine(migrationRoot,"missing.db")).Status==StoreSchemaCode.ABSENT,"r6.offline_store_absent");
    var orphan=Path.Combine(migrationRoot,"orphan.db");
    File.WriteAllText(orphan+"-wal","fixture");
    Check(StoreSchema.ObserveFile(orphan).Status==StoreSchemaCode.READBACK_FAILED,"r6.offline_store_orphan_wal_rejected");
    var backups=Directory.GetFiles(migrationRoot,"jobs.db.schema2-*.backup");
    Check(backups.Length==1,"r4.v2_backup_retained");
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+backups[0]+";Mode=ReadOnly;Pooling=False"))
    {
        db.Open();using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version";
        Check((long)cmd.ExecuteScalar()! ==2,"r4.v2_backup_schema");
    }
    using(var store=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
    {
        store.Initialize(database);
        Check(store.Summary().Completed==1 && Directory.GetFiles(migrationRoot,"jobs.db.schema2-*.backup").Length==1,
            "r4.v4_reopen_no_duplicate_migration");
    }
}
finally{Directory.Delete(migrationRoot,true);}

// Isolated real store: keyset pagination is stable, filtered and rejects invalid ranges.
string pagingRoot=Path.Combine(Path.GetTempPath(),"WindowsAdminMCP-paging-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(pagingRoot);
try
{
    using var store=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance);
    store.Initialize(Path.Combine(pagingRoot,"jobs.db"));
    JobReply Call(AdminRequest req)=>store.Dispatch(req,"paging");
    for(int i=0;i<5;i++)
        Check(Call(new(AdminOperation.Job,JobAction.submit,Key:"paging-"+i,Command:"Write-Output 1")).Status==StatusCode.OK,"paging.admission."+i);
    Check(Call(new(AdminOperation.Job,JobAction.cancel,Key:"paging-3")).Status==StatusCode.OK,"paging.cancel");
    var p1=Call(new(AdminOperation.Job,JobAction.list,StateFilter:JobState.QUEUED,PageSize:2));
    Check(p1.Status==StatusCode.OK && p1.Jobs is {Length:2} && p1.Jobs[0].OperationKey=="paging-4" &&
        p1.Jobs[1].OperationKey=="paging-2" && p1.NextBeforeSeq is >0,"paging.first");
    var p2=Call(new(AdminOperation.Job,JobAction.list,StateFilter:JobState.QUEUED,PageSize:2,BeforeSeq:p1.NextBeforeSeq));
    Check(p2.Status==StatusCode.OK && p2.Jobs is {Length:2} && p2.Jobs[0].OperationKey=="paging-1" &&
        p2.Jobs[1].OperationKey=="paging-0" && p2.NextBeforeSeq is null,"paging.second");
    Check(Call(new(AdminOperation.Job,JobAction.list,PageSize:0)).Status==StatusCode.INVALID_ARGUMENT &&
        Call(new(AdminOperation.Job,JobAction.status,Key:"paging-0",BeforeSeq:1)).Status==StatusCode.INVALID_ARGUMENT,"paging.invalid");
    var lookup=Call(new(AdminOperation.Job,JobAction.status,Key:"paging-3"));
    Check(lookup.Job?.State==JobState.CANCELLED,"paging.key_lookup");
}
finally{Directory.Delete(pagingRoot,true);}


// Schema 3 fixture: migrate encrypted legacy results and retain permanent job keys.
string retentionRoot=Path.Combine(Path.GetTempPath(),"WindowsAdminMCP-retention-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(retentionRoot);
try
{
    string path=Path.Combine(retentionRoot,"jobs.db"), cmdText="Write-Output 'old-audit'";
    string Digest(string value)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    string payload=Digest(System.Text.Json.JsonSerializer.Serialize(new {steps=new[]{new JobStep(cmdText)},cwd=Environment.SystemDirectory}));
    string old=DateTimeOffset.UtcNow.AddDays(-45).ToString("O");
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+path+";Pooling=False"))
    {
        db.Open();
        using var schema=db.CreateCommand();
        schema.CommandText="""
            CREATE TABLE jobs(seq INTEGER PRIMARY KEY AUTOINCREMENT,job_id TEXT NOT NULL UNIQUE,op_key TEXT NOT NULL UNIQUE,
              payload_hash TEXT NOT NULL,command_hash TEXT NOT NULL,cwd BLOB NOT NULL,state TEXT NOT NULL,
              created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL,completed_steps INTEGER NOT NULL,step_count INTEGER NOT NULL,
              reboot_stamp TEXT,reboot_evidence TEXT);
            CREATE TABLE job_steps(job_id TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE CASCADE,step_index INTEGER NOT NULL,
              command BLOB,timeout_seconds INTEGER NOT NULL,pause_after INTEGER NOT NULL,result BLOB,reboot_after INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(job_id,step_index));
            PRAGMA user_version=3;
            """;
        schema.ExecuteNonQuery();
        foreach(var (id,state,completed) in new[]{("old-finished","COMPLETED",1),("old-unknown","NEEDS_RECONCILIATION",0)})
        {
            using var seed=db.CreateCommand();
            seed.CommandText="""
                INSERT INTO jobs(job_id,op_key,payload_hash,command_hash,cwd,state,created_utc,updated_utc,completed_steps,step_count)
                VALUES($id,$id,$payload,$hash,$cwd,$state,$old,$old,$completed,1);
                INSERT INTO job_steps(job_id,step_index,command,timeout_seconds,pause_after,result)
                VALUES($id,1,NULL,30,0,$result);
                """;
            seed.Parameters.AddWithValue("$id",id);
            seed.Parameters.AddWithValue("$payload",payload);
            seed.Parameters.AddWithValue("$hash",Digest(cmdText));
            seed.Parameters.AddWithValue("$cwd",JobDataProtection.Protect(Environment.SystemDirectory,id,"cwd"));
            seed.Parameters.AddWithValue("$state",state);
            seed.Parameters.AddWithValue("$old",old);
            seed.Parameters.AddWithValue("$completed",completed);
            var execution=new ExecutionResult(completed==1?ExecutionState.COMPLETED:ExecutionState.TIMEOUT_UNKNOWN_EFFECT,Digest(cmdText),345,completed==1?0:null,"old-audit");
            seed.Parameters.AddWithValue("$result",JobDataProtection.Protect(System.Text.Json.JsonSerializer.Serialize(execution,PipeWire.Json),id,"step/1/result"));
            seed.ExecuteNonQuery();
        }
    }
    using(var store=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
    {
        store.Initialize(path);
        var original=store.Dispatch(new(AdminOperation.Job,JobAction.logs,JobId:"old-unknown"),"fixture");
        Check(original.Job is {State:JobState.NEEDS_RECONCILIATION,InterruptedSourceAvailable:false,Execution.Status:ExecutionState.TIMEOUT_UNKNOWN_EFFECT},"reconcile.v3_unknown_preserved");
        var prune=store.Dispatch(new(AdminOperation.Job,JobAction.prune),"fixture");
        Check(prune.Status==StatusCode.OK && prune.PrunedCount==1 && prune.Store is {Detailed:1,Pruned:1,NeedsReconciliation:1},"history.compacts_only_eligible");
        var logs=store.Dispatch(new(AdminOperation.Job,JobAction.logs,Key:"old-finished"),"fixture");
        Check(logs.Status==StatusCode.HISTORY_PRUNED && logs.Job is {HistoryPrunedUtc:not null,Execution:{Status:ExecutionState.COMPLETED,Stdout:"",ElapsedMs:345,AuditPersisted:true,PrunedOutput:{StdoutBytes:9}}},"history.retains_audit_metadata");
        var retry=store.Dispatch(new(AdminOperation.Job,JobAction.submit,Key:"old-finished",Command:cmdText),"fixture");
        Check(retry.Status==StatusCode.OK && retry.Job is {JobId:"old-finished",HistoryPrunedUtc:not null},"history.permanent_idempotency");
        Check(store.Dispatch(new(AdminOperation.Job,JobAction.submit,Key:"old-finished",Command:"different"),"fixture").Status==StatusCode.CONFLICT,"history.pruned_key_conflict");
        var resolve=store.Dispatch(new(AdminOperation.Job,JobAction.reconcile,JobId:"old-unknown",Decision:ReconciliationDecision.UNKNOWN,Evidence:"No source retained in v3."),"fixture","S-1-5-18");
        Check(resolve.Job is {State:JobState.RECONCILED,Reconciliation.Decision:ReconciliationDecision.UNKNOWN},"reconcile.legacy_unknown_closed_without_success");
        var noPrune=store.Dispatch(new(AdminOperation.Job,JobAction.prune),"fixture");
        Check(noPrune.PrunedCount==0 && noPrune.Store is {Reconciled:1,NeedsReconciliation:0},"reconcile.fresh_evidence_not_pruned");
    }
    Check(Directory.GetFiles(retentionRoot,"jobs.db.schema3-*.backup").Length==1 &&
        StoreSchema.ObserveFile(path) is {Status:StoreSchemaCode.OBSERVED,Version:4},"migration.v3_backup_and_v4_schema");
    using(var store=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
    {
        store.Initialize(path);
        var resolved=store.Dispatch(new(AdminOperation.Job,JobAction.logs,JobId:"old-unknown"),"fixture");
        Check(resolved.Job is {State:JobState.RECONCILED,Reconciliation.Decision:ReconciliationDecision.UNKNOWN,Execution.Stdout:"old-audit"} &&
            Directory.GetFiles(retentionRoot,"jobs.db.schema3-*.backup").Length==1,"reconcile.v4_reopen_preserves_evidence");
    }
}
finally{Directory.Delete(retentionRoot,true);}


// A genuine SQLite/DPAPI fixture exercises session routing without impersonating users.
string routingRoot=Path.Combine(Path.GetTempPath(),"WindowsAdminMCP-routing-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(routingRoot);
try
{
    using var routing=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance);
    routing.Initialize(Path.Combine(routingRoot,"jobs.db"));
    var owner=new UserSession("S-1-5-21-1-2-3-1001",1,"S-1-5-5-1-2");
    var other=new UserSession("S-1-5-21-1-2-3-1002",2,"S-1-5-5-1-3");
    var laterLogin=owner with {LogonSid="S-1-5-5-1-4"};
    var req=new AdminRequest(AdminOperation.Job,JobAction.submit,Key:"user-job",Command:"Write-Output 1",ExecutionContext:CommandContext.USER);
    Check(routing.Dispatch(req,"fixture",owner.Sid,owner).Status==StatusCode.USER_CONTEXT_UNAVAILABLE,"user.no_system_fallback");
    Check(routing.DispatchWorker(new(WorkerAction.poll),owner).Status==StatusCode.OK,"user.worker_presence");
    var admission=routing.Dispatch(req,"fixture",owner.Sid,owner);
    Check(admission.Job is {ExecutionContext:CommandContext.USER,ExecutionSessionId:1},"user.bound_admission");
    Check(routing.DispatchWorker(new(WorkerAction.poll),other).Work is null &&
        routing.DispatchWorker(new(WorkerAction.poll),laterLogin).Work is null,"user.no_other_session_claim");
    var lease=routing.DispatchWorker(new(WorkerAction.poll),owner).Work;
    Check(lease is not null && lease.JobId==admission.Job?.JobId,"user.owner_claim");
    if(lease is null)throw new InvalidOperationException("Missing fixture lease");
    Check(routing.DispatchWorker(new(WorkerAction.heartbeat,lease.LeaseId),other).Status==StatusCode.LEASE_LOST,"user.no_other_session_heartbeat");
    string hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(lease.Command))).ToLowerInvariant();
    var result=new ExecutionResult(ExecutionState.COMPLETED,hash,100,0,"user fixture");
    Check(routing.DispatchWorker(new(WorkerAction.complete,lease.LeaseId,result),other).Status==StatusCode.LEASE_LOST,"user.no_other_session_completion");
    Check(routing.DispatchWorker(new(WorkerAction.complete,lease.LeaseId,result),owner).Status==StatusCode.OK,"user.durable_completion");
    var readback=routing.Dispatch(new(AdminOperation.Job,JobAction.logs,Key:"user-job"),"fixture",owner.Sid,owner);
    Check(readback.Job is {State:JobState.COMPLETED,Execution:{ExecutionContext:CommandContext.USER,AuditPersisted:true,ExecutionSessionId:1}},"user.persisted_context");
    Check(routing.Dispatch(req,"fixture",owner.Sid,laterLogin).Status==StatusCode.CONFLICT,"user.no_replay_new_logon");
}
finally{Directory.Delete(routingRoot,true);}


// Receipt matching is separate from live remote acceptance.
var observed=DateTimeOffset.UtcNow;
var serviceFixture=new AdminStatus(StatusCode.OK,Guid.NewGuid().ToString("N"),123,StartedUtc:observed.AddMinutes(-5));
var receiptFixture=new AcceptanceReceipt(1,new string('a',40),"fixture",observed,123,serviceFixture.StartedUtc!.Value,
    456,observed.AddMinutes(-4),Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),
    Guid.NewGuid().ToString("N"),1,16,16,148,"image/png");
Check(AcceptanceEvidence.Validate(receiptFixture,receiptFixture.Commit,serviceFixture,456,receiptFixture.TunnelStartedUtc,1,observed)==EvidenceCode.MATCHED,
    "doctor.external_receipt_matches_generation");
Check(AcceptanceEvidence.Validate(receiptFixture with {TunnelPid=789},receiptFixture.Commit,serviceFixture,456,receiptFixture.TunnelStartedUtc,1,observed)==EvidenceCode.STALE_OR_DIFFERENT_INSTALLATION,
    "doctor.receipt_rejects_other_tunnel");
Check(AcceptanceEvidence.Validate(receiptFixture with {ScreenshotSessionId=2},receiptFixture.Commit,serviceFixture,456,receiptFixture.TunnelStartedUtc,1,observed)==EvidenceCode.INVALID,
    "doctor.receipt_rejects_other_desktop");
