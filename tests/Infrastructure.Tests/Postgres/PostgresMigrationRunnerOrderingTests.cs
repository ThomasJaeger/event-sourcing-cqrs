using EventSourcingCqrs.Infrastructure.Migrations.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

// The runner's version-ordering behavior against a real PostgreSQL container.
//
// A back-filled migration is absent from the tracking table, so the per-version set
// difference selects it and the ascending sort then runs it first, against a schema every
// higher-numbered migration has already shaped. Nothing in the runner observes that today.
//
// These facts drive the runner from a test-only migration set rather than from the shipped
// migrations/ directory, through the (assembly, resourcePrefix) seam the constructor already
// exposes and PostgresFixture already uses. The shipped set is contiguous and applies
// all-or-nothing, so neither state these facts need is reachable from it: removing a tracking
// row makes the runner re-run that migration's DDL against objects it already created, and the
// engine then decides the fact before the runner gets a say.
//
// Three logical sets, no file shared between them. The runner reads what is applied from the
// tracking table, so a follow-on set carries only the version under test: an already-applied
// version in it would be filtered out as not pending and would add nothing but a checksum
// comparison these facts do not exercise. It also keeps the highest applied version a fact
// about the database rather than about the resource set, so an implementation that derived it
// from the embedded versions instead would fail these facts rather than pass them.
//   Sparse      0001, 0003  the applied baseline, highest applied 3
//   BackFilled  0002        lands late, below the highest applied
//   Forward     0004        lands above the highest applied
public class PostgresMigrationRunnerOrderingTests : IClassFixture<PostgresFixture>
{
    private const string SparsePrefix = "Tests.OrderingMigrations.Postgres.Sparse.";
    private const string BackFilledPrefix = "Tests.OrderingMigrations.Postgres.BackFilled.";
    private const string ForwardPrefix = "Tests.OrderingMigrations.Postgres.Forward.";

    private readonly PostgresFixture _fixture;

    public PostgresMigrationRunnerOrderingTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task A_migration_numbered_below_the_highest_applied_version_is_refused_and_nothing_is_applied()
    {
        var connStr = await _fixture.CreateDatabaseAsync();
        await RunAsync(connStr, SparsePrefix);
        var before = await ReadSchemaMigrationsAsync(connStr);
        before.Select(r => r.Version).Should().Equal(1, 3);

        var act = async () => await RunAsync(connStr, BackFilledPrefix);

        await act.Should().ThrowAsync<MigrationOutOfOrderException>();
        (await ReadSchemaMigrationsAsync(connStr)).Should().BeEquivalentTo(before);
        (await TableExistsAsync(connStr, "event_store.ordering_beta")).Should().BeFalse();
    }

    [Fact]
    public async Task The_refusal_names_the_offending_version_and_the_highest_applied_version()
    {
        var connStr = await _fixture.CreateDatabaseAsync();
        await RunAsync(connStr, SparsePrefix);

        var act = async () => await RunAsync(connStr, BackFilledPrefix);

        var ex = (await act.Should().ThrowAsync<MigrationOutOfOrderException>()).Which;
        ex.Version.Should().Be(2);
        ex.Name.Should().Be("create_ordering_beta");
        ex.HighestApplied.Should().Be(3);
        ex.Message.Should().Contain("0002_create_ordering_beta");
        ex.Message.Should().Contain("0003");
    }

    [Fact]
    public async Task A_dry_run_surfaces_the_out_of_order_migration_rather_than_listing_it_as_pending()
    {
        var connStr = await _fixture.CreateDatabaseAsync();
        await RunAsync(connStr, SparsePrefix);
        var log = new List<string>();

        var act = async () => await RunAsync(connStr, BackFilledPrefix, dryRun: true, log: log.Add);

        await act.Should().ThrowAsync<MigrationOutOfOrderException>();
        log.Should().NotContain(m => m.Contains("create_ordering_beta", StringComparison.Ordinal));
    }

    [Fact]
    // Non-vacuity. Without this the three facts above pass against a runner that refuses
    // every pending migration once anything is applied.
    public async Task A_pending_migration_above_the_highest_applied_version_applies_normally()
    {
        var connStr = await _fixture.CreateDatabaseAsync();
        await RunAsync(connStr, SparsePrefix);

        var act = async () => await RunAsync(connStr, ForwardPrefix);

        await act.Should().NotThrowAsync();
        (await ReadSchemaMigrationsAsync(connStr)).Select(r => r.Version).Should().Equal(1, 3, 4);
        (await TableExistsAsync(connStr, "event_store.ordering_delta")).Should().BeTrue();
    }

    private static Task RunAsync(
        string connStr, string resourcePrefix, bool dryRun = false, Action<string>? log = null)
        => new MigrationRunner(typeof(PostgresMigrationRunnerOrderingTests).Assembly, resourcePrefix)
            .RunPendingAsync(
                new MigrationRunnerOptions
                {
                    ConnectionString = connStr,
                    DryRun = dryRun,
                    Log = log,
                },
                CancellationToken.None);

    private static async Task<IReadOnlyList<AppliedMigration>> ReadSchemaMigrationsAsync(string connStr)
    {
        await using var connection = new NpgsqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT version, name, checksum FROM event_store.schema_migrations " +
            "ORDER BY version";
        var rows = new List<AppliedMigration>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new AppliedMigration(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }
        return rows;
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

    private sealed record AppliedMigration(int Version, string Name, string Checksum);
}
