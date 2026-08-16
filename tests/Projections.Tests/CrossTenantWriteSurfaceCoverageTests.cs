using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
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
//
// Both properties run once per family in CrossTenantFamilies.All, so a family joins by adding its
// drive list and nothing here. What that list does not close is stated where it is declared.
public sealed class CrossTenantWriteSurfaceCoverageTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public CrossTenantWriteSurfaceCoverageTests(PostgresFixture fixture) => _fixture = fixture;

    // Family names rather than families: a CrossTenantFamily carries delegates, which xUnit cannot
    // serialize, and an unserializable data row collapses the per-family cases into one.
    public static IEnumerable<object[]> Families =>
        CrossTenantFamilies.All.Select(f => new object[] { f.Name });

    [Theory]
    [MemberData(nameof(Families))]
    public async Task No_drive_lets_a_second_tenant_change_the_owning_tenants_rows(string familyName)
    {
        var family = CrossTenantFamilies.Named(familyName);
        var crossed = new List<string>();

        foreach (var drive in family.Drives)
        {
            var connStr = await _fixture.CreateMigratedDatabaseAsync();
            await using var ds = NpgsqlDataSource.Create(connStr);
            var target = family.Build(ds, new HashSet<string>(StringComparer.Ordinal));
            var aggregateId = Guid.NewGuid();

            await drive.ArrangeAsOwner(target, aggregateId, connStr);
            var before = await SnapshotOwnerRowsAsync(connStr);
            await drive.ActAsOther(target, aggregateId, connStr);
            var after = await SnapshotOwnerRowsAsync(connStr);

            foreach (var table in before.Keys.Where(t => before[t] != after[t]))
            {
                crossed.Add($"{drive.Name} -> read_models.{table}");
            }
        }

        crossed.Should().BeEmpty(
            "a write dispatched under a tenant that does not own the row must leave the owning "
            + "tenant's rows untouched. {0} of {1} {2} drives crossed: {3}",
            crossed.Count,
            family.Drives.Count,
            family.Name,
            string.Join(", ", crossed));
    }

    [Theory]
    [MemberData(nameof(Families))]
    public async Task Every_write_is_reached_by_a_drive_acting_as_a_second_tenant(string familyName)
    {
        var family = CrossTenantFamilies.Named(familyName);
        var reachedAsOther = new HashSet<string>(StringComparer.Ordinal);

        foreach (var drive in family.Drives)
        {
            var connStr = await _fixture.CreateMigratedDatabaseAsync();
            await using var ds = NpgsqlDataSource.Create(connStr);
            var duringArrange = new HashSet<string>(StringComparer.Ordinal);
            var target = family.Build(ds, duringArrange);
            var aggregateId = Guid.NewGuid();

            // Only the act phase counts. The recording set is swapped between the phases so a
            // write reached while arranging as the owner earns no coverage.
            await drive.ArrangeAsOwner(target, aggregateId, connStr);
            duringArrange.Clear();
            await drive.ActAsOther(target, aggregateId, connStr);
            reachedAsOther.UnionWith(duringArrange);
        }

        var declared = CrossTenantCoverage.DeclaredWrites(family.UnitOfWorkPort);
        CrossTenantCoverage.FindUnexercisedWrites(family.UnitOfWorkPort, reachedAsOther)
            .Should().BeEmpty(
                "every mutating member {0} declares must be driven by a fact that runs it as a "
                + "tenant which does not own the row; {1} are declared",
                family.UnitOfWorkPort.Name,
                declared.Count);
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
