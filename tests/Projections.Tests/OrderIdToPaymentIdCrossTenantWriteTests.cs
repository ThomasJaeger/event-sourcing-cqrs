using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.ReadModels;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// The order-id-to-payment-id family's cross-tenant facts (ADR 0031's discriminator model).
//
// One table, given the discriminator column by migration 0017 and keyed on
// pk_order_id_to_payment_id (order_id) from 0010 until 0028 made it (tenant_id, order_id). Order ids
// reach this table from caller input: DraftOrder and PlaceOrder each declare an OrderId the request
// body supplies, both are registered for by-name HTTP dispatch, and the OrderFulfillment process
// manager carries that same id into AuthorizePayment, which is what this projection folds. So two
// tenants can present the same order id here for the same reason they can in order_list.
//
// What each statement did before 0028, which is the shape these facts were written against:
//
//   RecordAsync           named no tenant in its column list, and conflicted on (order_id)
//   GetPaymentIdAsync     carried no tenant predicate
//   TruncateAsync         whole-table, every tenant, and deliberate as the rebuild primitive
//   ResetTenantAsync      carried WHERE tenant_id = @tenant
//
// Three facts, one per defect, and each isolates its own. The fold fact counts rows rather than
// reading through the port, so a read that discloses cannot make it pass. The tagging fact and the
// disclosure fact both arrange the owning tenant alone, so no fold stands between the arrangement
// and what they assert.
//
// The disclosure fact is the one this arc has not met before. A fold costs the second tenant its
// own write; a disclosure costs the first tenant its privacy, and nothing about the first tenant's
// rows changes when it happens. That is why it is not a digest comparison: the isolation property
// digests the owner's rows across the act phase, and a read leaves them exactly as they were.
public sealed class OrderIdToPaymentIdCrossTenantWriteTests : IClassFixture<PostgresFixture>
{
    private static readonly TenantId TenantA = ProjectionTenantTaggingTests.TenantA;
    private static readonly TenantId TenantB = ProjectionTenantTaggingTests.TenantB;

    private readonly PostgresFixture _fixture;

    public OrderIdToPaymentIdCrossTenantWriteTests(PostgresFixture fixture) => _fixture = fixture;

    // The fold. Counted at the table rather than read through the port, so the fact turns on how many
    // rows the key admits rather than on what the read's predicate selects. That separation is what
    // kept it honest while the read still disclosed. Two tenants authorizing payment for one order id
    // must leave two mappings, one owned by each.
    [Fact]
    public async Task Two_tenants_authorizing_payment_for_the_same_order_id_each_keep_a_mapping()
    {
        var run = await RunAsync(bothPhases: true);

        var rows = await CountMappingsAsync(run.ConnectionString, run.OrderId);

        rows.Should().Be(
            2,
            "each tenant authorized a payment for its own order under its own tenant, so the table "
            + "must hold one mapping per tenant rather than folding the second into the first");
    }

    // The untagged write. Only the owning tenant arranges, so nothing here turns on the conflict
    // target: the single row that lands must carry the tenant of the event that produced it.
    [Fact]
    public async Task A_mapping_carries_the_tenant_of_the_event_that_produced_it()
    {
        var run = await RunAsync(bothPhases: false);

        var tenant = await ReadTenantAsync(run.ConnectionString, run.OrderId);

        tenant.Should().Be(
            TenantA.Value,
            "the projection wrote this mapping under tenant A's PaymentAuthorized, so the row must "
            + "carry tenant A rather than the column default every tenant's row falls to");
    }

    // The disclosure. Only the owning tenant holds a mapping, and the second tenant asks the port
    // for that order id. A correct read finds nothing under the asking tenant. A disclosing read
    // hands back the owner's payment id, which is the value the second tenant must never learn.
    // The assertion is null, and the failure prints the owner's payment id, so the two outcomes are
    // told apart by what comes back rather than by whether anything came back at all.
    [Fact]
    public async Task A_tenant_cannot_resolve_another_tenants_order_to_its_payment_id()
    {
        var run = await RunAsync(bothPhases: false);

        run.Stub.Current = TenantB;
        var resolved = await run.Store.GetPaymentIdAsync(run.OrderId, CancellationToken.None);

        resolved.Should().BeNull(
            "tenant B holds no mapping for this order id, so its lookup must find nothing rather "
            + "than resolving to tenant A's payment id");
    }

    // === arrangement ===

    private sealed record Run(
        IOrderIdToPaymentIdStore Store,
        StubTenantAccessor Stub,
        Guid OrderId,
        string ConnectionString,
        NpgsqlDataSource Source);

    // Runs the family's one drive against a fresh migrated database, through the same Build the
    // harness uses. The act phase is optional: the tagging and disclosure facts want the owning
    // tenant's arrangement alone, with no second write to explain their result.
    private async Task<Run> RunAsync(bool bothPhases)
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var ds = NpgsqlDataSource.Create(connStr);
        var family = OrderIdToPaymentIdCrossTenantDrive.Family;
        var target = family.Build(ds, new HashSet<string>(StringComparer.Ordinal));
        var drive = family.Drive("Record");
        var orderId = Guid.NewGuid();

        await drive.ArrangeAsOwner(target, orderId, connStr);
        if (bothPhases)
        {
            await drive.ActAsOther(target, orderId, connStr);
        }

        return new Run(
            (IOrderIdToPaymentIdStore)target.Store, target.Tenant, orderId, connStr, ds);
    }

    private static async Task<long> CountMappingsAsync(string connStr, Guid orderId)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT count(*) FROM read_models.order_id_to_payment_id WHERE order_id = @order_id";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<Guid> ReadTenantAsync(string connStr, Guid orderId)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT tenant_id FROM read_models.order_id_to_payment_id WHERE order_id = @order_id";
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }
}
