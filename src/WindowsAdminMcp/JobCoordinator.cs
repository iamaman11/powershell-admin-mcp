using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

internal sealed class JobCoordinator(ILogger<JobCoordinator> logger) : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private SqliteConnection? connection;
    private readonly Dictionary<string,Work> active=new(StringComparer.Ordinal);
    private readonly Dictionary<UserSession,long> workers=new();
    private readonly Dictionary<string,UserLease> leases=new(StringComparer.Ordinal);
    private const long PresenceMs=6000, LeaseMs=20000;
    private long recoveryHoldUntil;
    private SqliteConnection Db => connection ?? throw new InvalidOperationException("Store unavailable");
    private const string Columns = "job_id,op_key,state,command_hash,created_utc,updated_utc,completed_steps,step_count,reboot_evidence,reconciliation,history_pruned_utc,user_sid,user_session,user_logon_sid,resource_key";
    internal const int DetailedLimit=10000, RetentionDays=30, PruneBatch=8;
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal void Initialize() => Initialize(StateDirectory.DatabasePath());

    // Internal isolated fixture entry; no CLI, environment or IPC override.
    internal void Initialize(string databasePath)
    {
        lock (gate)
        {
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath, Pooling = false
            }.ToString());
            Db.Open();
            long versionValue;
            using (var version = Command("PRAGMA user_version", null))
            {
                long v = versionValue = (long)version.ExecuteScalar()!;
                if (v == 0)
                {
                    using var existing = Command("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='jobs'", null);
                    if ((long)existing.ExecuteScalar()! != 0) throw new InvalidOperationException("Unversioned database.");
                }
                else if (v is not (2 or 3 or StoreSchema.Current)) throw new InvalidOperationException("Unsupported job store version; offline migration required.");
            }
            using (var pragmas = Command("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;", null))
                pragmas.ExecuteNonQuery();
            bool migrating=versionValue is 2 or 3;
            if(migrating) BackupLegacy(versionValue);
            using var tx = Db.BeginTransaction();
            if(migrating)
            {
                using var rename=Command("ALTER TABLE job_steps RENAME TO job_steps_legacy; ALTER TABLE jobs RENAME TO jobs_legacy;",tx);
                rename.ExecuteNonQuery();
            }
            using(var schema=Command("""
                CREATE TABLE IF NOT EXISTS jobs(
                  seq INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL UNIQUE, op_key TEXT NOT NULL UNIQUE,
                  payload_hash TEXT NOT NULL, command_hash TEXT NOT NULL, cwd BLOB NOT NULL,
                  state TEXT NOT NULL CHECK(state IN ('QUEUED','RUNNING','CANCEL_REQUESTED','WAITING_FOR_RESUME','WAITING_FOR_REBOOT','COMPLETED','FAILED','CANCELLED','NEEDS_RECONCILIATION','RECONCILED')),
                  created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL,
                  completed_steps INTEGER NOT NULL DEFAULT 0, step_count INTEGER NOT NULL CHECK(step_count BETWEEN 1 AND 8),
                  reboot_stamp TEXT, reboot_evidence TEXT, reconciliation BLOB, history_pruned_utc TEXT,
                  user_sid TEXT, user_session INTEGER, user_logon_sid TEXT, resource_key TEXT,
                  CHECK((user_sid IS NULL AND user_session IS NULL AND user_logon_sid IS NULL) OR
                        (user_sid IS NOT NULL AND user_session>0 AND user_logon_sid IS NOT NULL)),
                  CHECK(completed_steps BETWEEN 0 AND step_count),
                  CHECK((state='RECONCILED')=(reconciliation IS NOT NULL)),
                  CHECK(history_pruned_utc IS NULL OR state IN ('COMPLETED','FAILED','CANCELLED','RECONCILED')));
                CREATE TABLE IF NOT EXISTS job_steps(
                  job_id TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE CASCADE,
                  step_index INTEGER NOT NULL CHECK(step_index BETWEEN 1 AND 8),
                  command BLOB, timeout_seconds INTEGER NOT NULL CHECK(timeout_seconds BETWEEN 1 AND 3600),
                  pause_after INTEGER NOT NULL CHECK(pause_after IN (0,1)), result BLOB,
                  reboot_after INTEGER NOT NULL DEFAULT 0 CHECK(reboot_after IN (0,1)),
                  PRIMARY KEY(job_id,step_index));
                CREATE INDEX IF NOT EXISTS ix_jobs_state_seq ON jobs(state,seq);
                CREATE INDEX IF NOT EXISTS ix_jobs_detail_retention ON jobs(updated_utc,seq) WHERE history_pruned_utc IS NULL;
                """,tx))schema.ExecuteNonQuery();
            if(migrating)
            {
                string boot=versionValue==3?",reboot_stamp,reboot_evidence":"";
                string stepBoot=versionValue==3?",reboot_after":"";
                using var migration=Command($"""
                    INSERT INTO jobs(seq,job_id,op_key,payload_hash,command_hash,cwd,state,created_utc,updated_utc,completed_steps,step_count{boot})
                    SELECT seq,job_id,op_key,payload_hash,command_hash,cwd,state,created_utc,updated_utc,completed_steps,step_count{boot} FROM jobs_legacy;
                    INSERT INTO job_steps(job_id,step_index,command,timeout_seconds,pause_after,result{stepBoot})
                    SELECT job_id,step_index,command,timeout_seconds,pause_after,result{stepBoot} FROM job_steps_legacy;
                    DROP TABLE job_steps_legacy;
                    DROP TABLE jobs_legacy;
                    """,tx);
                migration.ExecuteNonQuery();
                using var exclusive=Command("UPDATE jobs SET resource_key='*' WHERE EXISTS(SELECT 1 FROM job_steps s WHERE s.job_id=jobs.job_id AND s.reboot_after=1)",tx);
                exclusive.ExecuteNonQuery();
            }
            using(var check=Command("PRAGMA foreign_key_check",tx))
            using(var rows=check.ExecuteReader())
                if(rows.Read())throw new InvalidOperationException("Job store foreign key check failed.");
            using(var setVersion=Command($"PRAGMA user_version={StoreSchema.Current}",tx))setVersion.ExecuteNonQuery();
            using(var interruptedUsers=Command("SELECT count(*) FROM jobs WHERE user_sid IS NOT NULL AND state IN ('RUNNING','CANCEL_REQUESTED')",tx))
                if((long)interruptedUsers.ExecuteScalar()!>0)recoveryHoldUntil=Environment.TickCount64+LeaseMs;
            using var recovery = Command("""
                UPDATE job_steps SET command=NULL WHERE job_id IN (SELECT job_id FROM jobs WHERE state IN ('RUNNING','CANCEL_REQUESTED'))
                  AND step_index<>(SELECT completed_steps+1 FROM jobs WHERE jobs.job_id=job_steps.job_id);
                UPDATE jobs SET state='NEEDS_RECONCILIATION', updated_utc=$now WHERE state IN ('RUNNING','CANCEL_REQUESTED');
                """, tx, ("$now", Now()));
            int recovered = recovery.ExecuteNonQuery();
            tx.Commit();
            logger.LogInformation("Job store ready. Recovery changed rows {Recovered}", recovered);
        }
    }

    // SQLite's own backup API includes the committed WAL contents; the destination
    // is a new unique file in the already validated SYSTEM/Admin state directory.
    // Older binaries must not open the migrated database. Backup is not an automatic downgrade.
    private void BackupLegacy(long version)
    {
        string path=Db.DataSource+$".schema{version}-"+Guid.NewGuid().ToString("N")+".backup";
        using(var reserve=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){}
        using var backup=new SqliteConnection(new SqliteConnectionStringBuilder
        {DataSource=path,Mode=SqliteOpenMode.ReadWrite,Pooling=false}.ToString());
        backup.Open();
        Db.BackupDatabase(backup);
        using var check=backup.CreateCommand();
        check.CommandText="PRAGMA quick_check";
        if((string?)check.ExecuteScalar()!="ok")throw new InvalidOperationException("Schema backup integrity check failed.");
        check.CommandText="PRAGMA user_version";
        if((long)check.ExecuteScalar()! != version)throw new InvalidOperationException("Schema backup version mismatch.");
        logger.LogInformation("Protected schema {Version} backup created before migration.",version);
    }

    internal StoreSummary Summary(SqliteTransaction? tx=null)
    {
        lock(gate)
        {
            using var cmd=Command("""
                SELECT count(*),
                  coalesce(sum(state='QUEUED'),0),coalesce(sum(state='RUNNING'),0),
                  coalesce(sum(state='CANCEL_REQUESTED'),0),coalesce(sum(state='WAITING_FOR_RESUME'),0),
                  coalesce(sum(state='WAITING_FOR_REBOOT'),0),coalesce(sum(state='NEEDS_RECONCILIATION'),0),
                  coalesce(sum(state='COMPLETED'),0),coalesce(sum(state='FAILED'),0),coalesce(sum(state='CANCELLED'),0),
                  coalesce(sum(history_pruned_utc IS NULL),0),coalesce(sum(history_pruned_utc IS NOT NULL),0),coalesce(sum(state='RECONCILED'),0)
                FROM jobs
                """,tx);
            using var row=cmd.ExecuteReader();
            if(!row.Read())throw new InvalidOperationException("Store count unavailable.");
            return new(row.GetInt64(0),row.GetInt64(1),row.GetInt64(2),row.GetInt64(3),row.GetInt64(4),
                row.GetInt64(5),row.GetInt64(6),row.GetInt64(7),row.GetInt64(8),row.GetInt64(9),StoreSchema.Current,null,DetailedLimit,row.GetInt64(10),row.GetInt64(11),row.GetInt64(12),RetentionDays,ExecutionBudget.Observe(active.Count));
        }
    }

    private void Signal() { if (wake.CurrentCount == 0) wake.Release(); }

    internal JobReply Dispatch(AdminRequest request, string correlation,string? callerSid=null,UserSession? callerUser=null)
    {
        var dispatchClock=Stopwatch.StartNew();
        long gateMs=0;
        JobReply Reply(StatusCode code, JobInfo? job = null, JobInfo[]? jobs = null, long? nextBeforeSeq = null,StoreSummary? store=null,int? prunedCount=null)
            => new(code, correlation, job, jobs, request.Key, NextBeforeSeq:nextBeforeSeq,Store:store,PrunedCount:prunedCount,
                Timing:new(StoreWaitMs:gateMs,StoreMs:Math.Max(0,dispatchClock.ElapsedMilliseconds-gateMs)));
        if (request.Action is not {} action || !Enum.IsDefined(action) ||
            request.Key is {Length:0 or >100} || request.JobId is {Length:0 or >64} ||
            (request.StepIndex is not null && action != JobAction.logs) ||
            (request.ExpectedCompletedSteps is not null && action != JobAction.resume) ||
            ((request.StateFilter is not null || request.PageSize is not null || request.BeforeSeq is not null) && action != JobAction.list) ||
            (request.StateFilter is {} filter && !Enum.IsDefined(filter)) || request.PageSize is <1 or >100 || request.BeforeSeq is <1 ||
            ((request.Decision is not null || request.Evidence is not null) && action!=JobAction.reconcile) ||
            (request.RetainDays is not null && action!=JobAction.prune) || request.RetainDays is <0 or >RetentionDays ||
            (request.ExecutionContext is not null && action!=JobAction.submit) ||
            (request.IncludeInterruptedCommand && action!=JobAction.logs) ||
            (request.ResourceKey is not null && action!=JobAction.submit))
            return Reply(StatusCode.INVALID_ARGUMENT);

        lock (gate)
        {
            gateMs=dispatchClock.ElapsedMilliseconds;
            if (action == JobAction.submit)
            {
                if (request.Key is not {Length:>0} key || string.IsNullOrWhiteSpace(key) || request.JobId is not null ||
                    (request.Command is null) == (request.Steps is null))
                    return Reply(StatusCode.INVALID_ARGUMENT);
                JobStep[] steps = request.Steps ?? [new(request.Command!, request.TimeoutSeconds)];
                if (steps.Length is <1 or >8 || steps.Any(s => s is null || s.Command is not {Length:>0 and <=12000} ||
                     s.TimeoutSeconds is <1 or >3600) || steps.Sum(s => s.Command.Length) > 48000 ||
                     !ValidBoundaries(steps))
                    return Reply(StatusCode.INVALID_ARGUMENT);
                var context=request.ExecutionContext??CommandContext.SYSTEM; // legacy internal request
                if(context is not (CommandContext.SYSTEM or CommandContext.USER))return Reply(StatusCode.INVALID_ARGUMENT);
                UserSession? owner=context==CommandContext.USER?callerUser:null;
                if(context==CommandContext.USER && owner is null)return Reply(StatusCode.USER_CONTEXT_UNAVAILABLE);
                if(owner is not null && steps.Any(s=>s.RebootAfter || s.VerifyAfterReboot))return Reply(StatusCode.INVALID_ARGUMENT);
                string cwd = request.WorkingDirectory ?? (owner is null?Environment.SystemDirectory:"");
                if(cwd.Length>1024 || (cwd.Length==0 && request.WorkingDirectory is not null) ||
                    (cwd.Length!=0 && !Path.IsPathFullyQualified(cwd)))return Reply(StatusCode.INVALID_ARGUMENT);
                string? resource=request.ResourceKey?.Trim().ToLowerInvariant();
                if(resource is {Length:0 or >128} || (resource is not null && resource.Any(char.IsControl)))
                    return Reply(StatusCode.INVALID_ARGUMENT);
                bool rebootPlan=steps.Any(s=>s.RebootAfter);
                if(rebootPlan && resource is not (null or "*"))return Reply(StatusCode.INVALID_ARGUMENT);
                // Preserve legacy idempotency hashes for clients omitting the new argument.
                string payload=resource is null
                    ?owner is null?Hash(JsonSerializer.Serialize(new {steps,cwd})):Hash(JsonSerializer.Serialize(new {steps,cwd,owner}))
                    :Hash(JsonSerializer.Serialize(new {steps,cwd,owner,resource}));
                if(rebootPlan)resource="*";
                using var tx = Db.BeginTransaction();
                var prior = Find(key, null, tx);
                if (prior is not null)
                {
                    if(prior.ExecutionContext==CommandContext.USER && callerSid is not null && callerSid!="S-1-5-18" && callerSid!=prior.ExecutionSid)
                        return Reply(StatusCode.ACCESS_DENIED);
                    using var check = Command("SELECT payload_hash FROM jobs WHERE job_id=$id", tx, ("$id",prior.JobId));
                    return Reply((string?)check.ExecuteScalar() == payload ? StatusCode.OK : StatusCode.CONFLICT, prior);
                }
                if(owner is null && !Directory.Exists(cwd))return Reply(StatusCode.INVALID_ARGUMENT);
                if(owner is not null && !UserAvailable(owner))return Reply(StatusCode.USER_CONTEXT_UNAVAILABLE);
                if(CountDetails(tx)>=DetailedLimit)
                {
                    PruneExpired(tx,RetentionDays);
                    if(CountDetails(tx)>=DetailedLimit)
                    {
                        var full=Summary(tx);
                        tx.Commit();
                        return Reply(StatusCode.STORE_FULL,store:full);
                    }
                }
                string id = Guid.NewGuid().ToString("N"), now = Now(),
                    hash = steps.Length == 1 ? Hash(steps[0].Command) : Hash(JsonSerializer.Serialize(steps));
                using var insert = Command("""
                    INSERT INTO jobs(job_id,op_key,payload_hash,command_hash,cwd,state,created_utc,updated_utc,step_count,user_sid,user_session,user_logon_sid,resource_key)
                    VALUES($id,$key,$payload,$hash,$cwd,'QUEUED',$now,$now,$count,$sid,$session,$logon,$resource)
                    """, tx, ("$id",id), ("$key",key), ("$payload",payload), ("$hash",hash),
                    ("$cwd",JobDataProtection.Protect(cwd,id,"cwd")), ("$now",now), ("$count",steps.Length),
                    ("$sid",(object?)owner?.Sid??DBNull.Value),("$session",(object?)owner?.SessionId??DBNull.Value),
                    ("$logon",(object?)owner?.LogonSid??DBNull.Value),("$resource",(object?)resource??DBNull.Value));
                insert.ExecuteNonQuery();
                for (int i = 0; i < steps.Length; i++)
                {
                    var step = steps[i];
                    using var add = Command("""
                        INSERT INTO job_steps(job_id,step_index,command,timeout_seconds,pause_after,reboot_after)
                        VALUES($id,$step,$command,$timeout,$pause,$reboot)
                        """, tx, ("$id",id), ("$step",i+1),
                        ("$command",JobDataProtection.Protect(step.Command,id,Field(i+1,"command"))),
                        ("$timeout",step.TimeoutSeconds), ("$pause",step.PauseAfter?1:0), ("$reboot",step.RebootAfter?1:0));
                    add.ExecuteNonQuery();
                }
                var usage=CountDetails(tx)>=DetailedLimit*8/10?Summary(tx):null;
                tx.Commit();
                Signal();
                logger.LogInformation("Job admitted {JobId} correlation {Correlation}, step count {Count}",id,correlation,steps.Length);
                return Reply(StatusCode.OK,new(id,key,JobState.QUEUED,hash,now,now,TotalSteps:steps.Length,ExecutionContext:context,ExecutionSid:owner?.Sid??"S-1-5-18",
                    ExecutionSessionId:owner?.SessionId??0,ResourceKey:resource),store:usage);
            }

            if (request.Command is not null || request.WorkingDirectory is not null || request.Steps is not null)
                return Reply(StatusCode.INVALID_ARGUMENT);
            if (action == JobAction.list)
            {
                if (request.Key is not null || request.JobId is not null) return Reply(StatusCode.INVALID_ARGUMENT);
                int size=request.PageSize??20;
                using var cmd = Command("SELECT "+Columns+",seq FROM jobs WHERE ($state IS NULL OR state=$state) AND ($before IS NULL OR seq<$before) AND ($caller IS NULL OR user_sid IS NULL OR user_sid=$caller) ORDER BY seq DESC LIMIT $take",
                    null,("$state",(object?)request.StateFilter?.ToString()??DBNull.Value),("$before",(object?)request.BeforeSeq??DBNull.Value),("$take",size+1),
                    ("$caller",callerSid is null or "S-1-5-18"?DBNull.Value:callerSid));
                using var reader = cmd.ExecuteReader();
                var jobs = new List<JobInfo>();
                long? last=null,next=null;
                while(reader.Read())
                {
                    if(jobs.Count==size){next=last;break;}
                    jobs.Add(Read(reader,includeReconciliation:false));last=reader.GetInt64(15);
                }
                reader.Close();
                return Reply(StatusCode.OK,jobs:jobs.ToArray(),nextBeforeSeq:next,store:Summary());
            }
            if(action==JobAction.prune)
            {
                if(request.Key is not null || request.JobId is not null)return Reply(StatusCode.INVALID_ARGUMENT);
                using var compact=Db.BeginTransaction();
                int pruned=PruneExpired(compact,request.RetainDays??RetentionDays);
                var usage=Summary(compact);
                compact.Commit();
                return Reply(StatusCode.OK,store:usage,prunedCount:pruned);
            }
            if ((request.Key is null) == (request.JobId is null)) return Reply(StatusCode.INVALID_ARGUMENT);
            using var change = Db.BeginTransaction();
            var job = Find(request.Key,request.JobId,change);
            if (job is null) return Reply(StatusCode.NOT_FOUND);
            if(job.ExecutionContext==CommandContext.USER && callerSid is not null &&
                callerSid!="S-1-5-18" && callerSid!=job.ExecutionSid)return Reply(StatusCode.ACCESS_DENIED);
            if(action==JobAction.status)
            {
                if(job.State==JobState.QUEUED)
                {
                    bool missing=job.ExecutionContext==CommandContext.USER && !UserAvailable(ReadOwner(job.JobId,change));
                    var reason=missing?QueueWaitReason.USER_UNAVAILABLE:Environment.TickCount64<recoveryHoldUntil?
                        QueueWaitReason.RECOVERY_HOLD:!ExecutionBudget.Observe(active.Count).CanStart?QueueWaitReason.RESOURCE_PRESSURE:
                        Blocked(job.ResourceKey)?QueueWaitReason.RESOURCE_BUSY:QueueWaitReason.READY_OR_EARLIER_JOB;
                    job=job with {WaitingForUser=job.ExecutionContext==CommandContext.USER?missing:null,QueueReason=reason};
                }
                return Reply(StatusCode.OK,job);
            }
            if (action == JobAction.logs)
            {
                int index = request.StepIndex ?? Math.Min(job.TotalSteps, job.State is JobState.COMPLETED or JobState.WAITING_FOR_RESUME or JobState.WAITING_FOR_REBOOT
                    ? job.CompletedSteps : job.CompletedSteps + 1);
                if (index < 1 || index > job.TotalSteps) return Reply(StatusCode.INVALID_ARGUMENT);
                bool unresolved=job.State==JobState.NEEDS_RECONCILIATION && index==job.CompletedSteps+1;
                using var cmd = Command("SELECT result,command FROM job_steps WHERE job_id=$id AND step_index=$step",change,("$id",job.JobId),("$step",index));
                using var row=cmd.ExecuteReader();
                if(!row.Read())throw new InvalidOperationException("Missing job step.");
                var execution = row.IsDBNull(0)?null:(JsonSerializer.Deserialize<ExecutionResult>(
                    JobDataProtection.Open((byte[])row.GetValue(0),job.JobId,Field(index,"result")),PipeWire.Json)
                    ??throw new InvalidOperationException("Missing execution result."));
                if(request.IncludeInterruptedCommand && !unresolved)return Reply(StatusCode.INVALID_ARGUMENT,job);
                bool? sourceAvailable=unresolved?!row.IsDBNull(1):null;
                string? source=request.IncludeInterruptedCommand && sourceAvailable==true?
                    JobDataProtection.Open((byte[])row.GetValue(1),job.JobId,Field(index,"command")):null;
                return Reply(job.HistoryPrunedUtc is null?StatusCode.OK:StatusCode.HISTORY_PRUNED,job with
                {
                    Execution=execution is null?null:execution with {AuditPersisted=true},ResultStep=index,
                    InterruptedSourceAvailable=sourceAvailable,InterruptedCommand=source
                });
            }
            if(action==JobAction.reconcile)
            {
                if(request.Decision is not {} decision || !Enum.IsDefined(decision) ||
                    request.Evidence is not {Length:>0 and <=4000} evidence || string.IsNullOrWhiteSpace(evidence) ||
                    string.IsNullOrWhiteSpace(callerSid))return Reply(StatusCode.INVALID_ARGUMENT,job);
                if(job.State==JobState.RECONCILED)
                    return Reply(job.Reconciliation is {} previous && previous.Decision==decision && previous.Evidence==evidence?
                        StatusCode.OK:StatusCode.CONFLICT,job);
                if(job.State!=JobState.NEEDS_RECONCILIATION)return Reply(StatusCode.NOT_RECONCILABLE,job);
                string now=Now();
                var record=new ReconciliationRecord(decision,evidence,now,callerSid,correlation);
                using var save=Command("""
                    UPDATE jobs SET state='RECONCILED',reconciliation=$record,updated_utc=$now
                    WHERE job_id=$id AND state='NEEDS_RECONCILIATION'
                    """,change,("$id",job.JobId),("$now",now),
                    ("$record",JobDataProtection.Protect(JsonSerializer.Serialize(record,PipeWire.Json),job.JobId,"reconciliation")));
                if(save.ExecuteNonQuery()!=1)throw new InvalidOperationException("Lost reconciliation transition.");
                using var clear=Command("UPDATE job_steps SET command=NULL WHERE job_id=$id",change,("$id",job.JobId));
                clear.ExecuteNonQuery();
                change.Commit();
                logger.LogInformation("Job {JobId} reconciliation recorded {Decision} correlation {Correlation}",job.JobId,decision,correlation);
                return Reply(StatusCode.OK,job with {State=JobState.RECONCILED,UpdatedUtc=now,Reconciliation=record});
            }
            if (action == JobAction.resume)
            {
                if (request.ExpectedCompletedSteps is not int expected || expected < 1 ||
                    expected != job.CompletedSteps || job.State == JobState.NEEDS_RECONCILIATION)
                    return Reply(StatusCode.NOT_RESUMABLE,job);
                if(job.ExecutionContext==CommandContext.USER && (callerUser is null || callerUser!=ReadOwner(job.JobId,change)))
                    return Reply(StatusCode.USER_CONTEXT_UNAVAILABLE,job);
                if (job.State != JobState.WAITING_FOR_RESUME)
                    return Reply(StatusCode.NOT_RESUMABLE,job);
                string now=Now();
                using var resume=Command("UPDATE jobs SET state='QUEUED',updated_utc=$now WHERE job_id=$id AND state='WAITING_FOR_RESUME' AND completed_steps=$expected",
                    change,("$id",job.JobId),("$now",now),("$expected",expected));
                if(resume.ExecuteNonQuery()!=1)throw new InvalidOperationException("Lost resume checkpoint");
                change.Commit();
                Signal();
                return Reply(StatusCode.OK,job with {State=JobState.QUEUED,UpdatedUtc=now});
            }
            if (action != JobAction.cancel) return Reply(StatusCode.INVALID_ARGUMENT);
            if (job.State is JobState.QUEUED or JobState.WAITING_FOR_RESUME or JobState.WAITING_FOR_REBOOT)
            {
                string now=Now();
                using var cmd=Command("""
                    UPDATE jobs SET state='CANCELLED',updated_utc=$now WHERE job_id=$id;
                    UPDATE job_steps SET command=NULL WHERE job_id=$id;
                    """,change,("$now",now),("$id",job.JobId));
                cmd.ExecuteNonQuery();change.Commit();
                return Reply(StatusCode.OK,job with {State=JobState.CANCELLED,UpdatedUtc=now});
            }
            if (job.State is JobState.CANCEL_REQUESTED or JobState.CANCELLED) return Reply(StatusCode.OK,job);
            if (job.State != JobState.RUNNING || !active.TryGetValue(job.JobId,out var running))
                return Reply(StatusCode.NOT_CANCELLABLE,job);
            string when=Now();
            using var cancel=Command("UPDATE jobs SET state='CANCEL_REQUESTED',updated_utc=$now WHERE job_id=$id",change,("$now",when),("$id",job.JobId));
            cancel.ExecuteNonQuery();change.Commit();running.Cancel.Cancel();
            return Reply(StatusCode.OK,job with {State=JobState.CANCEL_REQUESTED,UpdatedUtc=when});
        }
    }

    internal static bool ValidBoundaries(JobStep[] steps)
    {
        for(int i=0;i<steps.Length;i++)
        {
            var step=steps[i];
            if(step.PauseAfter && (step.RebootAfter || i==steps.Length-1))return false;
            if(step.RebootAfter && (i==steps.Length-1 || !steps[i+1].VerifyAfterReboot))return false;
            if(step.VerifyAfterReboot && (i==0 || !steps[i-1].RebootAfter || step.PauseAfter || step.RebootAfter))return false;
        }
        return true;
    }

    private static string Field(int index,string field) => $"step/{index}/{field}";
    private JobInfo? Find(string? key,string? id,SqliteTransaction tx)
    {
        using var cmd=Command("SELECT "+Columns+" FROM jobs WHERE "+(key is not null?"op_key=$value":"job_id=$value"),tx,("$value",key??id!));
        using var reader=cmd.ExecuteReader();
        return reader.Read()?Read(reader):null;
    }
    private static JobInfo Read(SqliteDataReader r,bool includeReconciliation=true)=>new(r.GetString(0),r.GetString(1),
        Enum.Parse<JobState>(r.GetString(2)),r.GetString(3),r.GetString(4),r.GetString(5),
        CompletedSteps:r.GetInt32(6),TotalSteps:r.GetInt32(7),
        RebootEvidence:r.IsDBNull(8)?null:Enum.Parse<BootEvidenceCode>(r.GetString(8)),
        Reconciliation:!includeReconciliation||r.IsDBNull(9)?null:(JsonSerializer.Deserialize<ReconciliationRecord>(
            JobDataProtection.Open((byte[])r.GetValue(9),r.GetString(0),"reconciliation"))??throw new InvalidOperationException("Missing reconciliation.")),
        HistoryPrunedUtc:r.IsDBNull(10)?null:r.GetString(10),
        ExecutionContext:r.IsDBNull(11)?CommandContext.SYSTEM:CommandContext.USER,
        ExecutionSid:r.IsDBNull(11)?"S-1-5-18":r.GetString(11),
        ExecutionSessionId:r.IsDBNull(12)?0:r.GetInt32(12),ResourceKey:r.IsDBNull(14)?null:r.GetString(14));
    private SqliteCommand Command(string sql,SqliteTransaction? tx,params (string Name,object Value)[] parameters)
    {
        var cmd=Db.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;
        foreach(var (name,value) in parameters)cmd.Parameters.AddWithValue(name,value);
        return cmd;
    }
    private long CountDetails(SqliteTransaction? tx)
    {
        using var cmd=Command("SELECT count(*) FROM jobs WHERE history_pruned_utc IS NULL",tx);
        return (long)cmd.ExecuteScalar()!;
    }

    // Compact terminal output, never delete metadata, operation keys or reconciliation evidence.
    private int PruneExpired(SqliteTransaction tx,int retainDays)
    {
        string cutoff=DateTimeOffset.UtcNow.AddDays(-retainDays).ToString("O");
        var ids=new List<string>();
        using(var candidates=Command("""
            SELECT job_id FROM jobs WHERE history_pruned_utc IS NULL
            AND state IN ('COMPLETED','FAILED','CANCELLED','RECONCILED') AND updated_utc<$cutoff
            ORDER BY updated_utc,seq LIMIT $limit
            """,tx,("$cutoff",cutoff),("$limit",PruneBatch)))
        using(var reader=candidates.ExecuteReader())
            while(reader.Read())ids.Add(reader.GetString(0));
        foreach(string id in ids)
        {
            var results=new List<(int Index,byte[] Blob)>();
            using(var steps=Command("SELECT step_index,result FROM job_steps WHERE job_id=$id AND result IS NOT NULL",tx,("$id",id)))
            using(var reader=steps.ExecuteReader())
                while(reader.Read())results.Add((reader.GetInt32(0),(byte[])reader.GetValue(1)));
            foreach(var item in results)
            {
                string field=Field(item.Index,"result");
                var saved=JsonSerializer.Deserialize<ExecutionResult>(JobDataProtection.Open(item.Blob,id,field),PipeWire.Json)
                    ??throw new InvalidOperationException("Missing result for compaction.");
                var receipt=new PrunedOutput(Encoding.UTF8.GetByteCount(saved.Stdout),Hash(saved.Stdout),
                    Encoding.UTF8.GetByteCount(saved.Stderr),Hash(saved.Stderr),saved.Warnings?.Length??0);
                var compact=saved with {Stdout="",Stderr="",Warnings=[],AuditPersisted=true,PrunedOutput=receipt};
                using var write=Command("UPDATE job_steps SET result=$result,command=NULL WHERE job_id=$id AND step_index=$step",
                    tx,("$id",id),("$step",item.Index),
                    ("$result",JobDataProtection.Protect(JsonSerializer.Serialize(compact,PipeWire.Json),id,field)));
                if(write.ExecuteNonQuery()!=1)throw new InvalidOperationException("Lost history step.");
            }
            using var update=Command("UPDATE jobs SET history_pruned_utc=$now WHERE job_id=$id AND history_pruned_utc IS NULL",
                tx,("$id",id),("$now",Now()));
            if(update.ExecuteNonQuery()!=1)throw new InvalidOperationException("Lost history compaction.");
        }
        return ids.Count;
    }

    private void MaintainHistory()
    {
        lock(gate)
        {
            using var tx=Db.BeginTransaction();
            int count=PruneExpired(tx,RetentionDays);
            tx.Commit();
            if(count>0)logger.LogInformation("Compacted {Count} eligible job histories.",count);
        }
    }

    private sealed record Work(string Id,int Index,int Count,string Script,int Timeout,string Cwd,bool PauseAfter,bool RebootAfter,CancellationTokenSource Cancel,UserSession? Owner,long QueueWaitMs,string StartedUtc,string? ResourceKey);
    private sealed record UserLease(string Id,Work Work,long Expires);
    private UserSession? ReadOwner(string id,SqliteTransaction tx)
    {
        using var cmd=Command("SELECT user_sid,user_session,user_logon_sid FROM jobs WHERE job_id=$id",tx,("$id",id));
        using var row=cmd.ExecuteReader();
        return !row.Read()||row.IsDBNull(0)?null:new(row.GetString(0),row.GetInt32(1),row.GetString(2));
    }

    internal bool UserAvailable(UserSession? user)
    {
        lock(gate)return user is not null && workers.TryGetValue(user,out long seen) &&
            Environment.TickCount64-seen<PresenceMs;
    }

    private bool Blocked(string? resource)
        =>active.Values.Any(w=>w.ResourceKey=="*" || resource=="*" ||
            (resource is not null && StringComparer.OrdinalIgnoreCase.Equals(resource,w.ResourceKey)));

    // Same durable FIFO among eligible work. A blocked ordinary resource does
    // not block unrelated work; a reachable exclusive job drains active work
    // before starting rather than allowing an endless stream to starve it.
    private (long Seq,UserSession? Owner,string QueuedUtc,string? Resource)? NextRunnable(SqliteTransaction tx)
    {
        using var cmd=Command("SELECT seq,user_sid,user_session,user_logon_sid,updated_utc,resource_key FROM jobs WHERE state='QUEUED' ORDER BY seq",tx);
        using var row=cmd.ExecuteReader();
        while(row.Read())
        {
            UserSession? owner=row.IsDBNull(1)?null:new(row.GetString(1),row.GetInt32(2),row.GetString(3));
            if(owner is not null && !UserAvailable(owner))continue;
            string? resource=row.IsDBNull(5)?null:row.GetString(5);
            if(Blocked(resource))
            {
                if(resource=="*")return null;
                continue;
            }
            return (row.GetInt64(0),owner,row.GetString(4),resource);
        }
        return null;
    }

    private Work? Take(CancellationToken stop,UserSession? user=null)
    {
        lock(gate)
        {
            if(stop.IsCancellationRequested || Environment.TickCount64<recoveryHoldUntil ||
                !ExecutionBudget.Observe(active.Count).CanStart)return null;
            using var tx=Db.BeginTransaction();
            var next=NextRunnable(tx);
            if(next is null || next.Value.Owner!=user)return null;
            string started=Now();
            long queueWait=Math.Max(0,(long)(DateTimeOffset.Parse(started)-DateTimeOffset.Parse(next.Value.QueuedUtc)).TotalMilliseconds);
            using var cmd=Command("""
                UPDATE jobs SET state='RUNNING',updated_utc=$now
                WHERE seq=$seq AND state='QUEUED'
                RETURNING job_id,completed_steps,step_count,cwd
                """,tx,("$now",started),("$seq",next.Value.Seq));
            string id,cwd;int index,count;
            using(var row=cmd.ExecuteReader())
            {
                if(!row.Read())return null;
                id=row.GetString(0);index=row.GetInt32(1)+1;count=row.GetInt32(2);
                cwd=JobDataProtection.Open((byte[])row.GetValue(3),id,"cwd");
            }
            string script;int timeout;bool pause,reboot;
            using(var item=Command("SELECT command,timeout_seconds,pause_after,reboot_after FROM job_steps WHERE job_id=$id AND step_index=$step",tx,("$id",id),("$step",index)))
            using(var row=item.ExecuteReader())
            {
                if(!row.Read() || row.IsDBNull(0))throw new InvalidOperationException("Missing next step");
                script=JobDataProtection.Open((byte[])row.GetValue(0),id,Field(index,"command"));
                timeout=row.GetInt32(1);pause=row.GetInt32(2)!=0;reboot=row.GetInt32(3)!=0;
            }
            tx.Commit(); // durable start intent
            var work=new Work(id,index,count,script,timeout,cwd,pause,reboot,
                CancellationTokenSource.CreateLinkedTokenSource(stop),user,queueWait,started,next.Value.Resource);
            active.Add(id,work);
            return work;
        }
    }

    internal WorkerReply DispatchWorker(WorkerRequest request,UserSession user)
    {
        lock(gate)
        {
            long now=Environment.TickCount64;
            ExpireUserLease();
            foreach(var old in workers.Where(x=>now-x.Value>=PresenceMs).Select(x=>x.Key).ToArray())
                workers.Remove(old);
            if(request.Action==WorkerAction.poll)
            {
                if(request.LeaseId is not null || request.Result is not null)return new(StatusCode.INVALID_ARGUMENT);
                workers[user]=now;
                var work=Take(CancellationToken.None,user);
                if(work is null)return new(StatusCode.OK);
                var lease=new UserLease(Guid.NewGuid().ToString("N"),work,now+LeaseMs);
                leases.Add(lease.Id,lease);
                return new(StatusCode.OK,new(lease.Id,work.Id,work.Index,work.Script,work.Timeout,work.Cwd));
            }
            if(request.LeaseId is null || !leases.TryGetValue(request.LeaseId,out var current) || current.Work.Owner!=user)
                return new(StatusCode.LEASE_LOST);
            if(request.Action==WorkerAction.heartbeat && request.Result is null)
            {
                workers[user]=now;
                leases[current.Id]=current with {Expires=now+LeaseMs};
                return new(StatusCode.OK,CancelRequested:current.Work.Cancel.IsCancellationRequested);
            }
            if(request.Action!=WorkerAction.complete || request.Result is not {} result ||
                result.CommandSha256!=Hash(current.Work.Script) || result.ElapsedMs<0 ||
                result.Stdout is null || result.Stderr is null || result.Stdout.Length>16000 || result.Stderr.Length>16000 ||
                (result.Status==ExecutionState.COMPLETED && result.ExitCode!=0) ||
                result.Status is not (ExecutionState.COMPLETED or ExecutionState.COMMAND_ERROR or ExecutionState.INVALID_ARGUMENT
                    or ExecutionState.POWERSHELL_NOT_FOUND or ExecutionState.EXECUTION_FAILED or ExecutionState.TIMEOUT_BEFORE_EXECUTION
                    or ExecutionState.CANCELLED_BEFORE_EXECUTION or ExecutionState.TIMEOUT_UNKNOWN_EFFECT
                    or ExecutionState.CANCELLED_UNKNOWN_EFFECT or ExecutionState.OUTPUT_INCOMPLETE))
                return new(StatusCode.INVALID_ARGUMENT);
            workers[user]=now;
            CompleteWork(current.Work,result,null);
            return new(StatusCode.OK);
        }
    }

    private void ExpireUserLease()
    {
        lock(gate)
        {
            foreach(var lease in leases.Values.Where(l=>Environment.TickCount64>=l.Expires).ToArray())
            {
                // A lost worker might already have produced an effect. Never requeue the step.
                lease.Work.Cancel.Cancel();
                CompleteWork(lease.Work,new(ExecutionState.EXECUTION_FAILED,Hash(lease.Work.Script),0,
                    Stage:"USER_WORKER_LEASE_LOST"),null);
            }
        }
    }

    // Event log reads run outside the store lock; they cannot delay status/cancel.
    // Only the durable waiting state and exact saved checkpoint authorize the CAS.
    private void ObserveRebootBoundaries()
    {
        var pending=new List<(string Id,int Completed,string Stamp)>();
        lock(gate)
        {
            using var cmd=Command("SELECT job_id,completed_steps,reboot_stamp FROM jobs WHERE state='WAITING_FOR_REBOOT' AND reboot_stamp IS NOT NULL",null);
            using var row=cmd.ExecuteReader();
            while(row.Read())pending.Add((row.GetString(0),row.GetInt32(1),row.GetString(2)));
        }
        var observations=new Dictionary<string,BootEvidenceCode>(StringComparer.Ordinal);
        foreach(var item in pending)
        {
            if(!observations.TryGetValue(item.Stamp,out var evidence))
            {
                var prior=JsonSerializer.Deserialize<BootStamp>(item.Stamp,PipeWire.Json)
                    ?? throw new InvalidOperationException("Missing reboot checkpoint evidence.");
                evidence=BootEvidence.After(prior).Status;
                observations.Add(item.Stamp,evidence);
            }
            lock(gate)
            {
                using var tx=Db.BeginTransaction();
                using var update=Command("""
                    UPDATE jobs SET state=CASE WHEN $new=1 THEN 'QUEUED' ELSE state END,
                      reboot_evidence=$evidence,updated_utc=$now
                    WHERE job_id=$id AND completed_steps=$completed AND state='WAITING_FOR_REBOOT'
                      AND reboot_stamp=$stamp AND (reboot_evidence IS NULL OR reboot_evidence!=$evidence)
                    """,tx,("$new",evidence==BootEvidenceCode.NEW_FULL_BOOT_OBSERVED?1:0),
                    ("$evidence",evidence.ToString()),("$now",Now()),("$id",item.Id),
                    ("$completed",item.Completed),("$stamp",item.Stamp));
                bool changed=update.ExecuteNonQuery()==1;
                tx.Commit();
                if(changed && evidence==BootEvidenceCode.NEW_FULL_BOOT_OBSERVED)
                    logger.LogInformation("Job {JobId} new full boot confirmed; next step is declared postcondition check.",item.Id);
            }
        }
    }

    internal async Task RunAsync(CancellationToken stop)
    {
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(stop);
        var running=new HashSet<Task>();
        long nextBootProbe=0,nextHistoryMaintenance=0;
        try
        {
            while(!stop.IsCancellationRequested)
            {
                foreach(var done in running.Where(x=>x.IsCompleted).ToArray())
                {
                    running.Remove(done);
                    await done; // a failed durable transition is fatal, never a successful/forgotten job
                }
                long now=Environment.TickCount64;
                ExpireUserLease();
                if(now>=nextBootProbe)
                {
                    ObserveRebootBoundaries();
                    nextBootProbe=Environment.TickCount64+5000;
                }
                var work=Take(lifetime.Token);
                if(work is not null)
                {
                    // No fixed slot count, no resident PowerShell processes.
                    // Durable start intent and memory reservation precede launch.
                    running.Add(Task.Run(()=>ExecuteSystem(work)));
                    continue;
                }
                if(now>=nextHistoryMaintenance)
                {
                    MaintainHistory();
                    nextHistoryMaintenance=Environment.TickCount64+60000;
                }
                await wake.WaitAsync(TimeSpan.FromMilliseconds(250),stop);
            }
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested){}
        finally
        {
            await lifetime.CancelAsync();
            await Task.WhenAll(running); // finish/cancel all owned system trees before disposing store
        }
    }

    private async Task ExecuteSystem(Work work)
    {
        BootObservation? boot=work.RebootAfter?BootEvidence.Observe():null;
        var result=work.RebootAfter && boot?.Stamp is null
            ?new ExecutionResult(ExecutionState.COMMAND_ERROR,Hash(work.Script),0,Stage:"BOOT_EVIDENCE_UNAVAILABLE_BEFORE_EXECUTION")
            :await PowerShellRunner.RunAsync(work.Script,work.Timeout,work.Cwd,work.Cancel.Token);
        string? rebootStamp=boot?.Stamp is {} stamp && result.Status==ExecutionState.COMPLETED
            ?JsonSerializer.Serialize(stamp,PipeWire.Json):null;
        CompleteWork(work,result,rebootStamp);
    }

    private void CompleteWork(Work work,ExecutionResult result,string? rebootStamp)
    {
        JobState end;
        lock(gate)
        {
            using var tx=Db.BeginTransaction();
            var header=Find(null,work.Id,tx)??throw new InvalidOperationException("Unknown active job");
            bool cancelled=header.State==JobState.CANCEL_REQUESTED;
            bool success=result.Status==ExecutionState.COMPLETED;
            int completed=success?work.Index:work.Index-1;
            end=result.Status switch
            {
                ExecutionState.COMPLETED when work.Index==work.Count => JobState.COMPLETED,
                ExecutionState.COMPLETED when cancelled => JobState.CANCELLED, // finished step is known; remaining steps never start
                ExecutionState.COMPLETED when work.RebootAfter => JobState.WAITING_FOR_REBOOT,
                ExecutionState.COMPLETED when work.PauseAfter => JobState.WAITING_FOR_RESUME,
                ExecutionState.COMPLETED => JobState.QUEUED,
                ExecutionState.CANCELLED_BEFORE_EXECUTION => JobState.CANCELLED,
                ExecutionState.CANCELLED_UNKNOWN_EFFECT or ExecutionState.TIMEOUT_UNKNOWN_EFFECT or
                    ExecutionState.OUTPUT_INCOMPLETE or ExecutionState.EXECUTION_FAILED => JobState.NEEDS_RECONCILIATION,
                _=>JobState.FAILED
            };
            using var step=Command("""
                UPDATE job_steps SET result=$result,command=CASE WHEN $keep=1 THEN command ELSE NULL END
                WHERE job_id=$id AND step_index=$step AND result IS NULL
                """,tx,("$id",work.Id),("$step",work.Index),("$keep",end==JobState.NEEDS_RECONCILIATION?1:0),
                ("$result",JobDataProtection.Protect(JsonSerializer.Serialize(result with {AuditPersisted=true,ExecutionContext=work.Owner is null?CommandContext.SYSTEM:CommandContext.USER,
                    ExecutionSid=work.Owner?.Sid??"S-1-5-18",ExecutionSessionId=work.Owner?.SessionId??0,
                    QueueWaitMs=work.QueueWaitMs,StepStartedUtc=work.StartedUtc,StepFinishedUtc=Now(),Timing=null},PipeWire.Json),work.Id,Field(work.Index,"result"))));
            if(step.ExecuteNonQuery()!=1)throw new InvalidOperationException("Lost step transition");
            using var update=Command("""
                UPDATE jobs SET state=$state,completed_steps=$completed,updated_utc=$now,
                  reboot_stamp=CASE WHEN $state='WAITING_FOR_REBOOT' THEN $stamp ELSE reboot_stamp END,
                  reboot_evidence=CASE WHEN $state='WAITING_FOR_REBOOT' THEN 'SAME_BOOT' ELSE reboot_evidence END
                WHERE job_id=$id AND completed_steps=$prior AND state IN ('RUNNING','CANCEL_REQUESTED')
                """,tx,("$state",end.ToString()),("$completed",completed),("$now",Now()),("$id",work.Id),("$prior",work.Index-1),("$stamp",(object?)rebootStamp??DBNull.Value));
            if(update.ExecuteNonQuery()!=1)throw new InvalidOperationException("Lost job transition");
            if(end is JobState.CANCELLED or JobState.FAILED or JobState.NEEDS_RECONCILIATION)
            {
                using var clear=Command("UPDATE job_steps SET command=NULL WHERE job_id=$id AND ($keep=0 OR step_index<>$step)",
                    tx,("$id",work.Id),("$keep",end==JobState.NEEDS_RECONCILIATION?1:0),("$step",work.Index));
                clear.ExecuteNonQuery();
            }
            tx.Commit(); // output and checkpoint become visible together
            if(!active.Remove(work.Id))throw new InvalidOperationException("Missing active execution.");
            foreach(var lease in leases.Values.Where(l=>l.Work.Id==work.Id).ToArray())leases.Remove(lease.Id);
            work.Cancel.Dispose();Signal();
        }
        logger.LogInformation("Job {JobId} step {Step} finished {State}",work.Id,work.Index,end);
    }
    public void Dispose(){foreach(var work in active.Values){work.Cancel.Cancel();work.Cancel.Dispose();}connection?.Dispose();wake.Dispose();}
}
