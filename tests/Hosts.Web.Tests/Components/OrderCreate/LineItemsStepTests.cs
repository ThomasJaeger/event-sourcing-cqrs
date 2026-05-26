using Bunit;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Hosts.Web.Components.OrderCreate;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components.OrderCreate;

// LineItemsStep ships its entry form and controls disabled in the scaffold; the
// AddOrderLine and RemoveOrderLine dispatches land at Commit 25. These tests pin
// that the form renders, every control is disabled, and the line list reflects
// the LineItems parameter.
public sealed class LineItemsStepTests : BunitContext
{
    [Fact]
    public void Renders_the_line_item_entry_form()
    {
        var cut = Render<LineItemsStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.LineItems, new List<LineItem>()));

        cut.Markup.Should().Contain("SKU");
        cut.Markup.Should().Contain("Quantity");
        cut.Markup.Should().Contain("Unit price");
        cut.Markup.Should().Contain("Currency");
    }

    [Fact]
    public void All_entry_controls_render_disabled()
    {
        var cut = Render<LineItemsStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.LineItems, new List<LineItem>()));

        cut.FindAll("input").Should().OnlyContain(i => i.HasAttribute("disabled"));
        cut.Find("select").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Add_to_order_button_renders_disabled()
    {
        var cut = Render<LineItemsStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.LineItems, new List<LineItem>()));

        var add = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Add to order");
        add.HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Continue_button_renders_disabled()
    {
        var cut = Render<LineItemsStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.LineItems, new List<LineItem>()));

        var continueButton = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Continue to shipping");
        continueButton.HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Currency_selector_offers_the_four_supported_currencies()
    {
        var cut = Render<LineItemsStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.LineItems, new List<LineItem>()));

        var options = cut.FindAll("select option");
        options.Should().HaveCount(4);
        options.Select(o => o.GetAttribute("value")).Should().Equal("USD", "EUR", "GBP", "JPY");
    }

    [Fact]
    public void Empty_line_items_render_the_empty_message()
    {
        var cut = Render<LineItemsStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.LineItems, new List<LineItem>()));

        cut.Markup.Should().Contain("No line items yet");
    }

    [Fact]
    public void Line_items_with_content_render_the_line_list()
    {
        var lineItems = new List<LineItem>
        {
            new(Guid.NewGuid(), "SKU-1", 2, new Money(50m, Currency.USD)),
        };
        var cut = Render<LineItemsStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.LineItems, lineItems));

        cut.Markup.Should().Contain("SKU-1");
        cut.FindAll("li").Should().HaveCount(1);
    }
}
