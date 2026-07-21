using System.Data;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.EventStore.SqlServer;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.SqlServer;

// S2c RED: the SQL Server snapshot store's facts, against a migrated SQL Server 2019 container,
// mirroring PostgresSnapshotStoreTests in SQL Server idioms. SqlServerSnapshotStore ships as throwing
// skeletons this turn, so each fact fails at its first Save or Load with a NotImplementedException; the
// SQL and migration 0006 land in GREEN. OrderSnapshot is the shared memento and ISnapshotStore the
// shared port, so no scratch types are needed. The fixture is local to the SqlServer suite (ADR 0004),
// and connections come straight from the string with no NpgsqlDataSource counterpart to hold.
public class SqlServerSnapshotStoreTests : IClassFixture<SqlServerFixture>
{
    private static readonly Money TenUsd = new(10m, Currency.USD);
    private static readonly Address Shipping = new("1 Main St", "Smalltown", "12345", "US");

    private readonly SqlServerFixture _fixture;

    public SqlServerSnapshotStoreTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Save_then_load_returns_the_snapshot_and_its_stream_version()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var store = CreateStore(connStr);
        var streamId = NewStreamId();
        var snapshot = SampleSnapshot(OrderStatus.Placed);

        await store.SaveAsync(
            streamId, snapshot, streamVersion: 7, OrderSnapshot.SnapshotSchemaVersion,
            CancellationToken.None);
        var loaded = await store.LoadAsync<OrderSnapshot>(
            streamId, OrderSnapshot.SnapshotSchemaVersion, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.StreamVersion.Should().Be(7);
        loaded.Snapshot.Should().BeEquivalentTo(snapshot);
    }

    [Fact]
    public async Task A_second_save_upserts_the_stream_to_one_row_with_the_newer_state()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var store = CreateStore(connStr);
        var streamId = NewStreamId();

        await store.SaveAsync(
            streamId, SampleSnapshot(OrderStatus.Draft), 3, OrderSnapshot.SnapshotSchemaVersion,
            CancellationToken.None);
        await store.SaveAsync(
            streamId, SampleSnapshot(OrderStatus.Placed), 9, OrderSnapshot.SnapshotSchemaVersion,
            CancellationToken.None);

        var loaded = await store.LoadAsync<OrderSnapshot>(
            streamId, OrderSnapshot.SnapshotSchemaVersion, CancellationToken.None);
        loaded!.StreamVersion.Should().Be(9);
        loaded.Snapshot.Status.Should().Be(OrderStatus.Placed);
        (await CountRowsAsync(connStr, streamId)).Should().Be(1);
    }

    [Fact]
    public async Task A_snapshot_at_an_older_schema_version_loads_null()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var store = CreateStore(connStr);
        var streamId = NewStreamId();

        await store.SaveAsync(
            streamId, SampleSnapshot(OrderStatus.Placed), 5, snapshotSchemaVersion: 1,
            CancellationToken.None);
        var loaded = await store.LoadAsync<OrderSnapshot>(
            streamId, expectedSchemaVersion: 2, CancellationToken.None);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task An_absent_stream_loads_null()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var store = CreateStore(connStr);

        var loaded = await store.LoadAsync<OrderSnapshot>(
            NewStreamId(), OrderSnapshot.SnapshotSchemaVersion, CancellationToken.None);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task Snapshots_for_two_streams_do_not_cross()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var store = CreateStore(connStr);
        var streamA = NewStreamId();
        var streamB = NewStreamId();
        var snapA = SampleSnapshot(OrderStatus.Draft);
        var snapB = SampleSnapshot(OrderStatus.Placed);

        await store.SaveAsync(
            streamA, snapA, 2, OrderSnapshot.SnapshotSchemaVersion, CancellationToken.None);
        await store.SaveAsync(
            streamB, snapB, 4, OrderSnapshot.SnapshotSchemaVersion, CancellationToken.None);

        var loadedA = await store.LoadAsync<OrderSnapshot>(
            streamA, OrderSnapshot.SnapshotSchemaVersion, CancellationToken.None);
        var loadedB = await store.LoadAsync<OrderSnapshot>(
            streamB, OrderSnapshot.SnapshotSchemaVersion, CancellationToken.None);

        loadedA!.StreamVersion.Should().Be(2);
        loadedA.Snapshot.Should().BeEquivalentTo(snapA);
        loadedB!.StreamVersion.Should().Be(4);
        loadedB.Snapshot.Should().BeEquivalentTo(snapB);
    }

    private static SqlServerSnapshotStore CreateStore(string connStr)
        => new(new SqlServerConnectionFactory(connStr), SqlServerContractBackend.CreateJsonOptions());

    private static OrderSnapshot SampleSnapshot(OrderStatus status)
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            status,
            [new OrderLine(Guid.NewGuid(), "SKU-1", 2, TenUsd)],
            Shipping);

    private static StreamId NewStreamId() => StreamId.Parse($"test:{Guid.NewGuid():N}");

    private static async Task<int> CountRowsAsync(string connStr, StreamId streamId)
    {
        await using var connection = new SqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM event_store.snapshots WHERE stream_id = @sid",
            connection);
        cmd.Parameters.Add("@sid", SqlDbType.VarChar, 200).Value = streamId.Value;
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}
