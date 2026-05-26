using Bunit;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Hosts.Web;
using EventSourcingCqrs.Hosts.Web.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using OrderCreatePage = EventSourcingCqrs.Hosts.Web.Components.Pages.OrderCreate;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components;

// The wizard page owns step navigation, the working OrderId, the line list, and
// (design call M.a) the AddOrderLine and RemoveOrderLine dispatch. These tests
// cover navigation plus the page-owned line dispatches end to end through the
// rendered steps. The OrderCreatePage alias keeps the page class from colliding
// with this assembly's Components.OrderCreate test namespace.
public sealed class OrderCreatePageTests : BunitContext
{
    private readonly StubApiClient stubApiClient = new();

    public OrderCreatePageTests()
    {
        Services.AddSingleton<IApiClient>(stubApiClient);
        // The page injects TimeProvider for the PlaceOrder polling loop. These
        // navigation tests never start polling, so the system clock satisfies it.
        Services.AddSingleton(TimeProvider.System);
    }

    [Fact]
    public void Renders_with_the_New_Order_heading()
    {
        var cut = Render<OrderCreatePage>();

        cut.Find("h1").TextContent.Should().Contain("New Order");
    }

    [Fact]
    public void Initial_render_shows_the_customer_step()
    {
        var cut = Render<OrderCreatePage>();

        cut.Markup.Should().Contain("Customer ID");
        cut.Markup.Should().Contain("Start order");
    }

    [Fact]
    public void Step_indicator_highlights_the_first_step()
    {
        var cut = Render<OrderCreatePage>();

        var steps = cut.FindAll("nav ol li");
        steps.Should().HaveCount(4);
        steps[0].ClassName.Should().Contain("font-bold");
        steps[1].ClassName.Should().Contain("text-gray-400");
    }

    [Fact]
    public void Successful_customer_dispatch_advances_to_the_line_items_step()
    {
        stubApiClient.EnqueueCommandResult(typeof(DraftOrder), new CommandAcceptedResponse(DateTime.UtcNow));
        var cut = Render<OrderCreatePage>();

        cut.Find("#customerIdInput").Input(Guid.NewGuid().ToString());
        cut.Find("button").Click();

        cut.Markup.Should().Contain("No line items yet");
        cut.Markup.Should().NotContain("Customer ID");
    }

    [Fact]
    public void Step_one_dispatches_DraftOrder_with_the_wizard_OrderId_and_entered_CustomerId()
    {
        var customerId = Guid.NewGuid();
        stubApiClient.EnqueueCommandResult(typeof(DraftOrder), new CommandAcceptedResponse(DateTime.UtcNow));
        var cut = Render<OrderCreatePage>();

        cut.Find("#customerIdInput").Input(customerId.ToString());
        cut.Find("button").Click();

        var dispatched = stubApiClient.CapturedCommands.Should().ContainSingle().Subject.Command
            .Should().BeOfType<DraftOrder>().Subject;
        dispatched.OrderId.Should().NotBe(Guid.Empty);
        dispatched.CustomerId.Should().Be(customerId);
    }

    [Fact]
    public void Back_button_is_hidden_on_the_first_step()
    {
        var cut = Render<OrderCreatePage>();

        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain("Back");
    }

    [Fact]
    public void Back_button_returns_from_the_line_items_step_to_the_customer_step()
    {
        stubApiClient.EnqueueCommandResult(typeof(DraftOrder), new CommandAcceptedResponse(DateTime.UtcNow));
        var cut = Render<OrderCreatePage>();
        cut.Find("#customerIdInput").Input(Guid.NewGuid().ToString());
        cut.Find("button").Click();
        cut.Markup.Should().Contain("No line items yet");

        var back = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Back");
        back.Click();

        cut.Markup.Should().Contain("Customer ID");
        cut.Markup.Should().NotContain("No line items yet");
    }

    [Fact]
    public void AddOrderLine_dispatch_adds_a_line_to_the_visible_list()
    {
        stubApiClient.EnqueueCommandResult(typeof(DraftOrder), new CommandAcceptedResponse(DateTime.UtcNow));
        stubApiClient.EnqueueCommandResult(typeof(AddOrderLine), new CommandAcceptedResponse(DateTime.UtcNow));
        var cut = Render<OrderCreatePage>();
        AdvanceToLineItems(cut);

        AddLine(cut, "SKU-1", "2", "10");

        var add = stubApiClient.CapturedCommands.Select(c => c.Command).OfType<AddOrderLine>()
            .Should().ContainSingle().Subject;
        add.Sku.Should().Be("SKU-1");
        add.Quantity.Should().Be(2);
        add.UnitPrice.Amount.Should().Be(10m);
        var draft = stubApiClient.CapturedCommands.Select(c => c.Command).OfType<DraftOrder>().Single();
        add.OrderId.Should().Be(draft.OrderId);
        cut.Markup.Should().Contain("SKU-1");
        cut.Markup.Should().NotContain("No line items yet");
    }

    [Fact]
    public void RemoveOrderLine_dispatch_removes_the_line_with_its_minted_LineId()
    {
        stubApiClient.EnqueueCommandResult(typeof(DraftOrder), new CommandAcceptedResponse(DateTime.UtcNow));
        stubApiClient.EnqueueCommandResult(typeof(AddOrderLine), new CommandAcceptedResponse(DateTime.UtcNow));
        stubApiClient.EnqueueCommandResult(typeof(RemoveOrderLine), new CommandAcceptedResponse(DateTime.UtcNow));
        var cut = Render<OrderCreatePage>();
        AdvanceToLineItems(cut);
        AddLine(cut, "SKU-1", "1", "10");
        cut.Markup.Should().Contain("SKU-1");

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Remove").Click();

        var remove = stubApiClient.CapturedCommands.Select(c => c.Command).OfType<RemoveOrderLine>()
            .Should().ContainSingle().Subject;
        var add = stubApiClient.CapturedCommands.Select(c => c.Command).OfType<AddOrderLine>().Single();
        remove.LineId.Should().Be(add.LineId);
        cut.Markup.Should().Contain("No line items yet");
    }

    [Fact]
    public void AddOrderLine_failure_renders_the_message_and_does_not_add_the_line()
    {
        stubApiClient.EnqueueCommandResult(typeof(DraftOrder), new CommandAcceptedResponse(DateTime.UtcNow));
        stubApiClient.SeedCommandFailure<AddOrderLine>(new ApiBusinessRuleException(
            "RULE", "That SKU is discontinued."));
        var cut = Render<OrderCreatePage>();
        AdvanceToLineItems(cut);

        AddLine(cut, "SKU-1", "1", "10");

        cut.Markup.Should().Contain("That SKU is discontinued.");
        cut.Markup.Should().Contain("No line items yet");
    }

    [Fact]
    public void AddOrderLine_dispatches_a_fresh_idempotency_key_per_call()
    {
        stubApiClient.EnqueueCommandResult(typeof(DraftOrder), new CommandAcceptedResponse(DateTime.UtcNow));
        stubApiClient.EnqueueCommandResult(typeof(AddOrderLine), new CommandAcceptedResponse(DateTime.UtcNow));
        stubApiClient.EnqueueCommandResult(typeof(AddOrderLine), new CommandAcceptedResponse(DateTime.UtcNow));
        var cut = Render<OrderCreatePage>();
        AdvanceToLineItems(cut);

        AddLine(cut, "SKU-1", "1", "10");
        AddLine(cut, "SKU-2", "1", "10");

        var keys = stubApiClient.CapturedCommands
            .Where(c => c.Command is AddOrderLine)
            .Select(c => c.IdempotencyKey)
            .ToList();
        keys.Should().HaveCount(2);
        keys.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void AddOrderLine_mints_a_fresh_LineId_per_call()
    {
        stubApiClient.EnqueueCommandResult(typeof(DraftOrder), new CommandAcceptedResponse(DateTime.UtcNow));
        stubApiClient.EnqueueCommandResult(typeof(AddOrderLine), new CommandAcceptedResponse(DateTime.UtcNow));
        stubApiClient.EnqueueCommandResult(typeof(AddOrderLine), new CommandAcceptedResponse(DateTime.UtcNow));
        var cut = Render<OrderCreatePage>();
        AdvanceToLineItems(cut);

        AddLine(cut, "SKU-1", "1", "10");
        AddLine(cut, "SKU-2", "1", "10");

        var lineIds = stubApiClient.CapturedCommands.Select(c => c.Command).OfType<AddOrderLine>()
            .Select(a => a.LineId)
            .ToList();
        lineIds.Should().HaveCount(2);
        lineIds.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Wizard_advances_through_all_steps_to_review_with_a_stable_OrderId()
    {
        stubApiClient.EnqueueCommandResult(typeof(DraftOrder), new CommandAcceptedResponse(DateTime.UtcNow));
        stubApiClient.EnqueueCommandResult(typeof(AddOrderLine), new CommandAcceptedResponse(DateTime.UtcNow));
        stubApiClient.EnqueueCommandResult(typeof(SetOrderShippingAddress), new CommandAcceptedResponse(DateTime.UtcNow));
        var cut = Render<OrderCreatePage>();
        AdvanceToLineItems(cut);
        AddLine(cut, "SKU-1", "1", "10");

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Continue to shipping").Click();
        cut.Find("#streetInput").Change("1 Main St");
        cut.Find("#cityInput").Change("Springfield");
        cut.Find("#postalCodeInput").Change("12345");
        cut.Find("#countryInput").Change("USA");
        cut.FindAll("button").Single(b => b.TextContent.Trim().StartsWith("Sav")).Click();

        cut.Markup.Should().Contain("Review and place");
        cut.Markup.Should().Contain("SKU-1");
        cut.Markup.Should().Contain("1 Main St");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Place order")
            .HasAttribute("disabled").Should().BeFalse();

        var commands = stubApiClient.CapturedCommands.Select(c => c.Command).ToList();
        var orderIds = new[]
        {
            commands.OfType<DraftOrder>().Single().OrderId,
            commands.OfType<AddOrderLine>().Single().OrderId,
            commands.OfType<SetOrderShippingAddress>().Single().OrderId,
        };
        orderIds.Should().AllBeEquivalentTo(orderIds[0]);
    }

    private static void AdvanceToLineItems(IRenderedComponent<OrderCreatePage> cut)
    {
        cut.Find("#customerIdInput").Input(Guid.NewGuid().ToString());
        cut.Find("button").Click();
    }

    private static void AddLine(IRenderedComponent<OrderCreatePage> cut, string sku, string quantity, string unitPrice)
    {
        cut.Find("#skuInput").Change(sku);
        cut.Find("#quantityInput").Change(quantity);
        cut.Find("#unitPriceInput").Change(unitPrice);
        cut.FindAll("button").Single(b => b.TextContent.Trim().StartsWith("Add")).Click();
    }
}
