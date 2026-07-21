using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;
using EventSourcingCqrs.Infrastructure.EventStore.Kurrent;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.EventStore.SqlServer;
using EventSourcingCqrs.Infrastructure.Versioning;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.Versioning;

// Composition facts for the upcaster-pipeline seam: every event-store composition root must register
// an EventUpcasterPipeline built from that engine's EventTypeRegistry plus the IEventUpcaster
// singletons a host contributes, so a stored event upcasts on read. Container-free, following
// ServiceCollectionExtensionsTests: only the DI wiring is under test, resolved off a stub
// configuration, so no fact resolves IEventStore or touches a backend. The seam has five
// registration sites, the four Add*EventStore roots plus AddEventStoreReplayReader, which composes a
// PostgresEventStore whose read path deserializes aggregate payloads.
public class EventUpcasterPipelineCompositionTests
{
    [Fact]
    public void AddPostgresEventStore_composes_the_pipeline_from_registry_and_registered_upcasters()
    {
        using var provider = ScratchServices()
            .AddPostgresEventStore(opts => opts.ConnectionString = "Host=localhost;Database=stub")
            .BuildServiceProvider();

        AssertComposesScratchLineage(provider);
    }

    [Fact]
    public void AddSqlServerEventStore_composes_the_pipeline_from_registry_and_registered_upcasters()
    {
        using var provider = ScratchServices()
            .AddSqlServerEventStore(opts => opts.ConnectionString = "Server=localhost;Database=stub")
            .BuildServiceProvider();

        AssertComposesScratchLineage(provider);
    }

    [Fact]
    public void AddKurrentEventStore_composes_the_pipeline_from_registry_and_registered_upcasters()
    {
        using var provider = ScratchServices()
            .AddKurrentEventStore(opts => opts.ConnectionString = "esdb://localhost:2113?tls=false")
            .BuildServiceProvider();

        AssertComposesScratchLineage(provider);
    }

    [Fact]
    public void AddDynamoDbEventStore_composes_the_pipeline_from_registry_and_registered_upcasters()
    {
        using var provider = ScratchServices()
            .AddDynamoDbEventStore(opts =>
            {
                opts.ServiceUrl = "http://localhost:4566";
                opts.TableName = "stub";
                opts.AccessKeyId = "test";
                opts.SecretAccessKey = "test";
            })
            .BuildServiceProvider();

        AssertComposesScratchLineage(provider);
    }

    [Fact]
    public void AddEventStoreReplayReader_composes_the_pipeline_from_registry_and_registered_upcasters()
    {
        using var provider = ScratchServices()
            .AddEventStoreReplayReader()
            .BuildServiceProvider();

        AssertComposesScratchLineage(provider);
    }

    // The pipeline resolves, and its scratch lineage reads as two versions: proof it was built from
    // the adapter's registry (terminal ScratchEventV2) plus the DI-enumerated upcaster (V1 to V2)
    // rather than registered empty. Version, not registration mechanics, is the behavior pinned.
    private static void AssertComposesScratchLineage(ServiceProvider provider)
    {
        var pipeline = provider.GetRequiredService<EventUpcasterPipeline>();
        pipeline.CurrentVersionFor(nameof(ScratchEventV2)).Should().Be(2);
    }

    private static ServiceCollection ScratchServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IEventTypeProvider, ScratchLineageProvider>();
        services.AddSingleton<IEventUpcaster>(new ScratchV1ToV2());
        return services;
    }

    // Scratch lineage: ScratchEventV2 is the registry-current terminal; ScratchEventV1 is the older
    // shape, reachable only through the one registered upcaster. Storage names default to the CLR
    // type name, so the terminal registers under nameof(ScratchEventV2).
    private sealed record ScratchEventV1(Guid Id) : IDomainEvent;
    private sealed record ScratchEventV2(Guid Id, string Note) : IDomainEvent;

    private sealed class ScratchLineageProvider : IEventTypeProvider
    {
        public IEnumerable<Type> GetEventTypes() => [typeof(ScratchEventV2)];
    }

    private sealed class ScratchV1ToV2 : Upcaster<ScratchEventV1, ScratchEventV2>
    {
        public override ScratchEventV2 Upcast(ScratchEventV1 from) => new(from.Id, Note: "");
    }
}
