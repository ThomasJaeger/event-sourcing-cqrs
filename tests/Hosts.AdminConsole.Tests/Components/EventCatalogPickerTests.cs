using AngleSharp.Dom;
using Bunit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.AdminConsole.Browser;
using EventSourcingCqrs.Hosts.AdminConsole.Components.Pages;
using EventSourcingCqrs.Hosts.AdminConsole.Tracer;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace EventSourcingCqrs.Hosts.AdminConsole.Tests.Components;

// Chapter 17: discovery uses the metadata catalog; selection still uses the existing detail seams.
public class EventCatalogPickerTests : BunitContext
{
    private const string FirstStream = "order:11111111111111111111111111111111";
    private const string SecondStream = "payment:22222222222222222222222222222222";
    private static readonly Guid FirstCorrelation = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondCorrelation = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime Occurred = new(2026, 10, 7, 12, 30, 0, DateTimeKind.Utc);
    private readonly CatalogStub catalog = new();
    private readonly DetailsStub details = new();

    public EventCatalogPickerTests()
    {
        Services.AddSingleton<IEventCatalogReader>(catalog);
        Services.AddSingleton(EventCatalogAvailability.Available);
        Services.AddSingleton<IStreamInspector>(details);
        Services.AddSingleton<ICorrelationTracer>(details);
        Services.AddSingleton(CorrelationTracerAvailability.Available);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Catalog_rows_show_selectable_ids_and_metadata_across_all_tenants(bool correlations)
    {
        var cut = RenderTool(correlations);
        var row = cut.Find("[aria-label='Available ids'] li");
        row.TextContent.Should().Contain(Id(correlations)).And.Contain("31 events")
            .And.Contain("2 tenants").And.Contain("2026-10-07 12:30:00Z");
        if (correlations) row.TextContent.Should().Contain("4 streams");
        cut.Markup.Should().Contain("ID order").And.Contain("All tenants");
        catalog.PageSizes.Should().Equal(25);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Selecting_an_id_populates_manual_entry_and_loads_existing_details(bool correlations)
    {
        var cut = RenderTool(correlations);
        Select(cut).Click();
        details.Inputs.Should().Equal(Id(correlations));
        cut.Find("input").GetAttribute("value").Should().Be(Id(correlations));
        cut.Find("h2[data-detail-heading]").TextContent.Should().Contain(Id(correlations));
        cut.Find("pre").TextContent.Should().Contain("catalog-selection-payload");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Next_previous_and_refresh_use_exclusive_cursors_and_restart_at_the_first_page(bool correlations)
    {
        var cut = RenderTool(correlations);
        Button(cut, "Previous").HasAttribute("disabled").Should().BeTrue();
        Button(cut, "Next").Click();
        cut.Find("[aria-label='Available ids']").TextContent.Should().Contain(Id(correlations, second: true));
        Button(cut, "Next").HasAttribute("disabled").Should().BeTrue();
        Button(cut, "Previous").Click();
        cut.Find("[aria-label='Available ids']").TextContent.Should().Contain(Id(correlations));
        Button(cut, "Next").Click();
        Button(cut, "Refresh list").Click();
        catalog.Cursors.Should().Equal(null, Id(correlations), null, Id(correlations), null);
        catalog.PageSizes.Should().OnlyContain(size => size == 25);
        Button(cut, "Previous").HasAttribute("disabled").Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Retrying_a_failed_next_page_keeps_its_cursor_and_previous_returns_to_the_first_page(bool correlations)
    {
        var cut = RenderTool(correlations);
        catalog.Fail = true;
        Button(cut, "Next").Click();
        cut.Find("[role='alert']").TextContent.Should().Contain("Unable to load");
        catalog.Fail = false;
        Button(cut, "Retry list").Click();
        cut.Find("[aria-label='Available ids']").TextContent.Should().Contain(Id(correlations, second: true));
        Button(cut, "Previous").Click();
        catalog.Cursors.Should().Equal(null, Id(correlations), Id(correlations), null);
        cut.Find("[aria-label='Available ids']").TextContent.Should().Contain(Id(correlations));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Editing_manual_input_does_not_relabel_the_loaded_details(bool correlations)
    {
        var cut = RenderTool(correlations);
        Select(cut).Click();
        cut.Find("input").Change(Id(correlations, second: true));
        cut.Find("h2[data-detail-heading]").TextContent.Should().Contain(Id(correlations))
            .And.NotContain(Id(correlations, second: true));
        details.Inputs.Should().Equal(Id(correlations));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_empty_catalog_explains_the_state_and_manual_entry_still_works(bool correlations)
    {
        catalog.Empty = true;
        var cut = RenderTool(correlations);
        cut.Markup.Should().Contain(correlations ? "No correlation ids found" : "No aggregate stream ids found");
        EnterManually(cut, correlations);
        details.Inputs.Should().Equal(Id(correlations));
        cut.Find("pre").TextContent.Should().Contain("catalog-selection-payload");
        Button(cut, "Next").HasAttribute("disabled").Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_catalog_failure_keeps_manual_entry_working_and_can_be_retried(bool correlations)
    {
        catalog.Fail = true;
        var cut = RenderTool(correlations);
        cut.Find("[role='alert']").TextContent.Should().Contain("Unable to load");
        EnterManually(cut, correlations);
        details.Inputs.Should().Equal(Id(correlations));
        catalog.Fail = false;
        Button(cut, "Retry list").Click();
        cut.Find("[aria-label='Available ids']").TextContent.Should().Contain(Id(correlations));
        catalog.Cursors.Should().Equal(null, null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_unavailable_catalog_is_never_read_and_preserves_manual_entry(bool correlations)
    {
        Services.AddSingleton(EventCatalogAvailability.Unavailable("This provider has no catalog index."));
        var cut = RenderTool(correlations);
        cut.Markup.Should().Contain("This provider has no catalog index.");
        catalog.Cursors.Should().BeEmpty();
        EnterManually(cut, correlations);
        details.Inputs.Should().Equal(Id(correlations));
    }

    [Fact]
    public void Unavailable_correlation_tracing_does_not_load_the_catalog_or_render_inputs()
    {
        Services.AddSingleton(CorrelationTracerAvailability.Unavailable("Tracing is unavailable."));
        var cut = Render<CorrelationIdTracer>();
        catalog.Cursors.Should().BeEmpty();
        cut.FindAll("input, button").Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Loading_shows_progress_disables_paging_and_cancels_on_disposal(bool correlations)
    {
        catalog.Wait = true;
        var cut = RenderTool(correlations);
        cut.Find("[role='status']").TextContent.Should().Contain("Loading");
        Button(cut, "Refresh list").HasAttribute("disabled").Should().BeTrue();
        Button(cut, "Next").HasAttribute("disabled").Should().BeTrue();
        catalog.ReadToken.CanBeCanceled.Should().BeTrue();
        await DisposeAsync();
        catalog.ReadToken.IsCancellationRequested.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_active_detail_read_blocks_duplicate_selection_and_is_cancelled_on_disposal(bool correlations)
    {
        details.Wait = true;
        var cut = RenderTool(correlations);
        Select(cut).Click();
        cut.Find("input").HasAttribute("disabled").Should().BeTrue();
        Select(cut).HasAttribute("disabled").Should().BeTrue();
        Button(cut, correlations ? "Trace correlation" : "Inspect stream").Click();
        details.Inputs.Should().Equal(Id(correlations));
        JSInterop.Invocations.Should().BeEmpty("an unfinished read must not move focus");
        await DisposeAsync();
        details.ReadToken.IsCancellationRequested.Should().BeTrue();
        JSInterop.Invocations.Should().BeEmpty("disposing the page must not move focus");
    }

    private IRenderedComponent<ComponentBase> RenderTool(bool correlations)
        => correlations ? Render<CorrelationIdTracer>() : Render<EventStoreBrowser>();
    private static string Id(bool correlations, bool second = false)
        => correlations ? (second ? SecondCorrelation : FirstCorrelation).ToString() : second ? SecondStream : FirstStream;
    private static IElement Button(IRenderedComponent<ComponentBase> cut, string label)
        => cut.FindAll("button").Single(b => b.TextContent.Trim() == label);
    private static IElement Select(IRenderedComponent<ComponentBase> cut) => cut.Find("[aria-label='Available ids'] button");
    private static void EnterManually(IRenderedComponent<ComponentBase> cut, bool correlations)
    {
        cut.Find("input").Change(Id(correlations));
        Button(cut, correlations ? "Trace correlation" : "Inspect stream").Click();
    }

    private sealed class CatalogStub : IEventCatalogReader
    {
        public bool Empty { get; set; }
        public bool Fail { get; set; }
        public bool Wait { get; set; }
        public List<string?> Cursors { get; } = [];
        public List<int> PageSizes { get; } = [];
        public CancellationToken ReadToken { get; private set; }
        public async Task<StreamCatalogPage> ListStreamsAsync(string? afterStreamId, int pageSize, CancellationToken ct)
        {
            await RecordAsync(afterStreamId, pageSize, ct);
            return new StreamCatalogPage(Empty ? [] : [new(afterStreamId is null ? FirstStream : SecondStream, 31, Occurred, 2)], !Empty && afterStreamId is null);
        }
        public async Task<CorrelationCatalogPage> ListCorrelationsAsync(Guid? afterCorrelationId, int pageSize, CancellationToken ct)
        {
            await RecordAsync(afterCorrelationId?.ToString(), pageSize, ct);
            return new CorrelationCatalogPage(Empty ? [] : [new(afterCorrelationId is null ? FirstCorrelation : SecondCorrelation, 31, 4, 2, Occurred)], !Empty && afterCorrelationId is null);
        }
        private async Task RecordAsync(string? cursor, int size, CancellationToken ct)
        {
            Cursors.Add(cursor);
            PageSizes.Add(size);
            ReadToken = ct;
            if (Fail) throw new InvalidOperationException("Catalog read failed.");
            if (Wait) await Task.Delay(Timeout.Infinite, ct);
        }
    }

    private sealed class DetailsStub : IStreamInspector, ICorrelationTracer
    {
        public List<string> Inputs { get; } = [];
        public bool Wait { get; set; }
        public CancellationToken ReadToken { get; private set; }
        private static EventMetadata Metadata => new(Guid.NewGuid(), FirstCorrelation, Guid.NewGuid(), Guid.NewGuid(), "test", Occurred, WellKnownTenants.Default);
        public async Task<StreamInspectionResult> InspectStreamAsync(string streamIdInput, CancellationToken ct)
        {
            await RecordAsync(streamIdInput, ct);
            return new(StreamInspectionOutcome.Found, [new(1, "OrderPlaced", 1, Occurred, 10, "{\"marker\":\"catalog-selection-payload\"}", Metadata)]);
        }
        public async Task<CorrelationTraceView> TraceCorrelationAsync(string correlationIdInput, CancellationToken ct)
        {
            await RecordAsync(correlationIdInput, ct);
            return new(CorrelationTraceOutcome.Found, [new(FirstStream, TracedEventKind.Aggregate, 1, "OrderPlaced", 1, Occurred, 10, "{\"marker\":\"catalog-selection-payload\"}", Metadata)], false);
        }
        private async Task RecordAsync(string id, CancellationToken ct)
        {
            Inputs.Add(id);
            ReadToken = ct;
            if (Wait) await Task.Delay(Timeout.Infinite, ct);
        }
    }
}

internal static class CatalogTestServices
{
    public static void AddUnavailableCatalog(this IServiceCollection services)
    {
        services.AddSingleton(EventCatalogAvailability.Unavailable("Catalog unavailable in this detail-only test."));
        services.AddSingleton<IEventCatalogReader>(new UnavailableCatalog());
    }
    private sealed class UnavailableCatalog : IEventCatalogReader
    {
        public Task<StreamCatalogPage> ListStreamsAsync(string? afterStreamId, int pageSize, CancellationToken ct)
            => throw new InvalidOperationException("An unavailable catalog must not be read.");
        public Task<CorrelationCatalogPage> ListCorrelationsAsync(Guid? afterCorrelationId, int pageSize, CancellationToken ct)
            => throw new InvalidOperationException("An unavailable catalog must not be read.");
    }
}
