using System.Text.Json;
using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Application.Context;
using EventSourcingCqrs.Application.Pipelines;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Queries.Sales;

public sealed class GetOrderHistoryHandlerTests
{
    private static readonly Guid OrderId = Guid.NewGuid();
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly TenantId Tenant = TenantId.From(Guid.NewGuid());
    private static readonly DateTime At = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly IPermissionAuthorizer Authorizer = new PermissionAuthorizer(new RolePermissionRegistry(RolePermissionPolicy.Default));

    [Theory]
    [InlineData(Role.Customer, true)]
    [InlineData(Role.Customer, false)]
    [InlineData(Role.Support, false)]
    [InlineData(Role.Admin, false)]
    public async Task The_query_uses_authoritative_owner_and_current_tenant(Role role, bool isOwner)
    {
        var source = new Reader();
        var context = new AsyncLocalQueryContextAccessor
        {
            Current = new QueryContext { ActorId = isOwner ? Owner : Guid.NewGuid(), Roles = [role], IsAuthenticatedUserQuery = true },
        };
        var tenant = new AsyncLocalCurrentTenantAccessor { Current = Tenant };
        var handler = new GetOrderHistoryHandler(new OrderHistoryReader(source), context, tenant, Authorizer, new ActorIsCustomerOwnershipResolver());

        var result = await handler.HandleAsync(new GetOrderHistory(OrderId), CancellationToken.None);

        if (role == Role.Customer && !isOwner) result.Should().BeNull();
        else result!.OrderId.Should().Be(OrderId);
        source.Streams.Should().OnlyContain(id => id == StreamId.ForAggregate<Order>(Tenant, OrderId));
    }

    [Fact]
    public async Task Missing_tenant_context_fails_before_any_history_read()
    {
        var source = new Reader();
        var handler = new GetOrderHistoryHandler(new OrderHistoryReader(source), new AsyncLocalQueryContextAccessor(),
            new AsyncLocalCurrentTenantAccessor(), Authorizer, new ActorIsCustomerOwnershipResolver());
        await FluentActions.Awaiting(() => handler.HandleAsync(new GetOrderHistory(OrderId), CancellationToken.None))
            .Should().ThrowAsync<MissingTenantContextException>();
        source.Streams.Should().BeEmpty();
    }

    [Fact]
    public async Task The_existing_query_pipeline_denies_a_principal_without_ViewOrder_before_history_is_read()
    {
        var context = new AsyncLocalQueryContextAccessor
        {
            Current = new QueryContext { ActorId = Owner, Roles = [], IsAuthenticatedUserQuery = true },
        };
        var called = false;
        var behavior = new AuthorizationQueryBehavior<GetOrderHistory, OrderHistoryView?>(context, Authorizer);
        await FluentActions.Awaiting(() => behavior.HandleAsync(new GetOrderHistory(OrderId), () =>
        {
            called = true;
            return Task.FromResult<OrderHistoryView?>(null);
        }, CancellationToken.None)).Should().ThrowAsync<UnauthorizedQueryException>();
        called.Should().BeFalse();
    }

    [Fact]
    public async Task Unsupported_providers_resolve_the_history_reader_and_return_a_named_capability_error()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<OrderHistoryReader>();
        await FluentActions.Awaiting(() => reader.ReadAsync(Tenant, OrderId, CancellationToken.None))
            .Should().ThrowAsync<OrderHistoryUnavailableException>();
    }

    [Fact]
    public void Application_registration_preserves_a_provider_specific_bounded_reader()
    {
        var services = new ServiceCollection();
        var reader = new Reader();
        services.AddSingleton<IBoundedEventStreamReader>(reader);
        services.AddApplication();
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IBoundedEventStreamReader>().Should().BeSameAs(reader);
        new SalesQueryTypeProvider().GetQueryTypes().Should().Contain(typeof(GetOrderHistory));
    }

    [Fact]
    public void Captured_history_round_trips_through_the_existing_web_json_shape()
    {
        var snapshot = new OrderSnapshot(OrderId, Owner, OrderStatus.Draft,
            [new OrderLine(Guid.NewGuid(), "NOTEBOOK", 2, new Money(12.5m, Currency.USD))],
            new Address("1 Main St", "Seattle", "98101", "US"));
        var view = new OrderHistoryView(OrderId, [new OrderHistoryStep(2, At, "Item added", snapshot, new Money(25m, Currency.USD))]);
        var copy = JsonSerializer.Deserialize<OrderHistoryView>(JsonSerializer.Serialize(view, JsonSerializerOptions.Web), JsonSerializerOptions.Web);
        copy.Should().BeEquivalentTo(view);
    }

    private sealed class Reader : IBoundedEventStreamReader
    {
        public List<StreamId> Streams { get; } = [];
        public Task<EventStreamWindow> ReadAsync(StreamId streamId, int maxEvents, CancellationToken ct)
        {
            Streams.Add(streamId);
            var id = Guid.NewGuid();
            var metadata = new EventMetadata(id, Guid.NewGuid(), Guid.NewGuid(), Owner, "test", At, Tenant);
            return Task.FromResult(new EventStreamWindow(
                [new EventEnvelope(streamId, 1, id, nameof(OrderDrafted), 2, new OrderDrafted(OrderId, Owner, At, "web"), metadata, At, 1)], false));
        }
    }
}
