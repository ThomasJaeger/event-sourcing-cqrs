using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

// The SQL Server migration runner. Structurally the twin of the PostgreSQL runner, engine
// particulars and all, and self-contained inside this adapter per ADR 0004. It shares no code
// with it: the PostgreSQL runner is bound to Npgsql, pg_advisory_lock, and to_regclass, none of
// which exist here.
//
// Two things differ from PostgreSQL and both come from the engine, not from taste:
//
// 1. Batches. GO is a client-side separator that sqlcmd and SSMS understand and SqlCommand does
//    not. CREATE SCHEMA has to be the only statement in its batch, so a migration that creates a
//    schema and then a table cannot be sent as one command. The runner splits each file on GO.
//
// 2. The lock. sp_getapplock with @LockOwner='Session' is the counterpart of pg_advisory_lock:
//    session-scoped, held across the whole batch, released explicitly or when the connection
//    closes. Not 'Transaction' scope, which would release at each migration's commit and leave
//    the gap between migrations unguarded.
public sealed class SqlServerMigrationRunner
{
    // sp_getapplock resource name. A second runner against the same SQL Server instance picks a
    // different one. The PostgreSQL runner's key is a bigint; SQL Server names its lock
    // resources with a string, so this is the same idea in the engine's own vocabulary.
    public const string MigrationLockResource = "esrcq_migration_runner";

    private readonly Assembly _assembly;
    private readonly string _resourcePrefix;

    public SqlServerMigrationRunner(Assembly assembly, string resourcePrefix)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrEmpty(resourcePrefix);
        _assembly = assembly;
        _resourcePrefix = resourcePrefix;
    }

    public async Task RunPendingAsync(SqlServerMigrationRunnerOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var log = options.Log ?? (static _ => { });
        var migrations = LoadEmbeddedMigrations();

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(ct);

        if (options.DryRun)
        {
            var applied = await ReadAppliedAsync(connection, ct);
            VerifyAgainstApplied(migrations, applied);
            var pending = migrations.Where(m => !applied.ContainsKey(m.Version)).ToList();
            log($"Dry run: {pending.Count} migration(s) pending.");
            foreach (var migration in pending)
            {
                log($"  {migration.Version:0000} {migration.Name}");
            }
            return;
        }

        // The lock is held across the whole batch, not per migration. If one migration in the
        // middle of a batch fails, the lock releases on connection close, and on the operator's
        // re-run it blocks any concurrent runner from racing into a partially migrated database.
        await AcquireLockAsync(connection, ct);
        try
        {
            var applied = await ReadAppliedAsync(connection, ct);
            VerifyAgainstApplied(migrations, applied);
            var pending = migrations.Where(m => !applied.ContainsKey(m.Version)).ToList();
            if (pending.Count == 0)
            {
                log("No pending migrations.");
                return;
            }
            foreach (var migration in pending)
            {
                log($"Applying {migration.Version:0000} {migration.Name}.");
                await ApplyAsync(connection, migration, ct);
            }
            log($"Applied {pending.Count} migration(s).");
        }
        finally
        {
            await ReleaseLockAsync(connection);
        }
    }

    private static async Task AcquireLockAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("sp_getapplock", connection)
        { CommandType = System.Data.CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("@Resource", MigrationLockResource);
        cmd.Parameters.AddWithValue("@LockMode", "Exclusive");
        cmd.Parameters.AddWithValue("@LockOwner", "Session");
        cmd.Parameters.AddWithValue("@LockTimeout", -1);
        var returnValue = cmd.Parameters.Add("@ReturnValue", System.Data.SqlDbType.Int);
        returnValue.Direction = System.Data.ParameterDirection.ReturnValue;
        await cmd.ExecuteNonQueryAsync(ct);

        // 0 granted immediately, 1 granted after waiting. Anything else is a failure to
        // serialize, and running migrations unserialized is worse than not running them.
        var result = (int)returnValue.Value;
        if (result is not (0 or 1))
        {
            throw new InvalidOperationException(
                $"Could not acquire the migration lock '{MigrationLockResource}'. " +
                $"sp_getapplock returned {result}.");
        }
    }

    private static async Task ReleaseLockAsync(SqlConnection connection)
    {
        // Always release with CancellationToken.None: if the caller's token is cancelled
        // mid-run, the lock still has to come back.
        await using var cmd = new SqlCommand("sp_releaseapplock", connection)
        { CommandType = System.Data.CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("@Resource", MigrationLockResource);
        cmd.Parameters.AddWithValue("@LockOwner", "Session");
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<Dictionary<int, string>> ReadAppliedAsync(
        SqlConnection connection, CancellationToken ct)
    {
        // OBJECT_ID returns NULL when the schema or the table is absent, without raising. That
        // is the bootstrap-friendly behavior the runner needs on a fresh database, before 0001
        // has created event_store.schema_migrations. It is the counterpart of PostgreSQL's
        // to_regclass probe.
        await using (var probe = new SqlCommand(
            "SELECT OBJECT_ID('event_store.schema_migrations', 'U')", connection))
        {
            var result = await probe.ExecuteScalarAsync(ct);
            if (result is null or DBNull)
            {
                return [];
            }
        }

        var applied = new Dictionary<int, string>();
        await using var cmd = new SqlCommand(
            "SELECT version, checksum FROM event_store.schema_migrations", connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            applied[reader.GetInt32(0)] = reader.GetString(1);
        }
        return applied;
    }

    // Both checks measure the embedded set against what the tracking table reports, and both
    // branches of RunPendingAsync need both. One call site each keeps them that way: a branch
    // cannot pick up one check and miss the other.
    //
    // Checksums run first so that every state which raises a checksum mismatch today still
    // raises one. A set that is both tampered and back-filled reports the tampering, which is
    // the narrower fault and the one already pinned.
    private static void VerifyAgainstApplied(
        IReadOnlyList<MigrationFile> migrations, IReadOnlyDictionary<int, string> applied)
    {
        VerifyChecksums(migrations, applied);
        VerifyPendingFollowsApplied(migrations, applied);
    }

    private static void VerifyChecksums(
        IReadOnlyList<MigrationFile> migrations, IReadOnlyDictionary<int, string> applied)
    {
        foreach (var migration in migrations)
        {
            if (applied.TryGetValue(migration.Version, out var stored)
                && !string.Equals(stored, migration.Checksum, StringComparison.Ordinal))
            {
                throw new SqlServerMigrationChecksumMismatchException(
                    migration.Version, migration.Name, stored, migration.Checksum);
            }
        }
    }

    // A migration back-filled into a gap is absent from the tracking table, so the pending
    // selection picks it up like any other and the ascending sort then runs it first, against
    // a schema every higher-numbered migration has already shaped. That is a silent
    // data-corruption path, and refusing the run is the only safe answer: the operator
    // renumbers the file above the highest applied version and runs again.
    //
    // An empty applied set has no highest version, so the check does not fire and a first run
    // against a fresh database proceeds.
    private static void VerifyPendingFollowsApplied(
        IReadOnlyList<MigrationFile> migrations, IReadOnlyDictionary<int, string> applied)
    {
        if (applied.Count == 0)
        {
            return;
        }

        var highestApplied = applied.Keys.Max();

        // migrations is sorted ascending, so the first violator is the lowest-numbered one,
        // which is also the one that would have run first.
        foreach (var migration in migrations)
        {
            if (!applied.ContainsKey(migration.Version) && migration.Version < highestApplied)
            {
                throw new SqlServerMigrationOutOfOrderException(
                    migration.Version, migration.Name, highestApplied);
            }
        }
    }

    private static async Task ApplyAsync(
        SqlConnection connection, MigrationFile migration, CancellationToken ct)
    {
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            // Every batch of the file, and the tracking-table insert, in one transaction. SQL
            // Server rolls DDL back, so a migration is all-or-nothing exactly as on PostgreSQL.
            foreach (var batch in SplitOnGo(migration.Sql))
            {
                await using var ddl = new SqlCommand(batch, connection, tx);
                await ddl.ExecuteNonQueryAsync(ct);
            }

            await using (var insert = new SqlCommand(
                "INSERT INTO event_store.schema_migrations (version, name, checksum) " +
                "VALUES (@version, @name, @checksum)", connection, tx))
            {
                insert.Parameters.Add("@version", System.Data.SqlDbType.Int).Value = migration.Version;
                insert.Parameters.Add("@name", System.Data.SqlDbType.VarChar, 200).Value = migration.Name;
                insert.Parameters.Add("@checksum", System.Data.SqlDbType.VarChar, 64).Value = migration.Checksum;
                await insert.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    // GO alone on a line, any case, any surrounding whitespace. It is a batch separator and
    // never part of a statement, so a line that merely contains "go" is left alone.
    private static IReadOnlyList<string> SplitOnGo(string sql)
    {
        var batches = new List<string>();
        var current = new StringBuilder();

        foreach (var line in sql.Split('\n'))
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                AddIfNotBlank(batches, current);
                current.Clear();
                continue;
            }
            current.AppendLine(line.TrimEnd('\r'));
        }
        AddIfNotBlank(batches, current);

        if (batches.Count == 0)
        {
            throw new InvalidOperationException("Migration contains no executable statements.");
        }
        return batches;
    }

    private static void AddIfNotBlank(List<string> batches, StringBuilder current)
    {
        var batch = current.ToString();
        if (!string.IsNullOrWhiteSpace(batch))
        {
            batches.Add(batch);
        }
    }

    private IReadOnlyList<MigrationFile> LoadEmbeddedMigrations()
    {
        var migrations = new List<MigrationFile>();
        foreach (var resourceName in _assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(_resourcePrefix, StringComparison.Ordinal)
                || !resourceName.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }
            var fileName = resourceName[_resourcePrefix.Length..];
            if (fileName.Length < 6 || fileName[4] != '_')
            {
                throw new InvalidOperationException(
                    $"Embedded migration '{fileName}' does not match the NNNN_description.sql pattern.");
            }
            var version = int.Parse(
                fileName.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture);
            var name = Path.GetFileNameWithoutExtension(fileName)[5..];

            using var stream = _assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded migration '{resourceName}' could not be opened.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var sql = Encoding.UTF8.GetString(bytes);

            migrations.Add(new MigrationFile(version, name, sql, checksum));
        }
        migrations.Sort((a, b) => a.Version.CompareTo(b.Version));

        // Reject duplicate versions at load time so the failure is a clear runner error rather
        // than a primary-key violation on the second insert into schema_migrations.
        for (var i = 1; i < migrations.Count; i++)
        {
            if (migrations[i].Version == migrations[i - 1].Version)
            {
                throw new InvalidOperationException(
                    $"Duplicate migration version {migrations[i].Version:0000} across files " +
                    $"'{migrations[i - 1].Name}' and '{migrations[i].Name}'.");
            }
        }
        return migrations;
    }

    private sealed record MigrationFile(int Version, string Name, string Sql, string Checksum);
}
