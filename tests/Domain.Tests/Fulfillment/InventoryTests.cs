using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Tests.TestKit;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Domain.Tests.Fulfillment;

public class InventoryTests
{
    private static readonly Guid InventoryId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OrderId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid LineId1 = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private const string Sku = "SKU-1";
    private static readonly DateTime At = new(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_creates_an_inventory_bound_to_a_sku()
    {
        var inventory = Inventory.Create(InventoryId, Sku, At);

        inventory.Id.Should().Be(InventoryId);
        inventory.Sku.Should().Be(Sku);
        inventory.TotalAdjusted.Should().Be(0);
        inventory.Reserved.Should().Be(0);
        inventory.Available.Should().Be(0);
        inventory.DequeueUncommittedEvents()
            .Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new InventoryCreated(InventoryId, Sku, At));
    }

    [Fact]
    public void Create_throws_when_sku_is_empty()
    {
        var action = () => Inventory.Create(InventoryId, "", At);

        action.Should().Throw<DomainException>()
            .WithMessage("*SKU must be non-empty*");
    }

    [Fact]
    public void Adjust_increases_stock_when_delta_is_positive()
    {
        new AggregateTest<Inventory>()
            .Given(new InventoryCreated(InventoryId, Sku, At))
            .When(i => i.Adjust(100, "Initial stock", At))
            .Then(new InventoryAdjusted(InventoryId, Sku, 100, "Initial stock", At));
    }

    [Fact]
    public void Adjust_decreases_stock_when_delta_is_negative_and_does_not_overcommit()
    {
        new AggregateTest<Inventory>()
            .Given(
                new InventoryCreated(InventoryId, Sku, At),
                new InventoryAdjusted(InventoryId, Sku, 100, "Initial stock", At))
            .When(i => i.Adjust(-20, "Damage write-off", At))
            .Then(new InventoryAdjusted(InventoryId, Sku, -20, "Damage write-off", At));
    }

    [Fact]
    public void Adjust_throws_when_delta_is_zero()
    {
        new AggregateTest<Inventory>()
            .Given(new InventoryCreated(InventoryId, Sku, At))
            .When(i => i.Adjust(0, "Reason", At))
            .ThenThrows<DomainException>()
            .WithMessage("*delta must be non-zero*");
    }

    [Fact]
    public void Adjust_throws_when_reason_is_empty()
    {
        new AggregateTest<Inventory>()
            .Given(new InventoryCreated(InventoryId, Sku, At))
            .When(i => i.Adjust(10, "", At))
            .ThenThrows<DomainException>()
            .WithMessage("*reason must be non-empty*");
    }

    [Fact]
    public void Adjust_throws_when_would_make_available_negative()
    {
        // 100 in stock, 80 reserved, 20 available. Adjusting -30 would make
        // available -10 — strict invariant rejects.
        new AggregateTest<Inventory>()
            .Given(
                new InventoryCreated(InventoryId, Sku, At),
                new InventoryAdjusted(InventoryId, Sku, 100, "Initial stock", At),
                new InventoryReserved(InventoryId, OrderId, LineId1, Sku, 80, At))
            .When(i => i.Adjust(-30, "Damage write-off", At))
            .ThenThrows<DomainException>()
            .WithMessage("*would make available stock negative*");
    }

    [Fact]
    public void Reserve_records_the_reservation_when_stock_is_sufficient()
    {
        new AggregateTest<Inventory>()
            .Given(
                new InventoryCreated(InventoryId, Sku, At),
                new InventoryAdjusted(InventoryId, Sku, 100, "Initial stock", At))
            .When(i => i.Reserve(OrderId, LineId1, 10, At))
            .Then(new InventoryReserved(InventoryId, OrderId, LineId1, Sku, 10, At));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Reserve_throws_when_quantity_is_not_positive(int quantity)
    {
        new AggregateTest<Inventory>()
            .Given(
                new InventoryCreated(InventoryId, Sku, At),
                new InventoryAdjusted(InventoryId, Sku, 100, "Initial stock", At))
            .When(i => i.Reserve(OrderId, LineId1, quantity, At))
            .ThenThrows<DomainException>()
            .WithMessage("*quantity must be positive*");
    }

    [Fact]
    public void Reserve_throws_when_insufficient_stock()
    {
        new AggregateTest<Inventory>()
            .Given(
                new InventoryCreated(InventoryId, Sku, At),
                new InventoryAdjusted(InventoryId, Sku, 5, "Initial stock", At))
            .When(i => i.Reserve(OrderId, LineId1, 10, At))
            .ThenThrows<DomainException>()
            .WithMessage("*only 5 available*");
    }

    [Fact]
    public void Reserve_throws_when_lineId_already_reserved()
    {
        new AggregateTest<Inventory>()
            .Given(
                new InventoryCreated(InventoryId, Sku, At),
                new InventoryAdjusted(InventoryId, Sku, 100, "Initial stock", At),
                new InventoryReserved(InventoryId, OrderId, LineId1, Sku, 10, At))
            .When(i => i.Reserve(OrderId, LineId1, 5, At))
            .ThenThrows<DomainException>()
            .WithMessage($"*line {LineId1} already has an active reservation*");
    }

    [Fact]
    public void Release_releases_an_existing_reservation()
    {
        new AggregateTest<Inventory>()
            .Given(
                new InventoryCreated(InventoryId, Sku, At),
                new InventoryAdjusted(InventoryId, Sku, 100, "Initial stock", At),
                new InventoryReserved(InventoryId, OrderId, LineId1, Sku, 10, At))
            .When(i => i.Release(LineId1, "Order cancelled", At))
            .Then(new InventoryReleased(InventoryId, OrderId, LineId1, "Order cancelled", At));
    }

    [Fact]
    public void Release_throws_when_lineId_not_reserved()
    {
        new AggregateTest<Inventory>()
            .Given(new InventoryCreated(InventoryId, Sku, At))
            .When(i => i.Release(LineId1, "Order cancelled", At))
            .ThenThrows<DomainException>()
            .WithMessage($"*line {LineId1} has no active reservation*");
    }

    [Fact]
    public void Release_throws_when_reason_is_empty()
    {
        new AggregateTest<Inventory>()
            .Given(
                new InventoryCreated(InventoryId, Sku, At),
                new InventoryAdjusted(InventoryId, Sku, 100, "Initial stock", At),
                new InventoryReserved(InventoryId, OrderId, LineId1, Sku, 10, At))
            .When(i => i.Release(LineId1, "", At))
            .ThenThrows<DomainException>()
            .WithMessage("*reason must be non-empty*");
    }
}
