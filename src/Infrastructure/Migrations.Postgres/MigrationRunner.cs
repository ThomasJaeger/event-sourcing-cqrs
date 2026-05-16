using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace EventSourcingCqrs.Infrastructure.Migrations.Postgres;

public sealed class MigrationRunner
{
    // pg_advisory_lock(bigint) key. The eight bytes spell ASCII "ESRCQ_MR"
    // (event-sourcing reference implementation, migration runner). Recorded
    // in docs/sessions/0002-weeks3-4-postgres-adapter.md so a second runner
    // targeting the same PostgreSQL instance picks a different value.
    public const long MigrationAdvisoryLockKey = 0x4553_5243_515F_4D52L;

    private readonly Assembly _assembly;
    private readonly string _resourcePrefix;

    // Migrations live as embedded .sql resources in some engine's adapter
    // assembly. The runner is engine-agnostic per ADR 0004, so the assembly
    // handle and the LogicalName prefix that the csproj sets on those
    // resources come in through the constructor. The PostgreSQL adapter's
    // EventStorePostgresMigrations exposes both values in one place.
    public MigrationRunner(Assembly assembly, string resourcePrefix)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrEmpty(resourcePrefix);
        _assembly = assembly;
        _resourcePrefix = resourcePrefix;
    }

    public async Task RunPendingAsync(MigrationRunnerOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var log = options.Log ?? (static _ => { });
        var migrations = LoadEmbeddedMigrations();

        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(ct);

        if (options.DryRun)
        {
            var applied = await ReadAppliedAsync(connection, ct);
            VerifyChecksums(migrations, applied);
            var pending = migrations.Where(m => !applied.ContainsKey(m.Version)).ToList();
            log($"Dry run: {pending.Count} migration(s) pending.");
            foreach (var migration in pending)
            {
                log($"  {migration.Version:0000} {migration.Name}");
            }
            return;
        }

        // Hold the advisory lock across the whole batch, not inside a single
        // per-migration transaction. If a migration in the middle of a batch
        // fails, the lock releases on connection close; on the operator's
        // re-run the same lock blocks any concurrent runner from racing into
        // a partially migrated database. The gate keeps the system either
        // fully on a known-good migration set or fully recoverable.
        await AcquireLockAsync(connection, ct);
        try
        {
            var applied = await ReadAppliedAsync(connection, ct);
            VerifyChecksums(migrations, applied);
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

    private static async Task AcquireLockAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT pg_advisory_lock(@key)";
        cmd.Parameters.AddWithValue("key", MigrationAdvisoryLockKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ReleaseLockAsync(NpgsqlConnection connection)
    {
        // Always release with CancellationToken.None: if the caller's token
        // is cancelled mid-run, the lock still has to come back.
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT pg_advisory_unlock(@key)";
        cmd.Parameters.AddWithValue("key", MigrationAdvisoryLockKey);
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<Dictionary<int, string>> ReadAppliedAsync(
        NpgsqlConnection connection, CancellationToken ct)
    {
        // to_regclass returns NULL when either the schema or the table is
        // absent, without raising. That is exactly the bootstrap-friendly
        // behavior the runner needs on a fresh database, before migration
        // 0001 has created event_store.schema_migrations. The ::text cast
        // is there because Npgsql does not map the regclass result type
        // directly to a CLR value via ExecuteScalarAsync; the cast carries
        // the NULL through and gives back a plain text value when present.
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT to_regclass('event_store.schema_migrations')::text";
            var result = await probe.ExecuteScalarAsync(ct);
            if (result is null or DBNull)
            {
                return new Dictionary<int, string>();
            }
        }

        var applied = new Dictionary<int, string>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version, checksum FROM event_store.schema_migrations";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            applied[reader.GetInt32(0)] = reader.GetString(1);
        }
        return applied;
    }

    private static void VerifyChecksums(
        IReadOnlyList<MigrationFile> migrations, IReadOnlyDictionary<int, string> applied)
    {
        foreach (var migration in migrations)
        {
            if (applied.TryGetValue(migration.Version, out var stored)
                && !string.Equals(stored, migration.Checksum, StringComparison.Ordinal))
            {
                throw new MigrationChecksumMismatchException(
                    migration.Version, migration.Name, stored, migration.Checksum);
            }
        }
    }

    private static async Task ApplyAsync(
        NpgsqlConnection connection, MigrationFile migration, CancellationToken ct)
    {
        await using var tx = await connection.BeginTransactionAsync(ct);
        try
        {
            await using (var ddl = connection.CreateCommand())
            {
                ddl.Transaction = tx;
                ddl.CommandText = migration.Sql;
                await ddl.ExecuteNonQueryAsync(ct);
            }
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText =
                    "INSERT INTO event_store.schema_migrations (version, name, checksum) " +
                    "VALUES (@version, @name, @checksum)";
                insert.Parameters.AddWithValue("version", migration.Version);
                insert.Parameters.AddWithValue("name", migration.Name);
                insert.Parameters.AddWithValue("checksum", migration.Checksum);
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

        // Reject duplicate version numbers across embedded files at load time
        // so the failure surface is a clear runner error rather than a PG
        // primary-key violation on the second insert into schema_migrations.
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
