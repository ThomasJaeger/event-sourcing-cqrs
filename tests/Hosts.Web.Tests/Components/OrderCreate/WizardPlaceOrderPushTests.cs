using Bunit;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Hosts.Web;
using EventSourcingCqrs.Hosts.Web.Hubs;
using EventSourcingCqrs.Hosts.Web.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using OrderCreatePage = EventSourcingCqrs.Hosts.Web.Components.Pages.OrderCreate;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components.OrderCreate;

// The push retrofit of the wizard's PlaceOrder dispatch (P11.5, ADR 0032). Replaces the deleted
// WizardPlaceOrderPollingTests: instead of a page-owned polling loop, the page arms the in-process circuit
// subscription on PlaceOrder, watches for the Draft-to-Placed flip on a pushed read model, and on the
// terminal match settles the badge, disposes the subscription, then navigates to the order-detail page.
// Each push-driven case leads with the arm (StartCallCount) as its discriminator, so these stay red until
// GREEN wires the page to arm the injected subscription and arm a degraded-settlement timer. The four
// dispatch-failure cases assert the page does NOT arm on a failed dispatch, which is already true today
// (green on write). The OrderCreatePage alias avoids the page class colliding with this test namespace's
// last segment.
public sealed class WizardPlaceOrderPushTests : BunitContext
{
    private static readonly DateTimeOffset BaseTime = new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly StubApiClient stubApiClient = new();
    private readonly FakeTimeProvider fakeTime = new(BaseTime);
    private readonly StubCircuitResourceSubscription subscription = new();

    public WizardPlaceOrderPushTests()
    {
        Services.AddSingleton<IApiClient>(stubApiClient);
        Services.AddSingleton<TimeProvider>(fakeTime);
        Services.AddSingleton<ICircuitResourceSubscription>(subscription);
    }

    // Behavioral: clicking Place flips the badge to Pending optimistically and arms the subscription under
    // the order's default-form id (the "D" form OrderDetailProjection emits), the resource the page watches
    // for the Placed flip.
    [Fact]
    public void Clicking_place_transitions_the_badge_to_pending_and_arms_the_subscription()
    {
        var cut = RenderAtReview();
        stubApiClient.EnqueueCommandResult(typeof(PlaceOrder), Accepted());

        cut.Find("#placeOrderButton").Click();

        cut.Markup.Should().Contain("Placing order...");
        subscription.StartCallCount.Should().Be(1);
        subscription.LastResourceType.Should().Be(SubscriptionResourceType.Order);
        subscription.LastResourceId.Should().Be(WizardOrderId().ToString());
        subscription.LastResourceId.Should().NotBe(WizardOrderId().ToString("N"));
    }

    // Behavioral: a pushed read model showing Status == Placed settles the badge, disposes the subscription,
    // then navigates to /orders/{orderId}. The dispose is recorded before the navigation: closing the
    // delivery path at the source is the primary defense, navigation the terminal action.
    [Fact]
    public async Task A_placed_push_settles_disposes_then_navigates_to_the_order_detail_page()
    {
        var cut = RenderAtReview();
        stubApiClient.EnqueueCommandResult(typeof(PlaceOrder), Accepted());

        cut.Find("#placeOrderButton").Click();
        subscription.StartCallCount.Should().Be(1);

        var orderId = WizardOrderId();
        bool? disposedAtNavigation = null;
        var nav = Nav();
        nav.LocationChanged += (_, _) => disposedAtNavigation ??= subscription.Disposed;

        await subscription.DeliverStateAsync(SampleDetail(orderId, OrderStatus.Placed));

        cut.Markup.Should().Contain("Order placed.");
        subscription.Disposed.Should().BeTrue();
        nav.Uri.Should().EndWith($"/orders/{orderId}");
        disposedAtNavigation.Should().BeTrue("the page settles and disposes the subscription before navigating");
    }

    // Behavioral: armed, no settling push arrives, and after the 30s window the degraded timer surfaces the
    // "taking longer than expected" failed badge from a live armed subscription, not a polling loop.
    [Fact]
    public async Task Deadline_with_no_settling_push_shows_the_degraded_failed_badge()
    {
        var cut = RenderAtReview();
        stubApiClient.EnqueueCommandResult(typeof(PlaceOrder), Accepted());

        cut.Find("#placeOrderButton").Click();
        subscription.StartCallCount.Should().Be(1);

        await cut.InvokeAsync(() => fakeTime.Advance(TimeSpan.FromSeconds(31)));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("taking longer than expected"));
        Nav().Uri.Should().NotContain("/orders/");
    }

    // Behavioral: disposing the page after arming but before any settling push disposes the subscription and
    // does not navigate (navigating away mid-wait must not redirect to the order).
    [Fact]
    public async Task Disposing_mid_wait_disposes_the_subscription_and_does_not_navigate()
    {
        var cut = RenderAtReview();
        stubApiClient.EnqueueCommandResult(typeof(PlaceOrder), Accepted());

        cut.Find("#placeOrderButton").Click();
        subscription.StartCallCount.Should().Be(1);

        await DisposeComponentsAsync();

        subscription.Disposed.Should().BeTrue();
        Nav().Uri.Should().NotContain("/orders/");
    }

    // Invariant (already-terminal backstop): after the terminal Placed push has settled, disposed, and
    // navigated, a second Placed-or-later push (an in-flight delivery) does not navigate again and does not
    // throw on the navigating-away page.
    [Fact]
    public async Task A_second_terminal_push_after_navigation_does_not_navigate_again_and_does_not_throw()
    {
        var cut = RenderAtReview();
        stubApiClient.EnqueueCommandResult(typeof(PlaceOrder), Accepted());

        cut.Find("#placeOrderButton").Click();
        subscription.StartCallCount.Should().Be(1);

        var orderId = WizardOrderId();
        var navigations = 0;
        Nav().LocationChanged += (_, _) => navigations++;

        await subscription.DeliverStateAsync(SampleDetail(orderId, OrderStatus.Placed));

        var secondPush = () => subscription.DeliverStateAsync(SampleDetail(orderId, OrderStatus.Shipped));
        await secondPush.Should().NotThrowAsync();

        navigations.Should().Be(1);
        Nav().Uri.Should().EndWith($"/orders/{orderId}");
    }

    // Invariant (dispose-from-within-own-apply): the terminal apply disposes the very subscription whose
    // marshalled delivery is invoking it. This must complete cleanly, navigate exactly once, and not throw
    // or deadlock.
    [Fact]
    public async Task The_terminal_apply_disposes_its_own_subscription_and_navigates_once_without_throwing()
    {
        var cut = RenderAtReview();
        stubApiClient.EnqueueCommandResult(typeof(PlaceOrder), Accepted());

        cut.Find("#placeOrderButton").Click();
        subscription.StartCallCount.Should().Be(1);

        var orderId = WizardOrderId();
        var navigations = 0;
        Nav().LocationChanged += (_, _) => navigations++;

        var deliverTerminal = () => subscription.DeliverStateAsync(SampleDetail(orderId, OrderStatus.Placed));
        await deliverTerminal.Should().NotThrowAsync();

        subscription.Disposed.Should().BeTrue();
        navigations.Should().Be(1);
        Nav().Uri.Should().EndWith($"/orders/{orderId}");
    }

    // Invariant (arm-throw): when StartAsync faults at the arm with a non-OperationCanceledException, the
    // click handler does not throw, the subscription is left in place (not disposed), and the degraded timer
    // still surfaces the failed badge at the deadline.
    [Fact]
    public async Task When_the_arm_throws_the_click_does_not_throw_the_subscription_stays_and_the_deadline_degrades()
    {
        var cut = RenderAtReview();
        stubApiClient.EnqueueCommandResult(typeof(PlaceOrder), Accepted());
        subscription.ThrowFromStart(new InvalidOperationException("dispatcher unavailable"));

        var click = () => cut.Find("#placeOrderButton").Click();
        click.Should().NotThrow();

        subscription.StartCallCount.Should().Be(1);
        subscription.Disposed.Should().BeFalse();

        await cut.InvokeAsync(() => fakeTime.Advance(TimeSpan.FromSeconds(31)));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("taking longer than expected"));
    }

    // Invariant (arm-cancel): pins the OperationCanceledException catch arm so a cancellation faulting
    // StartAsync cannot escape and fault the circuit. The click does not throw, the page stays on its
    // initial data with the subscription in place, and the degraded timer still surfaces the failed badge
    // at the deadline.
    [Fact]
    public async Task When_the_arm_is_canceled_the_click_does_not_throw_the_subscription_stays_and_the_deadline_degrades()
    {
        var cut = RenderAtReview();
        stubApiClient.EnqueueCommandResult(typeof(PlaceOrder), Accepted());
        subscription.ThrowFromStart(new OperationCanceledException());

        var click = () => cut.Find("#placeOrderButton").Click();
        click.Should().NotThrow();

        subscription.StartCallCount.Should().Be(1);
        subscription.Disposed.Should().BeFalse();

        await cut.InvokeAsync(() => fakeTime.Advance(TimeSpan.FromSeconds(31)));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("taking longer than expected"));
    }

    // P11.9 (green on write): the OCE arm is teardown, not failure; it must not surface the NotLive badge.
    [Fact]
    public void When_the_arm_is_canceled_the_not_live_badge_does_not_surface()
    {
        var cut = RenderAtReview();
        stubApiClient.EnqueueCommandResult(typeof(PlaceOrder), Accepted());
        subscription.ThrowFromStart(new OperationCanceledException());

        cut.Find("#placeOrderButton").Click();

        cut.Markup.Should().NotContain("Live updates are unavailable");
    }

    // P11.9 (green on write): before any place dispatch the liveness state is Idle and renders no badge.
    [Fact]
    public void Before_place_dispatch_no_liveness_badge_renders()
    {
        var cut = RenderAtReview();

        cut.FindAll("#liveBadge").Should().BeEmpty();
    }

    // P11.9: a successful arm after Place surfaces the Live badge near the place controls.
    [Fact]
    public void A_successful_arm_after_place_surfaces_the_live_badge()
    {
        var cut = RenderAtReview();
        stubApiClient.EnqueueCommandResult(typeof(PlaceOrder), Accepted());

        cut.Find("#placeOrderButton").Click();

        cut.Find("#liveBadge").TextContent.Trim().Should().Be("Live");
    }

    // P11.9: when the arm throws, the degraded timer stays the dispatch-outcome authority and the NotLive
    // badge surfaces the liveness loss, pointing the user at the orders list.
    [Fact]
    public void When_the_arm_throws_the_not_live_badge_surfaces()
    {
        var cut = RenderAtReview();
        stubApiClient.EnqueueCommandResult(typeof(PlaceOrder), Accepted());
        subscription.ThrowFromStart(new InvalidOperationException("dispatcher unavailable"));

        cut.Find("#placeOrderButton").Click();

        cut.Find("#liveBadge").TextContent.Should().Contain("Live updates are unavailable");
    }

    // Does-not-subscribe (green on write): a failed dispatch renders its message and never arms the
    // subscription. The current page already never arms; GREEN arms only after an accepted dispatch.
    [Fact]
    public void Place_dispatch_validation_failure_renders_the_message_and_does_not_subscribe()
    {
        var cut = RenderAtReview();
        stubApiClient.SeedCommandFailure<PlaceOrder>(new ApiValidationException(
            new Dictionary<string, IReadOnlyList<string>> { ["Order"] = new[] { "Order is invalid" } }));

        cut.Find("#placeOrderButton").Click();

        cut.Markup.Should().Contain("Order is invalid");
        cut.Markup.Should().Contain("bg-red-100");
        subscription.StartCallCount.Should().Be(0);
    }

    [Fact]
    public void Place_dispatch_business_rule_failure_renders_the_message_and_does_not_subscribe()
    {
        var cut = RenderAtReview();
        stubApiClient.SeedCommandFailure<PlaceOrder>(new ApiBusinessRuleException(
            "RULE", "The order cannot be placed."));

        cut.Find("#placeOrderButton").Click();

        cut.Markup.Should().Contain("The order cannot be placed.");
        subscription.StartCallCount.Should().Be(0);
    }

    [Fact]
    public void Place_dispatch_concurrency_failure_renders_the_message_and_does_not_subscribe()
    {
        var cut = RenderAtReview();
        stubApiClient.SeedCommandFailure<PlaceOrder>(new ApiConcurrencyException(0));

        cut.Find("#placeOrderButton").Click();

        cut.Markup.Should().Contain("The order changed while you were placing it");
        subscription.StartCallCount.Should().Be(0);
    }

    [Fact]
    public void Place_dispatch_infrastructure_failure_renders_the_message_and_does_not_subscribe()
    {
        var cut = RenderAtReview();
        stubApiClient.SeedCommandFailure<PlaceOrder>(
            new ApiInfrastructureException("connection refused", statusCode: 500));

        cut.Find("#placeOrderButton").Click();

        cut.Markup.Should().Contain("could not be placed");
        cut.Markup.Should().Contain("connection refused");
        subscription.StartCallCount.Should().Be(0);
    }

    // Navigates the wizard through steps 1-3 and returns it parked on the review step with the Place button
    // enabled. Seeds the three step dispatches the navigation performs.
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

    private Guid WizardOrderId() =>
        stubApiClient.CapturedCommands.Select(c => c.Command).OfType<DraftOrder>().Single().OrderId;

    private NavigationManager Nav() => Services.GetRequiredService<NavigationManager>();

    private static CommandAcceptedResponse Accepted() => new(BaseTime.UtcDateTime);

    private static OrderDetailView SampleDetail(Guid orderId, OrderStatus status) =>
        new(
            new OrderDetailRow(
                OrderId: orderId,
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
