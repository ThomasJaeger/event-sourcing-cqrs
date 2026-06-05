using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Queries;

// Unit tests for the coverage-completeness check, the read-side twin of
// QueryPermissionValidation's FindUndeclared: a pure function returning the registered
// query types that have no cross-tenant coverage entry. The positive case proves a fully
// covered set is empty; the negative case proves a registered type with no entry is
// flagged, the same gap-detection shape the permission-declaration walk has its own test for.
public class CrossTenantCoverageTests
{
    private sealed record CoveredQuery;
    private sealed record UncoveredQuery;

    [Fact]
    public void FindUncovered_with_every_registered_type_covered_returns_empty()
    {
        var uncovered = CrossTenantCoverage.FindUncovered(
            new[] { typeof(CoveredQuery) },
            new HashSet<Type> { typeof(CoveredQuery) });

        uncovered.Should().BeEmpty();
    }

    [Fact]
    public void FindUncovered_flags_a_registered_type_with_no_coverage_entry()
    {
        var uncovered = CrossTenantCoverage.FindUncovered(
            new[] { typeof(CoveredQuery), typeof(UncoveredQuery) },
            new HashSet<Type> { typeof(CoveredQuery) });

        uncovered.Should().ContainSingle().Which.Should().Be(typeof(UncoveredQuery));
    }
}
