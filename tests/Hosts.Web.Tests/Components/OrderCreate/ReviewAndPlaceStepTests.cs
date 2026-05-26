using Bunit;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Hosts.Web.Components.OrderCreate;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components.OrderCreate;

// ReviewAndPlaceStep is display-only with a disabled Place button in the scaffold;
// the PlaceOrder dispatch and the pending-badge surface land at Commit 26. These
// tests pin the conditional rendering of the customer, line-item, and address
// sections against the working state the wizard accumulates.
public sealed class ReviewAndPlaceStepTests : BunitContext
{
    [Fact]
    public void Renders_the_order_id()
    {
        var orderId = Guid.NewGuid();
        var cut = Render<ReviewAndPlaceStep>(p => p
            .Add(x => x.OrderId, orderId)
            .Add(x => x.CustomerId, Guid.NewGuid())
            .Add(x => x.LineItems, new List<LineItem>()));

        cut.Markup.Should().Contain(orderId.ToString());
    }

    [Fact]
    public void Renders_the_customer_id_when_present()
    {
        var customerId = Guid.NewGuid();
        var cut = Render<ReviewAndPlaceStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.CustomerId, customerId)
            .Add(x => x.LineItems, new List<LineItem>()));

        cut.Markup.Should().Contain("Customer ID");
        cut.Markup.Should().Contain(customerId.ToString());
    }

    [Fact]
    public void Hides_the_customer_id_section_when_empty()
    {
        var cut = Render<ReviewAndPlaceStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.CustomerId, Guid.Empty)
            .Add(x => x.LineItems, new List<LineItem>()));

        cut.Markup.Should().NotContain("Customer ID");
    }

    [Fact]
    public void Renders_the_line_items_when_present()
    {
        var lineItems = new List<LineItem>
        {
            new(Guid.NewGuid(), "SKU-7", 3, new Money(20m, Currency.USD)),
        };
        var cut = Render<ReviewAndPlaceStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.CustomerId, Guid.NewGuid())
            .Add(x => x.LineItems, lineItems));

        cut.Markup.Should().Contain("SKU-7");
    }

    [Fact]
    public void Renders_the_shipping_address_when_present()
    {
        var cut = Render<ReviewAndPlaceStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.CustomerId, Guid.NewGuid())
            .Add(x => x.LineItems, new List<LineItem>())
            .Add(x => x.ShippingAddress, new Address("1 Main St", "Springfield", "12345", "USA")));

        cut.Markup.Should().Contain("Shipping address");
        cut.Markup.Should().Contain("1 Main St");
    }

    [Fact]
    public void Place_order_button_renders_disabled()
    {
        var cut = Render<ReviewAndPlaceStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.CustomerId, Guid.NewGuid())
            .Add(x => x.LineItems, new List<LineItem>()));

        var place = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Place order");
        place.HasAttribute("disabled").Should().BeTrue();
    }
}
