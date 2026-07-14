using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

public class PostgresIdempotencyStore_Tests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresIdempotencyStore_Tests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static PostgresIdempotencyStore NewStore(NpgsqlDataSource dataSource)
        => new(new NpgsqlConnectionFactory(dataSource));

    [Fact]
    public async Task ExistsAsync_returns_false_for_an_unrecorded_key()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = NewStore(dataSource);

        (await store.ExistsAsync(
            WellKnownTenants.Default, "pm-order-fulfillment:abc:authorize-payment", CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task TryRecordAsync_returns_true_on_first_write()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = NewStore(dataSource);

        (await store.TryRecordAsync(WellKnownTenants.Default, "key-1", "AuthorizePayment", CancellationToken.None))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_returns_true_after_the_key_is_recorded()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = NewStore(dataSource);
        await store.TryRecordAsync(WellKnownTenants.Default, "key-1", "AuthorizePayment", CancellationToken.None);

        (await store.ExistsAsync(WellKnownTenants.Default, "key-1", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task TryRecordAsync_returns_false_when_the_key_is_already_recorded()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = NewStore(dataSource);
        await store.TryRecordAsync(WellKnownTenants.Default, "key-1", "AuthorizePayment", CancellationToken.None);

        // The lazy-fallback signal: a second write of the same key reports false
        // rather than raising a unique-violation (ADR 0016).
        (await store.TryRecordAsync(WellKnownTenants.Default, "key-1", "AuthorizePayment", CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task TryRecordAsync_dedupes_on_the_key_even_when_command_type_differs()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = NewStore(dataSource);
        await store.TryRecordAsync(WellKnownTenants.Default, "key-1", "AuthorizePayment", CancellationToken.None);

        // The key is the identity; command_type is recorded but not part of it.
        (await store.TryRecordAsync(WellKnownTenants.Default, "key-1", "ReserveInventory", CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task ExistsAsync_rejects_a_blank_key()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = NewStore(dataSource);

        var act = async () => await store.ExistsAsync(WellKnownTenants.Default, "  ", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Same_idempotency_key_under_two_tenants_does_not_collide()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = NewStore(dataSource);
        var tenantA = WellKnownTenants.Default;
        var tenantB = TenantId.From(Guid.Parse("55555555-5555-5555-5555-555555555555"));
        const string key = "shared-key";

        (await store.TryRecordAsync(tenantA, key, "DoThing", CancellationToken.None)).Should().BeTrue();
        (await store.TryRecordAsync(tenantB, key, "DoThing", CancellationToken.None)).Should().BeTrue();
        (await store.ExistsAsync(tenantB, key, CancellationToken.None)).Should().BeTrue();
        (await store.ExistsAsync(tenantA, key, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task A_racing_write_waits_on_the_uncommitted_row_and_then_loses_without_throwing()
    {
        // Characterization, green on write, and the PostgreSQL peer of the SQL Server store's
        // deterministic race guard. It cannot go red against the shipped store: ON CONFLICT DO
        // NOTHING settles the race inside the engine, and no shipped shape has ever existed here for
        // this fact to fail against. Its teeth were proven instead against a scratch check-then-act
        // store, a SELECT followed by a bare INSERT, which fails this fact with a duplicate-key
        // violation. That is the production race the shipped statement rules out, and the scratch
        // shape was discarded once it had made the point.
        //
        // A first writer holds an uncommitted insert of the key. The racer's insert waits on that
        // transaction rather than reading past it, and once the holder commits, the conflict clause
        // does nothing, the rows-affected count is zero, and TryRecordAsync reports false. The engine
        // decided the race and the loser learned it lost without an exception.
        //
        // Unlike the SQL Server twin, this needs no isolation-level setup. Unique-index enforcement
        // is not snapshot-based, so the racer waits under the fixture's default READ COMMITTED, where
        // SQL Server needed READ_COMMITTED_SNAPSHOT on before the same hazard was even reachable.
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = NewStore(dataSource);
        const string key = "raced-key";

        await using var holder = await dataSource.OpenConnectionAsync();
        await using var holdTx = await holder.BeginTransactionAsync();
        await InsertIfAbsentAsync(holder, holdTx, key);

        var racer = Task.Run(async () => await store.TryRecordAsync(
            WellKnownTenants.Default, key, "AuthorizePayment", CancellationToken.None));

        var finishedEarly = await Task.WhenAny(racer, Task.Delay(TimeSpan.FromSeconds(2))) == racer;
        finishedEarly.Should().BeFalse(
            "the racing write must wait on the uncommitted row rather than read past it");

        await holdTx.CommitAsync();

        (await racer).Should().BeFalse();
        (await store.ExistsAsync(WellKnownTenants.Default, key, CancellationToken.None)).Should().BeTrue();
        (await CountRowsAsync(dataSource, key)).Should().Be(1);
    }

    // The store's own insert, run by hand so a test can hold its row uncommitted.
    private static async Task InsertIfAbsentAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string key)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO event_store.command_idempotency (tenant_id, idempotency_key, command_type) " +
            "VALUES (@tenant, @key, @command_type) " +
            "ON CONFLICT (tenant_id, idempotency_key) DO NOTHING",
            connection,
            transaction);
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, WellKnownTenants.Default.Value);
        cmd.Parameters.AddWithValue("key", NpgsqlDbType.Text, key);
        cmd.Parameters.AddWithValue("command_type", NpgsqlDbType.Text, "AuthorizePayment");
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountRowsAsync(NpgsqlDataSource dataSource, string key)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM event_store.command_idempotency WHERE idempotency_key = @key";
        cmd.Parameters.AddWithValue("key", NpgsqlDbType.Text, key);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }
}
