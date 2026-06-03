using System.Net;
using System.Net.Http.Json;
using EventSourcingCqrs.Application.Queries.Fulfillment;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.IntegrationTests.Queries;

// The single home of each registered query type's cross-tenant isolation case (ADR 0031's
// coverage mandate). One lambda per query type seeds a second tenant's row by direct insert,
// dispatches the query as the default tenant through the bus, and asserts the second tenant's
// data is invisible. Each lambda is self-contained in its state setup (a list query truncates
// its read model first; a single query uses a unique key) because it runs both from a
// standalone test and from the registry-driven meta-test, and xUnit guarantees no ordering
// between them. The logic lives here once; the standalone tests and the meta-test invoke it.
internal static class CrossTenantQueryCases
{
    private static readonly Guid OtherTenant = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTime SeededAt = new(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);
    private const string DefaultTenantSku = "SKU-DEFAULT-TENANT";

    // One entry per registered query type. A registered type with no entry here is what the
    // meta-test's completeness check flags, so a new query that ships without a case fails the build.
    public static IReadOnlyDictionary<Type, Func<Task>> For(ApiFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return new Dictionary<Type, Func<Task>>
        {
            [typeof(ListOrders)] = () => ListOrdersIsolatesAsync(fixture),
            [typeof(GetOrderDetail)] = () => GetOrderDetailIsolatesAsync(fixture),
            [typeof(GetCustomerSummary)] = () => GetCustomerSummaryIsolatesAsync(fixture),
            [typeof(GetAllInventoryDashboard)] = () => GetAllInventoryDashboardIsolatesAsync(fixture),
            [typeof(GetInventoryDashboardBySku)] = () => GetInventoryDashboardBySkuIsolatesAsync(fixture),
        };
    }

    private static async Task ListOrdersIsolatesAsync(ApiFixture fixture)
    {
        var store = fixture.Factory.Services.GetRequiredService<IOrderListStore>();
        await store.TruncateAsync(default);
        var orderA = Guid.NewGuid();
        var orderB = Guid.NewGuid();
        await fixture.SeedAsTenantAsync(WellKnownTenants.Default, async () =>
        {
            await using (var uow = await store.BeginAsync(default))
            {
                await uow.InsertAsync(OrderListRowFor(orderA), default);
                await uow.CommitAsync("cross-tenant-list-orders", 1, default);
            }
        });
        await SeedOrderListRowUnderTenantAsync(fixture, orderB, OtherTenant);

        var response = await fixture.Factory.CreateClient()
            .PostQueryAsync("ListOrders", new { offset = 0, limit = 50 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var ids = (await response.Content.ReadFromJsonAsync<IReadOnlyList<OrderListRow>>())!
            .Select(r => r.OrderId).ToList();
        ids.Should().Contain(orderA);
        ids.Should().NotContain(orderB);
    }

    private static async Task GetOrderDetailIsolatesAsync(ApiFixture fixture)
    {
        var orderId = Guid.NewGuid();
        await SeedOrderDetailHeaderUnderTenantAsync(fixture, orderId, Guid.NewGuid(), OtherTenant);

        var response = await fixture.Factory.CreateClient()
            .PostQueryAsync("GetOrderDetail", new { orderId });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task GetCustomerSummaryIsolatesAsync(ApiFixture fixture)
    {
        var customerId = Guid.NewGuid();
        await SeedCustomerSummaryUnderTenantAsync(fixture, customerId, OtherTenant);

        var response = await fixture.Factory.CreateClient()
            .PostQueryAsync("GetCustomerSummary", new { customerId });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task GetAllInventoryDashboardIsolatesAsync(ApiFixture fixture)
    {
        var store = fixture.Factory.Services.GetRequiredService<IInventoryDashboardStore>();
        await store.TruncateAsync(default);
        await fixture.SeedAsTenantAsync(WellKnownTenants.Default, async () =>
        {
            await using (var uow = await store.BeginAsync(default))
            {
                await uow.CreateDashboardAsync(Guid.NewGuid(), DefaultTenantSku, SeededAt, default);
                await uow.CommitAsync("cross-tenant-inventory-all", 1, default);
            }
        });
        var otherSku = "SKU-OTHER-" + Guid.NewGuid().ToString("N");
        await SeedInventoryDashboardUnderTenantAsync(fixture, Guid.NewGuid(), otherSku, OtherTenant);

        var response = await fixture.Factory.CreateClient()
            .PostQueryAsync("GetAllInventoryDashboard", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var skus = (await response.Content.ReadFromJsonAsync<IReadOnlyList<InventoryDashboardRow>>())!
            .Select(r => r.Sku).ToList();
        skus.Should().Contain(DefaultTenantSku);
        skus.Should().NotContain(otherSku);
    }

    private static async Task GetInventoryDashboardBySkuIsolatesAsync(ApiFixture fixture)
    {
        var otherSku = "SKU-OTHER-" + Guid.NewGuid().ToString("N");
        await SeedInventoryDashboardUnderTenantAsync(fixture, Guid.NewGuid(), otherSku, OtherTenant);

        var response = await fixture.Factory.CreateClient()
            .PostQueryAsync("GetInventoryDashboardBySku", new { sku = otherSku });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static OrderListRow OrderListRowFor(Guid orderId) => new(
        OrderId: orderId,
        CustomerId: Guid.NewGuid(),
        Status: OrderStatus.Placed,
        Total: new Money(10m, Currency.USD),
        PlacedUtc: SeededAt,
        LastUpdatedUtc: SeededAt,
        IsReturned: false,
        ReturnedUtc: null);

    private static async Task SeedOrderListRowUnderTenantAsync(ApiFixture fixture, Guid orderId, Guid tenant)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO read_models.order_list " +
            "(order_id, customer_id, status, total_amount, total_currency, placed_utc, last_updated_utc, tenant_id) " +
            "VALUES (@order_id, @customer_id, @status, @total_amount, @total_currency, @placed_utc, @last_updated_utc, @tenant_id)";
        command.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        command.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, Guid.NewGuid());
        command.Parameters.AddWithValue("status", NpgsqlDbType.Text, "Placed");
        command.Parameters.AddWithValue("total_amount", NpgsqlDbType.Numeric, 10m);
        command.Parameters.AddWithValue("total_currency", NpgsqlDbType.Text, "USD");
        command.Parameters.AddWithValue("placed_utc", NpgsqlDbType.TimestampTz, SeededAt);
        command.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, SeededAt);
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenant);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedOrderDetailHeaderUnderTenantAsync(
        ApiFixture fixture, Guid orderId, Guid customerId, Guid tenant)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO read_models.order_detail (order_id, customer_id, status, last_updated_utc, tenant_id) " +
            "VALUES (@order_id, @customer_id, @status, @last_updated_utc, @tenant_id)";
        command.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        command.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, customerId);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Text, "Placed");
        command.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, SeededAt);
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenant);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedCustomerSummaryUnderTenantAsync(ApiFixture fixture, Guid customerId, Guid tenant)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO read_models.customer_summary " +
            "(customer_id, order_count, lifetime_value_amount, lifetime_value_currency, " +
            "last_order_utc, last_updated_utc, tenant_id) " +
            "VALUES (@customer_id, @order_count, @lifetime_value_amount, @lifetime_value_currency, " +
            "@last_order_utc, @last_updated_utc, @tenant_id)";
        command.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, customerId);
        command.Parameters.AddWithValue("order_count", NpgsqlDbType.Integer, 1);
        command.Parameters.AddWithValue("lifetime_value_amount", NpgsqlDbType.Numeric, 50m);
        command.Parameters.AddWithValue("lifetime_value_currency", NpgsqlDbType.Text, "USD");
        command.Parameters.AddWithValue("last_order_utc", NpgsqlDbType.TimestampTz, SeededAt);
        command.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, SeededAt);
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenant);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedInventoryDashboardUnderTenantAsync(
        ApiFixture fixture, Guid inventoryId, string sku, Guid tenant)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO read_models.inventory_dashboard " +
            "(inventory_id, sku, on_hand_quantity, reserved_quantity, last_updated_utc, tenant_id) " +
            "VALUES (@inventory_id, @sku, @on_hand_quantity, @reserved_quantity, @last_updated_utc, @tenant_id)";
        command.Parameters.AddWithValue("inventory_id", NpgsqlDbType.Uuid, inventoryId);
        command.Parameters.AddWithValue("sku", NpgsqlDbType.Text, sku);
        command.Parameters.AddWithValue("on_hand_quantity", NpgsqlDbType.Integer, 0);
        command.Parameters.AddWithValue("reserved_quantity", NpgsqlDbType.Integer, 0);
        command.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, SeededAt);
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenant);
        await command.ExecuteNonQueryAsync();
    }
}
