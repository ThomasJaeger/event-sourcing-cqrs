using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.EventStore.InMemory;
using EventSourcingCqrs.Infrastructure.Tests.TestKit;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests;

public class EventStoreRepositoryMetadataTests
{
    private static readonly Guid OrderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CustomerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LineId1 = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTime At = new(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Money TenUsd = new(10m, "USD");

    [Fact]
    public async Task SaveAsync_stamps_metadata_with_values_from_the_current_command_context()
    {
        var correlationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var commandId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var actorId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var store = new InMemoryEventStore();
        var accessor = new StubAccessor
        {
            Current = new StubContext
            {
                CorrelationId = correlationId,
                CausationCommandId = commandId,
                ActorId = actorId,
                ServiceName = "Api"
            }
        };
        var repo = new EventStoreRepository<Order>(store, accessor);
        var order = Order.Draft(OrderId, CustomerId, At);

        await repo.SaveAsync(order, CancellationToken.None);

        var envelopes = await store.ReadStreamAsync(OrderId, fromVersion: 0, CancellationToken.None);
        envelopes.Should().ContainSingle();
        var metadata = envelopes[0].Metadata;
        metadata.CorrelationId.Should().Be(correlationId);
        metadata.CausationId.Should().Be(commandId);
        metadata.ActorId.Should().Be(actorId);
        metadata.Source.Should().Be("Api");
    }

    [Fact]
    public async Task SaveAsync_falls_back_to_stub_values_when_the_accessor_has_no_current_context()
    {
        var store = new InMemoryEventStore();
        var accessor = new StubAccessor { Current = null };
        var repo = new EventStoreRepository<Order>(store, accessor);
        var order = Order.Draft(OrderId, CustomerId, At);

        await repo.SaveAsync(order, CancellationToken.None);

        var envelopes = await store.ReadStreamAsync(OrderId, fromVersion: 0, CancellationToken.None);
        var metadata = envelopes[0].Metadata;
        metadata.CorrelationId.Should().Be(Guid.Empty);
        metadata.CausationId.Should().Be(Guid.Empty);
        metadata.ActorId.Should().Be(Guid.Empty);
        metadata.Source.Should().Be("Workers");
    }

    [Fact]
    public async Task SaveAsync_chains_causation_across_multiple_events_in_the_same_batch()
    {
        var commandId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var store = new InMemoryEventStore();
        var accessor = new StubAccessor
        {
            Current = new StubContext { CausationCommandId = commandId }
        };
        var repo = new EventStoreRepository<Order>(store, accessor);

        var order = Order.Draft(OrderId, CustomerId, At);
        order.AddLine(LineId1, "SKU-1", 2, TenUsd, At);

        await repo.SaveAsync(order, CancellationToken.None);

        var envelopes = await store.ReadStreamAsync(OrderId, fromVersion: 0, CancellationToken.None);
        envelopes.Should().HaveCount(2);
        envelopes[0].Metadata.CausationId.Should().Be(commandId);
        envelopes[1].Metadata.CausationId.Should().Be(envelopes[0].Metadata.EventId);
    }

    [Fact]
    public async Task SaveAsync_chains_correlation_unchanged_across_a_batch()
    {
        var correlationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var store = new InMemoryEventStore();
        var accessor = new StubAccessor
        {
            Current = new StubContext { CorrelationId = correlationId }
        };
        var repo = new EventStoreRepository<Order>(store, accessor);

        var order = Order.Draft(OrderId, CustomerId, At);
        order.AddLine(LineId1, "SKU-1", 2, TenUsd, At);

        await repo.SaveAsync(order, CancellationToken.None);

        var envelopes = await store.ReadStreamAsync(OrderId, fromVersion: 0, CancellationToken.None);
        envelopes.Should().AllSatisfy(e => e.Metadata.CorrelationId.Should().Be(correlationId));
    }
}
