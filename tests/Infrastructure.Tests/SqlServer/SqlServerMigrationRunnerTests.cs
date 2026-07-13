using EventSourcingCqrs.Infrastructure.EventStore.SqlServer;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace EventSourcingCqrs.Infrastructure.Tests.SqlServer;

// The SQL Server migration runner against a real 2019 container. The runner is the first thing
// the adapter needs and the first thing that can be wrong: GO-splitting, the sp_getapplock
// serialization, the OBJECT_ID bootstrap probe, and the tracking table all have to work before
// any event can be appended.
//
// The constraint-name assertions are not ceremony. SQL Server raises 2627 for a unique
// CONSTRAINT and 2601 for a unique INDEX, and slice 2's concurrency translation filters on the
// name, so the schema committing to named constraints is load-bearing for a behavior that does
// not exist yet.
public class SqlServerMigrationRunnerTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SqlServerMigrationRunnerTests(SqlServerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    // Pins the migration set by version and name, the SQL Server twin of
    // PostgresMigrationRunnerTests. Adding a migration to migrations/sqlserver/ is meant to break
    // this fact: the set is a fact about the schema, not an implementation detail, and a new file
    // that lands without anyone updating this list is a file nobody reviewed.
    public async Task Runner_applies_every_migration_and_records_them_in_the_tracking_table()
    {
        var connStr = await _fixture.CreateDatabaseAsync();
        var log = new List<string>();

        await NewRunner().RunPendingAsync(
            new SqlServerMigrationRunnerOptions { ConnectionString = connStr, Log = log.Add },
            CancellationToken.None);

        _output.WriteLine($"Container start-to-ready: {_fixture.ContainerStartupTime.TotalSeconds:F1}s");

        log.Should().Contain("Applying 0001 initial_event_store.");
        log.Should().Contain("Applying 0002 add_outbox.");
        log.Should().Contain("Applied 2 migration(s).");

        var rows = await ReadTrackingTableAsync(connStr);
        rows.Should().HaveCount(2);
        rows[0].Version.Should().Be(1);
        rows[0].Name.Should().Be("initial_event_store");
        rows[0].Checksum.Should().MatchRegex("^[0-9a-f]{64}$");
        rows[1].Version.Should().Be(2);
        rows[1].Name.Should().Be("add_outbox");
        rows[1].Checksum.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public async Task Runner_is_idempotent_on_a_second_run()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var log = new List<string>();

        await NewRunner().RunPendingAsync(
            new SqlServerMigrationRunnerOptions { ConnectionString = connStr, Log = log.Add },
            CancellationToken.None);

        log.Should().Contain("No pending migrations.");
        (await ReadTrackingTableAsync(connStr)).Should().HaveCount(2);
    }

    [Fact]
    public async Task The_outbox_and_quarantine_tables_carry_their_named_constraints()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();

        var outboxConstraints = await QueryNamesAsync(connStr,
            "SELECT name FROM sys.key_constraints " +
            "WHERE parent_object_id = OBJECT_ID('event_store.outbox')");
        outboxConstraints.Should().BeEquivalentTo("pk_outbox", "uq_outbox_event_id");

        // The filtered index is the counterpart of PostgreSQL's partial index: the processor scans
        // pending rows in FIFO order without indexing a column that is always NULL in the filter.
        var filtered = await QueryNamesAsync(connStr,
            "SELECT name FROM sys.indexes " +
            "WHERE object_id = OBJECT_ID('event_store.outbox') AND has_filter = 1");
        filtered.Should().BeEquivalentTo("ix_outbox_pending");

        var quarantineConstraints = await QueryNamesAsync(connStr,
            "SELECT name FROM sys.key_constraints " +
            "WHERE parent_object_id = OBJECT_ID('event_store.outbox_quarantine')");
        quarantineConstraints.Should().BeEquivalentTo("pk_outbox_quarantine");
    }

    [Fact]
    public async Task The_events_table_carries_the_named_constraints_and_a_clustered_position_key()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();

        // Named unique CONSTRAINTs, not bare indexes, and no auto-generated names anywhere.
        var uniqueConstraints = await QueryNamesAsync(connStr,
            "SELECT name FROM sys.key_constraints " +
            "WHERE parent_object_id = OBJECT_ID('event_store.events') AND type = 'UQ'");
        uniqueConstraints.Should().BeEquivalentTo("uq_events_stream_version", "uq_events_event_id");

        var primaryKey = await QueryNamesAsync(connStr,
            "SELECT name FROM sys.key_constraints " +
            "WHERE parent_object_id = OBJECT_ID('event_store.events') AND type = 'PK'");
        primaryKey.Should().BeEquivalentTo("pk_events");

        // The primary key is clustered on global_position: the log is read in position order, so
        // physical order and read order are the same.
        var clustered = await QueryNamesAsync(connStr,
            "SELECT i.name FROM sys.indexes i " +
            "WHERE i.object_id = OBJECT_ID('event_store.events') AND i.type_desc = 'CLUSTERED'");
        clustered.Should().BeEquivalentTo("pk_events");

        var clusteredColumn = await QueryNamesAsync(connStr,
            "SELECT c.name FROM sys.index_columns ic " +
            "JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id " +
            "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
            "WHERE i.object_id = OBJECT_ID('event_store.events') AND i.type_desc = 'CLUSTERED'");
        clusteredColumn.Should().BeEquivalentTo("global_position");

        // Index parity with the PostgreSQL schema: correlation_id (0001) and tenant_id (0016)
        // are indexed there, causation_id is not, so causation_id is not indexed here either.
        var nonClustered = await QueryNamesAsync(connStr,
            "SELECT name FROM sys.indexes " +
            "WHERE object_id = OBJECT_ID('event_store.events') " +
            "AND type_desc = 'NONCLUSTERED' AND is_unique = 0");
        nonClustered.Should().BeEquivalentTo("ix_events_correlation_id", "ix_events_tenant_id");
    }

    [Fact]
    public async Task The_test_database_runs_with_read_committed_snapshot_on()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();

        // RCSI is the Azure SQL default and the posture in which the ADR 0044 skip hazard is
        // real. Under SQL Server's lock-based default a tailing reader blocks rather than
        // skipping, which would hide the hazard the commit-visibility fact exists to catch.
        await using var connection = new SqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()",
            connection);

        (await cmd.ExecuteScalarAsync()).Should().Be(true);
    }

    private static SqlServerMigrationRunner NewRunner()
        => new(EventStoreSqlServerMigrations.Assembly, EventStoreSqlServerMigrations.ResourcePrefix);

    private static async Task<List<AppliedMigration>> ReadTrackingTableAsync(string connStr)
    {
        await using var connection = new SqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT version, name, checksum FROM event_store.schema_migrations ORDER BY version",
            connection);

        var rows = new List<AppliedMigration>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new AppliedMigration(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }
        return rows;
    }

    private static async Task<List<string>> QueryNamesAsync(string connStr, string sql)
    {
        await using var connection = new SqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = new SqlCommand(sql, connection);

        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    private sealed record AppliedMigration(int Version, string Name, string Checksum);
}
