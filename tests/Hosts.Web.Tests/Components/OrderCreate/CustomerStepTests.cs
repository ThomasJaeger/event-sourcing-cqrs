using Bunit;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Hosts.Web;
using EventSourcingCqrs.Hosts.Web.Components.OrderCreate;
using EventSourcingCqrs.Hosts.Web.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components.OrderCreate;

// CustomerStep is the only fully-wired step in the scaffold: it dispatches
// DraftOrder, parses and validates the customer-id input, and surfaces the four
// failure categories the same way CancelOrderButton does. These tests pin that
// behavior; the disabled steps 2-4 are covered by their own render tests.
public sealed class CustomerStepTests : BunitContext
{
    private readonly StubApiClient stubApiClient = new();

    public CustomerStepTests()
    {
        Services.AddSingleton<IApiClient>(stubApiClient);
    }

    [Fact]
    public void Renders_the_customer_id_input_and_start_button()
    {
        var cut = Render<CustomerStep>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        cut.Find("#customerIdInput").Should().NotBeNull();
        cut.Markup.Should().Contain("Start order");
    }

    [Fact]
    public void Empty_input_keeps_the_button_disabled()
    {
        var cut = Render<CustomerStep>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        cut.Find("button").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Valid_guid_input_enables_the_button()
    {
        var cut = Render<CustomerStep>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        cut.Find("#customerIdInput").Input(Guid.NewGuid().ToString());

        cut.Find("button").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Submit_dispatches_DraftOrder_with_the_order_id_and_parsed_customer_id()
    {
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        stubApiClient.EnqueueCommandResult(typeof(DraftOrder), new CommandAcceptedResponse(DateTime.UtcNow));
        var cut = Render<CustomerStep>(p => p.Add(x => x.OrderId, orderId));

        cut.Find("#customerIdInput").Input(customerId.ToString());
        cut.Find("button").Click();

        var dispatched = stubApiClient.CapturedCommands.Should().ContainSingle().Subject.Command
            .Should().BeOfType<DraftOrder>().Subject;
        dispatched.OrderId.Should().Be(orderId);
        dispatched.CustomerId.Should().Be(customerId);
    }

    [Fact]
    public void Submit_raises_OnCustomerIdChanged_before_OnDispatched()
    {
        var calls = new List<string>();
        stubApiClient.EnqueueCommandResult(typeof(DraftOrder), new CommandAcceptedResponse(DateTime.UtcNow));
        var cut = Render<CustomerStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.OnCustomerIdChanged, (Guid _) => calls.Add("changed"))
            .Add(x => x.OnDispatched, () => calls.Add("dispatched")));

        cut.Find("#customerIdInput").Input(Guid.NewGuid().ToString());
        cut.Find("button").Click();

        calls.Should().Equal("changed", "dispatched");
    }

    [Fact]
    public void Invalid_guid_input_shows_a_validation_message_and_does_not_dispatch()
    {
        var cut = Render<CustomerStep>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        cut.Find("#customerIdInput").Input("not-a-guid");
        cut.Find("button").Click();

        cut.Markup.Should().Contain("must be a valid GUID");
        stubApiClient.CapturedCommands.Should().BeEmpty();
    }

    [Fact]
    public void Validation_failure_renders_the_failure_message()
    {
        stubApiClient.SeedCommandFailure<DraftOrder>(new ApiValidationException(
            new Dictionary<string, IReadOnlyList<string>> { ["CustomerId"] = new[] { "Customer is unknown" } }));
        var cut = Render<CustomerStep>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        cut.Find("#customerIdInput").Input(Guid.NewGuid().ToString());
        cut.Find("button").Click();

        cut.Markup.Should().Contain("Customer is unknown");
    }

    [Fact]
    public void Business_rule_failure_renders_the_failure_message()
    {
        stubApiClient.SeedCommandFailure<DraftOrder>(new ApiBusinessRuleException(
            "RULE", "Drafting is closed for this customer."));
        var cut = Render<CustomerStep>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        cut.Find("#customerIdInput").Input(Guid.NewGuid().ToString());
        cut.Find("button").Click();

        cut.Markup.Should().Contain("Drafting is closed");
    }

    [Fact]
    public void Concurrency_failure_renders_the_already_exists_message()
    {
        stubApiClient.SeedCommandFailure<DraftOrder>(new ApiConcurrencyException(0));
        var cut = Render<CustomerStep>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        cut.Find("#customerIdInput").Input(Guid.NewGuid().ToString());
        cut.Find("button").Click();

        cut.Markup.Should().Contain("already exists");
    }

    [Fact]
    public void Infrastructure_failure_renders_the_infrastructure_message()
    {
        stubApiClient.SeedCommandFailure<DraftOrder>(
            new ApiInfrastructureException("connection refused", statusCode: 500));
        var cut = Render<CustomerStep>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        cut.Find("#customerIdInput").Input(Guid.NewGuid().ToString());
        cut.Find("button").Click();

        cut.Markup.Should().Contain("could not be saved");
        cut.Markup.Should().Contain("connection refused");
    }
}
