using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Infrastructure.EventStore.InMemory;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests;

// The tenant-sourcing invariant on the aggregate write path: a command context in flight with no
// tenant fails closed, and the no-context fallback stamps the default tenant on both the metadata and
// the stream id (so the legacy and worker path is unchanged).
public sealed class EventStoreRepositoryTenantTests
{
    private static readonly Guid OrderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CustomerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime At = new(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId Tenant =
        TenantId.From(Guid.Parse("44444444-4444-4444-4444-444444444444"));

    [Fact]
    public async Task SaveAsync_with_a_command_context_but_no_tenant_throws_MissingTenantContextException()
    {
        var store = new InMemoryEventStore();
        var contextAccessor = new StubCommandContextAccessor
        {
            Current = new CommandContext
            {
                CorrelationId = Guid.NewGuid(),
                CausationCommandId = Guid.NewGuid(),
                ActorId = Guid.Empty,
                ServiceName = "Api"
            }
        };
        var repo = new EventStoreRepository<Order>(
            store, contextAccessor, new StubTenantAccessor { Current = null });
        var order = Order.Draft(OrderId, CustomerId, At);

        var act = () => repo.SaveAsync(order, CancellationToken.None);

        await act.Should().ThrowAsync<MissingTenantContextException>();
    }

    [Fact]
    public async Task SaveAsync_with_no_command_context_stamps_the_default_tenant_on_metadata_and_stream_id()
    {
        var store = new InMemoryEventStore();
        var repo = new EventStoreRepository<Order>(
            store, new StubCommandContextAccessor { Current = null }, new StubTenantAccessor());
        var order = Order.Draft(OrderId, CustomerId, At);

        await repo.SaveAsync(order, CancellationToken.None);

        var defaultStream = StreamId.ForAggregate<Order>(WellKnownTenants.Default, OrderId);
        var envelopes = await store.ReadStreamAsync(defaultStream, fromVersion: 0, CancellationToken.None);
        envelopes.Should().ContainSingle();
        envelopes[0].Metadata.Tenant.Should().Be(WellKnownTenants.Default);
        envelopes[0].StreamId.Should().Be(defaultStream);
    }

    // LoadAsync off the command path resolves the tenant from the accessor, so a read on the
    // dispatcher-only process-manager loop (a set tenant, no command context) still targets its own
    // tenant's stream. RED today: ResolveTenant branches on the command-context accessor, so with the
    // context null it returns WellKnownTenants.Default and reads the empty Default-form stream, even
    // though the tenant accessor holds T. The save runs with the context present, so it resolves T and
    // lands the order at the tenant-form stream the load must read back.
    [Fact]
    public async Task LoadAsync_resolves_the_tenant_from_the_accessor_when_no_command_context_is_in_flight()
    {
        var store = new InMemoryEventStore();
        var contextAccessor = new StubCommandContextAccessor
        {
            Current = new CommandContext
            {
                CorrelationId = Guid.NewGuid(),
                CausationCommandId = Guid.NewGuid(),
                ActorId = Guid.Empty,
                ServiceName = "Workers"
            }
        };
        var repo = new EventStoreRepository<Order>(
            store, contextAccessor, new StubTenantAccessor { Current = Tenant });
        var order = Order.Draft(OrderId, CustomerId, At);

        await repo.SaveAsync(order, CancellationToken.None);

        // The present context made ResolveTenant return T, so the events live at the tenant-form stream.
        var tenantStream = StreamId.ForAggregate<Order>(Tenant, OrderId);
        (await store.ReadStreamAsync(tenantStream, fromVersion: 0, CancellationToken.None))
            .Should().ContainSingle();

        // Drop to the dispatcher-only PM-loop state: the tenant accessor still holds T, the context is gone.
        contextAccessor.Current = null;

        var loaded = await repo.LoadAsync(OrderId, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(OrderId);
        loaded.Version.Should().Be(order.Version);
    }
}
