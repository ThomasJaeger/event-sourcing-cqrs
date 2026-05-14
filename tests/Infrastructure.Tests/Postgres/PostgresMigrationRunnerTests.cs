using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

public class PostgresMigrationRunnerTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresMigrationRunnerTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task First_run_applies_all_migrations_in_order_to_empty_database()
    {
        var connStr = await _fixture.CreateDatabaseAsync();
        var log = new List<string>();
        var runner = new MigrationRunner();

        await runner.RunPendingAsync(
            new MigrationRunnerOptions { ConnectionString = connStr, Log = log.Add },
            CancellationToken.None);

        (await TableExistsAsync(connStr, "event_store.events")).Should().BeTrue();
        (await TableExistsAsync(connStr, "event_store.outbox")).Should().BeTrue();
        (await TableExistsAsync(connStr, "event_store.outbox_quarantine")).Should().BeTrue();
        (await TableExistsAsync(connStr, "event_store.schema_migrations")).Should().BeTrue();

        // Constraints carry the PRIMARY KEY and UNIQUE semantics. Asserting
        // against pg_constraint (not pg_indexes) catches a regression that
        // replaces CONSTRAINT pk_events PRIMARY KEY (...) with a bare
        // CREATE INDEX of the same name; the latter would still satisfy a
        // name-only pg_indexes check while losing the PK semantics.
        var constraints = await ReadEventStoreConstraintNamesAsync(connStr);
        constraints.Should().BeEquivalentTo(new[]
        {
            "pk_events",
            "uq_events_stream_version",
            "uq_events_event_id",
            "pk_outbox",
            "uq_outbox_event_id",
            "pk_outbox_quarantine",
            "pk_schema_migrations",
        });

        // Standalone (non-constraint) indexes.
        var indexes = await ReadEventStoreNonConstraintIndexNamesAsync(connStr);
        indexes.Should().BeEquivalentTo(new[]
        {
            "ix_events_correlation_id",
            "ix_outbox_pending",
            "ix_outbox_correlation",
        });

        // ix_outbox_pending's partial predicate is the point of the index.
        // A regression that drops WHERE sent_utc IS NULL but keeps the name
        // would pass the name-only check above; pg_indexes.indexdef catches
        // it.
        var pendingDef = await ReadIndexDefAsync(connStr, "ix_outbox_pending");
        pendingDef.Should().Contain("sent_utc IS NULL");

        var rows = await ReadSchemaMigrationsAsync(connStr);
        rows.Should().HaveCount(4);
        rows[0].Version.Should().Be(1);
        rows[0].Name.Should().Be("initial_event_store");
        rows[0].Checksum.Should().MatchRegex("^[0-9a-f]{64}$");
        rows[1].Version.Should().Be(2);
        rows[1].Name.Should().Be("add_outbox_global_position");
        rows[1].Checksum.Should().MatchRegex("^[0-9a-f]{64}$");
        rows[2].Version.Should().Be(3);
        rows[2].Name.Should().Be("initial_read_models");
        rows[2].Checksum.Should().MatchRegex("^[0-9a-f]{64}$");
        rows[3].Version.Should().Be(4);
        rows[3].Name.Should().Be("add_order_list_read_model");
        rows[3].Checksum.Should().MatchRegex("^[0-9a-f]{64}$");

        log.Should().Contain("Applying 0001 initial_event_store.");
        log.Should().Contain("Applying 0002 add_outbox_global_position.");
        log.Should().Contain("Applying 0003 initial_read_models.");
        log.Should().Contain("Applying 0004 add_order_list_read_model.");
        log.Should().Contain("Applied 4 migration(s).");
    }

    [Fact]
    public async Task Re_run_against_already_applied_database_is_a_no_op()
    {
        var connStr = await _fixture.CreateDatabaseAsync();
        var runner = new MigrationRunner();
        await runner.RunPendingAsync(
            new MigrationRunnerOptions { ConnectionString = connStr },
            CancellationToken.None);

        var log = new List<string>();

        await runner.RunPendingAsync(
            new MigrationRunnerOptions { ConnectionString = connStr, Log = log.Add },
            CancellationToken.None);

        (await ReadSchemaMigrationsAsync(connStr)).Should().HaveCount(4);
        log.Should().Contain("No pending migrations.");
    }

    [Fact]
    public async Task Concurrent_runners_apply_the_migration_exactly_once()
    {
        var connStr = await _fixture.CreateDatabaseAsync();

        // Hold the advisory lock from a third connection so both runners
        // block on lock acquisition from a known starting state. Without
        // this contention setup, the second runner can finish before the
        // first even reaches pg_advisory_lock, and the serialization
        // assertion below would still pass without ever exercising the
        // lock the test is meant to verify.
        await using var gate = new NpgsqlConnection(connStr);
        await gate.OpenAsync();
        await using (var lockCmd = gate.CreateCommand())
        {
            lockCmd.CommandText = "SELECT pg_advisory_lock(@key)";
            lockCmd.Parameters.AddWithValue("key", MigrationRunner.MigrationAdvisoryLockKey);
            await lockCmd.ExecuteNonQueryAsync();
        }

        var logA = new List<string>();
        var logB = new List<string>();
        var runner = new MigrationRunner();
        var taskA = Task.Run(() => runner.RunPendingAsync(
            new MigrationRunnerOptions { ConnectionString = connStr, Log = logA.Add },
            CancellationToken.None));
        var taskB = Task.Run(() => runner.RunPendingAsync(
            new MigrationRunnerOptions { ConnectionString = connStr, Log = logB.Add },
            CancellationToken.None));

        // Poll pg_locks until two sessions are confirmed blocked on the
        // advisory lock. Polling beats Task.Delay because it removes the
        // timing dependency: the test waits only as long as it has to.
        await WaitForBlockedAdvisoryLockWaitersAsync(
            connStr, expected: 2, timeout: TimeSpan.FromSeconds(5));
        (await TableExistsAsync(connStr, "event_store.schema_migrations")).Should().BeFalse();
        taskA.IsCompleted.Should().BeFalse();
        taskB.IsCompleted.Should().BeFalse();

        // Release the gate's lock. Both runners race from a known-blocked
        // state. One acquires, applies 0001, releases. The other acquires
        // next, sees 0001 applied in schema_migrations, exits cleanly.
        await using (var unlockCmd = gate.CreateCommand())
        {
            unlockCmd.CommandText = "SELECT pg_advisory_unlock(@key)";
            unlockCmd.Parameters.AddWithValue("key", MigrationRunner.MigrationAdvisoryLockKey);
            await unlockCmd.ExecuteNonQueryAsync();
        }
        await Task.WhenAll(taskA, taskB);

        (await ReadSchemaMigrationsAsync(connStr)).Should().HaveCount(4);

        // Across the two logs combined: exactly one "Applying 0001..." and
        // exactly one "No pending migrations." One runner applies the whole
        // pending batch (0001 through 0004); the other sees nothing pending.
        // That signature is what the advisory lock produces and nothing else does.
        var combined = logA.Concat(logB).ToList();
        combined.Count(m => m == "Applying 0001 initial_event_store.").Should().Be(1);
        combined.Count(m => m == "No pending migrations.").Should().Be(1);
    }

    [Fact]
    public async Task Checksum_mismatch_throws_with_migration_identity_in_message()
    {
        var connStr = await _fixture.CreateDatabaseAsync();
        var runner = new MigrationRunner();
        await runner.RunPendingAsync(
            new MigrationRunnerOptions { ConnectionString = connStr },
            CancellationToken.None);

        const string tamperedChecksum =
            "0000000000000000000000000000000000000000000000000000000000000000";
        await ExecuteAsync(
            connStr,
            "UPDATE event_store.schema_migrations SET checksum = @c WHERE version = 1",
            cmd => cmd.Parameters.AddWithValue("c", tamperedChecksum));

        var act = async () => await runner.RunPendingAsync(
            new MigrationRunnerOptions { ConnectionString = connStr },
            CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<MigrationChecksumMismatchException>()).Which;
        ex.Version.Should().Be(1);
        ex.Name.Should().Be("initial_event_store");
        ex.Stored.Should().Be(tamperedChecksum);
        ex.Computed.Should().MatchRegex("^[0-9a-f]{64}$");
        ex.Message.Should().Contain("0001_initial_event_store");
        ex.Message.Should().Contain(tamperedChecksum);
        ex.Message.Should().Contain(ex.Computed);
    }

    [Fact]
    public async Task Dry_run_reports_pending_without_touching_the_database()
    {
        var connStr = await _fixture.CreateDatabaseAsync();
        var log = new List<string>();
        var runner = new MigrationRunner();

        await runner.RunPendingAsync(
            new MigrationRunnerOptions { ConnectionString = connStr, DryRun = true, Log = log.Add },
            CancellationToken.None);

        log.Should().Contain("Dry run: 4 migration(s) pending.");
        log.Should().Contain(m => m.EndsWith("0001 initial_event_store"));
        log.Should().Contain(m => m.EndsWith("0002 add_outbox_global_position"));
        log.Should().Contain(m => m.EndsWith("0003 initial_read_models"));
        log.Should().Contain(m => m.EndsWith("0004 add_order_list_read_model"));

        (await TableExistsAsync(connStr, "event_store.events")).Should().BeFalse();
        (await TableExistsAsync(connStr, "event_store.schema_migrations")).Should().BeFalse();
    }

    // Helpers below. AddWithValue is fine here because every parameter is
    // a string or long and the inferred Npgsql types line up with their
    // PG columns. Future tests touching UUID, TIMESTAMPTZ, or JSONB through
    // this helper should prefer explicit NpgsqlParameter { NpgsqlDbType =
    // ... } construction so the inferred type does not surprise the server.

    private static async Task ExecuteAsync(
        string connStr, string sql, Action<NpgsqlCommand>? configure = null)
    {
        await using var connection = new NpgsqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        configure?.Invoke(cmd);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TableExistsAsync(string connStr, string qualifiedName)
    {
        await using var connection = new NpgsqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT to_regclass(@n)::text";
        cmd.Parameters.AddWithValue("n", qualifiedName);
        var result = await cmd.ExecuteScalarAsync();
        return result is not null and not DBNull;
    }

    private static async Task<HashSet<string>> ReadEventStoreConstraintNamesAsync(string connStr)
    {
        await using var connection = new NpgsqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT conname FROM pg_constraint " +
            "WHERE connamespace = 'event_store'::regnamespace";
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    private static async Task<HashSet<string>> ReadEventStoreNonConstraintIndexNamesAsync(string connStr)
    {
        await using var connection = new NpgsqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT indexname FROM pg_indexes " +
            "WHERE schemaname = 'event_store' " +
            "AND indexname NOT IN (" +
            "  SELECT conname FROM pg_constraint " +
            "  WHERE connamespace = 'event_store'::regnamespace)";
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    private static async Task<string> ReadIndexDefAsync(string connStr, string indexName)
    {
        await using var connection = new NpgsqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT indexdef FROM pg_indexes " +
            "WHERE schemaname = 'event_store' AND indexname = @n";
        cmd.Parameters.AddWithValue("n", indexName);
        var result = await cmd.ExecuteScalarAsync();
        return (string)result!;
    }

    private static async Task<IReadOnlyList<(int Version, string Name, string Checksum)>>
        ReadSchemaMigrationsAsync(string connStr)
    {
        await using var connection = new NpgsqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT version, name, checksum FROM event_store.schema_migrations " +
            "ORDER BY version";
        var rows = new List<(int, string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }
        return rows;
    }

    private static async Task WaitForBlockedAdvisoryLockWaitersAsync(
        string connStr, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var connection = new NpgsqlConnection(connStr);
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT count(*) FROM pg_locks " +
                "WHERE locktype = 'advisory' AND NOT granted";
            var actual = (long)(await cmd.ExecuteScalarAsync())!;
            if (actual >= expected) return;
            await Task.Delay(50);
        }
        throw new TimeoutException(
            $"Expected at least {expected} blocked advisory-lock waiters within {timeout}, " +
            "but the deadline expired. The runners likely failed before reaching pg_advisory_lock.");
    }
}
