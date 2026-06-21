using System.Net;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.AdminConsole;

// ADR 0040: the admit half of the AdminConsole deny-by-default posture. An authenticated principal
// whose seeded roles grant console access reaches a gated route and gets 200. Proves the fallback
// policy, the AdminConsoleAccessHandler, and the current-roles read end to end against real Postgres;
// the deny half is proven separately by AdminConsoleAuthorizationTests.
public class AdminConsoleAdmitTests : IClassFixture<AdminConsoleAdmitFixture>
{
    private readonly AdminConsoleAdmitFixture _fixture;

    public AdminConsoleAdmitTests(AdminConsoleAdmitFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Authenticated_admin_request_to_a_gated_route_is_admitted()
    {
        using var client = _fixture.Factory.CreateClient();

        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
