using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// The drift guard for the name-only projection roster. The roster a host composes
// to enumerate projections without their stores (IProjectionRoster) must list
// exactly the projection names the full read-model registration registers, no more
// and no fewer. The registered set is the production AddReadModels surface both
// hosts compose, resolved the same way CrossTenantProjectionCoverageTests and the
// AddReadModels graph test resolve it. A ninth projection registered without a
// roster entry, or a roster name that drifts from a projection's Name, fails here
// by construction rather than silently under-reporting projection lag.
public class ProjectionRosterCoverageTests
{
    [Fact]
    public async Task The_name_only_roster_lists_exactly_the_registered_projection_names()
    {
        // Stub connection strings suffice: resolving IProjection constructs the
        // projection singletons without opening a connection, the idiom the
        // AddReadModels graph and cross-tenant coverage tests use. AddPostgresEventStore
        // supplies the JsonSerializerOptions OrderDetailProjection takes.
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddPostgresEventStore(opts => opts.ConnectionString = "Host=localhost;Database=stub");
        services.AddReadModels(opts => opts.ConnectionString = "Host=localhost;Database=stub");
        services.AddSingleton<ICurrentTenantAccessor, StubTenantAccessor>();
        await using var provider = services.BuildServiceProvider();

        var registeredNames = provider.GetServices<IProjection>().Select(p => p.Name).ToList();

        var roster = provider.GetService<IProjectionRoster>();
        var rosterNames = roster?.Names ?? Array.Empty<string>();

        rosterNames.Should().BeEquivalentTo(registeredNames);
    }
}
