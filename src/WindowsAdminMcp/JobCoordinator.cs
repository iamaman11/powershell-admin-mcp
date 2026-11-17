using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

internal sealed class JobCoordinator(ILogger<JobCoordinator> logger) : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim wake = new(0,1);
    private SqliteConnection? connection;
    private CancellationTokenSource? active;
    private string? activeId;
    private SqliteConnection Db => connection ?? throw new InvalidOperationException("Store unavailable");
    private const string Columns = "job_id,op_key,state,command_hash,created_utc,updated_utc,result";
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal void Initialize()
    {
        lock(gate)
        {
            connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=StateDirectory.DatabasePath(), Pooling=false }.ToString());
            Db.Open();
            using var cmd=Command("""
                PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;
                CREATE TABLE IF NOT EXISTS jobs(
                  seq INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL UNIQUE, op_key TEXT NOT NULL UNIQUE,
                  payload_hash TEXT NOT NULL, command_hash TEXT NOT NULL, command TEXT, timeout_seconds INTEGER NOT NULL,
                  cwd TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN ('QUEUED','RUNNING','CANCEL_REQUESTED','COMPLETED','FAILED','CANCELLED','NEEDS_RECONCILIATION')),
                  created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL, result TEXT);
                UPDATE jobs SET state='NEEDS_RECONCILIATION', command=NULL, updated_utc=$now WHERE state IN ('RUNNING','CANCEL_REQUESTED');
                """,null,("$now",Now()));
            cmd.ExecuteNonQuery();
        }
    }
    private void Signal() { if(wake.CurrentCount==0) wake.Release(); }

    internal JobReply Dispatch(AdminRequest request,string correlation)
    {
        JobReply Reply(StatusCode code, JobInfo? job=null, JobInfo[]? jobs=null)
            => new(code,correlation,job,jobs,request.Key);
        if(request.Action is not {} action || !Enum.IsDefined(action) || request.Key is {Length:0 or >100}
            || request.JobId is {Length:0 or >64}) return Reply(StatusCode.INVALID_ARGUMENT);
        lock(gate)
        {
            if(action==JobAction.submit)
            {
                if(request.Key is not {Length:>0} key || string.IsNullOrWhiteSpace(key) || request.JobId is not null ||
                    request.Command is not {Length:>0 and <=12000} script || request.TimeoutSeconds is <1 or >3600)
                    return Reply(StatusCode.INVALID_ARGUMENT);
                string cwd=request.WorkingDirectory ?? Environment.SystemDirectory;
                if(cwd.Length>1024 || !Path.IsPathFullyQualified(cwd)) return Reply(StatusCode.INVALID_ARGUMENT);
                string payload=Hash(JsonSerializer.Serialize(new {script,request.TimeoutSeconds,cwd}));
                using var tx=Db.BeginTransaction();
                var prior=Find(key,null,tx,false);
                if(prior is not null)
                {
                    using var check=Command("SELECT payload_hash FROM jobs WHERE job_id=$id",tx,("$id",prior.JobId));
                    return Reply((string?)check.ExecuteScalar()==payload?StatusCode.OK:StatusCode.CONFLICT,prior);
                }
                if(!Directory.Exists(cwd)) return Reply(StatusCode.INVALID_ARGUMENT);
                using var count=Command("SELECT count(*) FROM jobs WHERE state IN ('QUEUED','RUNNING','CANCEL_REQUESTED')",tx);
                if((long)count.ExecuteScalar()!>=16) return Reply(StatusCode.BUSY);
                using var total=Command("SELECT count(*) FROM jobs",tx);
                if((long)total.ExecuteScalar()!>=10000) return Reply(StatusCode.STORE_FULL);
                string id=Guid.NewGuid().ToString("N"), now=Now(), hash=Hash(script);
                using var insert=Command("""
                    INSERT INTO jobs(job_id,op_key,payload_hash,command_hash,command,timeout_seconds,cwd,state,created_utc,updated_utc)
                    VALUES($id,$key,$payload,$hash,$script,$timeout,$cwd,'QUEUED',$now,$now)
                    """,tx,("$id",id),("$key",key),("$payload",payload),("$hash",hash),("$script",script),
                    ("$timeout",request.TimeoutSeconds),("$cwd",cwd),("$now",now));
                insert.ExecuteNonQuery();
                tx.Commit();
                Signal();
                logger.LogInformation("Job admitted {JobId} correlation {Correlation}",id,correlation);
                return Reply(StatusCode.OK,new(id,key,JobState.QUEUED,hash,now,now));
            }
            if(request.Command is not null || request.WorkingDirectory is not null) return Reply(StatusCode.INVALID_ARGUMENT);
            if(action==JobAction.list)
            {
                if(request.Key is not null || request.JobId is not null) return Reply(StatusCode.INVALID_ARGUMENT);
                using var cmd=Command("SELECT "+Columns+" FROM jobs ORDER BY seq DESC LIMIT 20",null);
                using var reader=cmd.ExecuteReader();
                var jobs=new List<JobInfo>();
                while(reader.Read()) jobs.Add(Read(reader,false));
                return Reply(StatusCode.OK,jobs:jobs.ToArray());
            }
            if((request.Key is null)==(request.JobId is null)) return Reply(StatusCode.INVALID_ARGUMENT);
            using var change=Db.BeginTransaction();
            var job=Find(request.Key,request.JobId,change,action==JobAction.logs);
            if(job is null) return Reply(StatusCode.NOT_FOUND);
            if(action is JobAction.status or JobAction.logs) return Reply(StatusCode.OK,job);
            if(action!=JobAction.cancel) return Reply(StatusCode.INVALID_ARGUMENT);
            if(job.State==JobState.QUEUED)
            {
                string now=Now();
                using var cmd=Command("UPDATE jobs SET state='CANCELLED',command=NULL,updated_utc=$now WHERE job_id=$id",change,("$now",now),("$id",job.JobId));
                cmd.ExecuteNonQuery();change.Commit();
                return Reply(StatusCode.OK,job with {State=JobState.CANCELLED,UpdatedUtc=now});
            }
            if(job.State is JobState.CANCEL_REQUESTED or JobState.CANCELLED) return Reply(StatusCode.OK,job);
            if(job.State!=JobState.RUNNING || activeId!=job.JobId || active is null) return Reply(StatusCode.NOT_CANCELLABLE,job);
            string when=Now();
            using var cancel=Command("UPDATE jobs SET state='CANCEL_REQUESTED',updated_utc=$now WHERE job_id=$id",change,("$now",when),("$id",job.JobId));
            cancel.ExecuteNonQuery();change.Commit();active.Cancel();
            return Reply(StatusCode.OK,job with {State=JobState.CANCEL_REQUESTED,UpdatedUtc=when});
        }
    }
    private JobInfo? Find(string? key,string? id,SqliteTransaction tx,bool result)
    {
        using var cmd=Command("SELECT "+Columns+" FROM jobs WHERE "+(key is not null?"op_key=$value":"job_id=$value"),tx,("$value",key??id!));
        using var reader=cmd.ExecuteReader();
        return reader.Read()?Read(reader,result):null;
    }
    private static JobInfo Read(SqliteDataReader r,bool result)=>new(r.GetString(0),r.GetString(1),
        Enum.Parse<JobState>(r.GetString(2)),r.GetString(3),r.GetString(4),r.GetString(5),
        result&&!r.IsDBNull(6)?JsonSerializer.Deserialize<ExecutionResult>(r.GetString(6),PipeWire.Json):null);
    private SqliteCommand Command(string sql,SqliteTransaction? tx,params (string Name,object Value)[] parameters)
    {
        var cmd=Db.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;
        foreach(var (name,value) in parameters)cmd.Parameters.AddWithValue(name,value);
        return cmd;
    }
    private sealed record Work(string Id,string Script,int Timeout,string Cwd,CancellationTokenSource Cancel);
    private Work? Take(CancellationToken stop)
    {
        lock(gate)
        {
            if(stop.IsCancellationRequested)return null;
            using var tx=Db.BeginTransaction();
            using var cmd=Command("""
                UPDATE jobs SET state='RUNNING',updated_utc=$now
                WHERE seq=(SELECT seq FROM jobs WHERE state='QUEUED' ORDER BY seq LIMIT 1)
                RETURNING job_id,command,timeout_seconds,cwd
                """,tx,("$now",Now()));
            string id,script,cwd;int timeout;
            using(var row=cmd.ExecuteReader())
            {
                if(!row.Read())return null;
                id=row.GetString(0);script=row.GetString(1);timeout=row.GetInt32(2);cwd=row.GetString(3);
            }
            tx.Commit();
            active=CancellationTokenSource.CreateLinkedTokenSource(stop);activeId=id;
            return new(id,script,timeout,cwd,active);
        }
    }
    internal async Task RunAsync(CancellationToken stop)
    {
        while(!stop.IsCancellationRequested)
        {
            var work=Take(stop);
            if(work is null){await wake.WaitAsync(stop);continue;}
            var result=await PowerShellRunner.RunAsync(work.Script,work.Timeout,work.Cwd,work.Cancel.Token);
            JobState end=result.Status switch
            {
                ExecutionState.COMPLETED=>JobState.COMPLETED,
                ExecutionState.CANCELLED_BEFORE_EXECUTION=>JobState.CANCELLED,
                ExecutionState.CANCELLED_UNKNOWN_EFFECT or ExecutionState.TIMEOUT_UNKNOWN_EFFECT or
                    ExecutionState.OUTPUT_INCOMPLETE or ExecutionState.EXECUTION_FAILED=>JobState.NEEDS_RECONCILIATION,
                _=>JobState.FAILED
            };
            lock(gate)
            {
                using var cmd=Command("""
                    UPDATE jobs SET state=$state,result=$result,command=NULL,updated_utc=$now
                    WHERE job_id=$id AND state IN ('RUNNING','CANCEL_REQUESTED')
                    """,null,("$state",end.ToString()),("$result",JsonSerializer.Serialize(result,PipeWire.Json)),("$now",Now()),("$id",work.Id));
                if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Lost job transition");
                active=null;activeId=null;work.Cancel.Dispose();
            }
            logger.LogInformation("Job {JobId} finished {State}",work.Id,end);
        }
    }
    public void Dispose(){active?.Dispose();connection?.Dispose();wake.Dispose();}
}
