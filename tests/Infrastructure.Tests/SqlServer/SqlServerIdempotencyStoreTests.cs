using System.Data;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.SqlServer;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.SqlServer;

// The SQL Server twins of every PostgresIdempotencyStore_Tests fact, plus two the PostgreSQL side
// does not have: a concurrent race on the same key, and a deterministic guard for it.
//
// Those are not ceremony. PostgreSQL gets its atomicity free from ON CONFLICT DO NOTHING, and its
// tests only ever exercise the sequential case. T-SQL has no ON CONFLICT, and the obvious
// translation of it is a check-then-act that passes every sequential test and loses data under
// load. The race facts are what tell the difference, and one of them was measured to be too weak
// on its own; see the comments on each.
public class SqlServerIdempotencyStoreTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;

    public SqlServerIdempotencyStoreTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ExistsAsync_returns_false_for_an_unrecorded_key()
    {
        var store = await NewStoreAsync();

        (await store.ExistsAsync(WellKnownTenants.Default, NewKey(), CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task TryRecordAsync_returns_true_on_first_write()
    {
        var store = await NewStoreAsync();

        (await store.TryRecordAsync(WellKnownTenants.Default, NewKey(), "PlaceOrder", CancellationToken.None))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_returns_true_after_the_key_is_recorded()
    {
        var store = await NewStoreAsync();
        var key = NewKey();

        await store.TryRecordAsync(WellKnownTenants.Default, key, "PlaceOrder", CancellationToken.None);

        (await store.ExistsAsync(WellKnownTenants.Default, key, CancellationToken.None))
            .Should().BeTrue();
    }

    [Fact]
    public async Task TryRecordAsync_returns_false_when_the_key_is_already_recorded()
    {
        var store = await NewStoreAsync();
        var key = NewKey();
        await store.TryRecordAsync(WellKnownTenants.Default, key, "PlaceOrder", CancellationToken.None);

        (await store.TryRecordAsync(WellKnownTenants.Default, key, "PlaceOrder", CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task TryRecordAsync_dedupes_on_the_key_even_when_the_command_type_differs()
    {
        var store = await NewStoreAsync();
        var key = NewKey();
        await store.TryRecordAsync(WellKnownTenants.Default, key, "PlaceOrder", CancellationToken.None);

        // The key is the dedup identity. A second command type under the same key is still a
        // duplicate, and the row keeps the type that won.
        (await store.TryRecordAsync(WellKnownTenants.Default, key, "CancelOrder", CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task ExistsAsync_rejects_a_blank_key()
    {
        var store = await NewStoreAsync();

        var act = async () =>
            await store.ExistsAsync(WellKnownTenants.Default, "  ", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task The_same_key_under_two_tenants_does_not_collide()
    {
        var store = await NewStoreAsync();
        var key = NewKey();
        var other = TenantId.From(Guid.NewGuid());

        (await store.TryRecordAsync(WellKnownTenants.Default, key, "PlaceOrder", CancellationToken.None))
            .Should().BeTrue();

        // The primary key is (tenant_id, idempotency_key), so the same key under a second tenant is
        // a different command and must record.
        (await store.TryRecordAsync(other, key, "PlaceOrder", CancellationToken.None))
            .Should().BeTrue();
        (await store.ExistsAsync(other, key, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_writes_of_the_same_key_produce_exactly_one_winner()
    {
        // The contract: eight dispatches of the same key race, and exactly one records.
        //
        // On its own this fact is NOT a sufficient guard, and that is measured rather than assumed:
        // a check-then-act implementation passes it, because eight tasks on a warm pool almost never
        // interleave inside a window that small. The fact below is the one with teeth. This one
        // states the contract; that one proves the engine is what enforces it.
        var store = await NewStoreAsync();
        var key = NewKey();

        var attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            await store.TryRecordAsync(
                WellKnownTenants.Default, key, "PlaceOrder", CancellationToken.None)));

        var results = await Task.WhenAll(attempts);

        results.Count(won => won).Should().Be(1);
        results.Count(won => !won).Should().Be(7);
        (await store.ExistsAsync(WellKnownTenants.Default, key, CancellationToken.None))
            .Should().BeTrue();
    }

    [Fact]
    public async Task A_racing_write_blocks_on_the_key_lock_and_then_loses_without_throwing()
    {
        // The deterministic guard, and the reason the store does not use IF NOT EXISTS.
        //
        // A first writer holds an uncommitted insert of the key, so it holds the key's range lock.
        // A second writer then calls TryRecordAsync for the same key. The correct implementation
        // BLOCKS on that lock, and once the first writer commits it re-evaluates, sees the row, and
        // returns false. The engine decided the race and the loser learned it lost from a
        // rows-affected count of zero.
        //
        // A check-then-act implementation fails here, and fails loudly. The fixture runs with
        // READ_COMMITTED_SNAPSHOT on, so its IF NOT EXISTS reads a snapshot in which the row does
        // not exist, sails past the check, and blocks on the INSERT instead. When the first writer
        // commits, that insert violates the primary key and raises 2627. This fact turns a
        // production race into a test failure.
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var store = new SqlServerIdempotencyStore(new SqlServerConnectionFactory(connStr));
        var key = NewKey();

        await using var holder = new SqlConnection(connStr);
        await holder.OpenAsync();
        await using var holdTx = (SqlTransaction)await holder.BeginTransactionAsync();
        await InsertIfAbsentAsync(holder, holdTx, key);

        var racer = Task.Run(async () => await store.TryRecordAsync(
            WellKnownTenants.Default, key, "PlaceOrder", CancellationToken.None));

        var finishedEarly = await Task.WhenAny(racer, Task.Delay(TimeSpan.FromSeconds(2))) == racer;
        finishedEarly.Should().BeFalse(
            "the racing write must block on the key lock rather than read past an uncommitted row");

        await holdTx.CommitAsync();

        (await racer).Should().BeFalse();
        (await store.ExistsAsync(WellKnownTenants.Default, key, CancellationToken.None))
            .Should().BeTrue();
        (await CountRowsAsync(connStr, key)).Should().Be(1);
    }

    // The store's own insert-if-absent statement, run by hand so a test can hold its locks.
    private static async Task InsertIfAbsentAsync(
        SqlConnection connection, SqlTransaction transaction, string key)
    {
        await using var cmd = new SqlCommand(
            "INSERT INTO event_store.command_idempotency (tenant_id, idempotency_key, command_type) " +
            "SELECT @tenant, @key, @command_type " +
            "WHERE NOT EXISTS ( " +
            "    SELECT 1 FROM event_store.command_idempotency WITH (UPDLOCK, HOLDLOCK) " +
            "    WHERE tenant_id = @tenant AND idempotency_key = @key)",
            connection,
            transaction);
        cmd.Parameters.Add("@tenant", SqlDbType.UniqueIdentifier).Value = WellKnownTenants.Default.Value;
        cmd.Parameters.Add("@key", SqlDbType.VarChar, 200).Value = key;
        cmd.Parameters.Add("@command_type", SqlDbType.VarChar, 200).Value = "PlaceOrder";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountRowsAsync(string connStr, string key)
    {
        await using var connection = new SqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM event_store.command_idempotency WHERE idempotency_key = @key",
            connection);
        cmd.Parameters.Add("@key", SqlDbType.VarChar, 200).Value = key;
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<SqlServerIdempotencyStore> NewStoreAsync()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        return new SqlServerIdempotencyStore(new SqlServerConnectionFactory(connStr));
    }

    private static string NewKey() => $"key-{Guid.NewGuid():N}";
}
