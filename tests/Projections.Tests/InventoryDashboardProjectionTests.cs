using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using EventSourcingCqrs.Projections.InventoryDashboard;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

public class InventoryDashboardProjectionTests
{
    private static readonly DateTime At = new(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SystemAt = new(2026, 5, 14, 9, 0, 5, DateTimeKind.Utc);

    [Fact]
    public async Task InventoryCreated_creates_the_dashboard_row_with_zeroed_quantities()
    {
        var store = new InMemoryInventoryDashboardStore();
        var projection = Projection(store);
        var inventoryId = Guid.NewGuid();

        await projection.HandleAsync(
            Context(new InventoryCreated(inventoryId, "SKU-1", At), position: 1, occurredUtc: SystemAt),
            CancellationToken.None);

        var row = await store.GetBySkuAsync("SKU-1", CancellationToken.None);
        row!.InventoryId.Should().Be(inventoryId);
        row.OnHandQuantity.Should().Be(0);
        row.ReservedQuantity.Should().Be(0);
        row.LastUpdatedUtc.Should().Be(SystemAt);
    }

    [Fact]
    public async Task InventoryAdjusted_accumulates_on_hand_in_both_directions()
    {
        var store = new InMemoryInventoryDashboardStore();
        var projection = Projection(store);
        var inventoryId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new InventoryCreated(inventoryId, "SKU-1", At), position: 1), CancellationToken.None);

        await projection.HandleAsync(
            Context(new InventoryAdjusted(inventoryId, "SKU-1", 100, "restock", At), position: 2),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new InventoryAdjusted(inventoryId, "SKU-1", -30, "shrinkage", At), position: 3),
            CancellationToken.None);

        (await store.GetBySkuAsync("SKU-1", CancellationToken.None))!.OnHandQuantity.Should().Be(70);
    }

    [Fact]
    public async Task InventoryReserved_adds_to_reserved_and_records_the_lookup()
    {
        var store = new InMemoryInventoryDashboardStore();
        var projection = Projection(store);
        var inventoryId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new InventoryCreated(inventoryId, "SKU-1", At), position: 1), CancellationToken.None);

        await projection.HandleAsync(
            Context(new InventoryReserved(inventoryId, orderId, lineId, "SKU-1", 5, At), position: 2),
            CancellationToken.None);

        (await store.GetBySkuAsync("SKU-1", CancellationToken.None))!.ReservedQuantity.Should().Be(5);
        await using var read = await store.BeginAsync(CancellationToken.None);
        (await read.GetReservationAsync(inventoryId, orderId, lineId, CancellationToken.None))
            .Should().Be(new InventoryReservationRow(inventoryId, orderId, lineId, 5, At));
    }

    [Fact]
    public async Task InventoryReleased_after_reserve_reverses_reserved_and_deletes_the_lookup()
    {
        var store = new InMemoryInventoryDashboardStore();
        var projection = Projection(store);
        var inventoryId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new InventoryCreated(inventoryId, "SKU-1", At), position: 1), CancellationToken.None);
        await projection.HandleAsync(
            Context(new InventoryReserved(inventoryId, orderId, lineId, "SKU-1", 5, At), position: 2),
            CancellationToken.None);

        await projection.HandleAsync(
            Context(new InventoryReleased(inventoryId, orderId, lineId, "cancelled", At), position: 3),
            CancellationToken.None);

        (await store.GetBySkuAsync("SKU-1", CancellationToken.None))!.ReservedQuantity.Should().Be(0);
        await using var read = await store.BeginAsync(CancellationToken.None);
        (await read.GetReservationAsync(inventoryId, orderId, lineId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task InventoryReleased_with_no_lookup_row_no_ops_and_advances_the_checkpoint()
    {
        var store = new InMemoryInventoryDashboardStore();
        var projection = Projection(store);
        var inventoryId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new InventoryCreated(inventoryId, "SKU-1", At), position: 1), CancellationToken.None);

        // No prior reserve for this line, so no lookup row. The release nets
        // nothing but still advances the checkpoint.
        await projection.HandleAsync(
            Context(new InventoryReleased(inventoryId, Guid.NewGuid(), Guid.NewGuid(), "cancelled", At),
                position: 5),
            CancellationToken.None);

        (await store.GetBySkuAsync("SKU-1", CancellationToken.None))!.ReservedQuantity.Should().Be(0);
        store.Checkpoints[projection.Name].Should().Be(5);
    }

    [Fact]
    public async Task Releasing_one_of_two_reservations_leaves_the_other()
    {
        var store = new InMemoryInventoryDashboardStore();
        var projection = Projection(store);
        var inventoryId = Guid.NewGuid();
        var order1 = Guid.NewGuid();
        var line1 = Guid.NewGuid();
        var order2 = Guid.NewGuid();
        var line2 = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new InventoryCreated(inventoryId, "SKU-1", At), position: 1), CancellationToken.None);
        await projection.HandleAsync(
            Context(new InventoryReserved(inventoryId, order1, line1, "SKU-1", 5, At), position: 2),
            CancellationToken.None);
        await projection.HandleAsync(
            Context(new InventoryReserved(inventoryId, order2, line2, "SKU-1", 3, At), position: 3),
            CancellationToken.None);

        // Release only the first reservation.
        await projection.HandleAsync(
            Context(new InventoryReleased(inventoryId, order1, line1, "cancelled", At), position: 4),
            CancellationToken.None);

        // Reserved reflects only the first line's reversal: 8 - 5 = 3.
        (await store.GetBySkuAsync("SKU-1", CancellationToken.None))!.ReservedQuantity.Should().Be(3);
        await using var read = await store.BeginAsync(CancellationToken.None);
        // The released line's lookup is gone; the surviving line's lookup remains.
        (await read.GetReservationAsync(inventoryId, order1, line1, CancellationToken.None)).Should().BeNull();
        (await read.GetReservationAsync(inventoryId, order2, line2, CancellationToken.None))
            .Should().Be(new InventoryReservationRow(inventoryId, order2, line2, 3, At));
    }

    [Fact]
    public async Task InventoryAdjusted_skips_when_position_is_at_or_below_checkpoint()
    {
        var store = new InMemoryInventoryDashboardStore();
        var projection = Projection(store);
        var inventoryId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new InventoryCreated(inventoryId, "SKU-1", At), position: 10), CancellationToken.None);

        // A redelivered InventoryAdjusted at position 10 (at the checkpoint)
        // returns early, so on-hand stays at zero.
        await projection.HandleAsync(
            Context(new InventoryAdjusted(inventoryId, "SKU-1", 100, "restock", At), position: 10),
            CancellationToken.None);

        (await store.GetBySkuAsync("SKU-1", CancellationToken.None))!.OnHandQuantity.Should().Be(0);
    }

    [Fact]
    public async Task InventoryReserved_skips_when_position_is_at_or_below_checkpoint()
    {
        var store = new InMemoryInventoryDashboardStore();
        var projection = Projection(store);
        var inventoryId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new InventoryCreated(inventoryId, "SKU-1", At), position: 10), CancellationToken.None);

        // A redelivered InventoryReserved at position 10 returns early, so
        // reserved stays at zero and no lookup row is recorded.
        await projection.HandleAsync(
            Context(new InventoryReserved(inventoryId, Guid.NewGuid(), Guid.NewGuid(), "SKU-1", 5, At),
                position: 10),
            CancellationToken.None);

        (await store.GetBySkuAsync("SKU-1", CancellationToken.None))!.ReservedQuantity.Should().Be(0);
    }

    [Fact]
    public async Task InventoryCreated_stages_an_inventory_notification_keyed_by_the_sku()
    {
        var store = new InMemoryInventoryDashboardStore();
        var projection = Projection(store);

        await projection.HandleAsync(
            Context(new InventoryCreated(Guid.NewGuid(), "SKU-1", At), position: 1), CancellationToken.None);

        var staged = store.StagedNotifications.Should().ContainSingle().Subject;
        staged.ProjectionName.Should().Be(projection.Name);
        staged.ResourceId.Should().Be("SKU-1");
        staged.EventName.Should().Be(nameof(InventoryCreated));
        staged.Widgets.Should().BeEmpty();
    }

    [Fact]
    public async Task InventoryAdjusted_stages_keyed_by_the_returned_sku()
    {
        var store = new InMemoryInventoryDashboardStore();
        var projection = Projection(store);
        var inventoryId = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new InventoryCreated(inventoryId, "SKU-1", At), position: 1), CancellationToken.None);

        await projection.HandleAsync(
            Context(new InventoryAdjusted(inventoryId, "SKU-1", 100, "restock", At), position: 2),
            CancellationToken.None);

        // The created row and the adjustment each stage; the adjustment keys on the
        // sku its UPDATE returns, not on the InventoryId the event carries.
        store.StagedNotifications.Should().HaveCount(2);
        store.StagedNotifications[1].ResourceId.Should().Be("SKU-1");
        store.StagedNotifications[1].EventName.Should().Be(nameof(InventoryAdjusted));
    }

    [Fact]
    public async Task InventoryAdjusted_on_an_unknown_inventory_id_stages_nothing()
    {
        var store = new InMemoryInventoryDashboardStore();
        var projection = Projection(store);

        // No dashboard row for this InventoryId (the ADR 0020 orphan case): the
        // UPDATE matches zero rows, returns no sku, and the handler stages nothing.
        await projection.HandleAsync(
            Context(new InventoryAdjusted(Guid.NewGuid(), "SKU-X", 100, "restock", At), position: 1),
            CancellationToken.None);

        store.StagedNotifications.Should().BeEmpty();
    }

    private static InventoryDashboardProjection Projection(InMemoryInventoryDashboardStore store)
        => new(store, NullLogger<InventoryDashboardProjection>.Instance);

    private static EventContext<TEvent> Context<TEvent>(
        TEvent @event, long position, DateTime? occurredUtc = null)
        where TEvent : IDomainEvent
        => new(@event, Metadata(occurredUtc ?? SystemAt), position);

    private static EventMetadata Metadata(DateTime occurredUtc)
        => new(
            EventId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "test",
            SchemaVersion: 1,
            OccurredUtc: occurredUtc);
}
