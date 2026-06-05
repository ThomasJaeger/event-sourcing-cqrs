using System;
using System.Linq;
using System.Threading.Tasks;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// The structural cross-tenant coverage harness for the registered projections (ADR 0031's coverage
// mandate), the projection-boundary twin of CrossTenantQueryCoverageTests. Projections are registered as a
// DI IEnumerable<IProjection>, so the meta-test resolves the registered set from the production
// AddReadModels registration surface and asserts every registered projection has a coverage entry
// (completeness), then runs every entry (correctness). A projection added to the read-model registration
// without a case fails the suite by construction. It lives in Projections.Tests, alongside the in-process
// projection drive its cases use.
public class CrossTenantProjectionCoverageTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public CrossTenantProjectionCoverageTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Every_registered_projection_has_cross_tenant_coverage()
    {
        // The registered set is the production AddReadModels multi-registration both hosts compose. Stub
        // connection strings suffice: resolving IProjection constructs the projection singletons without
        // opening a connection.
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddPostgresEventStore(opts => opts.ConnectionString = "Host=localhost;Database=stub");
        services.AddReadModels(opts => opts.ConnectionString = "Host=localhost;Database=stub");
        services.AddSingleton<ICurrentTenantAccessor, StubTenantAccessor>();
        await using var provider = services.BuildServiceProvider();
        var registered = provider.GetServices<IProjection>().Select(p => p.GetType()).ToList();

        var cases = CrossTenantProjectionCases.For(_fixture);

        // Completeness: a registered projection with no coverage entry fails here by construction,
        // mirroring the query and command coverage walks.
        CrossTenantCoverage.FindUncovered(registered, cases.Keys.ToHashSet())
            .Should().BeEmpty();

        // Correctness: run every entry, so the structural check exercises the real isolation or
        // recorded-decision assertion for each projection rather than asserting a case is merely present.
        foreach (var projectionType in registered)
        {
            await cases[projectionType]();
        }
    }
}
