using Bunit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.AdminConsole.Components.Pages;
using EventSourcingCqrs.Hosts.AdminConsole.Tracer;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Hosts.AdminConsole.Tests.Components;

// RED for slice 5's correlation-tracer capability gate. On a KurrentDB deployment the Correlation-ID
// Tracer is unavailable, so the /correlations page must render a notice naming the reason and must not
// render the trace control, keeping the operator away from the defense-in-depth throwing reader behind
// the seam. The page does not consume the capability yet, so it renders its input and button as today
// and the notice is absent; the GREEN slice injects the capability and gates the surface on it.
public class CorrelationIdTracerCapabilityTests : BunitContext
{
    [Fact]
    public void Under_an_unavailable_capability_the_page_renders_the_notice_and_not_the_trace_control()
    {
        // A throwing seam proves the page never reaches the reader while the capability is unavailable: a
        // render that does not touch the tracer cannot throw.
        Services.AddSingleton<ICorrelationTracer>(new ThrowingTracer());
        Services.AddSingleton(CorrelationTracerAvailability.Unavailable(
            "The KurrentDB event store has no correlation-id index."));

        var cut = Render<CorrelationIdTracer>();

        cut.Markup.Should().Contain("Correlation tracing is not available");
        cut.Markup.Should().Contain("The KurrentDB event store has no correlation-id index.");
        cut.FindAll("input").Should().BeEmpty();
        cut.FindAll("button").Should().BeEmpty();
    }

    // The DynamoDb reason reaches the notice the same way KurrentDB's does. Green on write, and the
    // redundancy is the honest report: the page gates on IsAvailable and never on which engine said
    // so, so the mechanism above already covers this and only the reason string differs. What this
    // adds is that the DynamoDb deployment's operator is told why rather than shown an empty notice,
    // which the capability's own ThrowIfNullOrEmpty already refuses to allow. It is a
    // characterization of the pair, not a discovery.
    [Fact]
    public void Under_the_dynamodb_unavailable_capability_the_page_names_the_missing_index()
    {
        Services.AddSingleton<ICorrelationTracer>(new ThrowingTracer());
        Services.AddSingleton(CorrelationTracerAvailability.Unavailable(
            "The DynamoDB event store has no cross-stream correlation-id index; a correlation trace "
            + "would scan the whole log, so it needs a dedicated read path, which is deferred."));

        var cut = Render<CorrelationIdTracer>();

        cut.Markup.Should().Contain("Correlation tracing is not available");
        cut.Markup.Should().Contain("no cross-stream correlation-id index");
        cut.FindAll("input").Should().BeEmpty();
        cut.FindAll("button").Should().BeEmpty();
    }

    private sealed class ThrowingTracer : ICorrelationTracer
    {
        public Task<CorrelationTraceView> TraceCorrelationAsync(
            string correlationIdInput, CancellationToken ct)
            => throw new InvalidOperationException(
                "The tracer must not be reached while the capability is unavailable.");
    }
}
