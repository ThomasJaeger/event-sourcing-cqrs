using Bunit;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Queries.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using EventSourcingCqrs.Hosts.Web;
using EventSourcingCqrs.Hosts.Web.Components.Pages;
using EventSourcingCqrs.Hosts.Web.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components.Inventory;

// The dashboard's AdjustInventory dispatch and its page-owned polling loop. Each
// test renders the dashboard with one row, opens the per-row adjust dialog, submits
// a signed delta and reason, and drives the FakeTimeProvider to exercise the
// optimistic badge, the settle-on-on-hand-match-and-refresh, the deadline, and the
// four dispatch-failure arms.
public sealed class InventoryDashboardAdjustPollingTests : BunitContext
{
    private static readonly DateTimeOffset BaseTime = new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly StubApiClient stubApiClient = new();
    private readonly FakeTimeProvider fakeTime = new(BaseTime);

    public InventoryDashboardAdjustPollingTests()
    {
        Services.AddSingleton<IApiClient>(stubApiClient);
        Services.AddSingleton<TimeProvider>(fakeTime);
    }

    [Fact]
    public void Clicking_adjust_opens_the_dialog_with_the_current_on_hand()
    {
        var cut = RenderDashboardWith(Row("SKU-1", onHand: 12));

        cut.Find(".adjust-button").Click();

        cut.Find("dd.font-semibold").TextContent.Trim().Should().Be("12");
    }

    [Fact]
    public void Submitting_dispatches_AdjustInventory_with_the_inventory_id_delta_and_reason()
    {
        var inventoryId = Guid.NewGuid();
        stubApiClient.EnqueueCommandResult(typeof(AdjustInventory), Accepted());
        var cut = RenderDashboardWith(Row("SKU-1", onHand: 12, inventoryId: inventoryId));

        OpenAndSubmit(cut, delta: "5", reason: "restock");

        var command = stubApiClient.CapturedCommands.Select(c => c.Command)
            .OfType<AdjustInventory>().Should().ContainSingle().Subject;
        command.InventoryId.Should().Be(inventoryId);
        command.QuantityDelta.Should().Be(5);
        command.Reason.Should().Be("restock");
    }

    [Fact]
    public void Dispatch_transitions_the_badge_to_pending()
    {
        stubApiClient.EnqueueCommandResult(typeof(AdjustInventory), Accepted());
        var cut = RenderDashboardWith(Row("SKU-1", onHand: 12));

        OpenAndSubmit(cut, delta: "5", reason: "restock");

        cut.Markup.Should().Contain("Adjusting...");
    }

    [Fact]
    public async Task Polling_settles_when_onhand_matches_the_expected_value_and_refreshes()
    {
        var cut = RenderDashboardWith(Row("SKU-1", onHand: 12));
        stubApiClient.EnqueueCommandResult(typeof(AdjustInventory), Accepted());
        stubApiClient.EnqueueQueryResult<GetInventoryDashboardBySku, InventoryDashboardRow?>(Row("SKU-1", onHand: 17));
        stubApiClient.EnqueueQueryResult<GetAllInventoryDashboard, IReadOnlyList<InventoryDashboardRow>>(
            new[] { Row("SKU-1", onHand: 17) });

        OpenAndSubmit(cut, delta: "5", reason: "restock");
        await PollOnce(cut);

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Adjusted");
            cut.FindAll("tbody tr td")[1].TextContent.Trim().Should().Be("17");
        });
    }

    [Fact]
    public async Task Polling_times_out_at_the_deadline_with_a_failed_badge()
    {
        var cut = RenderDashboardWith(Row("SKU-1", onHand: 12));
        stubApiClient.EnqueueCommandResult(typeof(AdjustInventory), Accepted());
        // On-hand never reaches the snapshot 12 plus the delta 5 (= 17).
        stubApiClient.EnqueueQueryResult<GetInventoryDashboardBySku, InventoryDashboardRow?>(Row("SKU-1", onHand: 12));

        OpenAndSubmit(cut, delta: "5", reason: "restock");
        await cut.InvokeAsync(() => fakeTime.Advance(TimeSpan.FromSeconds(31)));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("taking longer than expected"));
    }

    [Fact]
    public void Adjust_dispatch_validation_failure_renders_the_message_and_does_not_poll()
    {
        stubApiClient.SeedCommandFailure<AdjustInventory>(new ApiValidationException(
            new Dictionary<string, IReadOnlyList<string>> { ["Reason"] = new[] { "Reason is required" } }));
        var cut = RenderDashboardWith(Row("SKU-1", onHand: 12));

        OpenAndSubmit(cut, delta: "5", reason: "restock");

        cut.Markup.Should().Contain("Reason is required");
        stubApiClient.CapturedQueries.OfType<GetInventoryDashboardBySku>().Should().BeEmpty();
    }

    [Fact]
    public void Adjust_dispatch_business_rule_failure_renders_the_message()
    {
        stubApiClient.SeedCommandFailure<AdjustInventory>(new ApiBusinessRuleException(
            "RULE", "would make available stock negative."));
        var cut = RenderDashboardWith(Row("SKU-1", onHand: 12));

        OpenAndSubmit(cut, delta: "-99", reason: "shrinkage");

        cut.Markup.Should().Contain("would make available stock negative.");
        stubApiClient.CapturedQueries.OfType<GetInventoryDashboardBySku>().Should().BeEmpty();
    }

    [Fact]
    public void Adjust_dispatch_concurrency_failure_renders_the_concurrency_message()
    {
        stubApiClient.SeedCommandFailure<AdjustInventory>(new ApiConcurrencyException(0));
        var cut = RenderDashboardWith(Row("SKU-1", onHand: 12));

        OpenAndSubmit(cut, delta: "5", reason: "restock");

        cut.Markup.Should().Contain("changed while you were adjusting");
        stubApiClient.CapturedQueries.OfType<GetInventoryDashboardBySku>().Should().BeEmpty();
    }

    [Fact]
    public void Adjust_dispatch_infrastructure_failure_renders_the_infrastructure_message()
    {
        stubApiClient.SeedCommandFailure<AdjustInventory>(
            new ApiInfrastructureException("connection refused", statusCode: 500));
        var cut = RenderDashboardWith(Row("SKU-1", onHand: 12));

        OpenAndSubmit(cut, delta: "5", reason: "restock");

        cut.Markup.Should().Contain("Something went wrong");
        stubApiClient.CapturedQueries.OfType<GetInventoryDashboardBySku>().Should().BeEmpty();
    }

    private IRenderedComponent<InventoryDashboard> RenderDashboardWith(InventoryDashboardRow row)
    {
        stubApiClient.EnqueueQueryResult<GetAllInventoryDashboard, IReadOnlyList<InventoryDashboardRow>>(
            new[] { row });
        return Render<InventoryDashboard>();
    }

    private static void OpenAndSubmit(IRenderedComponent<InventoryDashboard> cut, string delta, string reason)
    {
        cut.Find(".adjust-button").Click();
        cut.Find("#deltaInput").Input(delta);
        cut.Find("#reasonInput").Input(reason);
        cut.Find("#dialogAdjustButton").Click();
    }

    private async Task PollOnce(IRenderedComponent<InventoryDashboard> cut)
    {
        var before = stubApiClient.CapturedQueries.Count;
        await cut.InvokeAsync(() => fakeTime.Advance(TimeSpan.FromSeconds(1)));
        cut.WaitForState(() => stubApiClient.CapturedQueries.Count > before);
    }

    private static CommandAcceptedResponse Accepted() => new(BaseTime.UtcDateTime);

    private static InventoryDashboardRow Row(string sku, int onHand = 0, int reserved = 0, Guid? inventoryId = null)
        => new(
            InventoryId: inventoryId ?? Guid.NewGuid(),
            Sku: sku,
            OnHandQuantity: onHand,
            ReservedQuantity: reserved,
            LastUpdatedUtc: new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
}
