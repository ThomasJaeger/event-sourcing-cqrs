using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
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
            "pm-order-fulfillment:abc:authorize-payment", CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task TryRecordAsync_returns_true_on_first_write()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = NewStore(dataSource);

        (await store.TryRecordAsync("key-1", "AuthorizePayment", CancellationToken.None))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_returns_true_after_the_key_is_recorded()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = NewStore(dataSource);
        await store.TryRecordAsync("key-1", "AuthorizePayment", CancellationToken.None);

        (await store.ExistsAsync("key-1", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task TryRecordAsync_returns_false_when_the_key_is_already_recorded()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = NewStore(dataSource);
        await store.TryRecordAsync("key-1", "AuthorizePayment", CancellationToken.None);

        // The lazy-fallback signal: a second write of the same key reports false
        // rather than raising a unique-violation (ADR 0016).
        (await store.TryRecordAsync("key-1", "AuthorizePayment", CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task TryRecordAsync_dedupes_on_the_key_even_when_command_type_differs()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = NewStore(dataSource);
        await store.TryRecordAsync("key-1", "AuthorizePayment", CancellationToken.None);

        // The key is the identity; command_type is recorded but not part of it.
        (await store.TryRecordAsync("key-1", "ReserveInventory", CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task ExistsAsync_rejects_a_blank_key()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = NewStore(dataSource);

        var act = async () => await store.ExistsAsync("  ", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
