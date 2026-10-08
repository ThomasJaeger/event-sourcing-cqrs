using Bunit;
using EventSourcingCqrs.Hosts.AdminConsole.Browser;
using EventSourcingCqrs.Hosts.AdminConsole.Tracer;
using EventSourcingCqrs.Hosts.AdminConsole.Components.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Hosts.AdminConsole.Tests.Components;

public class InvestigationLinkTests : BunitContext
{
    [Fact]
    public void Stream_link_opens_its_target_without_copying_an_id()
    {
        const string id = "order:11111111111111111111111111111111";
        Services.AddUnavailableCatalog();
        var reader = new Inspector();
        Services.AddSingleton<IStreamInspector>(reader);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/streams?streamId=" + Uri.EscapeDataString(id));
        var cut = Render<EventStoreBrowser>();
        cut.WaitForAssertion(() => reader.LastId.Should().Be(id));
        cut.Find("input").GetAttribute("value").Should().Be(id);
    }

    [Fact]
    public void Correlation_link_opens_its_trace_without_copying_an_id()
    {
        const string id = "11111111-1111-1111-1111-111111111111";
        Services.AddUnavailableCatalog();
        Services.AddSingleton(CorrelationTracerAvailability.Available);
        var reader = new Tracer();
        Services.AddSingleton<ICorrelationTracer>(reader);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/correlations?correlationId=" + id);
        var cut = Render<CorrelationIdTracer>();
        cut.WaitForAssertion(() => reader.LastId.Should().Be(id));
    }

    private sealed class Inspector : IStreamInspector
    {
        public string? LastId { get; private set; }
        public Task<StreamInspectionResult> InspectStreamAsync(string id, CancellationToken ct)
        {
            LastId = id;
            return Task.FromResult(new StreamInspectionResult(StreamInspectionOutcome.Empty, []));
        }
    }
    private sealed class Tracer : ICorrelationTracer
    {
        public string? LastId { get; private set; }
        public Task<CorrelationTraceView> TraceCorrelationAsync(string id, CancellationToken ct)
        {
            LastId = id;
            return Task.FromResult(new CorrelationTraceView(CorrelationTraceOutcome.Empty, [], false));
        }
    }
}
