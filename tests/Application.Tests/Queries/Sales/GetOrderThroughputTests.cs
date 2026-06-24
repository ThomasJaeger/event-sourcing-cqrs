using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Application.Pipelines;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Queries.Sales;

// Characterization of the admin-only order-throughput query (P11.12b). The authorization machinery is
// existing tested production code, so the refusal and allow specs pass on write; the handler is a thin
// forwarding delegate over IOrderThroughputStore.GetBucketsAsync. All three specs are green-on-write.
public sealed class GetOrderThroughputTests
{
    private static AuthorizationQueryBehavior<GetOrderThroughput, IReadOnlyList<OrderThroughputRow>> Behavior(
        IQueryContextAccessor accessor) =>
        new(accessor, new PermissionAuthorizer(new RolePermissionRegistry(RolePermissionPolicy.Default)));

    [Fact]
    public async Task A_customer_principal_is_refused_order_throughput()
    {
        // P11.12b (green on write): ViewOrderThroughput is admin-only, so a Customer is rejected before the
        // handler runs, the same gate the InventoryOnlyQuery refusal spec proves for the behavior.
        var accessor = new StubQueryContextAccessor
        {
            Current = new StubQueryContext
            {
                IsAuthenticatedUserQuery = true,
                Roles = new[] { Role.Customer },
            },
        };
        var nextInvoked = false;

        var act = () => Behavior(accessor).HandleAsync(
            new GetOrderThroughput(),
            () => { nextInvoked = true; return Task.FromResult<IReadOnlyList<OrderThroughputRow>>([]); },
            CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedQueryException>()
            .Where(e => e.RequiredPermission == Permission.ViewOrderThroughput
                && e.QueryType == typeof(GetOrderThroughput));
        nextInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task An_admin_principal_reaches_the_handler_for_order_throughput()
    {
        // P11.12b (green on write): Admin holds ViewOrderThroughput through the Enum.GetValues auto-grant,
        // so the behavior passes the query through to the handler.
        var accessor = new StubQueryContextAccessor
        {
            Current = new StubQueryContext
            {
                IsAuthenticatedUserQuery = true,
                Roles = new[] { Role.Admin },
            },
        };
        var nextInvoked = false;

        await Behavior(accessor).HandleAsync(
            new GetOrderThroughput(),
            () => { nextInvoked = true; return Task.FromResult<IReadOnlyList<OrderThroughputRow>>([]); },
            CancellationToken.None);

        nextInvoked.Should().BeTrue();
    }

    [Fact]
    public async Task The_handler_returns_the_buckets_the_store_produces()
    {
        // P11.12b (green on write): the handler forwards to IOrderThroughputStore.GetBucketsAsync, so it
        // returns exactly the store's rows.
        var buckets = new[]
        {
            new OrderThroughputRow(new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc), 3),
            new OrderThroughputRow(new DateTime(2026, 5, 18, 12, 0, 1, DateTimeKind.Utc), 7),
        };
        var store = new StubStore(buckets);
        var handler = new GetOrderThroughputHandler(store);

        var result = await handler.HandleAsync(new GetOrderThroughput(), CancellationToken.None);

        result.Should().BeEquivalentTo(buckets);
    }

    private sealed class StubStore : IOrderThroughputStore
    {
        private readonly IReadOnlyList<OrderThroughputRow> _buckets;

        public StubStore(IReadOnlyList<OrderThroughputRow> buckets) => _buckets = buckets;

        public Task<IReadOnlyList<OrderThroughputRow>> GetBucketsAsync(CancellationToken ct)
            => Task.FromResult(_buckets);

        // The handler reaches only the read method; the write path is not its contract.
        public Task<IOrderThroughputUnitOfWork> BeginAsync(CancellationToken ct)
            => throw new NotSupportedException();
    }
}
