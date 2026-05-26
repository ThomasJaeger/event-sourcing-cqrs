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

// The wizard page owns step navigation and the working OrderId; each step
// component owns its own dispatch. These tests cover the navigation the scaffold
// ships: step 1 (CustomerStep) dispatches DraftOrder and advances to step 2, Back
// returns, and the step indicator tracks the current step. Steps 3 and 4 are not
// reachable through the UI until Commit 25 enables the steps-2-and-3 Continue
// buttons, so assertions that depend on reaching them are deferred. The
// OrderCreatePage alias keeps the page class from colliding with this assembly's
// Components.OrderCreate test namespace.
public sealed class OrderCreatePageTests : BunitContext
{
    private readonly StubApiClient stubApiClient = new();

    public OrderCreatePageTests()
    {
        Services.AddSingleton<IApiClient>(stubApiClient);
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
        // Scaffold-level OrderId assertion: the dispatch carries the page's own
        // OrderId and the entered CustomerId. The step-1-to-step-4-review
        // stability comparison lands at Commit 26, when step 4 becomes reachable.
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
}
