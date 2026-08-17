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
// Seven projections are tenant-scoped and carry a real isolation case: a write tagged under one tenant is
// invisible under another. One is global by design and carries a recorded-decision case rather than an
// isolation assertion the read does not perform. CurrentRoles is a per-user roles read model the principal
// factory reads, with no tenant on the row and a tenant-free read. OrderIdToPaymentId was the second such
// projection until migration 0028 keyed it on the tenant; its recorded-decision case is retired and it
// carries an isolation case now.
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
            [typeof(OrderIdToPaymentIdProjection)] = () => OrderIdToPaymentIdIsolatesAsync(fixture),
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

    // Two tenants authorize payment for the same order id, which they can because order ids reach this
    // projection from caller input. Each keeps its own mapping under its own tenant, and neither
    // resolves the other's. Before migration 0028 this projection carried a recorded-decision case
    // instead, pinning the untagged write and the tenant-free read as intended on the premise that the
    // order id was globally unique. That premise was refuted, so the case is retired rather than
    // rewritten: its subject was the exemption, and the exemption is gone.
    private static async Task OrderIdToPaymentIdIsolatesAsync(PostgresFixture fixture)
    {
        var connStr = await fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var readModelFactory = new NpgsqlReadModelConnectionFactory(ds);
        var stub = new StubTenantAccessor { Current = TenantA };
        var store = new PostgresOrderIdToPaymentIdStore(
            readModelFactory, new PostgresCheckpointStore(readModelFactory), TestNotificationPublisher.Create(),
            stub);
        var projection = new OrderIdToPaymentIdProjection(store);

        var orderId = Guid.NewGuid();
        var (paymentA, paymentB) = (Guid.NewGuid(), Guid.NewGuid());

        stub.Current = TenantA;
        await projection.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new PaymentAuthorized(paymentA, orderId, new Money(10m, Currency.USD), "ref-a", At), 1, TenantA),
            CancellationToken.None);
        stub.Current = TenantB;
        await projection.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new PaymentAuthorized(paymentB, orderId, new Money(10m, Currency.USD), "ref-b", At), 2, TenantB),
            CancellationToken.None);

        // Each tenant resolves its own payment for the shared order id, and neither sees the other's.
        stub.Current = TenantA;
        (await store.GetPaymentIdAsync(orderId, CancellationToken.None)).Should().Be(paymentA);
        stub.Current = TenantB;
        (await store.GetPaymentIdAsync(orderId, CancellationToken.None)).Should().Be(paymentB);

        // The write tags the writing tenant, so the rows are partitioned rather than piled on one default.
        (await ReadTenantIdsAsync(connStr, orderId))
            .Should().BeEquivalentTo(new[] { TenantA.Value, TenantB.Value });
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

    private static async Task<IReadOnlyList<Guid>> ReadTenantIdsAsync(string connectionString, Guid orderId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT tenant_id FROM read_models.order_id_to_payment_id WHERE order_id = @order_id";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        var tenants = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tenants.Add(reader.GetGuid(0));
        }
        return tenants;
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
