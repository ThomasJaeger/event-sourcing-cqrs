using Bunit;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Hosts.Web;
using EventSourcingCqrs.Hosts.Web.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using OrderCreatePage = EventSourcingCqrs.Hosts.Web.Components.Pages.OrderCreate;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components.OrderCreate;

// The wizard's PlaceOrder dispatch and its page-owned polling loop, the surface
// that discharges PLAN done-when #1. Each test navigates the wizard through steps
// 1-3 (seeding the three step dispatches), reaches the review step, clicks Place,
// and drives the FakeTimeProvider to exercise the optimistic badge, the settle-and-
// redirect, the deadline, the dispose-mid-poll, the four dispatch-failure arms, and
// the transient-read-failure retry cap. The OrderCreatePage alias avoids the page
// class colliding with this test namespace's last segment.
public sealed class WizardPlaceOrderPollingTests : BunitContext
{
    private static readonly DateTimeOffset BaseTime = new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly StubApiClient stubApiClient = new();
    private readonly FakeTimeProvider fakeTime = new(BaseTime);

    public WizardPlaceOrderPollingTests()
    {
        Services.AddSingleton<IApiClient>(stubApiClient);
        Services.AddSingleton<TimeProvider>(fakeTime);
    }

    [Fact]
    public void Clicking_place_transitions_the_badge_to_pending()
    {
        var cut = RenderAtReview();
        stubApiClient.EnqueueCommandResult(typeof(PlaceOrder), Accepted());

        cut.Find("#placeOrderButton").Click();

        cut.Markup.Should().Contain("Placing order...");
    }

    [Fact]
    public async Task Polling_settles_on_the_status_flip_to_Placed_and_redirects_to_the_order_detail_page()
    {
        var cut = RenderAtReview();
        stubApiClient.EnqueueCommandResult(typeof(PlaceOrder), Accepted());
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(SampleDetail(OrderStatus.Placed));

        cut.Find("#placeOrderButton").Click();
        await PollOnce(cut);

        var orderId = WizardOrderId();
        cut.WaitForAssertion(() => Nav().Uri.Should().EndWith($"/orders/{orderId}"));
    }

    [Fact]
    public async Task Polling_times_out_at_the_deadline_with_a_failed_badge()
    {
        var cut = RenderAtReview();
        stubApiClient.EnqueueCommandResult(typeof(PlaceOrder), Accepted());
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(SampleDetail(OrderStatus.Draft));

        cut.Find("#placeOrderButton").Click();
        await cut.InvokeAsync(() => fakeTime.Advance(TimeSpan.FromSeconds(31)));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("taking longer than expected"));
        Nav().Uri.Should().NotContain("/orders/");
    }

    [Fact]
    public async Task Polling_stops_and_does_not_redirect_when_the_page_is_disposed_mid_poll()
    {
        var cut = RenderAtReview();
        stubApiClient.EnqueueCommandResult(typeof(PlaceOrder), Accepted());

        cut.Find("#placeOrderButton").Click();
        // Parked on the first delay, no query issued yet.
        stubApiClient.CapturedQueries.Should().BeEmpty();

        await DisposeComponentsAsync();

        stubApiClient.CapturedQueries.Should().BeEmpty();
        Nav().Uri.Should().NotContain("/orders/");
    }

    [Fact]
    public void Place_dispatch_validation_failure_renders_the_message_and_does_not_poll()
    {
        var cut = RenderAtReview();
        stubApiClient.SeedCommandFailure<PlaceOrder>(new ApiValidationException(
            new Dictionary<string, IReadOnlyList<string>> { ["Order"] = new[] { "Order is invalid" } }));

        cut.Find("#placeOrderButton").Click();

        cut.Markup.Should().Contain("Order is invalid");
        cut.Markup.Should().Contain("bg-red-100");
        stubApiClient.CapturedQueries.Should().BeEmpty();
    }

    [Fact]
    public void Place_dispatch_business_rule_failure_renders_the_message()
    {
        var cut = RenderAtReview();
        stubApiClient.SeedCommandFailure<PlaceOrder>(new ApiBusinessRuleException(
            "RULE", "The order cannot be placed."));

        cut.Find("#placeOrderButton").Click();

        cut.Markup.Should().Contain("The order cannot be placed.");
        stubApiClient.CapturedQueries.Should().BeEmpty();
    }

    [Fact]
    public void Place_dispatch_concurrency_failure_renders_the_concurrency_message()
    {
        var cut = RenderAtReview();
        stubApiClient.SeedCommandFailure<PlaceOrder>(new ApiConcurrencyException(0));

        cut.Find("#placeOrderButton").Click();

        cut.Markup.Should().Contain("The order changed while you were placing it");
        stubApiClient.CapturedQueries.Should().BeEmpty();
    }

    [Fact]
    public void Place_dispatch_infrastructure_failure_renders_the_infrastructure_message()
    {
        var cut = RenderAtReview();
        stubApiClient.SeedCommandFailure<PlaceOrder>(
            new ApiInfrastructureException("connection refused", statusCode: 500));

        cut.Find("#placeOrderButton").Click();

        cut.Markup.Should().Contain("could not be placed");
        cut.Markup.Should().Contain("connection refused");
        stubApiClient.CapturedQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task Polling_tolerates_transient_read_failures_within_the_retry_cap_and_settles()
    {
        var cut = RenderAtReview();
        stubApiClient.EnqueueCommandResult(typeof(PlaceOrder), Accepted());
        for (var i = 0; i < 4; i++)
        {
            stubApiClient.SeedQueryFailure<GetOrderDetail>(new ApiInfrastructureException("transient", statusCode: 503));
        }
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(SampleDetail(OrderStatus.Placed));

        cut.Find("#placeOrderButton").Click();
        for (var i = 0; i < 5; i++)
        {
            await PollOnce(cut);
        }

        var orderId = WizardOrderId();
        cut.WaitForAssertion(() => Nav().Uri.Should().EndWith($"/orders/{orderId}"));
    }

    [Fact]
    public async Task Polling_gives_up_after_five_consecutive_transient_read_failures()
    {
        var cut = RenderAtReview();
        stubApiClient.EnqueueCommandResult(typeof(PlaceOrder), Accepted());
        for (var i = 0; i < 5; i++)
        {
            stubApiClient.SeedQueryFailure<GetOrderDetail>(new ApiInfrastructureException("transient", statusCode: 503));
        }

        cut.Find("#placeOrderButton").Click();
        for (var i = 0; i < 5; i++)
        {
            await PollOnce(cut);
        }

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("could not be retrieved"));
        Nav().Uri.Should().NotContain("/orders/");
    }

    // Navigates the wizard through steps 1-3 and returns it parked on the review
    // step with the Place button enabled. Seeds the three step dispatches the
    // navigation performs.
    private IRenderedComponent<OrderCreatePage> RenderAtReview()
    {
        stubApiClient.EnqueueCommandResult(typeof(DraftOrder), Accepted());
        stubApiClient.EnqueueCommandResult(typeof(AddOrderLine), Accepted());
        stubApiClient.EnqueueCommandResult(typeof(SetOrderShippingAddress), Accepted());

        var cut = Render<OrderCreatePage>();
        cut.Find("#customerIdInput").Input(Guid.NewGuid().ToString());
        cut.Find("button").Click();

        cut.Find("#skuInput").Change("SKU-1");
        cut.Find("#quantityInput").Change("1");
        cut.Find("#unitPriceInput").Change("10");
        cut.FindAll("button").Single(b => b.TextContent.Trim().StartsWith("Add")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Continue to shipping").Click();

        cut.Find("#streetInput").Change("1 Main St");
        cut.Find("#cityInput").Change("Springfield");
        cut.Find("#postalCodeInput").Change("12345");
        cut.Find("#countryInput").Change("USA");
        cut.FindAll("button").Single(b => b.TextContent.Trim().StartsWith("Sav")).Click();

        return cut;
    }

    // Advances fake time by one second and waits for the resulting poll query to
    // run, so each loop iteration completes before the next advance.
    private async Task PollOnce(IRenderedComponent<OrderCreatePage> cut)
    {
        var before = stubApiClient.CapturedQueries.Count;
        await cut.InvokeAsync(() => fakeTime.Advance(TimeSpan.FromSeconds(1)));
        cut.WaitForState(() => stubApiClient.CapturedQueries.Count > before);
    }

    private Guid WizardOrderId() =>
        stubApiClient.CapturedCommands.Select(c => c.Command).OfType<DraftOrder>().Single().OrderId;

    private NavigationManager Nav() => Services.GetRequiredService<NavigationManager>();

    private static CommandAcceptedResponse Accepted() => new(BaseTime.UtcDateTime);

    private static OrderDetailView SampleDetail(OrderStatus status) =>
        new(
            new OrderDetailRow(
                OrderId: Guid.NewGuid(),
                CustomerId: Guid.NewGuid(),
                Status: status,
                PlacedUtc: null,
                ShippedUtc: null,
                CancelledUtc: null,
                CompletedUtc: null,
                ReturnedUtc: null,
                Total: null,
                ShippingAddress: null,
                LastUpdatedUtc: new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)),
            [],
            []);
}
