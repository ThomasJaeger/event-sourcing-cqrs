using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access.ReadModels;
using EventSourcingCqrs.Domain.Billing.ReadModels;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Infrastructure.SignalR;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// ResetTenantAsync's cross-tenant contract, one fact per store that implements it.
//
// What is being pinned
//   ITenantResettable states the property in its own words: ResetTenantAsync "removes the given
//   tenant's rows from this store's tables and leaves every other tenant's rows, and the global
//   projection checkpoint, untouched." A predicate lost from that DELETE turns one tenant's rebuild
//   into another tenant's data loss, and nothing about the rebuild path would report it: the
//   rebuilder would replay the tenant it was asked for, and the other tenant's rows would simply be
//   gone.
//
// Why the write-surface harness does not already reach this
//   Every drive in the cross-tenant harness acts through a projection's HandleAsync, whose path runs
//   BeginAsync to unit-of-work members to CommitAsync. ResetTenantAsync is on neither port. It sits
//   on ITenantResettable, at store level, and no projection handler calls it, so no drive can reach
//   it however many are written. These facts take the shape the harness cannot: they bypass the
//   projection and call the store directly.
//
// Why TruncateAsync is not here
//   It is the other store-level member, declared on seven ports, and it is deliberately excluded
//   rather than overlooked. TruncateAsync drops every row for every tenant by design, as its own
//   contract says and as two existing cross-tenant test files already record in their headers. A
//   member with no per-tenant behaviour has no per-tenant property to pin, and seven facts asserting
//   that a whole-table delete deletes the whole table would add coverage numbers without adding
//   coverage. It also has zero callers in src/, so nothing in production reaches it at all.
//
// Shape of each fact
//   Write one row as tenant one and one as tenant two through each store's own unit of work, reset
//   tenant one through ITenantResettable, then read back as both tenants. Tenant one sees nothing
//   and tenant two still sees its row. The read is through each store's own tenant-scoped accessor,
//   so a reset that over-deletes fails on the second assertion and a reset that under-deletes fails
//   on the first.
public class TenantResetIsolationTests : IClassFixture<PostgresFixture>
{
    private static readonly TenantId TenantOne = TenantId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly TenantId TenantTwo = TenantId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly DateTime Stamp = new(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public TenantResetIsolationTests(PostgresFixture fixture) => _fixture = fixture;

    private async Task<NpgsqlReadModelConnectionFactory> FactoryAsync(NpgsqlDataSource[] keepAlive)
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var dataSource = NpgsqlDataSource.Create(connStr);
        keepAlive[0] = dataSource;
        return new NpgsqlReadModelConnectionFactory(dataSource);
    }

    private static T StoreFor<T>(
        Func<IReadModelConnectionFactory, ICheckpointStore, PostgresPgNotifyPublisher, ICurrentTenantAccessor, T> ctor,
        IReadModelConnectionFactory factory,
        TenantId tenant) =>
        ctor(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(),
             new StubTenantAccessor { Current = tenant });

    [Fact]
    public async Task Order_id_to_payment_id_reset_clears_one_tenant_and_leaves_the_other()
    {
        var keep = new NpgsqlDataSource[1];
        var factory = await FactoryAsync(keep);
        await using var _ = keep[0];

        var one = StoreFor((f, c, p, t) => new PostgresOrderIdToPaymentIdStore(f, c, p, t), factory, TenantOne);
        var two = StoreFor((f, c, p, t) => new PostgresOrderIdToPaymentIdStore(f, c, p, t), factory, TenantTwo);
        var orderOne = Guid.NewGuid();
        var orderTwo = Guid.NewGuid();

        await using (var uow = await one.BeginAsync(CancellationToken.None))
        {
            await uow.RecordAsync(orderOne, Guid.NewGuid(), CancellationToken.None);
            await uow.CommitAsync("order-id-to-payment-id", 1, CancellationToken.None);
        }
        await using (var uow = await two.BeginAsync(CancellationToken.None))
        {
            await uow.RecordAsync(orderTwo, Guid.NewGuid(), CancellationToken.None);
            await uow.CommitAsync("order-id-to-payment-id", 2, CancellationToken.None);
        }

        await ((ITenantResettable)one).ResetTenantAsync(TenantOne, CancellationToken.None);

        (await one.GetPaymentIdAsync(orderOne, CancellationToken.None)).Should().BeNull();
        (await two.GetPaymentIdAsync(orderTwo, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task Sku_to_inventory_id_reset_clears_one_tenant_and_leaves_the_other()
    {
        var keep = new NpgsqlDataSource[1];
        var factory = await FactoryAsync(keep);
        await using var _ = keep[0];

        var one = StoreFor((f, c, p, t) => new PostgresSkuToInventoryIdStore(f, c, p, t), factory, TenantOne);
        var two = StoreFor((f, c, p, t) => new PostgresSkuToInventoryIdStore(f, c, p, t), factory, TenantTwo);

        // The same SKU under both tenants, which is the sharper case: the row differs only by the
        // discriminator, so a reset missing its predicate takes both.
        const string sku = "SKU-RESET-1";

        await using (var uow = await one.BeginAsync(CancellationToken.None))
        {
            await uow.RecordAsync(sku, Guid.NewGuid(), CancellationToken.None);
            await uow.CommitAsync("sku-to-inventory-id", 1, CancellationToken.None);
        }
        await using (var uow = await two.BeginAsync(CancellationToken.None))
        {
            await uow.RecordAsync(sku, Guid.NewGuid(), CancellationToken.None);
            await uow.CommitAsync("sku-to-inventory-id", 2, CancellationToken.None);
        }

        await ((ITenantResettable)one).ResetTenantAsync(TenantOne, CancellationToken.None);

        (await one.GetInventoryIdAsync(sku, CancellationToken.None)).Should().BeNull();
        (await two.GetInventoryIdAsync(sku, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task Order_list_reset_clears_one_tenant_and_leaves_the_other()
    {
        var keep = new NpgsqlDataSource[1];
        var factory = await FactoryAsync(keep);
        await using var _ = keep[0];

        var one = StoreFor((f, c, p, t) => new PostgresOrderListStore(f, c, p, t), factory, TenantOne);
        var two = StoreFor((f, c, p, t) => new PostgresOrderListStore(f, c, p, t), factory, TenantTwo);
        var orderOne = Guid.NewGuid();
        var orderTwo = Guid.NewGuid();

        await using (var uow = await one.BeginAsync(CancellationToken.None))
        {
            await uow.InsertAsync(Row(orderOne), CancellationToken.None);
            await uow.CommitAsync("order-list", 1, CancellationToken.None);
        }
        await using (var uow = await two.BeginAsync(CancellationToken.None))
        {
            await uow.InsertAsync(Row(orderTwo), CancellationToken.None);
            await uow.CommitAsync("order-list", 2, CancellationToken.None);
        }

        await ((ITenantResettable)one).ResetTenantAsync(TenantOne, CancellationToken.None);

        (await one.GetAsync(orderOne, CancellationToken.None)).Should().BeNull();
        (await two.GetAsync(orderTwo, CancellationToken.None)).Should().NotBeNull();

        static OrderListRow Row(Guid orderId) => new(
            orderId, Guid.NewGuid(), OrderStatus.Placed, new Money(10m, Currency.USD),
            Stamp, Stamp, false, null);
    }

    [Fact]
    public async Task Customer_summary_reset_clears_one_tenant_and_leaves_the_other()
    {
        var keep = new NpgsqlDataSource[1];
        var factory = await FactoryAsync(keep);
        await using var _ = keep[0];

        var one = StoreFor((f, c, p, t) => new PostgresCustomerSummaryStore(f, c, p, t), factory, TenantOne);
        var two = StoreFor((f, c, p, t) => new PostgresCustomerSummaryStore(f, c, p, t), factory, TenantTwo);
        var customerOne = Guid.NewGuid();
        var customerTwo = Guid.NewGuid();
        var orderOne = Guid.NewGuid();
        var orderTwo = Guid.NewGuid();

        await using (var uow = await one.BeginAsync(CancellationToken.None))
        {
            await uow.InsertOrderAsync(new CustomerSummaryOrderRow(customerOne, orderOne, new Money(10m, Currency.USD), Stamp), CancellationToken.None);
            await uow.CommitAsync("customer-summary", 1, CancellationToken.None);
        }
        await using (var uow = await two.BeginAsync(CancellationToken.None))
        {
            await uow.InsertOrderAsync(new CustomerSummaryOrderRow(customerTwo, orderTwo, new Money(10m, Currency.USD), Stamp), CancellationToken.None);
            await uow.CommitAsync("customer-summary", 2, CancellationToken.None);
        }

        await ((ITenantResettable)one).ResetTenantAsync(TenantOne, CancellationToken.None);

        // Read back through the orders table rather than through GetAsync. GetAsync reads
        // read_models.customer_summary, which InsertOrderAsync does not write: it writes
        // read_models.customer_summary_orders alone. Asserting through GetAsync would have both
        // tenants reading null for a reason that has nothing to do with the reset, which is a fact
        // that passes without testing anything.
        await using (var uow = await one.BeginAsync(CancellationToken.None))
        {
            (await uow.GetOrderByOrderIdAsync(orderOne, CancellationToken.None)).Should().BeNull();
        }
        await using (var uow = await two.BeginAsync(CancellationToken.None))
        {
            (await uow.GetOrderByOrderIdAsync(orderTwo, CancellationToken.None)).Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Order_detail_reset_clears_one_tenant_and_leaves_the_other()
    {
        var keep = new NpgsqlDataSource[1];
        var factory = await FactoryAsync(keep);
        await using var _ = keep[0];

        var one = StoreFor((f, c, p, t) => new PostgresOrderDetailStore(f, c, p, t), factory, TenantOne);
        var two = StoreFor((f, c, p, t) => new PostgresOrderDetailStore(f, c, p, t), factory, TenantTwo);
        var orderOne = Guid.NewGuid();
        var orderTwo = Guid.NewGuid();

        await using (var uow = await one.BeginAsync(CancellationToken.None))
        {
            await uow.CreateHeaderAsync(orderOne, Guid.NewGuid(), Stamp, CancellationToken.None);
            await uow.CommitAsync("order-detail", 1, CancellationToken.None);
        }
        await using (var uow = await two.BeginAsync(CancellationToken.None))
        {
            await uow.CreateHeaderAsync(orderTwo, Guid.NewGuid(), Stamp, CancellationToken.None);
            await uow.CommitAsync("order-detail", 2, CancellationToken.None);
        }

        await ((ITenantResettable)one).ResetTenantAsync(TenantOne, CancellationToken.None);

        (await one.GetHeaderAsync(orderOne, CancellationToken.None)).Should().BeNull();
        (await two.GetHeaderAsync(orderTwo, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task Inventory_dashboard_reset_clears_one_tenant_and_leaves_the_other()
    {
        var keep = new NpgsqlDataSource[1];
        var factory = await FactoryAsync(keep);
        await using var _ = keep[0];

        var one = StoreFor((f, c, p, t) => new PostgresInventoryDashboardStore(f, c, p, t), factory, TenantOne);
        var two = StoreFor((f, c, p, t) => new PostgresInventoryDashboardStore(f, c, p, t), factory, TenantTwo);

        // Same SKU under both tenants again, for the same reason as the lookup above.
        const string sku = "SKU-DASH-1";

        await using (var uow = await one.BeginAsync(CancellationToken.None))
        {
            await uow.CreateDashboardAsync(Guid.NewGuid(), sku, Stamp, CancellationToken.None);
            await uow.CommitAsync("inventory-dashboard", 1, CancellationToken.None);
        }
        await using (var uow = await two.BeginAsync(CancellationToken.None))
        {
            await uow.CreateDashboardAsync(Guid.NewGuid(), sku, Stamp, CancellationToken.None);
            await uow.CommitAsync("inventory-dashboard", 2, CancellationToken.None);
        }

        await ((ITenantResettable)one).ResetTenantAsync(TenantOne, CancellationToken.None);

        (await one.GetBySkuAsync(sku, CancellationToken.None)).Should().BeNull();
        (await two.GetBySkuAsync(sku, CancellationToken.None)).Should().NotBeNull();
    }
}
