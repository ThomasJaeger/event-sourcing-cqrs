using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// The read-side fail-closed tenant guard. ReadModelTenant.ResolveOrThrow reads the
// current tenant from the accessor and throws the same MissingTenantContextException
// the command write path uses when none is set, so a tenant-predicated read fails
// closed at one shared point rather than repeating the check per query. These pin
// the resolve; the predicate that binds the returned Guid lands in the next commit.
public sealed class ReadModelTenantTests
{
    private static readonly TenantId Tenant =
        TenantId.From(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    [Fact]
    public void ResolveOrThrow_with_no_tenant_set_throws_MissingTenantContextException()
    {
        var accessor = new StubTenantAccessor { Current = null };

        var act = () => ReadModelTenant.ResolveOrThrow(accessor);

        act.Should().Throw<MissingTenantContextException>();
    }

    [Fact]
    public void ResolveOrThrow_with_a_tenant_set_returns_its_guid()
    {
        var accessor = new StubTenantAccessor { Current = Tenant };

        var result = ReadModelTenant.ResolveOrThrow(accessor);

        result.Should().Be(Tenant.Value);
    }
}
