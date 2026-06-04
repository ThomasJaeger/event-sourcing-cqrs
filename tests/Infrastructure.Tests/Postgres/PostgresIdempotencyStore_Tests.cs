using EventSourcingCqrs.Domain.Abstractions;
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
}
