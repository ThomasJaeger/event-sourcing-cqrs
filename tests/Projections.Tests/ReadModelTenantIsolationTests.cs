using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// Read-side tenant isolation on the seven query-bus-only read-model reads (P10.5,
// ADR 0031), in two halves. The cross-tenant tests seed a default-tenant row through
// the unit of work and a second-tenant row by a direct insert that names tenant_id,
// set the accessor to the default tenant, and assert the read returns only the
// default tenant's rows. The fail-closed tests set no tenant and assert the read
// throws MissingTenantContextException before it touches the database, which is why
// each read resolves the tenant as its first statement. GetHeaderAsync is not here:
// its subscription-authorization caller has no tenant set, so its predicate lands
// with that set point in the next commit.
public class ReadModelTenantIsolationTests : IClassFixture<PostgresFixture>
{
    private static readonly TenantId TenantA = WellKnownTenants.Default;
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly DateTime At = new(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public ReadModelTenantIsolationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    // ---- Cross-tenant isolation: a default-tenant read sees none of tenant B's rows ----

    [Fact]
    public async Task OrderList_GetAsync_under_the_default_tenant_does_not_see_a_second_tenant_row()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var store = OrderListStore(ds, TenantA);
        var orderA = Guid.NewGuid();
        var orderB = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.InsertAsync(OrderListRowFor(orderA), CancellationToken.None);
            await uow.CommitAsync("order-list", 1, CancellationToken.None);
        }
        await SeedOrderListRowAsync(connStr, orderB, TenantB);

        (await store.GetAsync(orderA, CancellationToken.None)).Should().NotBeNull();
        (await store.GetAsync(orderB, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task OrderList_GetPageAsync_under_the_default_tenant_returns_only_default_tenant_rows()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var store = OrderListStore(ds, TenantA);
        var orderA1 = Guid.NewGuid();
        var orderA2 = Guid.NewGuid();
        var orderB = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.InsertAsync(OrderListRowFor(orderA1), CancellationToken.None);
            await uow.InsertAsync(OrderListRowFor(orderA2), CancellationToken.None);
            await uow.CommitAsync("order-list", 1, CancellationToken.None);
        }
        await SeedOrderListRowAsync(connStr, orderB, TenantB);

        var page = await store.GetPageAsync(0, 50, CancellationToken.None);
        page.Should().HaveCount(2);
        page.Select(r => r.OrderId).Should().NotContain(orderB);
    }

    [Fact]
    public async Task OrderDetail_GetLinesAsync_under_the_default_tenant_returns_only_default_tenant_lines()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var store = OrderDetailStore(ds, TenantA);
        var orderId = Guid.NewGuid();
        var lineA = Guid.NewGuid();
        var lineB = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.InsertLineAsync(
                new OrderDetailLineRow(orderId, lineA, "SKU-A", 1, new Money(5m, Currency.USD)),
                CancellationToken.None);
            await uow.CommitAsync("order-detail", 1, CancellationToken.None);
        }
        // Same order id under tenant B, a distinct line id (the line PK is
        // (order_id, line_id)), so only the tenant predicate can filter it out.
        await SeedOrderDetailLineAsync(connStr, orderId, lineB, TenantB);

        var lines = await store.GetLinesAsync(orderId, CancellationToken.None);
        lines.Should().ContainSingle();
        lines[0].LineId.Should().Be(lineA);
    }

    [Fact]
    public async Task OrderDetail_GetTimelineAsync_under_the_default_tenant_returns_only_default_tenant_entries()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var store = OrderDetailStore(ds, TenantA);
        var orderId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.AppendTimelineAsync(
                new OrderDetailTimelineRow(orderId, 10, "OrderPlaced", At, "{}"),
                CancellationToken.None);
            await uow.CommitAsync("order-detail", 10, CancellationToken.None);
        }
        // Same order id under tenant B, a distinct global position (the timeline PK
        // is (order_id, global_position)), so only the tenant predicate filters it.
        await SeedOrderDetailTimelineAsync(connStr, orderId, 20, TenantB);

        var timeline = await store.GetTimelineAsync(orderId, CancellationToken.None);
        timeline.Should().ContainSingle();
        timeline[0].GlobalPosition.Should().Be(10);
    }

    [Fact]
    public async Task CustomerSummary_GetAsync_under_the_default_tenant_does_not_see_a_second_tenant_row()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var store = CustomerSummaryStore(ds, TenantA);
        var custA = Guid.NewGuid();
        var custB = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.ApplyPlacementAsync(
                custA, new Money(50m, Currency.USD), At, At, CancellationToken.None);
            await uow.CommitAsync("customer-summary", 1, CancellationToken.None);
        }
        await SeedCustomerSummaryAsync(connStr, custB, TenantB);

        (await store.GetAsync(custA, CancellationToken.None)).Should().NotBeNull();
        (await store.GetAsync(custB, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task InventoryDashboard_GetBySkuAsync_under_the_default_tenant_does_not_see_a_second_tenant_sku()
    {
        // This test keeps the two tenants on distinct skus. The same-sku-across-tenants
        // case, which the global sku UNIQUE constraint once forbade, landed when migration
        // 0018 made the uniqueness composite on the tenant-and-sku pair; it is the adjacent
        // InventoryDashboard_GetBySkuAsync_returns_each_tenants_row test.
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var store = InventoryDashboardStore(ds, TenantA);

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateDashboardAsync(Guid.NewGuid(), "SKU-A", At, CancellationToken.None);
            await uow.CommitAsync("inventory-dashboard", 1, CancellationToken.None);
        }
        await SeedInventoryDashboardAsync(connStr, Guid.NewGuid(), "SKU-B", TenantB);

        (await store.GetBySkuAsync("SKU-A", CancellationToken.None)).Should().NotBeNull();
        (await store.GetBySkuAsync("SKU-B", CancellationToken.None)).Should().BeNull();
    }

    // The deferred P10.6 same-sku case: the same sku under two tenants is legal only
    // once UNIQUE(tenant_id, sku) lands (migration 0018). Under the current global
    // UNIQUE(sku) the second seed violates the constraint (Postgres 23505) before the
    // assertions are reached; that throw is this RED's intended failure.
    [Fact]
    public async Task InventoryDashboard_GetBySkuAsync_returns_each_tenants_row_for_the_same_sku_once_uniqueness_is_tenant_scoped()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        const string sku = "SKU-SHARED";
        var inventoryA = Guid.NewGuid();
        var inventoryB = Guid.NewGuid();

        var storeA = InventoryDashboardStore(ds, TenantA);
        await using (var uow = await storeA.BeginAsync(CancellationToken.None))
        {
            await uow.CreateDashboardAsync(inventoryA, sku, At, CancellationToken.None);
            await uow.CommitAsync("inventory-dashboard", 1, CancellationToken.None);
        }
        await SeedInventoryDashboardAsync(connStr, inventoryB, sku, TenantB);

        var storeB = InventoryDashboardStore(ds, TenantId.From(TenantB));
        (await storeA.GetBySkuAsync(sku, CancellationToken.None))!.InventoryId.Should().Be(inventoryA);
        (await storeB.GetBySkuAsync(sku, CancellationToken.None))!.InventoryId.Should().Be(inventoryB);
    }

    [Fact]
    public async Task InventoryDashboard_GetAllAsync_under_the_default_tenant_returns_only_default_tenant_rows()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var store = InventoryDashboardStore(ds, TenantA);

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateDashboardAsync(Guid.NewGuid(), "SKU-A1", At, CancellationToken.None);
            await uow.CreateDashboardAsync(Guid.NewGuid(), "SKU-A2", At, CancellationToken.None);
            await uow.CommitAsync("inventory-dashboard", 1, CancellationToken.None);
        }
        await SeedInventoryDashboardAsync(connStr, Guid.NewGuid(), "SKU-B1", TenantB);

        var all = await store.GetAllAsync(CancellationToken.None);
        all.Should().HaveCount(2);
        // ORDER BY sku is the contract; the tenant-B SKU-B1 is filtered out.
        all.Select(r => r.Sku).Should().ContainInOrder("SKU-A1", "SKU-A2");
    }

    // ---- Fail-closed: no tenant set, the read throws before it touches the database ----

    [Fact]
    public async Task OrderList_GetAsync_with_no_tenant_set_throws_MissingTenantContextException()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var store = OrderListStore(ds, tenant: null);
        await store.Invoking(s => s.GetAsync(Guid.NewGuid(), CancellationToken.None))
            .Should().ThrowAsync<MissingTenantContextException>();
    }

    [Fact]
    public async Task OrderList_GetPageAsync_with_no_tenant_set_throws_MissingTenantContextException()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var store = OrderListStore(ds, tenant: null);
        await store.Invoking(s => s.GetPageAsync(0, 50, CancellationToken.None))
            .Should().ThrowAsync<MissingTenantContextException>();
    }

    [Fact]
    public async Task OrderList_GetPageAsync_with_no_tenant_set_throws_even_when_the_limit_is_zero()
    {
        // A zero limit returns early before the connection opens, so this pins the
        // resolve ahead of that early return: an unset tenant still fails closed.
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var store = OrderListStore(ds, tenant: null);
        await store.Invoking(s => s.GetPageAsync(0, 0, CancellationToken.None))
            .Should().ThrowAsync<MissingTenantContextException>();
    }

    [Fact]
    public async Task OrderDetail_GetLinesAsync_with_no_tenant_set_throws_MissingTenantContextException()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var store = OrderDetailStore(ds, tenant: null);
        await store.Invoking(s => s.GetLinesAsync(Guid.NewGuid(), CancellationToken.None))
            .Should().ThrowAsync<MissingTenantContextException>();
    }

    [Fact]
    public async Task OrderDetail_GetTimelineAsync_with_no_tenant_set_throws_MissingTenantContextException()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var store = OrderDetailStore(ds, tenant: null);
        await store.Invoking(s => s.GetTimelineAsync(Guid.NewGuid(), CancellationToken.None))
            .Should().ThrowAsync<MissingTenantContextException>();
    }

    [Fact]
    public async Task CustomerSummary_GetAsync_with_no_tenant_set_throws_MissingTenantContextException()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var store = CustomerSummaryStore(ds, tenant: null);
        await store.Invoking(s => s.GetAsync(Guid.NewGuid(), CancellationToken.None))
            .Should().ThrowAsync<MissingTenantContextException>();
    }

    [Fact]
    public async Task InventoryDashboard_GetBySkuAsync_with_no_tenant_set_throws_MissingTenantContextException()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var store = InventoryDashboardStore(ds, tenant: null);
        await store.Invoking(s => s.GetBySkuAsync("SKU-1", CancellationToken.None))
            .Should().ThrowAsync<MissingTenantContextException>();
    }

    [Fact]
    public async Task InventoryDashboard_GetAllAsync_with_no_tenant_set_throws_MissingTenantContextException()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var store = InventoryDashboardStore(ds, tenant: null);
        await store.Invoking(s => s.GetAllAsync(CancellationToken.None))
            .Should().ThrowAsync<MissingTenantContextException>();
    }

    // ---- builders and direct-insert seeds ----

    private static PostgresOrderListStore OrderListStore(NpgsqlDataSource ds, TenantId? tenant)
    {
        var factory = new NpgsqlReadModelConnectionFactory(ds);
        return new PostgresOrderListStore(
            factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(),
            new StubTenantAccessor { Current = tenant });
    }

    private static PostgresOrderDetailStore OrderDetailStore(NpgsqlDataSource ds, TenantId? tenant)
    {
        var factory = new NpgsqlReadModelConnectionFactory(ds);
        return new PostgresOrderDetailStore(
            factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(),
            new StubTenantAccessor { Current = tenant });
    }

    private static PostgresCustomerSummaryStore CustomerSummaryStore(NpgsqlDataSource ds, TenantId? tenant)
    {
        var factory = new NpgsqlReadModelConnectionFactory(ds);
        return new PostgresCustomerSummaryStore(
            factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(),
            new StubTenantAccessor { Current = tenant });
    }

    private static PostgresInventoryDashboardStore InventoryDashboardStore(NpgsqlDataSource ds, TenantId? tenant)
    {
        var factory = new NpgsqlReadModelConnectionFactory(ds);
        return new PostgresInventoryDashboardStore(
            factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(),
            new StubTenantAccessor { Current = tenant });
    }

    private static OrderListRow OrderListRowFor(Guid orderId)
        => new(
            OrderId: orderId,
            CustomerId: Guid.NewGuid(),
            Status: OrderStatus.Placed,
            Total: new Money(10m, Currency.USD),
            PlacedUtc: At,
            LastUpdatedUtc: At,
            IsReturned: false,
            ReturnedUtc: null);

    private static async Task SeedOrderListRowAsync(string connStr, Guid orderId, Guid tenantId)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO read_models.order_list " +
            "(order_id, customer_id, status, total_amount, total_currency, placed_utc, last_updated_utc, tenant_id) " +
            "VALUES (@order_id, @customer_id, @status, @total_amount, @total_currency, @placed_utc, @last_updated_utc, @tenant_id)";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        cmd.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, Guid.NewGuid());
        cmd.Parameters.AddWithValue("status", NpgsqlDbType.Text, "Placed");
        cmd.Parameters.AddWithValue("total_amount", NpgsqlDbType.Numeric, 10m);
        cmd.Parameters.AddWithValue("total_currency", NpgsqlDbType.Text, "USD");
        cmd.Parameters.AddWithValue("placed_utc", NpgsqlDbType.TimestampTz, At);
        cmd.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, At);
        cmd.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenantId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SeedOrderDetailLineAsync(string connStr, Guid orderId, Guid lineId, Guid tenantId)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO read_models.order_detail_lines " +
            "(order_id, line_id, sku, quantity, unit_price_amount, unit_price_currency, tenant_id) " +
            "VALUES (@order_id, @line_id, @sku, @quantity, @unit_price_amount, @unit_price_currency, @tenant_id)";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        cmd.Parameters.AddWithValue("line_id", NpgsqlDbType.Uuid, lineId);
        cmd.Parameters.AddWithValue("sku", NpgsqlDbType.Text, "SKU-B");
        cmd.Parameters.AddWithValue("quantity", NpgsqlDbType.Integer, 1);
        cmd.Parameters.AddWithValue("unit_price_amount", NpgsqlDbType.Numeric, 5m);
        cmd.Parameters.AddWithValue("unit_price_currency", NpgsqlDbType.Text, "USD");
        cmd.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenantId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SeedOrderDetailTimelineAsync(
        string connStr, Guid orderId, long globalPosition, Guid tenantId)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO read_models.order_detail_timeline " +
            "(order_id, global_position, event_type, occurred_utc, payload, tenant_id) " +
            "VALUES (@order_id, @global_position, @event_type, @occurred_utc, @payload, @tenant_id)";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        cmd.Parameters.AddWithValue("global_position", NpgsqlDbType.Bigint, globalPosition);
        cmd.Parameters.AddWithValue("event_type", NpgsqlDbType.Text, "OrderPlaced");
        cmd.Parameters.AddWithValue("occurred_utc", NpgsqlDbType.TimestampTz, At);
        cmd.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, "{}");
        cmd.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenantId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SeedCustomerSummaryAsync(string connStr, Guid customerId, Guid tenantId)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO read_models.customer_summary " +
            "(customer_id, order_count, lifetime_value_amount, lifetime_value_currency, " +
            "last_order_utc, last_updated_utc, tenant_id) " +
            "VALUES (@customer_id, @order_count, @lifetime_value_amount, @lifetime_value_currency, " +
            "@last_order_utc, @last_updated_utc, @tenant_id)";
        cmd.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, customerId);
        cmd.Parameters.AddWithValue("order_count", NpgsqlDbType.Integer, 1);
        cmd.Parameters.AddWithValue("lifetime_value_amount", NpgsqlDbType.Numeric, 50m);
        cmd.Parameters.AddWithValue("lifetime_value_currency", NpgsqlDbType.Text, "USD");
        cmd.Parameters.AddWithValue("last_order_utc", NpgsqlDbType.TimestampTz, At);
        cmd.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, At);
        cmd.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenantId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SeedInventoryDashboardAsync(
        string connStr, Guid inventoryId, string sku, Guid tenantId)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO read_models.inventory_dashboard " +
            "(inventory_id, sku, on_hand_quantity, reserved_quantity, last_updated_utc, tenant_id) " +
            "VALUES (@inventory_id, @sku, @on_hand_quantity, @reserved_quantity, @last_updated_utc, @tenant_id)";
        cmd.Parameters.AddWithValue("inventory_id", NpgsqlDbType.Uuid, inventoryId);
        cmd.Parameters.AddWithValue("sku", NpgsqlDbType.Text, sku);
        cmd.Parameters.AddWithValue("on_hand_quantity", NpgsqlDbType.Integer, 0);
        cmd.Parameters.AddWithValue("reserved_quantity", NpgsqlDbType.Integer, 0);
        cmd.Parameters.AddWithValue("last_updated_utc", NpgsqlDbType.TimestampTz, At);
        cmd.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenantId);
        await cmd.ExecuteNonQueryAsync();
    }
}
