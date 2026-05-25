using Bunit;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Hosts.Web;
using EventSourcingCqrs.Hosts.Web.Components;
using EventSourcingCqrs.Hosts.Web.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Interactions;

public class CancelOrderInteractionTests : BunitContext
{
    private readonly StubApiClient stubApiClient = new();

    public CancelOrderInteractionTests()
    {
        Services.AddSingleton<IApiClient>(stubApiClient);
    }

    [Fact]
    public void Clicking_cancel_dispatches_CancelOrder_with_route_OrderId()
    {
        var orderId = Guid.NewGuid();
        stubApiClient.EnqueueCommandResult(typeof(CancelOrder),
            new CommandAcceptedResponse(DateTime.UtcNow));

        var cut = Render<CancelOrderButton>(p => p
            .Add(x => x.OrderId, orderId)
            .Add(x => x.OnDispatched, () => Task.CompletedTask));

        cut.Find("button").Click();

        var captured = stubApiClient.CapturedCommands.Should().ContainSingle().Subject;
        captured.Command.Should().BeOfType<CancelOrder>()
            .Which.OrderId.Should().Be(orderId);
    }

    [Fact]
    public void Clicking_cancel_passes_idempotency_key()
    {
        stubApiClient.EnqueueCommandResult(typeof(CancelOrder),
            new CommandAcceptedResponse(DateTime.UtcNow));

        var cut = Render<CancelOrderButton>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.OnDispatched, () => Task.CompletedTask));

        cut.Find("button").Click();

        stubApiClient.CapturedCommands.Should().ContainSingle()
            .Which.IdempotencyKey.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Clicking_cancel_transitions_to_pending_state()
    {
        stubApiClient.EnqueueCommandResult(typeof(CancelOrder),
            new CommandAcceptedResponse(DateTime.UtcNow));

        var cut = Render<CancelOrderButton>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.OnDispatched, () => Task.CompletedTask));

        cut.Find("button").Click();

        cut.Markup.Should().Contain("Cancelling...");
    }

    [Fact]
    public void Clicking_cancel_invokes_OnDispatched_callback_on_success()
    {
        stubApiClient.EnqueueCommandResult(typeof(CancelOrder),
            new CommandAcceptedResponse(DateTime.UtcNow));

        var callbackFired = false;
        var cut = Render<CancelOrderButton>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.OnDispatched, () =>
            {
                callbackFired = true;
                return Task.CompletedTask;
            }));

        cut.Find("button").Click();

        callbackFired.Should().BeTrue();
    }

    [Fact]
    public void Validation_failure_surfaces_validation_message()
    {
        stubApiClient.SeedCommandFailure<CancelOrder>(new ApiValidationException(
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["Reason"] = new[] { "Reason is required" },
            }));

        var cut = Render<CancelOrderButton>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.OnDispatched, () => Task.CompletedTask));

        cut.Find("button").Click();

        cut.Markup.Should().Contain("Cancel failed");
        cut.Markup.Should().Contain("Reason is required");
    }

    [Fact]
    public void BusinessRule_failure_surfaces_business_message()
    {
        stubApiClient.SeedCommandFailure<CancelOrder>(new ApiBusinessRuleException(
            "ORDER_ALREADY_SHIPPED", "The order has already shipped and cannot be cancelled."));

        var cut = Render<CancelOrderButton>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.OnDispatched, () => Task.CompletedTask));

        cut.Find("button").Click();

        cut.Markup.Should().Contain("Cancel failed");
        cut.Markup.Should().Contain("already shipped");
    }

    [Fact]
    public void Concurrency_failure_surfaces_refresh_message()
    {
        stubApiClient.SeedCommandFailure<CancelOrder>(new ApiConcurrencyException(5));

        var cut = Render<CancelOrderButton>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.OnDispatched, () => Task.CompletedTask));

        cut.Find("button").Click();

        cut.Markup.Should().Contain("Cancel failed");
        cut.Markup.Should().Contain("Refresh");
    }

    [Fact]
    public void Infrastructure_failure_surfaces_generic_message()
    {
        stubApiClient.SeedCommandFailure<CancelOrder>(
            new ApiInfrastructureException("Connection refused", statusCode: 500));

        var cut = Render<CancelOrderButton>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.OnDispatched, () => Task.CompletedTask));

        cut.Find("button").Click();

        cut.Markup.Should().Contain("Cancel failed");
        cut.Markup.Should().Contain("Something went wrong");
    }
}
