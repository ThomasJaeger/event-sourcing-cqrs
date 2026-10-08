using AngleSharp.Dom;
using Bunit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.AdminConsole.Audit;
using EventSourcingCqrs.Hosts.AdminConsole.Components.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Hosts.AdminConsole.Tests.Components;

public sealed class AuditExplorerTests : BunitContext
{
    private const string Stream = "order:33333333333333333333333333333333";
    private static readonly Guid Correlation = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly ReaderStub reader = new();

    public AuditExplorerTests()
    {
        Services.AddSingleton<IAuditEventReader>(reader);
        Services.AddSingleton(AuditExplorerAvailability.Available);
    }

    [Fact]
    public void The_first_page_shows_recorded_facts_scope_and_expandable_raw_documents()
    {
        var cut = Render<AuditExplorer>();
        reader.Requests.Should().ContainSingle().Which.Size.Should().Be(50);
        cut.Markup.Should().Contain("All tenants").And.Contain("System or not recorded")
            .And.Contain("retired.UnknownAuditEvent").And.Contain("audit-fixture")
            .And.Contain("Recorded event time").And.Contain("100");
        cut.Find("details[data-audit-json] summary").TextContent.Should().Contain("Stored JSON");
        cut.Find("pre[aria-label='Stored event payload']").TextContent.Should().Be("{\"original\":true}");
        cut.Find("pre[aria-label='Stored event metadata']").TextContent.Should().Contain("extra_audit_field");
        cut.Find("a[href^='/streams?streamId=']").GetAttribute("href").Should().Contain(Uri.EscapeDataString(Stream));
        cut.Find("a[href^='/correlations?correlationId=']").GetAttribute("href").Should().Contain(Correlation.ToString());
    }

    [Fact]
    public void Order_records_link_to_the_read_only_history_comparison()
    {
        var cut = Render<AuditExplorer>();
        cut.Find("a[href^='/order-history?streamId=']").GetAttribute("href")
            .Should().Be($"/order-history?streamId={Uri.EscapeDataString(Stream)}");
    }

    [Fact]
    public void Query_prefill_is_applied_to_the_first_read_and_the_filter_controls()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            $"/audit?streamId={Uri.EscapeDataString(Stream)}&correlationId={Correlation}&tenantId={WellKnownTenants.Default}&actorId={Guid.Empty}");
        var cut = Render<AuditExplorer>();
        reader.Requests.Should().ContainSingle().Which.Filter.Should().Be(
            new AuditEventFilter(Stream, Correlation, WellKnownTenants.Default, Guid.Empty));
        cut.Find("#audit-stream").GetAttribute("value").Should().Be(Stream);
    }

    [Fact]
    public void Applying_exact_filters_restarts_the_boundary_and_invalid_input_does_not_read()
    {
        var cut = Render<AuditExplorer>();
        cut.Find("#audit-stream").Change(Stream);
        cut.Find("#audit-correlation").Change(Correlation.ToString());
        cut.Find("#audit-tenant").Change(WellKnownTenants.Default.ToString());
        cut.Find("#audit-actor").Change(Guid.Empty.ToString());
        Button(cut, "Apply filters").Click();
        reader.Requests.Last().Should().Be((new AuditEventFilter(Stream, Correlation, WellKnownTenants.Default, Guid.Empty), (long?)null, (long?)null, 50));
        cut.Find("#audit-actor").Change("not-an-id");
        Button(cut, "Apply filters").Click();
        reader.Requests.Should().HaveCount(2);
        cut.Find("[role='alert']").TextContent.Should().Contain("actor");
    }

    [Fact]
    public void Older_and_newer_pages_reuse_the_boundary_and_refresh_captures_a_new_one()
    {
        var cut = Render<AuditExplorer>();
        Button(cut, "Newer events").HasAttribute("disabled").Should().BeTrue();
        Button(cut, "Older events").Click();
        reader.Requests.Last().Upper.Should().Be(100);
        reader.Requests.Last().Before.Should().Be(80);
        cut.Find("article[data-audit-position]").GetAttribute("data-audit-position").Should().Be("60");
        Button(cut, "Newer events").Click();
        reader.Requests.Last().Upper.Should().Be(100);
        reader.Requests.Last().Before.Should().BeNull();
        Button(cut, "Refresh results").Click();
        reader.Requests.Last().Upper.Should().BeNull();
        reader.Requests.Last().Before.Should().BeNull();
    }

    [Fact]
    public void A_failed_older_page_retries_the_same_boundary_and_cursor()
    {
        var cut = Render<AuditExplorer>();
        reader.Fail = true;
        Button(cut, "Older events").Click();
        cut.Find("[role='alert']").TextContent.Should().Contain("Unable to load");
        reader.Fail = false;
        Button(cut, "Retry results").Click();
        reader.Requests[^1].Should().Be(reader.Requests[^2]);
        cut.Find("article[data-audit-position]").GetAttribute("data-audit-position").Should().Be("60");
    }

    [Fact]
    public void Refresh_keeps_applied_filters_instead_of_unsubmitted_input()
    {
        var cut = Render<AuditExplorer>();
        cut.Find("#audit-actor").Change("not-submitted");
        Button(cut, "Refresh results").Click();
        reader.Requests.Last().Filter.Should().Be(new AuditEventFilter());
        cut.FindAll("[role='alert']").Should().BeEmpty();
    }

    [Fact]
    public async Task Superseded_query_reads_cannot_replace_new_results_even_if_they_ignore_cancellation()
    {
        var deferred = new DeferredReader();
        Services.AddSingleton<IAuditEventReader>(deferred);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/audit?streamId=" + Uri.EscapeDataString(Stream));
        var cut = Render<AuditExplorer>();
        const string newerStream = "order:44444444444444444444444444444444";
        await cut.InvokeAsync(() => navigation.NavigateTo("/audit?streamId=" + Uri.EscapeDataString(newerStream)));
        deferred.Reads.Should().HaveCount(2);
        deferred.Reads[0].Token.IsCancellationRequested.Should().BeTrue();
        await cut.InvokeAsync(() => deferred.Complete(1));
        cut.WaitForAssertion(() => cut.Find("article").TextContent.Should().Contain(newerStream));
        await cut.InvokeAsync(() => deferred.Complete(0));
        cut.Find("article").TextContent.Should().Contain(newerStream).And.NotContain(Stream);
        JSInterop.Invocations.Should().BeEmpty();
    }

    [Fact]
    public void Empty_results_are_distinct_from_a_read_error()
    {
        reader.Empty = true;
        var cut = Render<AuditExplorer>();
        cut.Markup.Should().Contain("No recorded events match these filters");
        cut.FindAll("[role='alert']").Should().BeEmpty();
        Button(cut, "Older events").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void An_unavailable_provider_never_calls_the_reader()
    {
        Services.AddSingleton(AuditExplorerAvailability.Unavailable("Raw audit browsing is unavailable on this provider."));
        var cut = Render<AuditExplorer>();
        reader.Requests.Should().BeEmpty();
        cut.Markup.Should().Contain("unavailable on this provider");
        cut.FindAll("input, button").Should().BeEmpty();
    }

    [Fact]
    public async Task Pending_reads_disable_navigation_and_cancel_without_focus_when_disposed()
    {
        reader.Wait = true;
        var cut = Render<AuditExplorer>();
        cut.Find("[role='status']").TextContent.Should().Contain("Loading");
        Button(cut, "Apply filters").HasAttribute("disabled").Should().BeTrue();
        reader.Token.CanBeCanceled.Should().BeTrue();
        JSInterop.Invocations.Should().BeEmpty();
        await DisposeAsync();
        reader.Token.IsCancellationRequested.Should().BeTrue();
        JSInterop.Invocations.Should().BeEmpty();
    }

    private static IElement Button(IRenderedComponent<AuditExplorer> cut, string label)
        => cut.FindAll("button").Single(button => button.TextContent.Trim() == label);

    private sealed class DeferredReader : IAuditEventReader
    {
        public List<(AuditEventFilter Filter, CancellationToken Token, TaskCompletionSource<AuditEventPage> Completion)> Reads { get; } = [];
        public Task<AuditEventPage> ReadPageAsync(AuditEventFilter filter, long? upperPosition,
            long? beforePosition, int pageSize, CancellationToken ct)
        {
            var completion = new TaskCompletionSource<AuditEventPage>();
            Reads.Add((filter, ct, completion));
            return completion.Task;
        }
        public void Complete(int index)
        {
            var request = Reads[index];
            var metadata = new EventMetadata(Guid.NewGuid(), Correlation, Guid.NewGuid(), Guid.Empty,
                "audit-fixture", DateTime.UtcNow, WellKnownTenants.Default);
            var row = new AuditEventRow(index + 1, request.Filter.StreamId!, 1, metadata.EventId,
                "OrderDrafted", 1, metadata.OccurredUtc, metadata, "{}", "{}");
            request.Completion.SetResult(new([row], index + 1, null));
        }
    }

    private sealed class ReaderStub : IAuditEventReader
    {
        public List<(AuditEventFilter Filter, long? Upper, long? Before, int Size)> Requests { get; } = [];
        public bool Fail { get; set; }
        public bool Empty { get; set; }
        public bool Wait { get; set; }
        public CancellationToken Token { get; private set; }
        public async Task<AuditEventPage> ReadPageAsync(AuditEventFilter filter, long? upperPosition,
            long? beforePosition, int pageSize, CancellationToken ct)
        {
            Requests.Add((filter, upperPosition, beforePosition, pageSize));
            Token = ct;
            if (Fail) throw new InvalidOperationException("Reader unavailable.");
            if (Wait) await Task.Delay(Timeout.Infinite, ct);
            var metadata = new EventMetadata(Guid.NewGuid(), Correlation, Guid.NewGuid(), Guid.Empty,
                "audit-fixture", new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc), WellKnownTenants.Default);
            var row = new AuditEventRow(beforePosition is null ? 100 : 60, Stream, 1, metadata.EventId,
                "retired.UnknownAuditEvent", 7, metadata.OccurredUtc, metadata,
                "{\"original\":true}", "{\"extra_audit_field\":\"preserved\"}");
            return new(Empty ? [] : [row], upperPosition ?? 100, Empty || beforePosition is not null ? null : 80);
        }
    }
}
