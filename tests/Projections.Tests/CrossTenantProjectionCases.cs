using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access.Events;
using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Versioning;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.CurrentRoles;
using EventSourcingCqrs.Projections.CustomerSummary;
using EventSourcingCqrs.Projections.Infrastructure;
using EventSourcingCqrs.Projections.InventoryDashboard;
using EventSourcingCqrs.Projections.OrderDetail;
using EventSourcingCqrs.Projections.OrderIdToPaymentId;
using EventSourcingCqrs.Projections.OrderList;
using EventSourcingCqrs.Projections.OrderThroughput;
using EventSourcingCqrs.Projections.SkuToInventoryId;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Projections.Tests;

// The single home of each registered projection's cross-tenant coverage case (ADR 0031's coverage
// mandate), the projection-boundary twin of CrossTenantQueryCases. One entry per registered projection;
// the isolation logic lives here once, run from the standalone tenant-tagging tests and from the
// registry-driven CrossTenantProjectionCoverageTests. The in-process drive harness (the New{Projection}
// builders and Ctx, the Sku Env and ReadMappingRowsAsync) stays in the tenant-tagging test classes the
// cases extend and is invoked here.
//
// Six projections are tenant-scoped and carry a real isolation case: a write tagged under one tenant is
// invisible under another. Two are global by design and carry a recorded-decision case rather than an
// isolation assertion the read does not perform. OrderIdToPaymentId is keyed on a globally-unique order_id
// whose sole consumer (ReturnProcessManagerHandler) passes a tenant-bound order_id, so its read needs no
// tenant predicate. CurrentRoles is a per-user roles read model the principal factory reads, with no
// tenant on the row and a tenant-free read.
internal static class CrossTenantProjectionCases
{
    // The same operative and control tenants the tenant-tagging tests use, so both boundaries assert
    // isolation against one tenant pair.
    private static readonly TenantId TenantA = ProjectionTenantTaggingTests.TenantA;
    private static readonly TenantId TenantB = ProjectionTenantTaggingTests.TenantB;
    private static readonly DateTime At = ProjectionTenantTaggingTests.At;

    // One entry per registered projection type. A registered projection with no entry here is what the
    // meta-test's completeness check flags, so a projection that ships without a case fails the build.
    public static IReadOnlyDictionary<Type, Func<Task>> For(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return new Dictionary<Type, Func<Task>>
        {
            [typeof(OrderListProjection)] = () => OrderListIsolatesAsync(fixture),
            [typeof(OrderDetailProjection)] = () => OrderDetailIsolatesAsync(fixture),
            [typeof(CustomerSummaryProjection)] = () => CustomerSummaryIsolatesAsync(fixture),
            [typeof(InventoryDashboardProjection)] = () => InventoryDashboardIsolatesAsync(fixture),
            [typeof(OrderThroughputProjection)] = () => OrderThroughputIsolatesAsync(fixture),
            [typeof(SkuToInventoryIdProjection)] = () => SkuToInventoryIdIsolatesAsync(fixture),
            [typeof(OrderIdToPaymentIdProjection)] = () => OrderIdToPaymentIdGlobalByOrderIdAsync(fixture),
            [typeof(CurrentRolesProjection)] = () => CurrentRolesGlobalPerUserAsync(fixture),
        };
    }

    // ---- Tenant-scoped isolation cases (the write-tag core extracted from ProjectionTenantTaggingTests) ----

    private static async Task OrderListIsolatesAsync(PostgresFixture fixture)
    {
        await using var ds = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        var (store, projection, stub) = ProjectionTenantTaggingTests.NewOrderList(ds, TenantA);
        var orderId = Guid.NewGuid();

        await projection.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new OrderPlaced(orderId, Guid.NewGuid(), new Money(10m, Currency.USD), At), 1),
            CancellationToken.None);

        stub.Current = TenantA;
        (await store.GetAsync(orderId, CancellationToken.None))
            .Should().NotBeNull("the write must tag the row with the writing tenant, so it is visible under it");
        stub.Current = TenantB;
        (await store.GetAsync(orderId, CancellationToken.None)).Should().BeNull();
    }

    private static async Task OrderDetailIsolatesAsync(PostgresFixture fixture)
    {
        await using var ds = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        var (store, projection, stub) = ProjectionTenantTaggingTests.NewOrderDetail(ds, TenantA);
        var orderId = Guid.NewGuid();

        await projection.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new OrderLineAdded(orderId, Guid.NewGuid(), "SKU-1", 1, new Money(5m, Currency.USD), At), 1),
            CancellationToken.None);

        stub.Current = TenantA;
        (await store.GetLinesAsync(orderId, CancellationToken.None))
            .Should().ContainSingle("the line must be tagged with the writing tenant");
        stub.Current = TenantB;
        (await store.GetLinesAsync(orderId, CancellationToken.None)).Should().BeEmpty();
    }

    private static async Task CustomerSummaryIsolatesAsync(PostgresFixture fixture)
    {
        await using var ds = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        var (store, projection, stub) = ProjectionTenantTaggingTests.NewCustomerSummary(ds, TenantA);
        var customerId = Guid.NewGuid();

        await projection.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new OrderPlaced(Guid.NewGuid(), customerId, new Money(50m, Currency.USD), At), 1),
            CancellationToken.None);

        stub.Current = TenantA;
        (await store.GetAsync(customerId, CancellationToken.None))
            .Should().NotBeNull("the summary must be tagged with the writing tenant");
        stub.Current = TenantB;
        (await store.GetAsync(customerId, CancellationToken.None)).Should().BeNull();
    }

    private static async Task InventoryDashboardIsolatesAsync(PostgresFixture fixture)
    {
        await using var ds = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        var (store, projection, stub) = ProjectionTenantTaggingTests.NewInventoryDashboard(ds, TenantA);

        await projection.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new InventoryCreated(Guid.NewGuid(), "SKU-INV", At), 1), CancellationToken.None);

        stub.Current = TenantA;
        (await store.GetBySkuAsync("SKU-INV", CancellationToken.None))
            .Should().NotBeNull("the dashboard row must be tagged with the writing tenant");
        stub.Current = TenantB;
        (await store.GetBySkuAsync("SKU-INV", CancellationToken.None)).Should().BeNull();
    }

    private static async Task OrderThroughputIsolatesAsync(PostgresFixture fixture)
    {
        await using var ds = NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync());
        var (store, projection, stub) = ProjectionTenantTaggingTests.NewOrderThroughput(ds, TenantA);

        // The projection counts an order event into its occurrence-second bucket under
        // the writing tenant.
        await projection.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new OrderPlaced(Guid.NewGuid(), Guid.NewGuid(), new Money(10m, Currency.USD), At), 1),
            CancellationToken.None);

        stub.Current = TenantA;
        (await store.GetBucketsAsync(CancellationToken.None))
            .Should().ContainSingle("the throughput bucket must be tagged with the writing tenant");
        stub.Current = TenantB;
        (await store.GetBucketsAsync(CancellationToken.None)).Should().BeEmpty();
    }

    private static async Task SkuToInventoryIdIsolatesAsync(PostgresFixture fixture)
    {
        var connStr = await fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        const string sku = "SKU-SHARED";
        var inventoryA = Guid.NewGuid();
        var inventoryB = Guid.NewGuid();

        var eventStore = new PostgresEventStore(
            new NpgsqlConnectionFactory(ds),
            new EventTypeRegistry().Register<InventoryCreated>(),
            new ProcessManagerEventTypeRegistry(),
            EventStoreJsonOptions.Create(),
            new EventUpcasterPipeline(new EventTypeRegistry().Register<InventoryCreated>(), []));
        var readModelFactory = new NpgsqlReadModelConnectionFactory(ds);
        var tenantAccessor = new StubTenantAccessor();
        var store = new PostgresSkuToInventoryIdStore(
            readModelFactory, new PostgresCheckpointStore(readModelFactory), TestNotificationPublisher.Create(),
            tenantAccessor);
        var projection = new SkuToInventoryIdProjection(store);

        var streamA = StreamId.ForAggregate<Inventory>(TenantA, inventoryA);
        var streamB = StreamId.ForAggregate<Inventory>(TenantB, inventoryB);
        await eventStore.AppendAsync(streamA, 0,
            [SkuToInventoryIdProjectionTenantTests.Env(streamA, 1, new InventoryCreated(inventoryA, sku, At), TenantA)],
            CancellationToken.None);
        await eventStore.AppendAsync(streamB, 0,
            [SkuToInventoryIdProjectionTenantTests.Env(streamB, 1, new InventoryCreated(inventoryB, sku, At), TenantB)],
            CancellationToken.None);

        await new ProjectionReplayer(eventStore, projection, tenantAccessor)
            .ReplayAsync(0, CancellationToken.None);

        var rows = await SkuToInventoryIdProjectionTenantTests.ReadMappingRowsAsync(connStr, sku);
        rows.Should().BeEquivalentTo(new[]
        {
            (TenantA.Value, inventoryA),
            (TenantB.Value, inventoryB),
        }, "each tenant's InventoryCreated for the same sku must record its own (tenant_id, inventory_id) mapping");
    }

    // ---- Recorded-decision cases (global by design; pin the behavior, not a false isolation) ----

    // OrderIdToPaymentId is global by a globally-unique order_id. Its sole consumer, ReturnProcessManagerHandler,
    // passes a tenant-bound order_id recovered from the current-tenant shipment, and in that same handler the
    // SKU lookup passes the tenant explicitly while this lookup omits it, so the read carries no tenant predicate
    // and needs none. This case pins the by-design behavior: each mapping is retrievable by its order_id with no
    // read-time tenant context, and the write tags no tenant. The decision is conditional on that single
    // tenant-bound consumer staying the only reader.
    private static async Task OrderIdToPaymentIdGlobalByOrderIdAsync(PostgresFixture fixture)
    {
        var connStr = await fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var readModelFactory = new NpgsqlReadModelConnectionFactory(ds);
        var store = new PostgresOrderIdToPaymentIdStore(
            readModelFactory, new PostgresCheckpointStore(readModelFactory), TestNotificationPublisher.Create());
        var projection = new OrderIdToPaymentIdProjection(store);

        var (orderA, paymentA) = (Guid.NewGuid(), Guid.NewGuid());
        var (orderB, paymentB) = (Guid.NewGuid(), Guid.NewGuid());

        // Two PaymentAuthorized under different event-metadata tenants for different orders; the write ignores
        // the event tenant either way.
        await projection.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new PaymentAuthorized(paymentA, orderA, new Money(10m, Currency.USD), "ref-a", At), 1, TenantA),
            CancellationToken.None);
        await projection.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new PaymentAuthorized(paymentB, orderB, new Money(10m, Currency.USD), "ref-b", At), 2, TenantB),
            CancellationToken.None);

        // Each mapping is retrievable by its globally-unique order_id, with no read-time tenant context.
        (await store.GetPaymentIdAsync(orderA, CancellationToken.None)).Should().Be(paymentA);
        (await store.GetPaymentIdAsync(orderB, CancellationToken.None)).Should().Be(paymentB);

        // The write tags no tenant: both rows carry the default tenant regardless of the event's tenant, so
        // there is no per-tenant partitioning to isolate.
        (await ReadTenantIdAsync(connStr, orderA)).Should().Be(WellKnownTenants.Default.Value);
        (await ReadTenantIdAsync(connStr, orderB)).Should().Be(WellKnownTenants.Default.Value);
    }

    // CurrentRoles is a per-user roles read model the principal factory reads. Roles ride on the principal, not
    // on a tenant-partitioned row: the store takes no tenant accessor and GetRolesForUserAsync(userId) has no
    // tenant predicate, so a user's roles are visible regardless of tenant context, and the row carries no
    // tenant_id.
    private static async Task CurrentRolesGlobalPerUserAsync(PostgresFixture fixture)
    {
        var connStr = await fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var readModelFactory = new NpgsqlReadModelConnectionFactory(ds);
        var store = new PostgresCurrentUserRolesStore(readModelFactory, new PostgresCheckpointStore(readModelFactory));
        var projection = new CurrentRolesProjection(store);
        var userId = Guid.NewGuid();

        // Two RoleAssigned for the same user under different event-metadata tenants.
        await projection.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new RoleAssigned(userId, Role.Admin), 1, TenantA), CancellationToken.None);
        await projection.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new RoleAssigned(userId, Role.Support), 2, TenantB), CancellationToken.None);

        // Both roles are visible through the tenant-free read, regardless of which tenant's event assigned them.
        (await store.GetRolesForUserAsync(userId, CancellationToken.None))
            .Should().BeEquivalentTo(new[] { Role.Admin, Role.Support });

        // The read model carries no tenant_id column: it is not partitioned by tenant.
        (await ColumnExistsAsync(connStr, "current_user_roles", "tenant_id")).Should().BeFalse();
    }

    private static async Task<Guid> ReadTenantIdAsync(string connectionString, Guid orderId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT tenant_id FROM read_models.order_id_to_payment_id WHERE order_id = @order_id";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<bool> ColumnExistsAsync(string connectionString, string table, string column)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT 1 FROM information_schema.columns " +
            "WHERE table_schema = 'read_models' AND table_name = @table AND column_name = @column";
        cmd.Parameters.AddWithValue("table", NpgsqlDbType.Text, table);
        cmd.Parameters.AddWithValue("column", NpgsqlDbType.Text, column);
        return await cmd.ExecuteScalarAsync() is not null;
    }
}
