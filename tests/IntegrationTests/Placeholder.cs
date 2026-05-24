using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests;

// Placeholder so the project is a discoverable test surface for CI before the
// first real integration test lands. Cluster 2 Commit 9's CommandsEndpointTests
// replaces this with the first endpoint integration test, at which point the
// project gains its Testcontainers and WebApplicationFactory dependencies.
public class Placeholder
{
    [Fact]
    public void Project_has_at_least_one_discoverable_test()
    {
        true.Should().BeTrue();
    }
}
