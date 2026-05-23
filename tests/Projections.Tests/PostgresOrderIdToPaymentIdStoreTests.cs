using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

public class PostgresOrderIdToPaymentIdStoreTests : IClassFixture<PostgresFixture>
{
    private const string ProjectionName = "order-id-to-payment-id";

    private readonly PostgresFixture _fixture;

    public PostgresOrderIdToPaymentIdStoreTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Record_and_commit_persist_the_mapping_and_advance_the_checkpoint()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderIdToPaymentIdStore(factory, new PostgresCheckpointStore(factory));
        var orderId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.RecordAsync(orderId, paymentId, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 5, CancellationToken.None);
        }

        (await store.GetPaymentIdAsync(orderId, CancellationToken.None)).Should().Be(paymentId);
        var checkpoint = await new PostgresCheckpointStore(factory)
            .GetPositionAsync(ProjectionName, CancellationToken.None);
        checkpoint.Should().Be(5);
    }

    [Fact]
    public async Task GetPaymentId_returns_null_for_an_unrecorded_order()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresOrderIdToPaymentIdStore(
            new NpgsqlReadModelConnectionFactory(dataSource),
            new PostgresCheckpointStore(new NpgsqlReadModelConnectionFactory(dataSource)));

        (await store.GetPaymentIdAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Record_is_idempotent_on_a_conflicting_order()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderIdToPaymentIdStore(factory, new PostgresCheckpointStore(factory));
        var orderId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await RecordAndCommitAsync(store, orderId, first, position: 1);
        await RecordAndCommitAsync(store, orderId, second, position: 2);

        // ON CONFLICT DO NOTHING: the first mapping stands, the second is discarded.
        (await store.GetPaymentIdAsync(orderId, CancellationToken.None)).Should().Be(first);
    }

    [Fact]
    public async Task Uncommitted_unit_of_work_rolls_back_the_mapping_and_the_checkpoint()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderIdToPaymentIdStore(factory, new PostgresCheckpointStore(factory));
        var orderId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.RecordAsync(orderId, Guid.NewGuid(), CancellationToken.None);
            // The block exits without CommitAsync: DisposeAsync rolls back.
        }

        (await store.GetPaymentIdAsync(orderId, CancellationToken.None)).Should().BeNull();
        var checkpoint = await new PostgresCheckpointStore(factory)
            .GetPositionAsync(ProjectionName, CancellationToken.None);
        checkpoint.Should().Be(0);
    }

    [Fact]
    public async Task Truncate_empties_the_table()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderIdToPaymentIdStore(factory, new PostgresCheckpointStore(factory));
        var orderA = Guid.NewGuid();
        var orderB = Guid.NewGuid();
        await RecordAndCommitAsync(store, orderA, Guid.NewGuid(), position: 1);
        await RecordAndCommitAsync(store, orderB, Guid.NewGuid(), position: 2);

        await store.TruncateAsync(CancellationToken.None);

        (await store.GetPaymentIdAsync(orderA, CancellationToken.None)).Should().BeNull();
        (await store.GetPaymentIdAsync(orderB, CancellationToken.None)).Should().BeNull();
    }

    private static async Task RecordAndCommitAsync(
        PostgresOrderIdToPaymentIdStore store, Guid orderId, Guid paymentId, long position)
    {
        await using var uow = await store.BeginAsync(CancellationToken.None);
        await uow.RecordAsync(orderId, paymentId, CancellationToken.None);
        await uow.CommitAsync(ProjectionName, position, CancellationToken.None);
    }
}
