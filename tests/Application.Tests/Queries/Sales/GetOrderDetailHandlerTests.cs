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

public sealed class GetOrderDetailHandlerTests
{
    private static readonly DateTime At = new(2026, 5, 20, 9, 0, 0, DateTimeKind.Utc);

    private static readonly IPermissionAuthorizer Authorizer =
        new PermissionAuthorizer(new RolePermissionRegistry(RolePermissionPolicy.Default));
    private static readonly IResourceOwnershipResolver Ownership = new ActorIsCustomerOwnershipResolver();

    private static GetOrderDetailHandler Handler(IOrderDetailStore store, IQueryContextAccessor accessor) =>
        new(store, accessor, Authorizer, Ownership);

    private static StubQueryContextAccessor NoContext => new() { Current = null };

    // Support holds ViewCustomer, so it is operational and sees any order.
    private static StubQueryContextAccessor Operational => new()
    {
        Current = new StubQueryContext { IsAuthenticatedUserQuery = true, Roles = new[] { Role.Support } },
    };

    // Customer does not hold ViewCustomer, so it is owner-scoped and sees only an order it owns.
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
    public async Task HandleAsync_returns_the_composed_view_for_a_known_order()
    {
        var orderId = Guid.NewGuid();
        var header = Header(orderId, Guid.NewGuid());
        var lines = new[]
        {
            new OrderDetailLineRow(orderId, Guid.NewGuid(), "SKU-1", 2, new Money(25m, Currency.USD)),
        };
        var timeline = new[]
        {
            new OrderDetailTimelineRow(orderId, 1, "OrderPlaced", At, """{"order_id":"x"}"""),
        };
        var store = new StubStore(header, lines, timeline);
        var handler = Handler(store, NoContext);

        var result = await handler.HandleAsync(new GetOrderDetail(orderId), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Header.Should().Be(header);
        result.Lines.Should().BeEquivalentTo(lines);
        result.Timeline.Should().BeEquivalentTo(timeline);
        // The handler passes the queried order id through to all three reads.
        store.LastOrderId.Should().Be(orderId);
    }

    [Fact]
    public async Task HandleAsync_returns_null_for_an_unknown_order()
    {
        var store = new StubStore(null, [], []);
        var handler = Handler(store, NoContext);

        var result = await handler.HandleAsync(new GetOrderDetail(Guid.NewGuid()), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_returns_a_view_with_empty_collections_for_a_header_without_lines_or_timeline()
    {
        // A header exists between OrderDrafted and the first OrderLineAdded: the
        // handler composes a view with empty lines and timeline rather than crashing.
        var orderId = Guid.NewGuid();
        var header = new OrderDetailRow(
            orderId, Guid.NewGuid(), OrderStatus.Draft,
            PlacedUtc: null, ShippedUtc: null, CancelledUtc: null, CompletedUtc: null, ReturnedUtc: null,
            Total: null, ShippingAddress: null, LastUpdatedUtc: At);
        var store = new StubStore(header, [], []);
        var handler = Handler(store, NoContext);

        var result = await handler.HandleAsync(new GetOrderDetail(orderId), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Header.Status.Should().Be(OrderStatus.Draft);
        result.Lines.Should().BeEmpty();
        result.Timeline.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_returns_the_view_for_an_owner_scoped_principal_querying_its_own_order()
    {
        var actor = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        // actor-equals-customer: the header's CustomerId is the actor id the resolver returns.
        var store = new StubStore(Header(orderId, actor), [], []);
        var handler = Handler(store, OwnerScoped(actor));

        var result = await handler.HandleAsync(new GetOrderDetail(orderId), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Header.OrderId.Should().Be(orderId);
    }

    [Fact]
    public async Task HandleAsync_returns_null_for_an_owner_scoped_principal_querying_another_customers_order()
    {
        var actor = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var store = new StubStore(Header(orderId, customerId: Guid.NewGuid()), [], []);
        var handler = Handler(store, OwnerScoped(actor));

        var result = await handler.HandleAsync(new GetOrderDetail(orderId), CancellationToken.None);

        // Null, not a distinct forbidden signal: the endpoint maps null to 404 so a customer cannot tell
        // an order it does not own from one that does not exist.
        result.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_returns_the_view_for_an_operational_principal_regardless_of_owner()
    {
        var orderId = Guid.NewGuid();
        var store = new StubStore(Header(orderId, customerId: Guid.NewGuid()), [], []);
        var handler = Handler(store, Operational);

        var result = await handler.HandleAsync(new GetOrderDetail(orderId), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Header.OrderId.Should().Be(orderId);
    }

    private static OrderDetailRow Header(Guid orderId, Guid customerId)
        => new(
            orderId, customerId, OrderStatus.Placed,
            PlacedUtc: At, ShippedUtc: null, CancelledUtc: null, CompletedUtc: null, ReturnedUtc: null,
            Total: new Money(50m, Currency.USD), ShippingAddress: null, LastUpdatedUtc: At);

    private sealed class StubStore : IOrderDetailStore
    {
        private readonly OrderDetailRow? _header;
        private readonly IReadOnlyList<OrderDetailLineRow> _lines;
        private readonly IReadOnlyList<OrderDetailTimelineRow> _timeline;

        public StubStore(
            OrderDetailRow? header,
            IReadOnlyList<OrderDetailLineRow> lines,
            IReadOnlyList<OrderDetailTimelineRow> timeline)
        {
            _header = header;
            _lines = lines;
            _timeline = timeline;
        }

        public Guid LastOrderId { get; private set; }

        public Task<OrderDetailRow?> GetHeaderAsync(Guid orderId, CancellationToken ct)
        {
            LastOrderId = orderId;
            return Task.FromResult(_header);
        }

        public Task<IReadOnlyList<OrderDetailLineRow>> GetLinesAsync(Guid orderId, CancellationToken ct)
            => Task.FromResult(_lines);

        public Task<IReadOnlyList<OrderDetailTimelineRow>> GetTimelineAsync(Guid orderId, CancellationToken ct)
            => Task.FromResult(_timeline);

        // The handler reaches only the three read methods; the write path and the
        // rebuild truncate are not its contract.
        public Task<IOrderDetailUnitOfWork> BeginAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public Task TruncateAsync(CancellationToken ct)
            => throw new NotSupportedException();
    }
}
