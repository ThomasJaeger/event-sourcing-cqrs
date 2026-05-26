using Bunit;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Hosts.Web.Components.OrderCreate;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components.OrderCreate;

// LineItemsStep is enabled at Commit 25: the form reports its inputs back to the
// page through OnAddLine, each line's Remove button raises OnRemoveLine, and
// Continue is gated by the page's CanContinue (at-least-one-line, design call
// G.a). The dispatch is page-owned, so these tests pin the form gating, the
// callback payloads, and the parameter-driven dispatch and failure states.
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
    public void All_entry_controls_render_enabled_when_not_dispatching()
    {
        var cut = Render<LineItemsStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.LineItems, new List<LineItem>()));

        cut.FindAll("input").Should().OnlyContain(i => !i.HasAttribute("disabled"));
        cut.Find("select").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Add_button_is_disabled_until_the_form_is_valid()
    {
        var cut = Render<LineItemsStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.LineItems, new List<LineItem>()));

        var add = cut.FindAll("button").Single(b => b.TextContent.Trim().StartsWith("Add"));
        add.HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Add_button_is_enabled_once_a_sku_is_entered()
    {
        var cut = Render<LineItemsStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.LineItems, new List<LineItem>()));

        cut.Find("#skuInput").Change("SKU-1");

        var add = cut.FindAll("button").Single(b => b.TextContent.Trim().StartsWith("Add"));
        add.HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Continue_is_disabled_when_there_are_no_lines()
    {
        var cut = Render<LineItemsStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.LineItems, new List<LineItem>())
            .Add(x => x.CanContinue, false));

        var continueButton = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Continue to shipping");
        continueButton.HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Continue_is_enabled_when_at_least_one_line_exists()
    {
        var cut = Render<LineItemsStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.LineItems, new List<LineItem>())
            .Add(x => x.CanContinue, true));

        var continueButton = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Continue to shipping");
        continueButton.HasAttribute("disabled").Should().BeFalse();
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

    [Fact]
    public void Clicking_add_raises_OnAddLine_with_the_form_values()
    {
        (string Sku, int Quantity, Money UnitPrice)? captured = null;
        var cut = Render<LineItemsStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.LineItems, new List<LineItem>())
            .Add(x => x.OnAddLine, ((string Sku, int Quantity, Money UnitPrice) input) => captured = input));

        cut.Find("#skuInput").Change("SKU-9");
        cut.Find("#quantityInput").Change("3");
        cut.Find("#unitPriceInput").Change("12");
        cut.FindAll("button").Single(b => b.TextContent.Trim().StartsWith("Add")).Click();

        captured.Should().NotBeNull();
        captured!.Value.Sku.Should().Be("SKU-9");
        captured.Value.Quantity.Should().Be(3);
        captured.Value.UnitPrice.Should().Be(new Money(12m, Currency.USD));
    }

    [Fact]
    public void Clicking_remove_raises_OnRemoveLine_with_the_line_id()
    {
        var lineId = Guid.NewGuid();
        Guid? removed = null;
        var lineItems = new List<LineItem> { new(lineId, "SKU-1", 1, new Money(5m, Currency.USD)) };
        var cut = Render<LineItemsStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.LineItems, lineItems)
            .Add(x => x.OnRemoveLine, (Guid id) => removed = id));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Remove").Click();

        removed.Should().Be(lineId);
    }

    [Fact]
    public void Dispatching_disables_the_add_remove_and_continue_controls()
    {
        var lineItems = new List<LineItem> { new(Guid.NewGuid(), "SKU-1", 1, new Money(5m, Currency.USD)) };
        var cut = Render<LineItemsStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.LineItems, lineItems)
            .Add(x => x.CanContinue, true)
            .Add(x => x.IsDispatching, true));

        cut.FindAll("input").Should().OnlyContain(i => i.HasAttribute("disabled"));
        cut.FindAll("button").Single(b => b.TextContent.Trim().StartsWith("Add")).HasAttribute("disabled").Should().BeTrue();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Remove").HasAttribute("disabled").Should().BeTrue();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Continue to shipping").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Failure_message_renders_when_populated()
    {
        var cut = Render<LineItemsStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.LineItems, new List<LineItem>())
            .Add(x => x.FailureMessage, "The line could not be added."));

        cut.Markup.Should().Contain("The line could not be added.");
    }

    [Fact]
    public void Form_clears_after_an_add()
    {
        var cut = Render<LineItemsStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.LineItems, new List<LineItem>())
            .Add(x => x.OnAddLine, ((string, int, Money) _) => { }));

        cut.Find("#skuInput").Change("SKU-1");
        cut.Find("#quantityInput").Change("5");
        cut.FindAll("button").Single(b => b.TextContent.Trim().StartsWith("Add")).Click();

        cut.Find("#skuInput").GetAttribute("value").Should().BeNullOrEmpty();
        cut.Find("#quantityInput").GetAttribute("value").Should().Be("1");
    }
}
