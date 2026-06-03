using System.Net;
using System.Net.Http.Json;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Authorization;

// Proves query authorization end to end at the Api host: the permission gate refuses a query whose
// permission the principal's roles do not grant, and the ownership filter scopes a customer to its own
// orders while an operational principal sees all. The forwarded identity carries the actor id with an
// empty role set the way the Web host signs it; the Api host loads the actor's authoritative roles from
// the read model the fixture seeds.
public class QueryAuthorizationTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    private static readonly DateTime SeededAt =
        new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    public QueryAuthorizationTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_query_whose_permission_the_principals_roles_do_not_grant_is_refused_with_403()
    {
        // GetInventoryDashboardBySku requires ViewInventory, which Customer does not hold.
        var customerActor = Guid.NewGuid();
        await _fixture.SeedRoleAsync(customerActor, Role.Customer);
        var client = _fixture.Factory.CreateClient();

        var response = await client.PostQueryAsAsync(
            "GetInventoryDashboardBySku", new { sku = "SKU-1" }, customerActor);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_roleless_authenticated_principal_is_denied_at_the_behavior_not_filtered_to_empty()
    {
        // An authenticated actor the read model returns an empty role set for: it is never seeded, so it
        // holds no roles. ListOrders requires ViewOrder, which an empty role set does not grant, so the
        // authorization behavior denies with 403 before the handler runs. A 200 with an empty array would
        // mean the ownership filter ran and returned empty, which is the failure mode this test exists to
        // catch: the behavior denies first, so the filter is never the last line of defense. ListOrders is
        // an order query, so this proves the order path denies before the ownership filter, the parallel
        // to the inventory 403 above.
        var rolelessActor = Guid.NewGuid();
        var client = _fixture.Factory.CreateClient();

        var response = await client.PostQueryAsAsync(
            "ListOrders", new { offset = 0, limit = 10 }, rolelessActor);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // Confirm the FORBIDDEN body, not a 200 with an empty array: a 200 would mean the filter, not the
        // behavior, was the line of defense.
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("FORBIDDEN");
    }

    [Fact]
    public async Task An_operational_principal_sees_all_orders_from_ListOrders()
    {
        var supportActor = Guid.NewGuid();
        await _fixture.SeedRoleAsync(supportActor, Role.Support);
        var client = _fixture.Factory.CreateClient();

        var store = _fixture.Factory.Services.GetRequiredService<IOrderListStore>();
        await store.TruncateAsync(default);
        await _fixture.SeedAsTenantAsync(WellKnownTenants.Default, async () =>
        {
            await using (var uow = await store.BeginAsync(default))
            {
                await uow.InsertAsync(SampleOrderRow(Guid.NewGuid()), default);
                await uow.InsertAsync(SampleOrderRow(Guid.NewGuid()), default);
                await uow.CommitAsync("query-authz-support-seed", 1, default);
            }
        });

        var response = await client.PostQueryAsAsync("ListOrders", new { offset = 0, limit = 10 }, supportActor);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<IReadOnlyList<OrderListRow>>();
        rows!.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_owner_scoped_principal_sees_only_its_own_orders_from_ListOrders()
    {
        var customerActor = Guid.NewGuid();
        await _fixture.SeedRoleAsync(customerActor, Role.Customer);
        var client = _fixture.Factory.CreateClient();

        var store = _fixture.Factory.Services.GetRequiredService<IOrderListStore>();
        await store.TruncateAsync(default);
        // actor-equals-customer: the owned row's CustomerId is the actor id.
        var owned = SampleOrderRow(customerActor);
        var someoneElses = SampleOrderRow(Guid.NewGuid());
        await _fixture.SeedAsTenantAsync(WellKnownTenants.Default, async () =>
        {
            await using (var uow = await store.BeginAsync(default))
            {
                await uow.InsertAsync(owned, default);
                await uow.InsertAsync(someoneElses, default);
                await uow.CommitAsync("query-authz-owner-seed", 1, default);
            }
        });

        var response = await client.PostQueryAsAsync("ListOrders", new { offset = 0, limit = 10 }, customerActor);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<IReadOnlyList<OrderListRow>>();
        rows!.Should().ContainSingle().Which.OrderId.Should().Be(owned.OrderId);
    }

    [Fact]
    public async Task An_owner_scoped_principal_gets_its_own_order_detail_and_404_for_another_customers()
    {
        var customerActor = Guid.NewGuid();
        await _fixture.SeedRoleAsync(customerActor, Role.Customer);
        var client = _fixture.Factory.CreateClient();

        var ownedOrderId = Guid.NewGuid();
        var othersOrderId = Guid.NewGuid();
        var store = _fixture.Factory.Services.GetRequiredService<IOrderDetailStore>();
        await _fixture.SeedAsTenantAsync(WellKnownTenants.Default, async () =>
        {
            await using (var uow = await store.BeginAsync(default))
            {
                await uow.CreateHeaderAsync(ownedOrderId, customerActor, SeededAt, default);
                await uow.CreateHeaderAsync(othersOrderId, Guid.NewGuid(), SeededAt, default);
                await uow.CommitAsync("query-authz-detail-seed", 1, default);
            }
        });

        var ownResponse = await client.PostQueryAsAsync(
            "GetOrderDetail", new { orderId = ownedOrderId }, customerActor);
        ownResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await ownResponse.Content.ReadFromJsonAsync<OrderDetailView>();
        view!.Header.OrderId.Should().Be(ownedOrderId);

        // The other customer's order reads as 404, not 403: the null-to-404 mapping hides its existence.
        var otherResponse = await client.PostQueryAsAsync(
            "GetOrderDetail", new { orderId = othersOrderId }, customerActor);
        otherResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static OrderListRow SampleOrderRow(Guid customerId) => new OrderListRow(
        OrderId: Guid.NewGuid(),
        CustomerId: customerId,
        Status: OrderStatus.Placed,
        Total: new Money(149.95m, Currency.USD),
        PlacedUtc: SeededAt,
        LastUpdatedUtc: SeededAt,
        IsReturned: false,
        ReturnedUtc: null);
}
