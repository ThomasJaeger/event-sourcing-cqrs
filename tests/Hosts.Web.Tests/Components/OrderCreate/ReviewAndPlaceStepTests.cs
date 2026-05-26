using Bunit;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Hosts.Web.Components;
using EventSourcingCqrs.Hosts.Web.Components.OrderCreate;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components.OrderCreate;

// ReviewAndPlaceStep gains the Place button and the pending badge at Commit 26.
// The page owns the dispatch and polling; the step renders the badge from its
// parameters, gates the button against double-place, and raises OnPlace. These
// tests pin the display, the button gating across badge states, and the badge
// rendering.
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
    public void Place_button_renders_enabled_when_idle()
    {
        var cut = RenderStep();

        cut.Find("#placeOrderButton").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Place_button_is_disabled_while_placing()
    {
        var cut = RenderStep(p => p.Add(x => x.IsPlacing, true));

        cut.Find("#placeOrderButton").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Place_button_is_disabled_while_pending()
    {
        var cut = RenderStep(p => p.Add(x => x.BadgeState, BadgeState.Pending));

        cut.Find("#placeOrderButton").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Place_button_is_disabled_after_settled()
    {
        var cut = RenderStep(p => p.Add(x => x.BadgeState, BadgeState.Settled));

        cut.Find("#placeOrderButton").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Place_button_is_enabled_after_a_failure_for_retry()
    {
        var cut = RenderStep(p => p.Add(x => x.BadgeState, BadgeState.Failed));

        cut.Find("#placeOrderButton").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Clicking_place_raises_OnPlace()
    {
        var raised = false;
        var cut = RenderStep(p => p.Add(x => x.OnPlace, () => raised = true));

        cut.Find("#placeOrderButton").Click();

        raised.Should().BeTrue();
    }

    [Fact]
    public void Idle_state_renders_no_badge()
    {
        var cut = RenderStep(p => p
            .Add(x => x.BadgeState, BadgeState.Idle)
            .Add(x => x.BadgeMessage, "Placing order..."));

        cut.Markup.Should().NotContain("Placing order...");
    }

    [Fact]
    public void Pending_state_renders_the_pending_badge_with_message()
    {
        var cut = RenderStep(p => p
            .Add(x => x.BadgeState, BadgeState.Pending)
            .Add(x => x.BadgeMessage, "Placing order..."));

        cut.Markup.Should().Contain("Placing order...");
        cut.Markup.Should().Contain("bg-yellow-100");
    }

    [Fact]
    public void Settled_state_renders_the_settled_badge()
    {
        var cut = RenderStep(p => p
            .Add(x => x.BadgeState, BadgeState.Settled)
            .Add(x => x.BadgeMessage, "Order placed."));

        cut.Markup.Should().Contain("Order placed.");
        cut.Markup.Should().Contain("bg-green-100");
    }

    [Fact]
    public void Failed_state_renders_the_failed_badge()
    {
        var cut = RenderStep(p => p
            .Add(x => x.BadgeState, BadgeState.Failed)
            .Add(x => x.BadgeMessage, "Place failed"));

        cut.Markup.Should().Contain("bg-red-100");
    }

    [Fact]
    public void Failure_message_renders_when_populated()
    {
        var cut = RenderStep(p => p.Add(x => x.FailureMessage, "The order could not be placed."));

        cut.Markup.Should().Contain("The order could not be placed.");
    }

    private IRenderedComponent<ReviewAndPlaceStep> RenderStep(
        Action<Bunit.ComponentParameterCollectionBuilder<ReviewAndPlaceStep>>? extra = null) =>
        Render<ReviewAndPlaceStep>(p =>
        {
            p.Add(x => x.OrderId, Guid.NewGuid())
                .Add(x => x.CustomerId, Guid.NewGuid())
                .Add(x => x.LineItems, new List<LineItem>());
            extra?.Invoke(p);
        });
}
