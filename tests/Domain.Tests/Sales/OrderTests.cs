using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Domain.Tests.TestKit;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Domain.Tests.Sales;

public class OrderTests
{
    private static readonly Guid OrderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CustomerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LineId1 = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid LineId2 = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid UserId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTime At = new(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Address Shipping = new("1 Main St", "Smalltown", "12345", "US");
    private static readonly Money TenUsd = new(10m, "USD");

    [Fact]
    public void Draft_creates_an_order_with_Draft_status()
    {
        var order = Order.Draft(OrderId, CustomerId, At);

        order.Id.Should().Be(OrderId);
        order.Status.Should().Be(OrderStatus.Draft);
        order.DequeueUncommittedEvents()
            .Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new OrderDrafted(OrderId, CustomerId, At));
    }

    [Fact]
    public void AddLine_adds_a_line_when_status_is_Draft()
    {
        new AggregateTest<Order>()
            .Given(new OrderDrafted(OrderId, CustomerId, At))
            .When(o => o.AddLine(LineId1, "SKU-1", 2, TenUsd, At))
            .Then(new OrderLineAdded(OrderId, LineId1, "SKU-1", 2, TenUsd, At));
    }

    [Fact]
    public void AddLine_throws_when_status_is_Placed()
    {
        new AggregateTest<Order>()
            .Given(
                new OrderDrafted(OrderId, CustomerId, At),
                new OrderLineAdded(OrderId, LineId1, "SKU-1", 1, TenUsd, At),
                new ShippingAddressSet(OrderId, Shipping, At),
                new OrderPlaced(OrderId, CustomerId, TenUsd, At))
            .When(o => o.AddLine(LineId2, "SKU-2", 1, TenUsd, At))
            .ThenThrows<DomainException>()
            .WithMessage("Cannot add line*order is Placed*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddLine_throws_when_quantity_is_not_positive(int quantity)
    {
        new AggregateTest<Order>()
            .Given(new OrderDrafted(OrderId, CustomerId, At))
            .When(o => o.AddLine(LineId1, "SKU-1", quantity, TenUsd, At))
            .ThenThrows<DomainException>()
            .WithMessage("*quantity must be positive*");
    }

    [Fact]
    public void RemoveLine_removes_the_named_line()
    {
        new AggregateTest<Order>()
            .Given(
                new OrderDrafted(OrderId, CustomerId, At),
                new OrderLineAdded(OrderId, LineId1, "SKU-1", 1, TenUsd, At))
            .When(o => o.RemoveLine(LineId1, At))
            .Then(new OrderLineRemoved(OrderId, LineId1, At));
    }

    [Fact]
    public void RemoveLine_throws_when_line_not_found()
    {
        new AggregateTest<Order>()
            .Given(new OrderDrafted(OrderId, CustomerId, At))
            .When(o => o.RemoveLine(LineId1, At))
            .ThenThrows<DomainException>()
            .WithMessage($"Line {LineId1} not found*");
    }

    [Fact]
    public void SetShippingAddress_sets_the_address()
    {
        new AggregateTest<Order>()
            .Given(new OrderDrafted(OrderId, CustomerId, At))
            .When(o => o.SetShippingAddress(Shipping, At))
            .Then(new ShippingAddressSet(OrderId, Shipping, At));
    }

    [Fact]
    public void Place_transitions_to_Placed_when_prereqs_met()
    {
        new AggregateTest<Order>()
            .Given(
                new OrderDrafted(OrderId, CustomerId, At),
                new OrderLineAdded(OrderId, LineId1, "SKU-1", 2, TenUsd, At),
                new ShippingAddressSet(OrderId, Shipping, At))
            .When(o => o.Place(At))
            .Then(new OrderPlaced(OrderId, CustomerId, new Money(20m, "USD"), At));
    }

    [Fact]
    public void Place_throws_when_no_lines()
    {
        new AggregateTest<Order>()
            .Given(
                new OrderDrafted(OrderId, CustomerId, At),
                new ShippingAddressSet(OrderId, Shipping, At))
            .When(o => o.Place(At))
            .ThenThrows<DomainException>()
            .WithMessage("*no lines*");
    }

    [Fact]
    public void Place_throws_when_no_shipping_address()
    {
        new AggregateTest<Order>()
            .Given(
                new OrderDrafted(OrderId, CustomerId, At),
                new OrderLineAdded(OrderId, LineId1, "SKU-1", 1, TenUsd, At))
            .When(o => o.Place(At))
            .ThenThrows<DomainException>()
            .WithMessage("*shipping address not set*");
    }

    [Fact]
    public void Place_throws_when_not_Draft()
    {
        new AggregateTest<Order>()
            .Given(
                new OrderDrafted(OrderId, CustomerId, At),
                new OrderLineAdded(OrderId, LineId1, "SKU-1", 1, TenUsd, At),
                new ShippingAddressSet(OrderId, Shipping, At),
                new OrderPlaced(OrderId, CustomerId, TenUsd, At))
            .When(o => o.Place(At))
            .ThenThrows<DomainException>()
            .WithMessage("*order is Placed*");
    }

    [Fact]
    public void Cancel_transitions_to_Cancelled_from_Placed()
    {
        new AggregateTest<Order>()
            .Given(
                new OrderDrafted(OrderId, CustomerId, At),
                new OrderLineAdded(OrderId, LineId1, "SKU-1", 1, TenUsd, At),
                new ShippingAddressSet(OrderId, Shipping, At),
                new OrderPlaced(OrderId, CustomerId, TenUsd, At))
            .When(o => o.Cancel("Customer changed mind", UserId, At))
            .Then(new OrderCancelled(OrderId, "Customer changed mind", UserId, At));
    }

    [Fact]
    public void Cancel_throws_when_already_Cancelled()
    {
        new AggregateTest<Order>()
            .Given(
                new OrderDrafted(OrderId, CustomerId, At),
                new OrderLineAdded(OrderId, LineId1, "SKU-1", 1, TenUsd, At),
                new ShippingAddressSet(OrderId, Shipping, At),
                new OrderPlaced(OrderId, CustomerId, TenUsd, At),
                new OrderCancelled(OrderId, "Reason", UserId, At))
            .When(o => o.Cancel("Again", UserId, At))
            .ThenThrows<DomainException>()
            .WithMessage("*already cancelled*");
    }

    [Fact]
    public void Cancel_throws_when_Shipped()
    {
        new AggregateTest<Order>()
            .Given(
                new OrderDrafted(OrderId, CustomerId, At),
                new OrderLineAdded(OrderId, LineId1, "SKU-1", 1, TenUsd, At),
                new ShippingAddressSet(OrderId, Shipping, At),
                new OrderPlaced(OrderId, CustomerId, TenUsd, At),
                new OrderShipped(OrderId, "UPS", "1Z999", At))
            .When(o => o.Cancel("Reason", UserId, At))
            .ThenThrows<DomainException>()
            .WithMessage("*already shipped*");
    }

    [Fact]
    public void Ship_transitions_to_Shipped_from_Placed()
    {
        new AggregateTest<Order>()
            .Given(
                new OrderDrafted(OrderId, CustomerId, At),
                new OrderLineAdded(OrderId, LineId1, "SKU-1", 1, TenUsd, At),
                new ShippingAddressSet(OrderId, Shipping, At),
                new OrderPlaced(OrderId, CustomerId, TenUsd, At))
            .When(o => o.Ship("UPS", "1Z999", At))
            .Then(new OrderShipped(OrderId, "UPS", "1Z999", At));
    }

    [Fact]
    public void AddLine_throws_when_LineId_already_exists()
    {
        new AggregateTest<Order>()
            .Given(
                new OrderDrafted(OrderId, CustomerId, At),
                new OrderLineAdded(OrderId, LineId1, "SKU-1", 1, TenUsd, At))
            .When(o => o.AddLine(LineId1, "SKU-1-DUPLICATE", 1, TenUsd, At))
            .ThenThrows<DomainException>()
            .WithMessage("*already exists*");
    }

    [Fact]
    public void RemoveLine_throws_when_order_is_Placed()
    {
        new AggregateTest<Order>()
            .Given(
                new OrderDrafted(OrderId, CustomerId, At),
                new OrderLineAdded(OrderId, LineId1, "SKU-1", 1, TenUsd, At),
                new ShippingAddressSet(OrderId, Shipping, At),
                new OrderPlaced(OrderId, CustomerId, TenUsd, At))
            .When(o => o.RemoveLine(LineId1, At))
            .ThenThrows<DomainException>()
            .WithMessage("*order is Placed*");
    }

    [Fact]
    public void SetShippingAddress_throws_when_order_is_Placed()
    {
        new AggregateTest<Order>()
            .Given(
                new OrderDrafted(OrderId, CustomerId, At),
                new OrderLineAdded(OrderId, LineId1, "SKU-1", 1, TenUsd, At),
                new ShippingAddressSet(OrderId, Shipping, At),
                new OrderPlaced(OrderId, CustomerId, TenUsd, At))
            .When(o => o.SetShippingAddress(Shipping, At))
            .ThenThrows<DomainException>()
            .WithMessage("*order is Placed*");
    }

    [Fact]
    public void Ship_throws_when_order_is_Draft()
    {
        new AggregateTest<Order>()
            .Given(new OrderDrafted(OrderId, CustomerId, At))
            .When(o => o.Ship("UPS", "1Z999", At))
            .ThenThrows<DomainException>()
            .WithMessage("*order is Draft*");
    }

    [Fact]
    public void Ship_throws_when_order_is_Cancelled()
    {
        new AggregateTest<Order>()
            .Given(
                new OrderDrafted(OrderId, CustomerId, At),
                new OrderLineAdded(OrderId, LineId1, "SKU-1", 1, TenUsd, At),
                new ShippingAddressSet(OrderId, Shipping, At),
                new OrderPlaced(OrderId, CustomerId, TenUsd, At),
                new OrderCancelled(OrderId, "Reason", UserId, At))
            .When(o => o.Ship("UPS", "1Z999", At))
            .ThenThrows<DomainException>()
            .WithMessage("*order is Cancelled*");
    }

    [Fact]
    public void Ship_throws_when_order_is_Shipped()
    {
        new AggregateTest<Order>()
            .Given(
                new OrderDrafted(OrderId, CustomerId, At),
                new OrderLineAdded(OrderId, LineId1, "SKU-1", 1, TenUsd, At),
                new ShippingAddressSet(OrderId, Shipping, At),
                new OrderPlaced(OrderId, CustomerId, TenUsd, At),
                new OrderShipped(OrderId, "UPS", "1Z999", At))
            .When(o => o.Ship("UPS", "1Z999", At))
            .ThenThrows<DomainException>()
            .WithMessage("*order is Shipped*");
    }
}
