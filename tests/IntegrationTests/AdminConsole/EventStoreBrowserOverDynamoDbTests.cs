extern alias AdminConsoleHost;
using System.Text.Json;
using AdminConsoleHost::EventSourcingCqrs.Hosts.AdminConsole.Browser;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;
using EventSourcingCqrs.IntegrationTests.Commands;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.AdminConsole;

// The browser done-when (PLAN.md:476, "The Event Store Browser works against DynamoDB"), the twin of
// EventStoreBrowserOverKurrentTests. The browser's read seam, IStreamInspector, reads one aggregate
// stream through IEventStore and re-serializes payloads; it is engine-agnostic over the port, so
// composing it over a DynamoDb-composed IEventStore and reading back an appended stream is the
// seam-level evidence the done-when is met.
//
// Green on write, and declared rather than dressed up. IStreamInspector is already engine-agnostic
// and DynamoDbEventStore already passes the shared contract suite, so nothing here is a discovery:
// it is the done-when's evidence, and it would go red the day the browser grew an engine-shaped
// dependency. Its Kurrent twin was green on write for the same reason and says so.
//
// Trade named, the same one that file names: this pins the SEAM over DynamoDbEventStore rather than
// booting the AdminConsole host under DynamoDb. The host composing this seam on its DynamoDb arm is
// AdminConsoleProviderCompositionTests' DynamoDb case. Together they cover the done-when: the seam
// reads DynamoDB here, and the host composes it there.
//
// LocalStack is pinned to 4.14.0 for the reason recorded in the repository's own instructions: the
// latest tag exits 55 on a license check unless a token is set, and the community edition is
// archived rather than superseded.
public sealed class EventStoreBrowserOverDynamoDbTests : IAsyncLifetime
{
    private static readonly DateTime SeedTime = new(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc);

    private readonly IContainer _localStack = new ContainerBuilder()
        .WithImage("localstack/localstack:4.14.0")
        .WithPortBinding(4566, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilContainerIsHealthy())
        .Build();

    public Task InitializeAsync() => _localStack.StartAsync();

    public Task DisposeAsync() => _localStack.DisposeAsync().AsTask();

    [Fact]
    public async Task The_browser_reads_back_an_appended_stream_from_a_dynamodb_event_store()
    {
        var serviceUrl = $"http://{_localStack.Hostname}:{_localStack.GetMappedPublicPort(4566)}";
        var services = new ServiceCollection();
        services.AddSingleton<IEventTypeProvider, SalesEventTypeProvider>();
        services.AddDynamoDbEventStore(opts =>
        {
            opts.ServiceUrl = serviceUrl;
            opts.AccessKeyId = "test";
            opts.SecretAccessKey = "test";
        });
        await using var provider = services.BuildServiceProvider();

        // The table is the schema, and this host runs no provisioner, so the fact provisions it the
        // way the relational fixtures migrate before handing a database over.
        await provider.GetRequiredService<DynamoDbTableProvisioner>()
            .EnsureTableAsync(CancellationToken.None);

        var eventStore = provider.GetRequiredService<IEventStore>();
        var inspector = new StreamInspector(
            eventStore, provider.GetRequiredService<JsonSerializerOptions>());

        var orderId = Guid.NewGuid();
        var stream = StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderId);
        await EventStoreSeed.AppendAsync(
            eventStore,
            stream,
            [new OrderDrafted(orderId, Guid.NewGuid(), SeedTime, "web")],
            CancellationToken.None);

        var result = await inspector.InspectStreamAsync(stream.Value, CancellationToken.None);

        result.Outcome.Should().Be(StreamInspectionOutcome.Found);
        result.Events.Should().ContainSingle();
        result.Events[0].EventType.Should().Be(nameof(OrderDrafted));
        result.Events[0].PayloadJson.Should().Contain(orderId.ToString());
    }
}
