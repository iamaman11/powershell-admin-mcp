using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

[JsonConverter(typeof(JsonStringEnumConverter<LegacyCleanupCode>))]
internal enum LegacyCleanupCode
{
    READY, CLEARED, ALREADY_CLEARED, INVALID_ARGUMENT, UNAUTHORIZED,
    INSTALLATION_UNVERIFIED, SERVICE_NOT_STOPPED, STORE_UNAVAILABLE,
    LEGACY_SET_MISMATCH, PLAN_CHANGED, ACTIVE_JOBS, INTEGRITY_FAILED,
    BACKUP_PRESENT, POSTCOMMIT_INCOMPLETE
}

internal sealed record LegacyCleanupResult(
    [property: JsonPropertyName("status")] LegacyCleanupCode Status,
    [property: JsonPropertyName("legacy_count")] int LegacyCount = 0,
    [property: JsonPropertyName("fingerprint")] string? Fingerprint = null,
    [property: JsonPropertyName("deleted_count")] int DeletedCount = 0,
    [property: JsonPropertyName("remaining_count")] int? RemainingCount = null,
    [property: JsonPropertyName("schema_version")] int? SchemaVersion = null);

// Deliberately narrow one-time maintenance for the previous test generation.
// No MCP "delete job", no new store owner, and never execute/reconcile a command.
internal static class LegacyUnknownCleanup
{
    internal const int ExpectedCount = 17;
    internal static readonly DateTimeOffset Earliest =
        new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset Cutoff =
        new(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);
    private const string BackupPrefix = "jobs.db.legacy-unknown-";

    private sealed record Row(string Id, string Key, string Created);

    internal static LegacyCleanupResult Execute(bool apply, string? fingerprint)
    {
        if (apply && (fingerprint is null || fingerprint.Length != 64 ||
            fingerprint.Any(c => !Uri.IsHexDigit(c))))
            return new(LegacyCleanupCode.INVALID_ARGUMENT);
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsAdminMCP");
        string exe = Path.Combine(root, "native", "WindowsAdminMCP.exe");
        if (!string.Equals(Environment.ProcessPath, exe, StringComparison.OrdinalIgnoreCase) ||
            PackageIntegrity.Observe(root).Status != PackageCode.VERIFIED)
            return new(LegacyCleanupCode.INSTALLATION_UNVERIFIED);
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!identity.IsSystem && !principal.IsInRole(WindowsBuiltInRole.Administrator))
            return new(LegacyCleanupCode.UNAUTHORIZED);
        try { ServiceIdentity.RequireStoppedOrAbsent(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { return new(LegacyCleanupCode.SERVICE_NOT_STOPPED); }
        try { return OnDatabase(StateDirectory.DatabasePath(create: false), apply, fingerprint); }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or
            InvalidOperationException or System.Security.SecurityException)
        { return new(LegacyCleanupCode.STORE_UNAVAILABLE); }
    }

    // Tests use isolated SQLite files; production calls only Execute after
    // verified installed-package, elevated identity and stopped-SCM checks.
    internal static LegacyCleanupResult OnDatabase(string path, bool apply, string? fingerprint,
        Action? beforeCommit = null)
    {
        if (apply && (fingerprint is null || fingerprint.Length != 64 ||
            fingerprint.Any(c => !Uri.IsHexDigit(c))))
            return new(LegacyCleanupCode.INVALID_ARGUMENT);
        if (!File.Exists(path)) return new(LegacyCleanupCode.STORE_UNAVAILABLE);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 2,
            ForeignKeys = true
        }.ToString());
        db.Open();
        int schema = checked((int)Scalar(db, "PRAGMA user_version"));
        if (schema != StoreSchema.Current)
            return new(LegacyCleanupCode.STORE_UNAVAILABLE, SchemaVersion: schema);
        int active = checked((int)Scalar(db,
            "SELECT count(*) FROM jobs WHERE state IN ('QUEUED','RUNNING','CANCEL_REQUESTED')"));
        if (active > 0) return new(LegacyCleanupCode.ACTIVE_JOBS, SchemaVersion: schema);
        var initial = CandidateRows(db, null);
        if (initial.Count == 0)
            return new(LegacyCleanupCode.ALREADY_CLEARED, RemainingCount: 0, SchemaVersion: schema);
        if (!ValidLegacyGeneration(initial))
            return new(LegacyCleanupCode.LEGACY_SET_MISMATCH, initial.Count, SchemaVersion: schema);
        string hash = Fingerprint(initial);
        if (!apply) return new(LegacyCleanupCode.READY, initial.Count, hash,
            RemainingCount: initial.Count, SchemaVersion: schema);
        if (!string.Equals(hash, fingerprint, StringComparison.OrdinalIgnoreCase))
            return new(LegacyCleanupCode.PLAN_CHANGED, initial.Count, hash, SchemaVersion: schema);
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Store parent missing.");
        if (Directory.EnumerateFiles(directory, BackupPrefix + "*.backup").Any())
            return new(LegacyCleanupCode.BACKUP_PRESENT, initial.Count, hash, SchemaVersion: schema);

        string backup = Path.Combine(directory, BackupPrefix + Guid.NewGuid().ToString("N") + ".backup");
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backup, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString()))
        {
            destination.Open();
            db.BackupDatabase(destination);
        }
        bool committed = false;
        try
        {
            using (var tx = db.BeginTransaction())
            {
                // Re-read under the write transaction. A changing set is never
                // authorization to delete some other or newer operation.
                var current = CandidateRows(db, tx);
                if (!ValidLegacyGeneration(current) || Fingerprint(current) != hash)
                    return new(LegacyCleanupCode.PLAN_CHANGED, current.Count,
                        Fingerprint(current), SchemaVersion: schema);
                long totalBefore = Scalar(db, "SELECT count(*) FROM jobs", tx);
                foreach (Row row in current)
                {
                    using var delete = db.CreateCommand();
                    delete.Transaction = tx;
                    delete.CommandText = "DELETE FROM jobs WHERE job_id=$id AND op_key=$key AND state='NEEDS_RECONCILIATION'";
                    delete.Parameters.AddWithValue("$id", row.Id);
                    delete.Parameters.AddWithValue("$key", row.Key);
                    if (delete.ExecuteNonQuery() != 1)
                        throw new InvalidOperationException("Legacy row changed.");
                }
                if (Scalar(db, "SELECT count(*) FROM jobs", tx) != totalBefore - ExpectedCount ||
                    Scalar(db, "SELECT count(*) FROM jobs WHERE state='NEEDS_RECONCILIATION'", tx) != 0 ||
                    !DatabaseIntegrity(db, tx))
                    throw new InvalidOperationException("Legacy cleanup integrity check failed.");
                beforeCommit?.Invoke();
                tx.Commit();
                committed = true;
            }
            if (Scalar(db, "SELECT count(*) FROM jobs WHERE state='NEEDS_RECONCILIATION'") != 0 ||
                !DatabaseIntegrity(db, null))
                return new(LegacyCleanupCode.POSTCOMMIT_INCOMPLETE, ExpectedCount, hash,
                    ExpectedCount, SchemaVersion: schema);
            File.Delete(backup);
            return new(LegacyCleanupCode.CLEARED, ExpectedCount, hash,
                ExpectedCount, 0, schema);
        }
        finally
        {
            // Do not retain a second copy of the old generation when no
            // transaction committed. A postcommit failure must retain the
            // backup for a deliberate recovery, never silently retry delete.
            if (!committed && File.Exists(backup)) File.Delete(backup);
        }
    }

    private static bool ValidLegacyGeneration(IReadOnlyList<Row> rows) =>
        rows.Count == ExpectedCount && rows.All(row =>
            DateTimeOffset.TryParse(row.Created, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var created) &&
            created >= Earliest && created < Cutoff);

    private static List<Row> CandidateRows(SqliteConnection db, SqliteTransaction? transaction)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT job_id,op_key,created_utc FROM jobs " +
            "WHERE state='NEEDS_RECONCILIATION' ORDER BY job_id COLLATE BINARY";
        using var reader = cmd.ExecuteReader();
        var rows = new List<Row>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    private static string Fingerprint(IEnumerable<Row> rows) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
            rows.Select(row => row.Id + "|" + row.Key + "|" + row.Created)))))
            .ToLowerInvariant();

    private static long Scalar(SqliteConnection db, string sql, SqliteTransaction? transaction = null)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private static bool DatabaseIntegrity(SqliteConnection db, SqliteTransaction? transaction)
    {
        using var foreign = db.CreateCommand();
        foreign.Transaction = transaction;
        foreign.CommandText = "PRAGMA foreign_key_check";
        using (var rows = foreign.ExecuteReader())
            if (rows.Read()) return false;
        using var integrity = db.CreateCommand();
        integrity.Transaction = transaction;
        integrity.CommandText = "PRAGMA integrity_check";
        return string.Equals(integrity.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal);
    }
}
