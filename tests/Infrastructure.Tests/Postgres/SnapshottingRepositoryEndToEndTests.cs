using System.Text.Json;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Tests.TestKit;
using EventSourcingCqrs.Infrastructure.Versioning;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Xunit;
using static EventSourcingCqrs.Infrastructure.Tests.Postgres.PostgresEventStoreTestKit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

// The snapshot arc, end to end (Chapter 12), against a migrated Testcontainers PostgreSQL: the
// SnapshottingEventStoreRepository over the real PostgresEventStore and the real PostgresSnapshotStore,
// the shape the sibling PostgresSnapshotStoreTests and OrderDraftedChannelUpcastTests use. These pin
// S2's composition end to end; the load-then-replay and capture-on-interval mechanisms were RED-proven
// at the S2b unit level over the in-memory store, so each fact here is a green-on-write characterization
// that the same behavior holds through the real store. The interval is set small so the boundary math is
// exercised without materializing fifty events; production defaults it to 50.
public class SnapshottingRepositoryEndToEndTests : IClassFixture<PostgresFixture>
{
    private static readonly Money TenUsd = new(10m, Currency.USD);
    private static readonly Address Shipping = new("1 Main St", "Smalltown", "12345", "US");
    private static readonly DateTime At = new(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

    private const int Interval = 3;

    private readonly PostgresFixture _fixture;

    public SnapshottingRepositoryEndToEndTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    // (a) Equivalence (green-on-write characterization; the mechanism was RED-proven at S2b unit level):
    // an order driven past the interval through the snapshotting repository against the real store, loaded
    // back through the snapshotting repository, equals the same stream full-replayed through a plain
    // repository over the same store. Snapshot-plus-tail equals full replay (ADR 0051's equivalence requirement).
    [Fact]
    public async Task Loading_through_the_snapshotting_repository_equals_a_full_replay()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlConnectionFactory(dataSource);
        var jsonOptions = CreateJsonOptions();
        var store = CreateStore(factory, jsonOptions);
        var snapshotStore = new PostgresSnapshotStore(factory, jsonOptions);
        var repository = SnapshottingRepo(store, snapshotStore);
        var orderId = Guid.NewGuid();

        // Draft, two lines, one more line, a shipping address: five events across the interval, so a
        // snapshot is captured at version 3 and the tail after it changes the aggregate's state.
        var order = Order.Draft(orderId, Guid.NewGuid(), At, "web");
        await repository.SaveAsync(order, CancellationToken.None);                    // version 1
        order.AddLine(Guid.NewGuid(), "SKU-1", 2, TenUsd, At);
        await repository.SaveAsync(order, CancellationToken.None);                    // version 2
        order.AddLine(Guid.NewGuid(), "SKU-2", 1, TenUsd, At);
        await repository.SaveAsync(order, CancellationToken.None);                    // version 3, captures
        order.AddLine(Guid.NewGuid(), "SKU-3", 4, TenUsd, At);
        await repository.SaveAsync(order, CancellationToken.None);                    // version 4
        order.SetShippingAddress(Shipping, At);
        await repository.SaveAsync(order, CancellationToken.None);                    // version 5

        var loaded = await repository.LoadAsync(orderId, CancellationToken.None);
        var fullReplay = await PlainRepo(store).LoadAsync(orderId, CancellationToken.None);

        loaded.Should().NotBeNull();
        fullReplay.Should().NotBeNull();
        loaded!.Version.Should().Be(fullReplay!.Version);
        loaded.Should().BeEquivalentTo(fullReplay);
    }

    // (b) Replay count, the speedup pin (green-on-write): after a snapshot exists past two boundaries, a
    // load reads only the tail from the snapshot's version, strictly fewer events than the full stream.
    // The speedup is pinned as a replay count, not a wall clock (ADR 0051's equivalence requirement).
    [Fact]
    public async Task Loading_through_the_snapshotting_repository_reads_only_the_tail_after_the_snapshot()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlConnectionFactory(dataSource);
        var jsonOptions = CreateJsonOptions();
        var recording = new RecordingEventStore(CreateStore(factory, jsonOptions));
        var snapshotStore = new PostgresSnapshotStore(factory, jsonOptions);
        var repository = SnapshottingRepo(recording, snapshotStore);
        var orderId = Guid.NewGuid();

        // Seven events across two boundaries (3 and 6). The snapshot store upserts one row, so the
        // surviving snapshot is at the last boundary, version 6; version 7 is the short tail after it.
        var order = Order.Draft(orderId, Guid.NewGuid(), At, "web");
        await repository.SaveAsync(order, CancellationToken.None);                    // version 1
        for (var i = 0; i < 6; i++)
        {
            order.AddLine(Guid.NewGuid(), $"SKU-{i}", 1, TenUsd, At);
            await repository.SaveAsync(order, CancellationToken.None);                // versions 2 through 7
        }

        var streamId = StreamFor(orderId);
        var streamLength = await StreamLengthAsync(factory, streamId);
        var snapshotRow = await ReadSnapshotRowAsync(factory, streamId);

        recording.ReadFromVersions.Clear();
        var loaded = await repository.LoadAsync(orderId, CancellationToken.None);

        snapshotRow.Should().NotBeNull();
        snapshotRow!.Value.StreamVersion.Should().Be(6);
        recording.LastReadFromVersion.Should().Be(6);
        recording.LastReadCount.Should().Be(streamLength - 6);
        recording.LastReadCount.Should().BeLessThan(streamLength);
        loaded!.Version.Should().Be(streamLength);
    }

    // (c) Discard and rebuild (green-on-write): a snapshot captured at schema version 1 loads as null
    // for a repository configured at schema version 2 (full replay from version 0, state correct), and
    // the next boundary crossing captures a fresh snapshot stored at schema version 2. A snapshot shape
    // change is a discard-and-rebuild, never an upcast (ADR 0051's snapshot-versioning posture).
    [Fact]
    public async Task A_schema_mismatched_snapshot_is_discarded_and_the_next_boundary_rebuilds_it()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlConnectionFactory(dataSource);
        var jsonOptions = CreateJsonOptions();
        var recording = new RecordingEventStore(CreateStore(factory, jsonOptions));
        var snapshotStore = new PostgresSnapshotStore(factory, jsonOptions);
        var orderId = Guid.NewGuid();

        // A snapshot captured at schema version 1: drive to the first boundary through a repository
        // configured at schema version 1.
        var repositoryV1 = SnapshottingRepo(recording, snapshotStore, schemaVersion: 1);
        var order = Order.Draft(orderId, Guid.NewGuid(), At, "web");
        await repositoryV1.SaveAsync(order, CancellationToken.None);                  // version 1
        order.AddLine(Guid.NewGuid(), "SKU-1", 2, TenUsd, At);
        await repositoryV1.SaveAsync(order, CancellationToken.None);                  // version 2
        order.AddLine(Guid.NewGuid(), "SKU-2", 1, TenUsd, At);
        await repositoryV1.SaveAsync(order, CancellationToken.None);                  // version 3, captures at schema 1

        // A repository configured at schema version 2 cannot consume the schema-1 snapshot: the store's
        // WHERE clause filters it out, so the load is a full replay from version 0.
        var repositoryV2 = SnapshottingRepo(recording, snapshotStore, schemaVersion: 2);
        recording.ReadFromVersions.Clear();
        var loaded = await repositoryV2.LoadAsync(orderId, CancellationToken.None);
        var fullReplay = await PlainRepo(CreateStore(factory, jsonOptions)).LoadAsync(orderId, CancellationToken.None);

        recording.LastReadFromVersion.Should().Be(0);
        loaded.Should().NotBeNull();
        loaded!.Version.Should().Be(3);
        loaded.Should().BeEquivalentTo(fullReplay);

        // The next boundary crossing rebuilds the snapshot at schema version 2.
        loaded.AddLine(Guid.NewGuid(), "SKU-3", 1, TenUsd, At);
        loaded.AddLine(Guid.NewGuid(), "SKU-4", 1, TenUsd, At);
        loaded.AddLine(Guid.NewGuid(), "SKU-5", 1, TenUsd, At);
        await repositoryV2.SaveAsync(loaded, CancellationToken.None);                 // version 6, recaptures at schema 2

        var rebuilt = await ReadSnapshotRowAsync(factory, StreamFor(orderId));
        rebuilt.Should().NotBeNull();
        rebuilt!.Value.SchemaVersion.Should().Be(2);
        rebuilt.Value.StreamVersion.Should().Be(6);
    }

    private static PostgresEventStore CreateStore(NpgsqlConnectionFactory factory, JsonSerializerOptions jsonOptions)
    {
        var registry = OrderRegistry();
        return new PostgresEventStore(
            factory, registry, CreatePmRegistry(), jsonOptions, new EventUpcasterPipeline(registry, []));
    }

    // Every Order event type, so the store serializes and resolves the whole lifecycle. The pipeline is
    // empty, so each event is at version 1 and rounds-trips without upcasting; the Order aggregate does
    // not apply the OrderDrafted channel to its state, so no lineage is needed to compare state.
    private static EventTypeRegistry OrderRegistry()
        => new EventTypeRegistry()
            .Register<OrderDrafted>()
            .Register<OrderLineAdded>()
            .Register<OrderLineRemoved>()
            .Register<ShippingAddressSet>()
            .Register<OrderPlaced>()
            .Register<OrderCancelled>()
            .Register<OrderShipped>()
            .Register<OrderCompleted>();

    private static SnapshottingEventStoreRepository<Order, OrderSnapshot> SnapshottingRepo(
        IEventStore store, ISnapshotStore snapshotStore, int schemaVersion = OrderSnapshot.SnapshotSchemaVersion)
        => new(
            store,
            new StubAccessor(),
            new StubTenantAccessor(),
            new StubCurrentVersions(),
            snapshotStore,
            schemaVersion,
            NullLogger<SnapshottingEventStoreRepository<Order, OrderSnapshot>>.Instance,
            snapshotInterval: Interval);

    private static EventStoreRepository<Order> PlainRepo(IEventStore store)
        => new(store, new StubAccessor(), new StubTenantAccessor(), new StubCurrentVersions());

    private static StreamId StreamFor(Guid orderId)
        => StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderId);

    private static async Task<int> StreamLengthAsync(NpgsqlConnectionFactory factory, StreamId streamId)
    {
        await using var conn = await factory.OpenConnectionAsync(CancellationToken.None);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM event_store.events WHERE stream_id = @sid";
        cmd.Parameters.AddWithValue("sid", NpgsqlDbType.Text, streamId.Value);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<(int SchemaVersion, int StreamVersion)?> ReadSnapshotRowAsync(
        NpgsqlConnectionFactory factory, StreamId streamId)
    {
        await using var conn = await factory.OpenConnectionAsync(CancellationToken.None);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT snapshot_schema_version, stream_version FROM event_store.snapshots WHERE stream_id = @sid";
        cmd.Parameters.AddWithValue("sid", NpgsqlDbType.Text, streamId.Value);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }
        return (reader.GetInt16(0), reader.GetInt32(1));
    }
}
