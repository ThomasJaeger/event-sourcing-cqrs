using Bunit;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Queries.Fulfillment;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using EventSourcingCqrs.Hosts.Web;
using EventSourcingCqrs.Hosts.Web.Components.Pages;
using EventSourcingCqrs.Hosts.Web.Hubs;
using EventSourcingCqrs.Hosts.Web.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components.Inventory;

// The dashboard's CreateInventory dispatch under the in-process notification
// subscription that replaced the page-owned polling loop (ADR 0032, ADR 0033).
// The page arms one collection-scoped inventory subscription on first interactive
// render and re-queries the whole list on any notification; the create badge
// settles when a re-queried list includes the new SKU. The page-level
// subscription behavior (arm, re-query, arm failure, dispose) is exercised here.
public sealed class InventoryDashboardCreatePushTests : BunitContext
{
    private static readonly DateTimeOffset BaseTime = new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly StubApiClient stubApiClient = new();
    private readonly FakeTimeProvider fakeTime = new(BaseTime);
    private readonly StubCircuitResourceSubscription subscription = new();

    public InventoryDashboardCreatePushTests()
    {
        Services.AddSingleton<IApiClient>(stubApiClient);
        Services.AddSingleton<TimeProvider>(fakeTime);
        Services.AddSingleton<ICircuitResourceSubscription>(subscription);
    }

    [Fact]
    public void Clicking_create_opens_the_dialog()
    {
        var cut = RenderDashboard();
        cut.Find("#createInventoryButton").Click();
        cut.FindAll("#skuInput").Should().ContainSingle();
    }

    [Fact]
    public void Submitting_the_dialog_dispatches_CreateInventory_with_the_sku()
    {
        stubApiClient.EnqueueCommandResult(typeof(CreateInventory), Accepted());
        var cut = RenderDashboard();
        OpenAndSubmit(cut, "SKU-1");
        var command = stubApiClient.CapturedCommands.Select(c => c.Command)
            .OfType<CreateInventory>().Should().ContainSingle().Subject;
        command.Sku.Should().Be("SKU-1");
    }

    [Fact]
    public void Dispatch_transitions_the_badge_to_pending()
    {
        stubApiClient.EnqueueCommandResult(typeof(CreateInventory), Accepted());
        var cut = RenderDashboard();
        OpenAndSubmit(cut, "SKU-1");
        cut.Markup.Should().Contain("Creating...");
    }

    [Fact]
    public void Page_arms_one_collection_scoped_inventory_subscription_on_first_interactive_render()
    {
        RenderDashboard();
        subscription.StartCallCount.Should().Be(1);
        subscription.LastResourceType.Should().Be(SubscriptionResourceType.Inventory);
        subscription.LastResourceId.Should().Be(CollectionResourceIds.AllInventory);
    }

    [Fact]
    public async Task A_pushed_notification_requeries_the_full_list_and_rerenders()
    {
        var cut = RenderDashboard();
        stubApiClient.EnqueueQueryResult<GetAllInventoryDashboard, IReadOnlyList<InventoryDashboardRow>>(
            new[] { Row("SKU-1") });
        var queriesAfterRender = stubApiClient.CapturedQueries.Count;
        await subscription.DeliverAsync();
        stubApiClient.CapturedQueries.Count.Should().Be(queriesAfterRender + 1);
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().ContainSingle());
    }

    [Fact]
    public async Task A_push_reflecting_the_new_sku_settles_the_create_badge_and_refreshes()
    {
        var cut = RenderDashboard();
        stubApiClient.EnqueueCommandResult(typeof(CreateInventory), Accepted());
        stubApiClient.EnqueueQueryResult<GetAllInventoryDashboard, IReadOnlyList<InventoryDashboardRow>>(
            new[] { Row("SKU-1") });
        OpenAndSubmit(cut, "SKU-1");
        await subscription.DeliverAsync();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Created");
            cut.FindAll("tbody tr").Should().ContainSingle();
        });
    }

    [Fact]
    public async Task No_settling_push_within_the_window_shows_the_degraded_badge()
    {
        stubApiClient.EnqueueCommandResult(typeof(CreateInventory), Accepted());
        stubApiClient.EnqueueQueryResult<GetInventoryDashboardBySku, InventoryDashboardRow?>(null);
        var cut = RenderDashboard();
        OpenAndSubmit(cut, "SKU-1");
        await cut.InvokeAsync(() => fakeTime.Advance(TimeSpan.FromSeconds(31)));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("taking longer than expected"));
    }

    [Fact]
    public void An_arm_failure_at_render_is_caught_and_leaves_the_page_on_its_initial_data()
    {
        subscription.ThrowFromStart(new ApiInfrastructureException("subscription arm failed", statusCode: 503));
        stubApiClient.EnqueueQueryResult<GetAllInventoryDashboard, IReadOnlyList<InventoryDashboardRow>>(
            new[] { Row("SKU-1") });
        var cut = Render<InventoryDashboard>();
        subscription.StartCallCount.Should().Be(1);
        subscription.Disposed.Should().BeFalse();
        cut.FindAll("tbody tr").Should().ContainSingle();
    }

    [Fact]
    public void A_cancellation_during_the_arm_is_caught_and_leaves_the_page_on_its_initial_data()
    {
        subscription.ThrowFromStart(new OperationCanceledException());
        stubApiClient.EnqueueQueryResult<GetAllInventoryDashboard, IReadOnlyList<InventoryDashboardRow>>(
            new[] { Row("SKU-1") });
        var cut = Render<InventoryDashboard>();
        subscription.StartCallCount.Should().Be(1);
        subscription.Disposed.Should().BeFalse();
        cut.FindAll("tbody tr").Should().ContainSingle();
    }

    // P11.9 (green on write): cancellation during the arm must not surface the NotLive badge.
    [Fact]
    public void A_cancellation_during_the_arm_does_not_surface_the_not_live_badge()
    {
        subscription.ThrowFromStart(new OperationCanceledException());
        stubApiClient.EnqueueQueryResult<GetAllInventoryDashboard, IReadOnlyList<InventoryDashboardRow>>(
            new[] { Row("SKU-1") });
        var cut = Render<InventoryDashboard>();

        cut.Markup.Should().NotContain("Live updates unavailable");
    }

    // P11.9: a successful arm at render surfaces the Live badge in the dashboard header.
    [Fact]
    public void A_successful_arm_at_render_surfaces_the_live_badge()
    {
        var cut = RenderDashboard();

        cut.Find("#liveBadge").TextContent.Trim().Should().Be("Live");
    }

    // P11.9: the arm failure that previously left the dashboard silently not live now surfaces the
    // NotLive badge.
    [Fact]
    public void An_arm_failure_at_render_surfaces_the_not_live_badge()
    {
        subscription.ThrowFromStart(new ApiInfrastructureException("subscription arm failed", statusCode: 503));
        stubApiClient.EnqueueQueryResult<GetAllInventoryDashboard, IReadOnlyList<InventoryDashboardRow>>(
            new[] { Row("SKU-1") });
        var cut = Render<InventoryDashboard>();

        cut.Find("#liveBadge").TextContent.Should().Contain("Live updates unavailable; reload to refresh.");
    }

    [Fact]
    public async Task Disposing_the_page_disposes_the_subscription()
    {
        RenderDashboard();
        await DisposeComponentsAsync();
        subscription.DisposeCallCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void Create_dispatch_validation_failure_renders_the_message_and_leaves_the_subscription_live()
    {
        stubApiClient.SeedCommandFailure<CreateInventory>(new ApiValidationException(
            new Dictionary<string, IReadOnlyList<string>> { ["Sku"] = new[] { "SKU is invalid" } }));
        var cut = RenderDashboard();
        OpenAndSubmit(cut, "SKU-1");
        cut.Markup.Should().Contain("SKU is invalid");
        subscription.DisposeCallCount.Should().Be(0);
    }

    [Fact]
    public void Create_dispatch_business_rule_failure_renders_the_message()
    {
        stubApiClient.SeedCommandFailure<CreateInventory>(new ApiBusinessRuleException(
            "RULE", "SKU must be non-empty."));
        var cut = RenderDashboard();
        OpenAndSubmit(cut, "SKU-1");
        cut.Markup.Should().Contain("SKU must be non-empty.");
        subscription.DisposeCallCount.Should().Be(0);
    }

    [Fact]
    public void Create_dispatch_concurrency_failure_renders_the_concurrency_message()
    {
        stubApiClient.SeedCommandFailure<CreateInventory>(new ApiConcurrencyException(0));
        var cut = RenderDashboard();
        OpenAndSubmit(cut, "SKU-1");
        cut.Markup.Should().Contain("already exists");
        subscription.DisposeCallCount.Should().Be(0);
    }

    [Fact]
    public void Create_dispatch_infrastructure_failure_renders_the_infrastructure_message()
    {
        stubApiClient.SeedCommandFailure<CreateInventory>(
            new ApiInfrastructureException("connection refused", statusCode: 500));
        var cut = RenderDashboard();
        OpenAndSubmit(cut, "SKU-1");
        cut.Markup.Should().Contain("Something went wrong");
        subscription.DisposeCallCount.Should().Be(0);
    }

    private IRenderedComponent<InventoryDashboard> RenderDashboard()
    {
        stubApiClient.EnqueueQueryResult<GetAllInventoryDashboard, IReadOnlyList<InventoryDashboardRow>>(
            Array.Empty<InventoryDashboardRow>());
        return Render<InventoryDashboard>();
    }

    private static void OpenAndSubmit(IRenderedComponent<InventoryDashboard> cut, string sku)
    {
        cut.Find("#createInventoryButton").Click();
        cut.Find("#skuInput").Input(sku);
        cut.Find("#dialogCreateButton").Click();
    }

    private static CommandAcceptedResponse Accepted() => new(BaseTime.UtcDateTime);

    private static InventoryDashboardRow Row(string sku, int onHand = 0, int reserved = 0)
        => new(
            InventoryId: Guid.NewGuid(),
            Sku: sku,
            OnHandQuantity: onHand,
            ReservedQuantity: reserved,
            LastUpdatedUtc: new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
}
