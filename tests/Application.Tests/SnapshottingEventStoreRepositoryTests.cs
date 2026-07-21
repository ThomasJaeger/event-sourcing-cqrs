using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.EventStore.InMemory;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EventSourcingCqrs.Application.Tests;

// The snapshotting repository's behavior (Chapter 12). Two halves: LoadAsync restores from a memento
// and reads only the tail from its version, falling back to full replay when the memento is absent or
// its schema no longer matches; SaveAsync captures a fresh memento when the append crosses a snapshot
// interval, and a capture failure never fails the command.
//
// S2b RED: SnapshottingEventStoreRepository.LoadAsync and SaveAsync are throwing skeletons, so every
// fact fails at its SUT call with a NotImplementedException, before its assertions. The behavior these
// facts pin lands in S2b GREEN, which turns them green with no assertion edited.
//
// The facts drive the SUT over the real InMemoryEventStore (wrapped in RecordingEventStore only to
// observe the read position) and a RecordingSnapshotStore that honors the schema-version filter. A
// small snapshotInterval is injected through the constructor seam so the boundary math is testable
// without materializing fifty events; production defaults the interval to 50.
public sealed class SnapshottingEventStoreRepositoryTests
{
    private static readonly Guid OrderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CustomerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Line1 = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Line2 = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Line3 = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Money TenUsd = new(10m, Currency.USD);
    private static readonly Address Shipping = new("1 Main St", "Smalltown", "12345", "US");
    private static readonly DateTime At = new(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

    private const int OrderSchema = OrderSnapshot.SnapshotSchemaVersion;

    private static readonly StreamId Stream =
        StreamId.ForAggregate<Order>(WellKnownTenants.Default, OrderId);

    // (a) A snapshot at the current schema restores the aggregate and the repository reads only the
    // tail from the snapshot's version. The restored state and Version equal a full replay.
    [Fact]
    public async Task Load_with_a_snapshot_restores_from_the_memento_and_reads_only_the_tail()
    {
        var store = new RecordingEventStore(new InMemoryEventStore());
        var snapshots = new RecordingSnapshotStore();
        var expected = await SeedFivePlacedEventsAsync(store);
        var snapshotAtThree = await SnapshotAtAsync(store, atVersion: 3);
        snapshots.Seed(Stream, snapshotAtThree, streamVersion: 3, schemaVersion: OrderSchema);
        store.ReadFromVersions.Clear();
        var repo = SnapshottingRepo(store, snapshots);

        var loaded = await repo.LoadAsync(OrderId, CancellationToken.None);

        store.LastReadFromVersion.Should().Be(3);
        loaded.Should().NotBeNull();
        loaded!.Version.Should().Be(expected.Version);
        loaded.Should().BeEquivalentTo(expected);
    }

    // (b) No snapshot: the repository reads the whole stream from version 0 and full-replays.
    [Fact]
    public async Task Load_with_no_snapshot_replays_the_whole_stream_from_version_zero()
    {
        var store = new RecordingEventStore(new InMemoryEventStore());
        var snapshots = new RecordingSnapshotStore();
        var expected = await SeedFivePlacedEventsAsync(store);
        store.ReadFromVersions.Clear();
        var repo = SnapshottingRepo(store, snapshots);

        var loaded = await repo.LoadAsync(OrderId, CancellationToken.None);

        store.LastReadFromVersion.Should().Be(0);
        loaded.Should().NotBeNull();
        loaded!.Version.Should().Be(expected.Version);
        loaded.Should().BeEquivalentTo(expected);
    }

    // (c) A snapshot at an older schema: the repository asks the store at the current schema version,
    // the store returns null (the filter honors the mismatch), and a full replay proceeds.
    [Fact]
    public async Task Load_with_a_schema_mismatched_snapshot_asks_at_the_current_schema_then_full_replays()
    {
        const int currentSchema = OrderSchema + 1;
        var store = new RecordingEventStore(new InMemoryEventStore());
        var snapshots = new RecordingSnapshotStore();
        var expected = await SeedFivePlacedEventsAsync(store);
        var staleSnapshot = await SnapshotAtAsync(store, atVersion: 3);
        snapshots.Seed(Stream, staleSnapshot, streamVersion: 3, schemaVersion: OrderSchema);
        store.ReadFromVersions.Clear();
        var repo = SnapshottingRepo(store, snapshots, schemaVersion: currentSchema);

        var loaded = await repo.LoadAsync(OrderId, CancellationToken.None);

        snapshots.LastLoadExpectedSchemaVersion.Should().Be(currentSchema);
        store.LastReadFromVersion.Should().Be(0);
        loaded.Should().NotBeNull();
        loaded!.Version.Should().Be(expected.Version);
        loaded.Should().BeEquivalentTo(expected);
    }

    // (d) A save whose append crosses a multiple of the interval captures exactly one snapshot, at the
    // post-append version and the current schema version.
    [Fact]
    public async Task Save_crossing_a_boundary_captures_one_snapshot_at_the_post_append_version()
    {
        var store = new InMemoryEventStore();
        var snapshots = new RecordingSnapshotStore();
        var order = Order.Draft(OrderId, CustomerId, At, "web");
        order.AddLine(Line1, "SKU-1", 2, TenUsd, At);
        await PlainRepo(store).SaveAsync(order, CancellationToken.None); // seed the stream at version 2
        var repo = SnapshottingRepo(store, snapshots, interval: 3);

        order.AddLine(Line2, "SKU-2", 1, TenUsd, At); // append takes the stream from 2 across 3
        await repo.SaveAsync(order, CancellationToken.None);

        snapshots.Saves.Should().ContainSingle();
        snapshots.Saves[0].StreamVersion.Should().Be(3);
        snapshots.Saves[0].SchemaVersion.Should().Be(OrderSchema);
        ((OrderSnapshot)snapshots.Saves[0].Snapshot).Should().BeEquivalentTo(order.ToSnapshot());
    }

    // (e) A save that lands inside the same interval bucket captures nothing.
    [Fact]
    public async Task Save_inside_a_boundary_captures_nothing()
    {
        var store = new InMemoryEventStore();
        var snapshots = new RecordingSnapshotStore();
        var order = Order.Draft(OrderId, CustomerId, At, "web");
        await PlainRepo(store).SaveAsync(order, CancellationToken.None); // seed the stream at version 1
        var repo = SnapshottingRepo(store, snapshots, interval: 3);

        order.AddLine(Line1, "SKU-1", 2, TenUsd, At); // append takes the stream from 1 to 2, no crossing
        await repo.SaveAsync(order, CancellationToken.None);

        snapshots.Saves.Should().BeEmpty();
    }

    // (f) The boundary-math pin: a multi-event append that jumps past a multiple captures exactly once,
    // at the post-append version, not at the multiple it leapt over and not once per event.
    [Fact]
    public async Task Save_jumping_past_a_boundary_captures_once_at_the_post_append_version()
    {
        var store = new InMemoryEventStore();
        var snapshots = new RecordingSnapshotStore();
        var order = Order.Draft(OrderId, CustomerId, At, "web");
        order.AddLine(Line1, "SKU-1", 2, TenUsd, At);
        await PlainRepo(store).SaveAsync(order, CancellationToken.None); // seed the stream at version 2
        var repo = SnapshottingRepo(store, snapshots, interval: 3);

        order.AddLine(Line2, "SKU-2", 1, TenUsd, At);        // version 3
        order.AddLine(Line3, "SKU-3", 4, TenUsd, At);        // version 4
        order.SetShippingAddress(Shipping, At);              // version 5
        await repo.SaveAsync(order, CancellationToken.None); // append jumps from 2 to 5, past 3

        snapshots.Saves.Should().ContainSingle();
        snapshots.Saves[0].StreamVersion.Should().Be(5);
    }

    // (g) Best-effort capture: when the snapshot store throws, the append still lands, the command does
    // not fail, and the failure is logged at warning naming the stream.
    [Fact]
    public async Task Save_when_the_snapshot_store_throws_still_lands_the_append_and_logs_a_warning()
    {
        var store = new InMemoryEventStore();
        var snapshots = new RecordingSnapshotStore { ThrowOnSave = true };
        var logger = NewLogger();
        var order = Order.Draft(OrderId, CustomerId, At, "web");
        order.AddLine(Line1, "SKU-1", 2, TenUsd, At);
        await PlainRepo(store).SaveAsync(order, CancellationToken.None); // seed the stream at version 2
        var repo = SnapshottingRepo(store, snapshots, logger: logger, interval: 3);

        order.AddLine(Line2, "SKU-2", 1, TenUsd, At); // crosses 3, so a capture is attempted and throws
        var act = async () => await repo.SaveAsync(order, CancellationToken.None);

        await act.Should().NotThrowAsync();
        (await store.ReadStreamAsync(Stream, fromVersion: 0, CancellationToken.None)).Should().HaveCount(3);
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains(Stream.Value));
    }

    // Drives an Order through Draft, two lines, a shipping address, and Place: five events, versions 1
    // through 5, ending Placed. Returns the aggregate as the full-replay reference (its state after all
    // five events), with the stream seeded into the store at version 5.
    private static async Task<Order> SeedFivePlacedEventsAsync(IEventStore store)
    {
        var order = Order.Draft(OrderId, CustomerId, At, "web");
        order.AddLine(Line1, "SKU-1", 2, TenUsd, At);
        order.AddLine(Line2, "SKU-2", 1, TenUsd, At);
        order.SetShippingAddress(Shipping, At);
        order.Place(At);
        await PlainRepo(store).SaveAsync(order, CancellationToken.None);
        return order;
    }

    // Rebuilds the genuine memento at a given version by replaying the first atVersion events from the
    // stream into a pristine Order and capturing it, so a seeded snapshot is consistent with the tail.
    private static async Task<OrderSnapshot> SnapshotAtAsync(IEventStore store, int atVersion)
    {
        var envelopes = await store.ReadStreamAsync(Stream, fromVersion: 0, CancellationToken.None);
        var order = new Order();
        for (var i = 0; i < atVersion; i++)
        {
            order.ApplyHistoric(envelopes[i].Payload);
        }
        return order.ToSnapshot();
    }

    private static EventStoreRepository<Order> PlainRepo(IEventStore store)
        => new(
            store,
            new StubCommandContextAccessor { Current = null },
            new StubTenantAccessor(),
            new StubCurrentVersions());

    private static SnapshottingEventStoreRepository<Order, OrderSnapshot> SnapshottingRepo(
        IEventStore store,
        ISnapshotStore snapshots,
        RecordingLogger<SnapshottingEventStoreRepository<Order, OrderSnapshot>>? logger = null,
        int schemaVersion = OrderSchema,
        int interval = 50)
        => new(
            store,
            new StubCommandContextAccessor { Current = null },
            new StubTenantAccessor(),
            new StubCurrentVersions(),
            snapshots,
            schemaVersion,
            logger ?? NewLogger(),
            interval);

    private static RecordingLogger<SnapshottingEventStoreRepository<Order, OrderSnapshot>> NewLogger()
        => new();
}
