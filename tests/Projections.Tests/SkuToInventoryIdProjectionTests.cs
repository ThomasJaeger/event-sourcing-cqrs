using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Projections.SkuToInventoryId;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

public class SkuToInventoryIdProjectionTests
{
    private static readonly DateTime CreatedAt = new(2026, 5, 21, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task InventoryCreated_handler_records_the_sku_to_inventory_mapping()
    {
        var store = new InMemorySkuToInventoryIdStore();
        var projection = new SkuToInventoryIdProjection(store);
        var inventoryId = Guid.NewGuid();

        await projection.HandleAsync(
            Context(new InventoryCreated(inventoryId, "SKU-1", CreatedAt), position: 1),
            CancellationToken.None);

        (await store.GetInventoryIdAsync("SKU-1", CancellationToken.None)).Should().Be(inventoryId);
    }

    [Fact]
    public async Task Handler_advances_the_checkpoint_under_the_projection_name()
    {
        var store = new InMemorySkuToInventoryIdStore();
        var projection = new SkuToInventoryIdProjection(store);
        projection.Name.Should().Be("sku-to-inventory-id");

        await projection.HandleAsync(
            Context(new InventoryCreated(Guid.NewGuid(), "SKU-1", CreatedAt), position: 7),
            CancellationToken.None);

        store.Checkpoints[projection.Name].Should().Be(7);
    }

    [Fact]
    public async Task Handler_skips_when_position_is_at_or_below_checkpoint()
    {
        var store = new InMemorySkuToInventoryIdStore();
        var projection = new SkuToInventoryIdProjection(store);
        // First record advances the checkpoint to 10.
        await projection.HandleAsync(
            Context(new InventoryCreated(Guid.NewGuid(), "SKU-1", CreatedAt), position: 10),
            CancellationToken.None);
        var recordsAfterFirst = store.RecordCount;

        // A second InventoryCreated at position 10 (or below) is an at-least-once
        // redelivery from the projection's perspective: the handler returns early
        // without touching the unit of work.
        await projection.HandleAsync(
            Context(new InventoryCreated(Guid.NewGuid(), "SKU-2", CreatedAt), position: 10),
            CancellationToken.None);

        store.RecordCount.Should().Be(recordsAfterFirst);
        (await store.GetInventoryIdAsync("SKU-2", CancellationToken.None)).Should().BeNull();
        store.Checkpoints[projection.Name].Should().Be(10);
    }

    [Fact]
    public async Task A_second_inventory_for_the_same_sku_keeps_the_first_mapping()
    {
        var store = new InMemorySkuToInventoryIdStore();
        var projection = new SkuToInventoryIdProjection(store);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new InventoryCreated(first, "SKU-1", CreatedAt), position: 1),
            CancellationToken.None);

        // An anomalous second InventoryCreated for the same SKU at a later
        // position. ON CONFLICT DO NOTHING keeps the first mapping.
        await projection.HandleAsync(
            Context(new InventoryCreated(second, "SKU-1", CreatedAt), position: 2),
            CancellationToken.None);

        (await store.GetInventoryIdAsync("SKU-1", CancellationToken.None)).Should().Be(first);
    }

    private static EventContext<TEvent> Context<TEvent>(TEvent @event, long position)
        where TEvent : IDomainEvent
        => new(
            @event,
            new EventMetadata(
                EventId: Guid.NewGuid(),
                CorrelationId: Guid.NewGuid(),
                CausationId: Guid.NewGuid(),
                ActorId: Guid.Empty,
                Source: "test",
                SchemaVersion: 1,
                OccurredUtc: CreatedAt),
            position);
}
