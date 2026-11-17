using System.Security.Principal;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

if (args is ["pipe-client", var pipe, var pid, var json])
{
    await PipeProbe.ClientAsync(pipe, uint.Parse(pid, System.Globalization.CultureInfo.InvariantCulture), json);
    return;
}

if (args is ["restricted-client", var restrictedPipe, var database])
{
    SecurityProbe.RestrictedClient(restrictedPipe, database);
    return;
}
if (args is ["fake-server"])
{
    await SecurityProbe.RejectFakeServer();
    return;
}
if (args is ["job-object"])
{
    JobObjectProbe.Run();
    return;
}

if (args is not [var mode, var path])
    throw new ArgumentException("Expected service/inspect/crash-write and an absolute database path.");
if (!Path.IsPathFullyQualified(path))
    throw new ArgumentException("Absolute database path required.");

if (mode == "inspect")
{
    using var db = Store.Open(path);
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT state, starts, checkpoint, identity FROM probe WHERE id=1";
    using var row = cmd.ExecuteReader();
    if (!row.Read()) throw new InvalidOperationException("Missing probe state");
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        state = row.GetString(0), starts = row.GetInt64(1),
        checkpoint = row.GetInt64(2), identity = row.GetString(3)
    }));
    return;
}
if (mode == "crash-write")
{
    using var db = Store.Open(path);
    using var tx = db.BeginTransaction();
    Store.Execute(db, "UPDATE probe SET checkpoint=999 WHERE id=1", tx);
    // Intentional abrupt process death; the transaction must not become visible.
    System.Diagnostics.Process.GetCurrentProcess().Kill();
    return;
}
if (mode != "service" || !WindowsServiceHelpers.IsWindowsService())
    throw new InvalidOperationException("Service mode may only run under SCM.");

var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Services.AddWindowsService(options => options.ServiceName = "WindowsAdminMcpR0Probe");
builder.Services.AddSingleton(new Store(path));
builder.Services.AddHostedService<ProbeWorker>();
builder.Services.AddHostedService(_ => new PipeProbe(path));
await builder.Build().RunAsync();

sealed class Store(string path)
{
    public static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false
        }.ToString());
        db.Open();
        Execute(db, "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;");
        return db;
    }

    public static void Execute(SqliteConnection db, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Start()
    {
        using var db = Open(path);
        Execute(db, """
            CREATE TABLE IF NOT EXISTS probe (
                id INTEGER PRIMARY KEY CHECK (id=1),
                state TEXT NOT NULL CHECK(state IN ('RUNNING','STOPPED','NEEDS_RECONCILIATION')),
                starts INTEGER NOT NULL,
                checkpoint INTEGER NOT NULL,
                identity TEXT NOT NULL);
            """);
        using var tx = db.BeginTransaction();
        using var insert = db.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO probe VALUES (1,'RUNNING',1,1,$identity)
            ON CONFLICT(id) DO UPDATE SET
                state=CASE WHEN state='RUNNING' OR state='NEEDS_RECONCILIATION'
                    THEN 'NEEDS_RECONCILIATION' ELSE 'RUNNING' END,
                starts=starts+1, identity=excluded.identity;
            """;
        using var identity = WindowsIdentity.GetCurrent();
        insert.Parameters.AddWithValue("$identity", identity.User?.Value ?? throw new InvalidOperationException("No SID"));
        insert.ExecuteNonQuery();
        tx.Commit();
    }

    public void Stop()
    {
        using var db = Open(path);
        using var tx = db.BeginTransaction();
        Execute(db, "UPDATE probe SET state='STOPPED' WHERE id=1 AND state='RUNNING'", tx);
        tx.Commit();
    }
}

sealed class ProbeWorker(Store store) : BackgroundService
{
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        store.Start();
        return base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        store.Stop();
    }
}
