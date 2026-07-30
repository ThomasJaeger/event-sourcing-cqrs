using Bunit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.AdminConsole.Browser;
using EventSourcingCqrs.Hosts.AdminConsole.Components.Pages;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Hosts.AdminConsole.Tests.Components;

// The Event Store Browser page (/streams) lets an operator inspect one aggregate stream for display
// (Phase 12). The page depends on the narrow IStreamInspector seam; these specs fake it (a one-method
// double seeded with the StreamInspectionResult to return) and drive the page over bUnit: enter a stream
// id, inspect, and assert the rendered outcome. The page is read-only (no destructive action, no
// confirmation), so the specs are render-plus-inspect. Authorization is the host fallback gate (ADR 0040),
// upstream of render, so the page carries no denied branch.
//
// Metadata reveal mechanism the specs render against (GREEN must satisfy): each found event shows its
// scalar header fields and its payload JSON by default, and a per-event "Show metadata" toggle button that
// conditionally renders the metadata block on demand, hidden from the markup until the toggle is clicked
// rather than merely CSS-hidden.
public class EventStoreBrowserTests : BunitContext
{
    private const string AnyStreamId = "order:0f8fad5b07d04015b9d063e8e9e7d6f8";

    [Fact]
    public void The_page_renders_the_stream_id_input_and_an_inspect_control()
    {
        Services.AddSingleton<IStreamInspector>(new StubInspector(Outcome(StreamInspectionOutcome.Empty)));

        var cut = Render<EventStoreBrowser>();

        cut.FindAll("input").Should().NotBeEmpty();
        cut.Find("button").TextContent.Should().Contain("Inspect");
    }

    [Fact]
    public void Inspecting_a_found_stream_renders_each_event_with_its_payload_json_visible()
    {
        const string payloadToken = "payload-visible-marker";
        Services.AddSingleton<IStreamInspector>(
            new StubInspector(Found($"{{\"marker\":\"{payloadToken}\"}}", Guid.NewGuid())));

        var cut = Render<EventStoreBrowser>();
        cut.Find("input").Change(AnyStreamId);
        cut.Find("button").Click();

        // The scalar header field and the payload JSON both render: payload is visible by default.
        cut.Markup.Should().Contain("OrderPlaced");
        cut.Markup.Should().Contain(payloadToken);
    }

    [Fact]
    public void Inspecting_a_found_stream_renders_the_stream_version()
    {
        // Stream version 37, a value that collides with nothing else the row renders: not the schema
        // version, not the global position, and not a hex guid. A match on "v37" is the version or nothing.
        Services.AddSingleton<IStreamInspector>(new StubInspector(FoundAtStreamVersion(37)));

        var cut = Render<EventStoreBrowser>();
        cut.Find("input").Change(AnyStreamId);
        cut.Find("button").Click();

        cut.Markup.Should().Contain("v37");
        // Razor's email-address heuristic swallows an at-sign flanked by word characters, so a version
        // written as v@evt.StreamVersion never parses and the raw source text reaches the operator. The
        // suite had no assertion here, which is how it shipped.
        cut.Markup.Should().NotContain("v@evt.StreamVersion");
    }

    [Fact]
    public void Inspecting_an_empty_stream_renders_the_guidance_empty_state()
    {
        Services.AddSingleton<IStreamInspector>(new StubInspector(Outcome(StreamInspectionOutcome.Empty)));

        var cut = Render<EventStoreBrowser>();
        cut.Find("input").Change(AnyStreamId);
        cut.Find("button").Click();

        // Not a bare "no events": the empty state guides the operator to check the kind, the id, the tenant.
        cut.Markup.Should().Contain("aggregate id");
        cut.Markup.Should().Contain("tenant");
    }

    [Fact]
    public void Inspecting_a_process_manager_stream_renders_the_unsupported_message()
    {
        Services.AddSingleton<IStreamInspector>(
            new StubInspector(Outcome(StreamInspectionOutcome.ProcessManagerUnsupported)));

        var cut = Render<EventStoreBrowser>();
        cut.Find("input").Change(AnyStreamId);
        cut.Find("button").Click();

        cut.Markup.Should().Contain("Process-manager streams are not supported");
    }

    [Fact]
    public void Inspecting_a_malformed_input_renders_the_invalid_format_message()
    {
        Services.AddSingleton<IStreamInspector>(
            new StubInspector(Outcome(StreamInspectionOutcome.InvalidFormat)));

        var cut = Render<EventStoreBrowser>();
        cut.Find("input").Change("not-a-valid-stream-id");
        cut.Find("button").Click();

        cut.Markup.Should().Contain("valid stream id");
    }

    [Fact]
    public void Event_metadata_is_hidden_by_default_and_revealed_on_demand()
    {
        var correlationId = Guid.NewGuid();
        Services.AddSingleton<IStreamInspector>(new StubInspector(Found("{}", correlationId)));

        var cut = Render<EventStoreBrowser>();
        cut.Find("input").Change(AnyStreamId);
        cut.Find("button").Click();

        // Metadata is not splayed by default: the correlation id is not in the rendered markup yet.
        cut.Markup.Should().NotContain(correlationId.ToString());

        // Reveal it on demand through the per-event metadata toggle.
        cut.FindAll("button").First(b => b.TextContent.Contains("metadata")).Click();

        cut.Markup.Should().Contain(correlationId.ToString());
    }

    private static StreamInspectionResult Found(string payloadJson, Guid correlationId)
    {
        var metadata = new EventMetadata(
            EventId: Guid.NewGuid(),
            CorrelationId: correlationId,
            CausationId: Guid.NewGuid(),
            ActorId: Guid.NewGuid(),
            Source: "test",
            OccurredUtc: new DateTime(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc),
            Tenant: TenantId.From(Guid.NewGuid()));
        var inspected = new InspectedEvent(
            StreamVersion: 1,
            EventType: "OrderPlaced",
            EventVersion: 1,
            OccurredUtc: new DateTime(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc),
            GlobalPosition: 42,
            PayloadJson: payloadJson,
            Metadata: metadata);
        return new StreamInspectionResult(StreamInspectionOutcome.Found, new[] { inspected });
    }

    // The Found helper above pins StreamVersion at 1, which the event version also carries, so it
    // cannot tell a rendered version from a coincidence. This one takes the version.
    private static StreamInspectionResult FoundAtStreamVersion(int streamVersion)
    {
        var metadata = new EventMetadata(
            EventId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.NewGuid(),
            Source: "test",
            OccurredUtc: new DateTime(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc),
            Tenant: TenantId.From(Guid.NewGuid()));
        var inspected = new InspectedEvent(
            StreamVersion: streamVersion,
            EventType: "OrderPlaced",
            EventVersion: 1,
            OccurredUtc: new DateTime(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc),
            GlobalPosition: 42,
            PayloadJson: "{}",
            Metadata: metadata);
        return new StreamInspectionResult(StreamInspectionOutcome.Found, new[] { inspected });
    }

    private static StreamInspectionResult Outcome(StreamInspectionOutcome outcome)
        => new(outcome, Array.Empty<InspectedEvent>());

    private sealed class StubInspector(StreamInspectionResult result) : IStreamInspector
    {
        public Task<StreamInspectionResult> InspectStreamAsync(string streamIdInput, CancellationToken ct)
            => Task.FromResult(result);
    }
}
