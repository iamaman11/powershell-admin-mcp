using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

internal sealed record PipeRequest(string Action, string? Key = null, int DelayMs = 0);
internal sealed record PipeReply(string Status, long? Id = null, string? State = null, string? Result = null);

internal sealed class PipeProbe(string path) : BackgroundService
{
    // R0 fixture only: inert bounded delays, no shell, no real credentials.
    // The only owner of writes is this service. A lock serializes short SQLite
    // transactions, not network reads/writes or the actual execution.
    private readonly object gate = new();
    private readonly string name = "WindowsAdminMcpR0_" + Path.GetFileName(Path.GetDirectoryName(path)!);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static PipeSecurity Security()
    {
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
        return acl;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        lock (gate)
        {
            using var db = Store.Open(path);
            Store.Execute(db, """
                CREATE TABLE IF NOT EXISTS pipe_jobs (
                    id INTEGER PRIMARY KEY, op_key TEXT NOT NULL UNIQUE,
                    delay_ms INTEGER NOT NULL,
                    state TEXT NOT NULL CHECK(state IN ('QUEUED','RUNNING','COMPLETED','CANCELLED','NEEDS_RECONCILIATION')),
                    result TEXT);
                UPDATE pipe_jobs SET state='NEEDS_RECONCILIATION' WHERE state='RUNNING';
                """);
        }
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ServeAsync(stoppingToken))
            .Append(ExecuteQueueAsync(stoppingToken)));
    }

    private async Task ServeAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 4,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, Security());
            try
            {
                await pipe.WaitForConnectionAsync(stop);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                // Read bounded frame before impersonation, as required by Windows pipes.
                string input = await ReadFrameAsync(pipe, deadline.Token);
                bool authenticated = false;
                pipe.RunAsClient(() =>
                {
                    using var id = WindowsIdentity.GetCurrent(true);
                    authenticated = id is not null && (id.IsSystem ||
                        new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator));
                });
                var reply = authenticated
                    ? Dispatch(JsonSerializer.Deserialize<PipeRequest>(input, Json) ?? throw new JsonException())
                    : new PipeReply("ACCESS_DENIED");
                await WriteFrameAsync(pipe, JsonSerializer.Serialize(reply, Json), deadline.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or JsonException or UnauthorizedAccessException)
            {
                // An abandoned, malformed or slow connection does not stop the listener.
                // Unexpected exceptions propagate and fail the service in this fixture.
            }
        }
    }

    private PipeReply Dispatch(PipeRequest request)
    {
        if (request.Action == "ping") return new("OK");
        if (request.Key is not { Length: > 0 and <= 64 } key) return new("INVALID_ARGUMENT");
        lock (gate)
        {
            using var db = Store.Open(path);
            using var tx = db.BeginTransaction();
            PipeReply answer;
            using var lookup = db.CreateCommand();
            lookup.Transaction = tx;
            lookup.CommandText = "SELECT id, delay_ms, state, result FROM pipe_jobs WHERE op_key=$key";
            lookup.Parameters.AddWithValue("$key", key);
            long? id = null;
            int delay = 0;
            string? state = null, result = null;
            using (var row = lookup.ExecuteReader())
            {
                if (row.Read())
                {
                    id = row.GetInt64(0); delay = row.GetInt32(1); state = row.GetString(2);
                    result = row.IsDBNull(3) ? null : row.GetString(3);
                }
            }
            if (request.Action == "submit")
            {
                if (request.DelayMs is < 0 or > 10000) answer = new("INVALID_ARGUMENT");
                else if (id.HasValue)
                    answer = delay == request.DelayMs ? new("OK", id, state, result) : new("CONFLICT");
                else
                {
                    using var count = db.CreateCommand();
                    count.Transaction = tx;
                    count.CommandText = "SELECT COUNT(*) FROM pipe_jobs WHERE state IN ('QUEUED','RUNNING')";
                    if ((long)count.ExecuteScalar()! >= 4) answer = new("BUSY");
                    else
                    {
                        using var insert = db.CreateCommand();
                        insert.Transaction = tx;
                        insert.CommandText = "INSERT INTO pipe_jobs(op_key,delay_ms,state) VALUES($key,$delay,'QUEUED') RETURNING id";
                        insert.Parameters.AddWithValue("$key", key);
                        insert.Parameters.AddWithValue("$delay", request.DelayMs);
                        answer = new("OK", (long)insert.ExecuteScalar()!, "QUEUED");
                    }
                }
            }
            else if (request.Action == "status")
                answer = id.HasValue ? new("OK", id, state, result) : new("NOT_FOUND");
            else if (request.Action == "cancel")
            {
                if (!id.HasValue) answer = new("NOT_FOUND");
                // Fixture cancels queued entries only. Running cancel is not claimed.
                else if (state == "QUEUED")
                {
                    using var cancel = db.CreateCommand();
                    cancel.Transaction = tx;
                    cancel.CommandText = "UPDATE pipe_jobs SET state='CANCELLED' WHERE id=$id";
                    cancel.Parameters.AddWithValue("$id", id.Value);
                    cancel.ExecuteNonQuery();
                    answer = new("OK", id, "CANCELLED");
                }
                else answer = new(state == "CANCELLED" ? "OK" : "NOT_CANCELLABLE", id, state);
            }
            else answer = new("INVALID_ARGUMENT");
            tx.Commit(); // positive submit is returned only after durable admission
            return answer;
        }
    }

    private async Task ExecuteQueueAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            long? id = null;
            int delay = 0;
            string? key = null;
            lock (gate)
            {
                using var db = Store.Open(path);
                using var tx = db.BeginTransaction();
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE pipe_jobs SET state='RUNNING'
                    WHERE id=(SELECT id FROM pipe_jobs WHERE state='QUEUED' ORDER BY id LIMIT 1)
                    RETURNING id,delay_ms,op_key
                    """;
                using (var row = cmd.ExecuteReader())
                {
                    if (row.Read()) { id = row.GetInt64(0); delay = row.GetInt32(1); key = row.GetString(2); }
                }
                tx.Commit();
            }
            if (id.HasValue)
            {
                await Task.Delay(delay, stop); // no DB lock held during job
                lock (gate)
                {
                    using var db = Store.Open(path);
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = "UPDATE pipe_jobs SET state='COMPLETED',result=$result WHERE id=$id AND state='RUNNING'";
                    cmd.Parameters.AddWithValue("$result", "fixture:" + key);
                    cmd.Parameters.AddWithValue("$id", id.Value);
                    if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Unexpected transition");
                }
            }
            else await Task.Delay(25, stop);
        }
    }

    // Explicit small length-prefixed frames; no unbounded ReadLine or new protocol server.
    private static async Task<string> ReadFrameAsync(Stream s, CancellationToken token)
    {
        byte[] header = new byte[4];
        await s.ReadExactlyAsync(header, token);
        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > 4096) throw new IOException("Invalid fixture frame length");
        byte[] body = new byte[length];
        await s.ReadExactlyAsync(body, token);
        return new UTF8Encoding(false, true).GetString(body);
    }
    private static async Task WriteFrameAsync(Stream s, string value, CancellationToken token)
    {
        byte[] body = Encoding.UTF8.GetBytes(value);
        if (body.Length is < 1 or > 4096) throw new IOException("Invalid fixture frame length");
        byte[] header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await s.WriteAsync(header, token);
        await s.WriteAsync(body, token);
        await s.FlushAsync(token);
    }

    public static async Task ClientAsync(string name, string payload)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        await pipe.ConnectAsync(deadline.Token);
        await WriteFrameAsync(pipe, payload, deadline.Token);
        Console.WriteLine(await ReadFrameAsync(pipe, deadline.Token));
    }
}
