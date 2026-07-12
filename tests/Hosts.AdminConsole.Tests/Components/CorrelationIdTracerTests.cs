using Bunit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.AdminConsole.Components.Pages;
using EventSourcingCqrs.Hosts.AdminConsole.Tracer;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Hosts.AdminConsole.Tests.Components;

// The Correlation-ID Tracer page (/correlations) traces one workflow across every stream and every tenant
// carrying its correlation id (Phase 12). The page depends on the narrow ICorrelationTracer seam; these
// specs fake it (a one-method double seeded with the CorrelationTraceView to return) and drive the page over
// bUnit: enter a correlation id, trace, and assert the rendered outcome. The page is read-only, so the specs
// are render-plus-trace. Authorization is the host fallback gate (ADR 0040), upstream of render, so the page
// carries no denied branch.
//
// The four outcomes the page renders (GREEN must satisfy): InvalidFormat names the expected form with a
// concrete example, the Browser's pattern; SentinelRefused says the empty guid is a sentinel rather than a
// workflow and that rows carrying it predate correlation propagation on the outbox route; Empty is
// informational; Found renders one block per row. Per-row scalars and the row's tenant render by default,
// because full-chain cross-tenant exposure is the finding the tool exists to reveal. Correlation, causation,
// actor, and source sit behind a per-row toggle, the Browser's metadata-reveal shape.
//
// The metadata probe below is the causation id, not the correlation id, because the correlation id an
// operator types is bound to the input and renders in the input's value. It would be in the markup before
// any toggle, and would make a hidden-until-revealed assertion pass for the wrong reason.
public class CorrelationIdTracerTests : BunitContext
{
    private static readonly Guid AnyCorrelationId = Guid.Parse("0f8fad5b-07d0-4015-b9d0-63e8e9e7d6f8");
    private static readonly DateTime AnyTime = new(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_page_renders_the_correlation_id_input_and_a_trace_control_and_nothing_below()
    {
        Services.AddSingleton<ICorrelationTracer>(new StubTracer(Refused(CorrelationTraceOutcome.Empty)));

        var cut = Render<CorrelationIdTracer>();

        cut.Find("h1").TextContent.Should().Contain("Correlation");
        cut.FindAll("input").Should().NotBeEmpty();
        cut.Find("button").TextContent.Should().Contain("Trace");
        // The baseline page shows nothing below the input: every state sits under a result-present guard.
        cut.FindAll("pre").Should().BeEmpty();
        cut.Markup.Should().NotContain("No events found");
    }

    [Fact]
    public void Tracing_an_unparseable_input_renders_the_invalid_format_message()
    {
        Services.AddSingleton<ICorrelationTracer>(
            new StubTracer(Refused(CorrelationTraceOutcome.InvalidFormat)));

        var cut = Render<CorrelationIdTracer>();
        Trace(cut, "not-a-correlation-id");

        // The Browser's InvalidFormat idiom: name the expected form and show one.
        cut.Markup.Should().Contain("valid correlation id");
        cut.Markup.Should().Contain("for example");
    }

    [Fact]
    public void Tracing_the_empty_guid_renders_the_sentinel_message_distinct_from_invalid_format()
    {
        Services.AddSingleton<ICorrelationTracer>(
            new StubTracer(Refused(CorrelationTraceOutcome.SentinelRefused)));

        var cut = Render<CorrelationIdTracer>();
        Trace(cut, Guid.Empty.ToString());

        // The empty guid parses, so the format message would be wrong and misleading. It is refused because
        // it is a sentinel, and the rows carrying it predate correlation propagation on the outbox route
        // (ADR 0042), so they are not traceable as a chain.
        cut.Markup.Should().Contain("sentinel");
        cut.Markup.Should().Contain("not traceable");
        cut.Markup.Should().NotContain("valid correlation id");
    }

    [Fact]
    public void Tracing_a_correlation_with_no_rows_renders_the_empty_state()
    {
        Services.AddSingleton<ICorrelationTracer>(new StubTracer(Refused(CorrelationTraceOutcome.Empty)));

        var cut = Render<CorrelationIdTracer>();
        Trace(cut, AnyCorrelationId.ToString());

        cut.Markup.Should().Contain("No events found");
    }

    [Fact]
    public void A_traced_row_renders_its_scalars_and_payload_with_metadata_revealed_on_demand()
    {
        const string payloadToken = "payload-visible-marker";
        var causationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var actorId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var row = Row(
            streamId: "order:0f8fad5b07d04015b9d063e8e9e7d6f8",
            kind: TracedEventKind.Aggregate,
            eventType: "OrderPlaced",
            payloadJson: $"{{\"marker\": \"{payloadToken}\"}}",
            causationId: causationId,
            actorId: actorId);
        Services.AddSingleton<ICorrelationTracer>(new StubTracer(Found(row)));

        var cut = Render<CorrelationIdTracer>();
        Trace(cut, AnyCorrelationId.ToString());

        // Scalars and payload render by default.
        cut.Markup.Should().Contain("order:0f8fad5b07d04015b9d063e8e9e7d6f8");
        cut.Markup.Should().Contain("OrderPlaced");
        cut.Markup.Should().Contain("v3");
        cut.Markup.Should().Contain("77");
        cut.Markup.Should().Contain(payloadToken);

        // Metadata is not splayed by default.
        cut.Markup.Should().NotContain(causationId.ToString());
        cut.Markup.Should().NotContain(actorId.ToString());

        cut.FindAll("button").First(b => b.TextContent.Contains("metadata")).Click();

        cut.Markup.Should().Contain(causationId.ToString());
        cut.Markup.Should().Contain(actorId.ToString());
        cut.Markup.Should().Contain("trace-source");
    }

    [Fact]
    public void A_trace_spanning_both_families_renders_each_rows_kind()
    {
        var aggregate = Row(
            streamId: "order:0f8fad5b07d04015b9d063e8e9e7d6f8",
            kind: TracedEventKind.Aggregate,
            eventType: "OrderPlaced");
        var processManager = Row(
            streamId: "pm-order-fulfillment:0f8fad5b07d04015b9d063e8e9e7d6f8",
            kind: TracedEventKind.ProcessManager,
            eventType: "OrderFulfillmentStarted");
        Services.AddSingleton<ICorrelationTracer>(new StubTracer(Found(aggregate, processManager)));

        var cut = Render<CorrelationIdTracer>();
        Trace(cut, AnyCorrelationId.ToString());

        // One list, both families, each row saying which it is. This is the one-table join ADR 0013
        // promised, rendered.
        cut.Markup.Should().Contain("Aggregate");
        cut.Markup.Should().Contain("Process manager");
        cut.Markup.Should().Contain("pm-order-fulfillment:0f8fad5b07d04015b9d063e8e9e7d6f8");
    }

    [Fact]
    public void A_trace_crossing_tenants_renders_each_rows_own_tenant()
    {
        var firstTenant = TenantId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));
        var secondTenant = TenantId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"));
        var first = Row(streamId: "order:aaaa", kind: TracedEventKind.Aggregate, tenant: firstTenant);
        var second = Row(streamId: "order:bbbb", kind: TracedEventKind.Aggregate, tenant: secondTenant);
        Services.AddSingleton<ICorrelationTracer>(new StubTracer(Found(first, second)));

        var cut = Render<CorrelationIdTracer>();
        Trace(cut, AnyCorrelationId.ToString());

        // Per-row tenant renders without a toggle. A chain crossing tenants is an isolation defect, and the
        // operator has to be able to see it without hunting for it.
        cut.Markup.Should().Contain(firstTenant.Value.ToString());
        cut.Markup.Should().Contain(secondTenant.Value.ToString());
    }

    [Fact]
    public void A_truncated_trace_renders_the_cap_state()
    {
        Services.AddSingleton<ICorrelationTracer>(
            new StubTracer(new CorrelationTraceView(
                CorrelationTraceOutcome.Found, [Row()], Truncated: true)));

        var cut = Render<CorrelationIdTracer>();
        Trace(cut, AnyCorrelationId.ToString());

        cut.Markup.Should().Contain("cap");
        cut.Markup.Should().Contain("query tool");
    }

    [Fact]
    public void An_untruncated_trace_renders_no_cap_state()
    {
        Services.AddSingleton<ICorrelationTracer>(new StubTracer(Found(Row())));

        var cut = Render<CorrelationIdTracer>();
        Trace(cut, AnyCorrelationId.ToString());

        cut.Markup.Should().NotContain("query tool");
    }

    [Fact]
    public void Renders_an_error_state_when_the_trace_read_throws()
    {
        Services.AddSingleton<ICorrelationTracer>(new ThrowingTracer());

        var cut = Render<CorrelationIdTracer>();
        Trace(cut, AnyCorrelationId.ToString());

        // The seam classifies every expected input; a throw here is the port's metadata failure surfacing
        // (ADR 0043). The operator sees it, and the exception does not escape the render.
        cut.Markup.Should().Contain("Unable to trace");
        cut.FindAll("pre").Should().BeEmpty();
    }

    private static void Trace(IRenderedComponent<CorrelationIdTracer> cut, string input)
    {
        cut.Find("input").Change(input);
        cut.Find("button").Click();
    }

    private static CorrelationTraceView Found(params TracedEvent[] rows)
        => new(CorrelationTraceOutcome.Found, rows, Truncated: false);

    private static CorrelationTraceView Refused(CorrelationTraceOutcome outcome)
        => new(outcome, [], Truncated: false);

    private static TracedEvent Row(
        string streamId = "order:0f8fad5b07d04015b9d063e8e9e7d6f8",
        TracedEventKind kind = TracedEventKind.Aggregate,
        string eventType = "OrderPlaced",
        string payloadJson = "{}",
        Guid? causationId = null,
        Guid? actorId = null,
        TenantId? tenant = null)
        => new(
            StreamId: streamId,
            Kind: kind,
            StreamVersion: 3,
            EventType: eventType,
            EventVersion: 1,
            OccurredUtc: AnyTime,
            GlobalPosition: 77,
            PayloadJson: payloadJson,
            Metadata: new EventMetadata(
                EventId: Guid.NewGuid(),
                CorrelationId: AnyCorrelationId,
                CausationId: causationId ?? Guid.NewGuid(),
                ActorId: actorId ?? Guid.NewGuid(),
                Source: "trace-source",
                SchemaVersion: 1,
                OccurredUtc: AnyTime,
                Tenant: tenant ?? WellKnownTenants.Default));

    private sealed class StubTracer(CorrelationTraceView view) : ICorrelationTracer
    {
        public Task<CorrelationTraceView> TraceCorrelationAsync(
            string correlationIdInput, CancellationToken ct)
            => Task.FromResult(view);
    }

    private sealed class ThrowingTracer : ICorrelationTracer
    {
        public Task<CorrelationTraceView> TraceCorrelationAsync(
            string correlationIdInput, CancellationToken ct)
            => throw new InvalidOperationException("trace read failed");
    }
}
