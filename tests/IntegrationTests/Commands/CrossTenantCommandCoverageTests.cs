using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.IntegrationTests.Queries;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Commands;

// The structural cross-tenant coverage harness for the registered command types (ADR 0031's
// coverage mandate), the command-boundary twin of CrossTenantQueryCoverageTests. The meta-test
// enumerates the command types the host actually registers, asserts every one has a coverage entry
// (completeness), and runs every entry (correctness), so the structural check exercises the real
// isolation rather than asserting a case is merely present. Each case lives once in
// CrossTenantCommandCases.
public class CrossTenantCommandCoverageTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public CrossTenantCommandCoverageTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Every_registered_command_type_has_cross_tenant_coverage()
    {
        var registered = _fixture.Factory.Services
            .GetRequiredService<CommandTypeRegistry>()
            .EnumerateCommands();
        var cases = CrossTenantCommandCases.For(_fixture);

        // Completeness: a registered command type with no coverage entry fails here by
        // construction, mirroring the query coverage walk.
        CrossTenantCoverage.FindUncovered(registered, cases.Keys.ToHashSet())
            .Should().BeEmpty();

        // Correctness: run every entry, so the structural check exercises the real isolation
        // assertion for each type rather than asserting a case is merely present.
        foreach (var commandType in registered)
        {
            await cases[commandType]();
        }
    }
}
