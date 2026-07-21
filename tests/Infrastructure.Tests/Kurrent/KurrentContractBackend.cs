using System.Text.Json;
using DotNet.Testcontainers.Containers;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.EventStore.ContractTests;
using EventSourcingCqrs.Infrastructure.EventStore.Kurrent;
using EventSourcingCqrs.Infrastructure.Versioning;
using KurrentDB.Client;
using Microsoft.Extensions.DependencyInjection;

namespace EventSourcingCqrs.Infrastructure.Tests.Kurrent;

// One backend per fact: its own fresh KurrentDB node, its own composed store, torn down with the
// fact. The suite's event types register into the adapter's registries here through the same
// provider seam AddKurrentEventStore walks, which is the whole job of a backend; the suite itself
// knows nothing about EventTypeRegistry.
//
// It implements the universal IEventStoreContractBackend only, not IHeldWriterContractBackend:
// KurrentDB has no interactive, held-open append, so the held-writer probes do not run here.
internal sealed class KurrentContractBackend : IEventStoreContractBackend, IUpcastSeedingContractBackend
{
    private readonly IContainer _container;
    private readonly ServiceProvider _provider;
    private readonly KurrentDBClient _client;

    private KurrentContractBackend(
        IContainer container, ServiceProvider provider, KurrentDBClient client, IEventStore store)
    {
        _container = container;
        _provider = provider;
        _client = client;
        Store = store;
    }

    public IEventStore Store { get; }

    public static async Task<KurrentContractBackend> CreateAsync(KurrentFixture fixture)
    {
        var container = await fixture.StartNodeAsync();
        try
        {
            var connectionString =
                $"esdb://{container.Hostname}:{container.GetMappedPublicPort(2113)}?tls=false";

            var services = new ServiceCollection();
            services.AddSingleton<IEventTypeProvider, ContractDomainEventTypeProvider>();
            services.AddSingleton<IProcessManagerEventTypeProvider, ContractProcessManagerEventTypeProvider>();
            // The upcaster reaches the composed pipeline the way a host contributes one: an
            // IEventUpcaster singleton the Add* registration enumerates into EventUpcasterPipeline.
            services.AddSingleton<IEventUpcaster, ContractItemArchivedV1ToV2>();
            services.AddKurrentEventStore(opts => opts.ConnectionString = connectionString);

            var provider = services.BuildServiceProvider();
            try
            {
                // Resolving the store runs its constructor and forces the registry provider walk,
                // so a registration defect surfaces here rather than as a fact failure. The client
                // is the same singleton the store holds; the provider owns its disposal.
                var store = provider.GetRequiredService<IEventStore>();
                var client = provider.GetRequiredService<KurrentDBClient>();
                return new KurrentContractBackend(container, provider, client, store);
            }
            catch
            {
                await provider.DisposeAsync();
                throw;
            }
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
    }

    public async Task<IReadOnlyList<long>> ReadCommittedPositionsAsync()
    {
        var positions = new List<long>();
        await foreach (var resolved in _client.ReadAllAsync(Direction.Forwards, Position.Start))
        {
            // Include pm- streams: the committed-position sequence spans both append paths, so a
            // probe blind to PM rows would be blind to half the contract. Exclude KurrentDB system
            // events, whose stream name or event type carries the '$' sigil.
            if (resolved.OriginalStreamId.StartsWith('$') || resolved.Event.EventType.StartsWith('$'))
            {
                continue;
            }
            positions.Add(checked((long)resolved.Event.Position.CommitPosition));
        }
        return positions;
    }

    // A historical write, reproduced through the adapter's own append with a throwaway registry that
    // maps the V1 shape onto the terminal's stable storage name. The stored event lands at schema
    // version 1 with the V1 payload; the suite's own store reads it back through the real pipeline.
    public async Task SeedVersionOneAsync(
        StreamId stream, string terminalStorageName, IDomainEvent versionOnePayload)
    {
        var seedRegistry = new EventTypeRegistry().Register(
            versionOnePayload.GetType(), terminalStorageName);
        var seedStore = new KurrentEventStore(
            _client,
            seedRegistry,
            new ProcessManagerEventTypeRegistry(),
            _provider.GetRequiredService<JsonSerializerOptions>(),
            new EventUpcasterPipeline(seedRegistry, []));
        await seedStore.AppendAsync(
            stream, 0, [ContractEnvelopes.Build(stream, 1, versionOnePayload)], CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        // Disposing the provider disposes the singleton KurrentDBClient with it; then the node goes.
        await _provider.DisposeAsync();
        await _container.DisposeAsync();
    }
}

// The suite's event types, handed to AddKurrentEventStore through the same provider seam a host
// uses. Registered per bounded context on a real host; here the whole suite is one "context".
internal sealed class ContractDomainEventTypeProvider : IEventTypeProvider
{
    public IEnumerable<Type> GetEventTypes() => ContractEventTypes.DomainEvents;
}

internal sealed class ContractProcessManagerEventTypeProvider : IProcessManagerEventTypeProvider
{
    public IEnumerable<Type> GetEventTypes() => ContractEventTypes.ProcessManagerEvents;
}
