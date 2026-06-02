using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Queries.Fulfillment;
using EventSourcingCqrs.Application.Queries.Sales;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Queries;

// The structural cross-tenant coverage harness for the registered query types (ADR 0031's
// coverage mandate). Each query type has one standalone test, named for the query so a failure
// points at the query that leaks. The meta-test enumerates the query types the host actually
// registers, asserts every one has a coverage entry (completeness), and runs every entry
// (correctness), so the structural check exercises the real isolation rather than asserting a
// case is merely present. Each case lives once in CrossTenantQueryCases; these are thin invokes.
public class CrossTenantQueryCoverageTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public CrossTenantQueryCoverageTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public Task ListOrders_omits_another_tenants_orders()
        => CrossTenantQueryCases.For(_fixture)[typeof(ListOrders)]();

    [Fact]
    public Task GetOrderDetail_does_not_return_another_tenants_order()
        => CrossTenantQueryCases.For(_fixture)[typeof(GetOrderDetail)]();

    [Fact]
    public Task GetCustomerSummary_does_not_return_another_tenants_row()
        => CrossTenantQueryCases.For(_fixture)[typeof(GetCustomerSummary)]();

    [Fact]
    public Task GetAllInventoryDashboard_omits_another_tenants_rows()
        => CrossTenantQueryCases.For(_fixture)[typeof(GetAllInventoryDashboard)]();

    [Fact]
    public Task GetInventoryDashboardBySku_does_not_return_another_tenants_sku()
        => CrossTenantQueryCases.For(_fixture)[typeof(GetInventoryDashboardBySku)]();

    [Fact]
    public async Task Every_registered_query_type_has_cross_tenant_coverage()
    {
        var registered = _fixture.Factory.Services
            .GetRequiredService<QueryTypeRegistry>()
            .EnumerateQueries();
        var cases = CrossTenantQueryCases.For(_fixture);

        // Completeness: a registered query type with no coverage entry fails here by
        // construction, mirroring the permission-declaration walk.
        CrossTenantCoverage.FindUncovered(registered, cases.Keys.ToHashSet())
            .Should().BeEmpty();

        // Correctness: run every entry, so the structural check exercises the real isolation
        // assertion for each type rather than asserting a case is merely present.
        foreach (var queryType in registered)
        {
            await cases[queryType]();
        }
    }
}
