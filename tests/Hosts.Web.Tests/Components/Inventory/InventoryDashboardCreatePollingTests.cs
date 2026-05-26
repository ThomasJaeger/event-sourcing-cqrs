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

// The dashboard's CreateInventory dispatch and its page-owned polling loop. Each
// test renders the dashboard, opens the dialog, submits a SKU, and drives the
// FakeTimeProvider to exercise the optimistic badge, the settle-and-refresh, the
// deadline, and the four dispatch-failure arms.
public sealed class InventoryDashboardCreatePollingTests : BunitContext
{
    private static readonly DateTimeOffset BaseTime = new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly StubApiClient stubApiClient = new();
    private readonly FakeTimeProvider fakeTime = new(BaseTime);

    public InventoryDashboardCreatePollingTests()
    {
        Services.AddSingleton<IApiClient>(stubApiClient);
        Services.AddSingleton<TimeProvider>(fakeTime);
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
    public async Task Polling_settles_when_the_new_sku_row_appears_and_refreshes_the_table()
    {
        // Render first so the initial load consumes the empty list RenderDashboard
        // enqueues; the refresh result below is then second in the GetAll queue.
        var cut = RenderDashboard();
        stubApiClient.EnqueueCommandResult(typeof(CreateInventory), Accepted());
        stubApiClient.EnqueueQueryResult<GetInventoryDashboardBySku, InventoryDashboardRow?>(Row("SKU-1"));
        stubApiClient.EnqueueQueryResult<GetAllInventoryDashboard, IReadOnlyList<InventoryDashboardRow>>(
            new[] { Row("SKU-1") });

        OpenAndSubmit(cut, "SKU-1");
        await PollOnce(cut);

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("tbody tr").Should().ContainSingle();
            cut.Markup.Should().Contain("Created");
        });
    }

    [Fact]
    public async Task Polling_times_out_at_the_deadline_with_a_failed_badge()
    {
        stubApiClient.EnqueueCommandResult(typeof(CreateInventory), Accepted());
        stubApiClient.EnqueueQueryResult<GetInventoryDashboardBySku, InventoryDashboardRow?>(null);
        var cut = RenderDashboard();

        OpenAndSubmit(cut, "SKU-1");
        await cut.InvokeAsync(() => fakeTime.Advance(TimeSpan.FromSeconds(31)));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("taking longer than expected"));
    }

    [Fact]
    public void Create_dispatch_validation_failure_renders_the_message_and_does_not_poll()
    {
        stubApiClient.SeedCommandFailure<CreateInventory>(new ApiValidationException(
            new Dictionary<string, IReadOnlyList<string>> { ["Sku"] = new[] { "SKU is invalid" } }));
        var cut = RenderDashboard();

        OpenAndSubmit(cut, "SKU-1");

        cut.Markup.Should().Contain("SKU is invalid");
        stubApiClient.CapturedQueries.OfType<GetInventoryDashboardBySku>().Should().BeEmpty();
    }

    [Fact]
    public void Create_dispatch_business_rule_failure_renders_the_message()
    {
        stubApiClient.SeedCommandFailure<CreateInventory>(new ApiBusinessRuleException(
            "RULE", "SKU must be non-empty."));
        var cut = RenderDashboard();

        OpenAndSubmit(cut, "SKU-1");

        cut.Markup.Should().Contain("SKU must be non-empty.");
        stubApiClient.CapturedQueries.OfType<GetInventoryDashboardBySku>().Should().BeEmpty();
    }

    [Fact]
    public void Create_dispatch_concurrency_failure_renders_the_concurrency_message()
    {
        stubApiClient.SeedCommandFailure<CreateInventory>(new ApiConcurrencyException(0));
        var cut = RenderDashboard();

        OpenAndSubmit(cut, "SKU-1");

        cut.Markup.Should().Contain("already exists");
        stubApiClient.CapturedQueries.OfType<GetInventoryDashboardBySku>().Should().BeEmpty();
    }

    [Fact]
    public void Create_dispatch_infrastructure_failure_renders_the_infrastructure_message()
    {
        stubApiClient.SeedCommandFailure<CreateInventory>(
            new ApiInfrastructureException("connection refused", statusCode: 500));
        var cut = RenderDashboard();

        OpenAndSubmit(cut, "SKU-1");

        cut.Markup.Should().Contain("Something went wrong");
        stubApiClient.CapturedQueries.OfType<GetInventoryDashboardBySku>().Should().BeEmpty();
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

    // Advances fake time by one second and waits for the resulting poll query to
    // run, so the loop iteration completes before the assertion.
    private async Task PollOnce(IRenderedComponent<InventoryDashboard> cut)
    {
        var before = stubApiClient.CapturedQueries.Count;
        await cut.InvokeAsync(() => fakeTime.Advance(TimeSpan.FromSeconds(1)));
        cut.WaitForState(() => stubApiClient.CapturedQueries.Count > before);
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
