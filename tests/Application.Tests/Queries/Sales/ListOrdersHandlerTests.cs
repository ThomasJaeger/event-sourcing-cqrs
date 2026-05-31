using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Queries.Sales;

public sealed class ListOrdersHandlerTests
{
    private static readonly DateTime PlacedAt = new(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc);

    private static readonly IPermissionAuthorizer Authorizer =
        new PermissionAuthorizer(new RolePermissionRegistry(RolePermissionPolicy.Default));
    private static readonly IResourceOwnershipResolver Ownership = new ActorIsCustomerOwnershipResolver();

    private static ListOrdersHandler Handler(IOrderListStore store, IQueryContextAccessor accessor) =>
        new(store, accessor, Authorizer, Ownership);

    // A non-authenticated query: the handler treats it as operational, so the pass-through assertions
    // hold without an ownership filter.
    private static StubQueryContextAccessor NoContext => new() { Current = null };

    // Support holds ViewCustomer, so it is operational and sees every order unfiltered.
    private static StubQueryContextAccessor Operational => new()
    {
        Current = new StubQueryContext { IsAuthenticatedUserQuery = true, Roles = new[] { Role.Support } },
    };

    // Customer does not hold ViewCustomer, so it is owner-scoped and sees only the orders it owns.
    private static StubQueryContextAccessor OwnerScoped(Guid actorId) => new()
    {
        Current = new StubQueryContext
        {
            IsAuthenticatedUserQuery = true,
            ActorId = actorId,
            Roles = new[] { Role.Customer },
        },
    };

    [Fact]
    public async Task HandleAsync_returns_the_rows_the_store_produces()
    {
        var rows = new[]
        {
            SampleRow(Guid.NewGuid()),
            SampleRow(Guid.NewGuid())
        };
        var store = new StubStore(rows);
        var handler = Handler(store, NoContext);

        var result = await handler.HandleAsync(new ListOrders(Offset: 0, Limit: 50), CancellationToken.None);

        result.Should().BeEquivalentTo(rows);
    }

    [Fact]
    public async Task HandleAsync_propagates_offset_and_limit_to_the_store()
    {
        var store = new StubStore(Array.Empty<OrderListRow>());
        var handler = Handler(store, NoContext);

        await handler.HandleAsync(new ListOrders(Offset: 25, Limit: 10), CancellationToken.None);

        store.LastOffset.Should().Be(25);
        store.LastLimit.Should().Be(10);
    }

    [Fact]
    public async Task HandleAsync_returns_all_rows_for_an_operational_principal()
    {
        var rows = new[] { SampleRow(Guid.NewGuid()), SampleRow(Guid.NewGuid()) };
        var store = new StubStore(rows);
        var handler = Handler(store, Operational);

        var result = await handler.HandleAsync(new ListOrders(0, 50), CancellationToken.None);

        result.Should().BeEquivalentTo(rows);
    }

    [Fact]
    public async Task HandleAsync_filters_to_the_owners_rows_for_an_owner_scoped_principal()
    {
        var actor = Guid.NewGuid();
        // actor-equals-customer: the owned row's CustomerId is the actor id the resolver returns.
        var owned = SampleRow(actor);
        var someoneElses = SampleRow(Guid.NewGuid());
        var store = new StubStore(new[] { owned, someoneElses });
        var handler = Handler(store, OwnerScoped(actor));

        var result = await handler.HandleAsync(new ListOrders(0, 50), CancellationToken.None);

        result.Should().ContainSingle().Which.OrderId.Should().Be(owned.OrderId);
    }

    private static OrderListRow SampleRow(Guid customerId)
        => new(
            OrderId: Guid.NewGuid(),
            CustomerId: customerId,
            Status: OrderStatus.Placed,
            Total: new Money(50m, Currency.USD),
            PlacedUtc: PlacedAt,
            LastUpdatedUtc: PlacedAt,
            IsReturned: false,
            ReturnedUtc: null);

    private sealed class StubStore : IOrderListStore
    {
        private readonly IReadOnlyList<OrderListRow> _rows;

        public StubStore(IReadOnlyList<OrderListRow> rows) => _rows = rows;

        public int LastOffset { get; private set; }
        public int LastLimit { get; private set; }

        public Task<IReadOnlyList<OrderListRow>> GetPageAsync(int offset, int limit, CancellationToken ct)
        {
            LastOffset = offset;
            LastLimit = limit;
            return Task.FromResult(_rows);
        }

        public Task<IOrderListUnitOfWork> BeginAsync(CancellationToken ct)
            => throw new NotSupportedException("Handler should not need a unit of work.");

        public Task<OrderListRow?> GetAsync(Guid orderId, CancellationToken ct)
            => throw new NotSupportedException("Handler should not call GetAsync.");

        public Task TruncateAsync(CancellationToken ct)
            => throw new NotSupportedException("Handler should not truncate.");
    }
}
