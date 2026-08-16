using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Infrastructure.Versioning;
using EventSourcingCqrs.Projections.OrderDetail;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// The write-surface half of ADR 0031's coverage mandate.
//
// CrossTenantProjectionCoverageTests is closed over registered projection CLR types: a
// projection owes one entry, and nothing constrains what the entry does. Every case in that
// dictionary drives one creating event and asserts one read, so the harness is green while the
// mutations those projections perform go unreached by any two-tenant assertion. That is not a
// defect in that harness; it is the class it closes over. This is the sibling closed over the
// other class.
//
// Two properties, and the first is the one that bites:
//
//  1  Isolation. For every drive, the owning tenant's rows are snapshotted across every
//     discriminator-carrying read_models table after the owner has arranged and before a second
//     tenant acts. After the act, they must be byte-identical. The table set is read from
//     information_schema, so a read-model table that gains a tenant_id column joins the property
//     with no edit here, and the comparison is over the rows themselves rather than over a
//     column anyone chose to assert on.
//
//  2  Coverage. Every mutating member the port declares must be reached during an act phase,
//     that is, under a tenant that does not own the row. Reached under the owner does not count:
//     that is the tagging property, which the tenant-tagging tests already hold, and counting it
//     here would let a drive earn coverage without ever crossing a boundary.
//
// Neither side of either property is a list anyone maintains, which is what keeps the check from
// agreeing with its own assumption. The declared writes come off the port by reflection, the
// tenant-carrying tables come off the schema, and what changed comes off the database.
public sealed class CrossTenantWriteSurfaceCoverageTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public CrossTenantWriteSurfaceCoverageTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task No_order_detail_drive_lets_a_second_tenant_change_the_owning_tenants_rows()
    {
        var crossed = new List<string>();

        foreach (var drive in OrderDetailCrossTenantDrive.All)
        {
            var connStr = await _fixture.CreateMigratedDatabaseAsync();
            await using var ds = NpgsqlDataSource.Create(connStr);
            var (projection, stub) = BuildAsync(ds, new HashSet<string>(StringComparer.Ordinal));
            var orderId = Guid.NewGuid();

            await drive.ArrangeAsOwner(projection, stub, orderId, connStr);
            var before = await SnapshotOwnerRowsAsync(connStr);
            await drive.ActAsOther(projection, stub, orderId, connStr);
            var after = await SnapshotOwnerRowsAsync(connStr);

            foreach (var table in before.Keys.Where(t => before[t] != after[t]))
            {
                crossed.Add($"{drive.Name} -> read_models.{table}");
            }
        }

        crossed.Should().BeEmpty(
            "a write dispatched under a tenant that does not own the row must leave the owning "
            + "tenant's rows untouched. {0} of {1} drives crossed: {2}",
            crossed.Count,
            OrderDetailCrossTenantDrive.All.Count,
            string.Join(", ", crossed));
    }

    [Fact]
    public async Task Every_order_detail_write_is_reached_by_a_drive_acting_as_a_second_tenant()
    {
        var reachedAsOther = new HashSet<string>(StringComparer.Ordinal);

        foreach (var drive in OrderDetailCrossTenantDrive.All)
        {
            var connStr = await _fixture.CreateMigratedDatabaseAsync();
            await using var ds = NpgsqlDataSource.Create(connStr);
            var duringArrange = new HashSet<string>(StringComparer.Ordinal);
            var (projection, stub) = BuildAsync(ds, duringArrange);
            var orderId = Guid.NewGuid();

            // Only the act phase counts. The recording set is swapped between the phases so a
            // write reached while arranging as the owner earns no coverage.
            await drive.ArrangeAsOwner(projection, stub, orderId, connStr);
            duringArrange.Clear();
            await drive.ActAsOther(projection, stub, orderId, connStr);
            reachedAsOther.UnionWith(duringArrange);
        }

        var declared = CrossTenantCoverage.DeclaredWrites(typeof(IOrderDetailUnitOfWork));
        CrossTenantCoverage.FindUnexercisedWrites(typeof(IOrderDetailUnitOfWork), reachedAsOther)
            .Should().BeEmpty(
                "every mutating member IOrderDetailUnitOfWork declares must be driven by a fact "
                + "that runs it as a tenant which does not own the row; {0} are declared",
                declared.Count);
    }

    private static (OrderDetailProjection Projection, StubTenantAccessor Stub) BuildAsync(
        NpgsqlDataSource ds, ISet<string> invoked)
    {
        var factory = new NpgsqlReadModelConnectionFactory(ds);
        var stub = new StubTenantAccessor { Current = ProjectionTenantTaggingTests.TenantA };
        var real = new PostgresOrderDetailStore(
            factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), stub);
        var projection = new OrderDetailProjection(
            new RecordingOrderDetailStore(real, invoked),
            EventStoreJsonOptions.Create(),
            NullLogger<OrderDetailProjection>.Instance);
        return (projection, stub);
    }

    // One digest per discriminator-carrying read_models table, over the owning tenant's rows
    // only. The table set comes from information_schema rather than from a list here, so the
    // property widens with the schema. Row order is normalised so the digest depends on content
    // alone.
    private static async Task<IReadOnlyDictionary<string, string>> SnapshotOwnerRowsAsync(
        string connStr)
    {
        var owner = ProjectionTenantTaggingTests.TenantA.Value;
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var tables = new List<string>();
        await using (var listing = conn.CreateCommand())
        {
            listing.CommandText =
                "SELECT table_name FROM information_schema.columns " +
                "WHERE table_schema = 'read_models' AND column_name = 'tenant_id' " +
                "ORDER BY table_name";
            await using var reader = await listing.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        tables.Should().NotBeEmpty(
            "the snapshot is only meaningful if the schema reports tenant-carrying tables; an "
            + "empty set would make every comparison trivially equal");

        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"SELECT COALESCE(md5(string_agg(t::text, '|' ORDER BY t::text)), 'empty') " +
                $"FROM read_models.\"{table}\" t WHERE t.tenant_id = @tenant";
            cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, owner);
            digests[table] = (string)(await cmd.ExecuteScalarAsync())!;
        }
        return digests;
    }
}
