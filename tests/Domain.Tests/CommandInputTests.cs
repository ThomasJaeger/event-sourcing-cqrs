using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.SharedKernel;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Domain.Tests;

public sealed class CommandInputTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Address Destination = new("1 Main", "City", "12345", "US");

    [Theory]
    [InlineData("null")]
    [InlineData("currency")]
    [InlineData("negative")]
    [InlineData("other-currency")]
    [InlineData("oversized")]
    [InlineData("rounded-overflow")]
    public void Order_rejects_invalid_prices_before_recording_lines(string invalid)
    {
        var price = invalid switch
        {
            "null" => null,
            "currency" => new Money(1m, null!),
            "negative" => new Money(-1m, Currency.USD),
            "other-currency" => new Money(1m, Currency.EUR),
            "oversized" => new Money(decimal.MaxValue, Currency.USD),
            _ => new Money(99_999_999_999_999.9999m, Currency.USD)
        };
        var order = Draft();
        Action add = () => order.AddLine(Guid.NewGuid(), "sku", 1, price!, Now);
        add.Should().Throw<DomainException>();
        order.DequeueUncommittedEvents().Should().BeEmpty();
    }

    [Fact]
    public void Order_rejects_a_line_whose_addition_exceeds_the_supported_total()
    {
        var order = Draft();
        order.AddLine(Guid.NewGuid(), "sku", 1, new Money(50_000_000_000_000m, Currency.USD), Now);
        order.DequeueUncommittedEvents();
        Action add = () => order.AddLine(Guid.NewGuid(), "sku", 1,
            new Money(50_000_000_000_000m, Currency.USD), Now);
        add.Should().Throw<DomainException>();
        order.DequeueUncommittedEvents().Should().BeEmpty();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("street")]
    [InlineData("city")]
    [InlineData("postal")]
    [InlineData("country")]
    public void Order_and_shipment_reject_incomplete_shipping_addresses(string missing)
    {
        var address = missing switch
        {
            "null" => null,
            "street" => Destination with { Street = null! },
            "city" => Destination with { City = "" },
            "postal" => Destination with { PostalCode = " " },
            _ => Destination with { Country = null! }
        };
        var order = Draft();
        Action set = () => order.SetShippingAddress(address!, Now);
        set.Should().Throw<DomainException>();
        order.DequeueUncommittedEvents().Should().BeEmpty();
        Action schedule = () => Shipment.Schedule(Guid.NewGuid(), order.Id, address!,
            [new ShipmentLine(order.Id, Guid.NewGuid(), "sku", 1)], Now);
        schedule.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("currency")]
    [InlineData("oversized")]
    public void Payment_rejects_incomplete_or_oversized_amounts(string invalid)
    {
        var amount = invalid switch
        {
            "null" => null,
            "currency" => new Money(1m, null!),
            _ => new Money(decimal.MaxValue, Currency.USD)
        };
        Action authorize = () => Payment.Authorize(Guid.NewGuid(), Guid.NewGuid(), amount!, "card", Now);
        authorize.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData("null-list")]
    [InlineData("null-line")]
    [InlineData("sku")]
    [InlineData("quantity")]
    public void Shipment_rejects_malformed_lines_before_recording_scheduling(string invalid)
    {
        var orderId = Guid.NewGuid();
        IReadOnlyList<ShipmentLine>? lines = invalid switch
        {
            "null-list" => null,
            "null-line" => [null!],
            "sku" => [new ShipmentLine(orderId, Guid.NewGuid(), null!, 1)],
            _ => [new ShipmentLine(orderId, Guid.NewGuid(), "sku", -1)]
        };
        Action schedule = () => Shipment.Schedule(Guid.NewGuid(), orderId, Destination, lines!, Now);
        schedule.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData("channel")]
    [InlineData("cancel-reason")]
    [InlineData("carrier")]
    [InlineData("tracking")]
    public void Order_rejects_missing_event_text_before_recording_it(string missing)
    {
        if (missing == "channel")
        {
            Action draft = () => Order.Draft(Guid.NewGuid(), Guid.NewGuid(), Now, null!);
            draft.Should().Throw<DomainException>();
            return;
        }
        var order = Draft();
        order.AddLine(Guid.NewGuid(), "sku", 1, new Money(10m, Currency.USD), Now);
        order.SetShippingAddress(Destination, Now);
        order.Place(Now);
        order.DequeueUncommittedEvents();
        Action command = missing switch
        {
            "cancel-reason" => () => order.Cancel(null!, Guid.NewGuid(), Now),
            "carrier" => () => order.Ship(null!, "tracking", Now),
            _ => () => order.Ship("carrier", null!, Now)
        };
        command.Should().Throw<DomainException>();
        order.DequeueUncommittedEvents().Should().BeEmpty();
    }

    [Theory]
    [InlineData("order-channel")]
    [InlineData("order-sku")]
    [InlineData("order-address")]
    [InlineData("order-cancel")]
    [InlineData("order-carrier")]
    [InlineData("order-tracking")]
    [InlineData("inventory-sku")]
    [InlineData("inventory-adjust")]
    [InlineData("inventory-release")]
    [InlineData("payment-method")]
    [InlineData("payment-void")]
    [InlineData("payment-refund")]
    [InlineData("shipment-sku")]
    [InlineData("shipment-dispatch")]
    [InlineData("shipment-return")]
    public void Embedded_NUL_cannot_be_written_into_event_text(string field)
    {
        const string malformed = "text\0value";
        var order = Draft();
        var lineId = Guid.NewGuid();
        var inventory = Inventory.Create(Guid.NewGuid(), "sku", Now);
        inventory.Adjust(2, "Stock", Now);
        inventory.Reserve(order.Id, lineId, 1, Now);
        var payment = Payment.Authorize(Guid.NewGuid(), order.Id, new Money(10m, Currency.USD), "card", Now);
        var shipment = Shipment.Schedule(Guid.NewGuid(), order.Id, Destination,
            [new ShipmentLine(order.Id, lineId, "sku", 1)], Now);
        if (field is "order-cancel" or "order-carrier" or "order-tracking")
        {
            order.AddLine(lineId, "sku", 1, new Money(10m, Currency.USD), Now);
            order.SetShippingAddress(Destination, Now);
            order.Place(Now);
        }
        if (field == "payment-refund") payment.Capture(Now);
        if (field == "shipment-return") { shipment.Dispatch("carrier", Now); shipment.Deliver(Now); }
        Action command = field switch
        {
            "order-channel" => () => Order.Draft(Guid.NewGuid(), Guid.NewGuid(), Now, malformed),
            "order-sku" => () => order.AddLine(lineId, malformed, 1, new Money(10m, Currency.USD), Now),
            "order-address" => () => order.SetShippingAddress(Destination with { Street = malformed }, Now),
            "order-cancel" => () => order.Cancel(malformed, Guid.NewGuid(), Now),
            "order-carrier" => () => order.Ship(malformed, "tracking", Now),
            "order-tracking" => () => order.Ship("carrier", malformed, Now),
            "inventory-sku" => () => Inventory.Create(Guid.NewGuid(), malformed, Now),
            "inventory-adjust" => () => inventory.Adjust(1, malformed, Now),
            "inventory-release" => () => inventory.Release(lineId, malformed, Now),
            "payment-method" => () => Payment.Authorize(Guid.NewGuid(), order.Id, new Money(10m, Currency.USD), malformed, Now),
            "payment-void" => () => payment.Void(malformed, Now),
            "payment-refund" => () => payment.Refund(malformed, Now),
            "shipment-sku" => () => Shipment.Schedule(Guid.NewGuid(), order.Id, Destination,
                [new ShipmentLine(order.Id, lineId, malformed, 1)], Now),
            "shipment-dispatch" => () => shipment.Dispatch(malformed, Now),
            _ => () => shipment.Return(malformed, Now)
        };
        command.Should().Throw<DomainException>();
    }

    [Fact]
    public void Inventory_adjustment_rejects_counter_overflow_even_with_reserved_stock()
    {
        var inventory = Inventory.Create(Guid.NewGuid(), "sku", Now);
        inventory.Adjust(int.MaxValue, "Stock", Now);
        inventory.Reserve(Guid.NewGuid(), Guid.NewGuid(), 1, Now);
        inventory.DequeueUncommittedEvents();
        Action adjust = () => inventory.Adjust(1, "Overflow", Now);
        adjust.Should().Throw<DomainException>();
        inventory.TotalAdjusted.Should().Be(int.MaxValue);
        inventory.DequeueUncommittedEvents().Should().BeEmpty();
    }

    private static Order Draft()
    {
        var order = Order.Draft(Guid.NewGuid(), Guid.NewGuid(), Now, "web");
        order.DequeueUncommittedEvents();
        return order;
    }
}
