using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

internal sealed class JobCoordinator(ILogger<JobCoordinator> logger) : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private SqliteConnection? connection;
    private CancellationTokenSource? active;
    private string? activeId;
    private SqliteConnection Db => connection ?? throw new InvalidOperationException("Store unavailable");
    private const string Columns = "job_id,op_key,state,command_hash,created_utc,updated_utc,completed_steps,step_count";
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal void Initialize()
    {
        lock (gate)
        {
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = StateDirectory.DatabasePath(), Pooling = false
            }.ToString());
            Db.Open();
            using (var version = Command("PRAGMA user_version", null))
            {
                long v = (long)version.ExecuteScalar()!;
                if (v == 0)
                {
                    using var existing = Command("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='jobs'", null);
                    if ((long)existing.ExecuteScalar()! != 0) throw new InvalidOperationException("Unversioned database.");
                }
                else if (v != 2) throw new InvalidOperationException("Unsupported job store version; offline migration required.");
            }
            using (var pragmas = Command("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;", null))
                pragmas.ExecuteNonQuery();
            using var tx = Db.BeginTransaction();
            using var schema = Command("""
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
                """, tx);
            schema.ExecuteNonQuery();
            using var recovery = Command("""
                UPDATE job_steps SET command=NULL WHERE job_id IN (SELECT job_id FROM jobs WHERE state IN ('RUNNING','CANCEL_REQUESTED'));
                UPDATE jobs SET state='NEEDS_RECONCILIATION', updated_utc=$now WHERE state IN ('RUNNING','CANCEL_REQUESTED');
                """, tx, ("$now", Now()));
            int recovered = recovery.ExecuteNonQuery();
            tx.Commit();
            logger.LogInformation("Job store ready. Recovery changed rows {Recovered}", recovered);
        }
    }

    private void Signal() { if (wake.CurrentCount == 0) wake.Release(); }

    internal JobReply Dispatch(AdminRequest request, string correlation)
    {
        JobReply Reply(StatusCode code, JobInfo? job = null, JobInfo[]? jobs = null)
            => new(code, correlation, job, jobs, request.Key);
        if (request.Action is not {} action || !Enum.IsDefined(action) ||
            request.Key is {Length:0 or >100} || request.JobId is {Length:0 or >64} ||
            (request.StepIndex is not null && action != JobAction.logs) ||
            (request.ExpectedCompletedSteps is not null && action != JobAction.resume))
            return Reply(StatusCode.INVALID_ARGUMENT);

        lock (gate)
        {
            if (action == JobAction.submit)
            {
                if (request.Key is not {Length:>0} key || string.IsNullOrWhiteSpace(key) || request.JobId is not null ||
                    (request.Command is null) == (request.Steps is null))
                    return Reply(StatusCode.INVALID_ARGUMENT);
                JobStep[] steps = request.Steps ?? [new(request.Command!, request.TimeoutSeconds)];
                if (steps.Length is <1 or >8 || steps.Any(s => s is null || s.Command is not {Length:>0 and <=12000} ||
                     s.TimeoutSeconds is <1 or >3600) || steps.Sum(s => s.Command.Length) > 48000 ||
                     steps[^1].PauseAfter)
                    return Reply(StatusCode.INVALID_ARGUMENT);
                string cwd = request.WorkingDirectory ?? Environment.SystemDirectory;
                if (cwd.Length > 1024 || !Path.IsPathFullyQualified(cwd)) return Reply(StatusCode.INVALID_ARGUMENT);
                string payload = Hash(JsonSerializer.Serialize(new {steps,cwd}));
                using var tx = Db.BeginTransaction();
                var prior = Find(key, null, tx);
                if (prior is not null)
                {
                    using var check = Command("SELECT payload_hash FROM jobs WHERE job_id=$id", tx, ("$id",prior.JobId));
                    return Reply((string?)check.ExecuteScalar() == payload ? StatusCode.OK : StatusCode.CONFLICT, prior);
                }
                if (!Directory.Exists(cwd)) return Reply(StatusCode.INVALID_ARGUMENT);
                using var count = Command("SELECT count(*) FROM jobs WHERE state IN ('QUEUED','RUNNING','CANCEL_REQUESTED','WAITING_FOR_RESUME')", tx);
                if ((long)count.ExecuteScalar()! >= 16) return Reply(StatusCode.BUSY);
                using var total = Command("SELECT count(*) FROM jobs", tx);
                if ((long)total.ExecuteScalar()! >= 10000) return Reply(StatusCode.STORE_FULL);
                string id = Guid.NewGuid().ToString("N"), now = Now(),
                    hash = steps.Length == 1 ? Hash(steps[0].Command) : Hash(JsonSerializer.Serialize(steps));
                using var insert = Command("""
                    INSERT INTO jobs(job_id,op_key,payload_hash,command_hash,cwd,state,created_utc,updated_utc,step_count)
                    VALUES($id,$key,$payload,$hash,$cwd,'QUEUED',$now,$now,$count)
                    """, tx, ("$id",id), ("$key",key), ("$payload",payload), ("$hash",hash),
                    ("$cwd",JobDataProtection.Protect(cwd,id,"cwd")), ("$now",now), ("$count",steps.Length));
                insert.ExecuteNonQuery();
                for (int i = 0; i < steps.Length; i++)
                {
                    var step = steps[i];
                    using var add = Command("""
                        INSERT INTO job_steps(job_id,step_index,command,timeout_seconds,pause_after)
                        VALUES($id,$step,$command,$timeout,$pause)
                        """, tx, ("$id",id), ("$step",i+1),
                        ("$command",JobDataProtection.Protect(step.Command,id,Field(i+1,"command"))),
                        ("$timeout",step.TimeoutSeconds), ("$pause",step.PauseAfter?1:0));
                    add.ExecuteNonQuery();
                }
                tx.Commit();
                Signal();
                logger.LogInformation("Job admitted {JobId} correlation {Correlation}, step count {Count}",id,correlation,steps.Length);
                return Reply(StatusCode.OK,new(id,key,JobState.QUEUED,hash,now,now,TotalSteps:steps.Length));
            }

            if (request.Command is not null || request.WorkingDirectory is not null || request.Steps is not null)
                return Reply(StatusCode.INVALID_ARGUMENT);
            if (action == JobAction.list)
            {
                if (request.Key is not null || request.JobId is not null) return Reply(StatusCode.INVALID_ARGUMENT);
                using var cmd = Command("SELECT "+Columns+" FROM jobs ORDER BY seq DESC LIMIT 20",null);
                using var reader = cmd.ExecuteReader();
                var jobs = new List<JobInfo>();
                while(reader.Read()) jobs.Add(Read(reader));
                return Reply(StatusCode.OK,jobs:jobs.ToArray());
            }
            if ((request.Key is null) == (request.JobId is null)) return Reply(StatusCode.INVALID_ARGUMENT);
            using var change = Db.BeginTransaction();
            var job = Find(request.Key,request.JobId,change);
            if (job is null) return Reply(StatusCode.NOT_FOUND);
            if (action == JobAction.status) return Reply(StatusCode.OK,job);
            if (action == JobAction.logs)
            {
                int index = request.StepIndex ?? Math.Min(job.TotalSteps, job.State is JobState.COMPLETED or JobState.WAITING_FOR_RESUME
                    ? job.CompletedSteps : job.CompletedSteps + 1);
                if (index < 1 || index > job.TotalSteps) return Reply(StatusCode.INVALID_ARGUMENT);
                using var cmd = Command("SELECT result FROM job_steps WHERE job_id=$id AND step_index=$step",change,("$id",job.JobId),("$step",index));
                var blob = cmd.ExecuteScalar();
                var execution = blob is byte[] bytes ? JsonSerializer.Deserialize<ExecutionResult>(
                    JobDataProtection.Open(bytes,job.JobId,Field(index,"result")),PipeWire.Json) : null;
                return Reply(StatusCode.OK,job with {Execution=execution,ResultStep=index});
            }
            if (action == JobAction.resume)
            {
                if (request.ExpectedCompletedSteps is not int expected || expected < 1 ||
                    expected != job.CompletedSteps || job.State == JobState.NEEDS_RECONCILIATION)
                    return Reply(StatusCode.NOT_RESUMABLE,job);
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
            if (job.State is JobState.QUEUED or JobState.WAITING_FOR_RESUME)
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
            if (job.State != JobState.RUNNING || activeId != job.JobId || active is null)
                return Reply(StatusCode.NOT_CANCELLABLE,job);
            string when=Now();
            using var cancel=Command("UPDATE jobs SET state='CANCEL_REQUESTED',updated_utc=$now WHERE job_id=$id",change,("$now",when),("$id",job.JobId));
            cancel.ExecuteNonQuery();change.Commit();active.Cancel();
            return Reply(StatusCode.OK,job with {State=JobState.CANCEL_REQUESTED,UpdatedUtc=when});
        }
    }

    private static string Field(int index,string field) => $"step/{index}/{field}";
    private JobInfo? Find(string? key,string? id,SqliteTransaction tx)
    {
        using var cmd=Command("SELECT "+Columns+" FROM jobs WHERE "+(key is not null?"op_key=$value":"job_id=$value"),tx,("$value",key??id!));
        using var reader=cmd.ExecuteReader();
        return reader.Read()?Read(reader):null;
    }
    private static JobInfo Read(SqliteDataReader r)=>new(r.GetString(0),r.GetString(1),
        Enum.Parse<JobState>(r.GetString(2)),r.GetString(3),r.GetString(4),r.GetString(5),
        CompletedSteps:r.GetInt32(6),TotalSteps:r.GetInt32(7));
    private SqliteCommand Command(string sql,SqliteTransaction? tx,params (string Name,object Value)[] parameters)
    {
        var cmd=Db.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;
        foreach(var (name,value) in parameters)cmd.Parameters.AddWithValue(name,value);
        return cmd;
    }
    private sealed record Work(string Id,int Index,int Count,string Script,int Timeout,string Cwd,bool PauseAfter,CancellationTokenSource Cancel);
    private Work? Take(CancellationToken stop)
    {
        lock(gate)
        {
            if(stop.IsCancellationRequested)return null;
            using var tx=Db.BeginTransaction();
            using var cmd=Command("""
                UPDATE jobs SET state='RUNNING',updated_utc=$now
                WHERE seq=(SELECT seq FROM jobs WHERE state='QUEUED' ORDER BY seq LIMIT 1)
                RETURNING job_id,completed_steps,step_count,cwd
                """,tx,("$now",Now()));
            string id,cwd;int index,count;
            using(var row=cmd.ExecuteReader())
            {
                if(!row.Read())return null;
                id=row.GetString(0);index=row.GetInt32(1)+1;count=row.GetInt32(2);
                cwd=JobDataProtection.Open((byte[])row.GetValue(3),id,"cwd");
            }
            string script;int timeout;bool pause;
            using(var item=Command("SELECT command,timeout_seconds,pause_after FROM job_steps WHERE job_id=$id AND step_index=$step",tx,("$id",id),("$step",index)))
            using(var row=item.ExecuteReader())
            {
                if(!row.Read() || row.IsDBNull(0))throw new InvalidOperationException("Missing next step");
                script=JobDataProtection.Open((byte[])row.GetValue(0),id,Field(index,"command"));
                timeout=row.GetInt32(1);pause=row.GetInt32(2)!=0;
            }
            tx.Commit(); // durable start intent
            active=CancellationTokenSource.CreateLinkedTokenSource(stop);activeId=id;
            return new(id,index,count,script,timeout,cwd,pause,active);
        }
    }

    internal async Task RunAsync(CancellationToken stop)
    {
        while(!stop.IsCancellationRequested)
        {
            var work=Take(stop);
            if(work is null){await wake.WaitAsync(stop);continue;}
            var result=await PowerShellRunner.RunAsync(work.Script,work.Timeout,work.Cwd,work.Cancel.Token);
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
                    ExecutionState.COMPLETED when work.PauseAfter => JobState.WAITING_FOR_RESUME,
                    ExecutionState.COMPLETED => JobState.QUEUED,
                    ExecutionState.CANCELLED_BEFORE_EXECUTION => JobState.CANCELLED,
                    ExecutionState.CANCELLED_UNKNOWN_EFFECT or ExecutionState.TIMEOUT_UNKNOWN_EFFECT or
                        ExecutionState.OUTPUT_INCOMPLETE or ExecutionState.EXECUTION_FAILED => JobState.NEEDS_RECONCILIATION,
                    _=>JobState.FAILED
                };
                using var step=Command("""
                    UPDATE job_steps SET result=$result,command=NULL
                    WHERE job_id=$id AND step_index=$step AND result IS NULL
                    """,tx,("$id",work.Id),("$step",work.Index),
                    ("$result",JobDataProtection.Protect(JsonSerializer.Serialize(result,PipeWire.Json),work.Id,Field(work.Index,"result"))));
                if(step.ExecuteNonQuery()!=1)throw new InvalidOperationException("Lost step transition");
                using var update=Command("""
                    UPDATE jobs SET state=$state,completed_steps=$completed,updated_utc=$now
                    WHERE job_id=$id AND completed_steps=$prior AND state IN ('RUNNING','CANCEL_REQUESTED')
                    """,tx,("$state",end.ToString()),("$completed",completed),("$now",Now()),("$id",work.Id),("$prior",work.Index-1));
                if(update.ExecuteNonQuery()!=1)throw new InvalidOperationException("Lost job transition");
                if(end is JobState.CANCELLED or JobState.FAILED or JobState.NEEDS_RECONCILIATION)
                {
                    using var clear=Command("UPDATE job_steps SET command=NULL WHERE job_id=$id",tx,("$id",work.Id));
                    clear.ExecuteNonQuery();
                }
                tx.Commit(); // output and checkpoint become visible together
                active=null;activeId=null;work.Cancel.Dispose();
            }
            logger.LogInformation("Job {JobId} step {Step} finished {State}",work.Id,work.Index,end);
        }
    }
    public void Dispose(){active?.Dispose();connection?.Dispose();wake.Dispose();}
}
