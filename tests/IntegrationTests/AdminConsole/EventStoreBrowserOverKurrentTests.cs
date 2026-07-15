extern alias AdminConsoleHost;
using System.Text.Json;
using AdminConsoleHost::EventSourcingCqrs.Hosts.AdminConsole.Browser;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Infrastructure.EventStore.Kurrent;
using EventSourcingCqrs.IntegrationTests.Commands;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.AdminConsole;

// Slice 5's browser done-when (PLAN.md Phase 13, "The Event Store Browser works against KurrentDB").
// The browser's read seam, IStreamInspector, reads one aggregate stream through IEventStore and
// re-serializes payloads; it is engine-agnostic over the port, so composing it over a Kurrent-composed
// IEventStore and reading back an appended stream is the seam-level evidence the done-when is met.
//
// Trade named: this pins the SEAM over KurrentEventStore, which passes today because IStreamInspector is
// already engine-agnostic, rather than booting the AdminConsole host under Kurrent. The host composing
// this seam on its Kurrent arm is AdminConsoleProviderCompositionTests' Kurrent case, which runs RED
// against the inline guard until the GREEN slice composes the arm. Together they cover the done-when:
// the seam reads Kurrent here, and the host composes it there.
public sealed class EventStoreBrowserOverKurrentTests : IAsyncLifetime
{
    private static readonly DateTime SeedTime = new(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc);

    private readonly IContainer _kurrent = new ContainerBuilder()
        .WithImage("kurrentplatform/kurrentdb:26.0.2")
        .WithCommand("--insecure", "--mem-db")
        .WithPortBinding(2113, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilContainerIsHealthy())
        .Build();

    public Task InitializeAsync() => _kurrent.StartAsync();

    public Task DisposeAsync() => _kurrent.DisposeAsync().AsTask();

    [Fact]
    public async Task The_browser_reads_back_an_appended_stream_from_a_kurrent_event_store()
    {
        var connectionString =
            $"esdb://{_kurrent.Hostname}:{_kurrent.GetMappedPublicPort(2113)}?tls=false";
        var services = new ServiceCollection();
        services.AddSingleton<IEventTypeProvider, SalesEventTypeProvider>();
        services.AddKurrentEventStore(opts => opts.ConnectionString = connectionString);
        await using var provider = services.BuildServiceProvider();
        var eventStore = provider.GetRequiredService<IEventStore>();
        var inspector = new StreamInspector(
            eventStore, provider.GetRequiredService<JsonSerializerOptions>());

        var orderId = Guid.NewGuid();
        var stream = StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderId);
        await EventStoreSeed.AppendAsync(
            eventStore,
            stream,
            [new OrderDrafted(orderId, Guid.NewGuid(), SeedTime)],
            CancellationToken.None);

        var result = await inspector.InspectStreamAsync(stream.Value, CancellationToken.None);

        result.Outcome.Should().Be(StreamInspectionOutcome.Found);
        result.Events.Should().ContainSingle();
        result.Events[0].EventType.Should().Be(nameof(OrderDrafted));
        result.Events[0].PayloadJson.Should().Contain(orderId.ToString());
    }
}
