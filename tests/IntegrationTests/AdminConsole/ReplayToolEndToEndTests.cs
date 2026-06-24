using System.Net;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.AdminConsole;

// Characterizes the wired admit-and-render path of the Replay Tool page (Phase 12). Reuses the admit
// fixture, which boots the AdminConsole over a migrated Testcontainers Postgres with an Admin principal.
// An authenticated Admin GET of /replay must return 200 with the page rendered. This proves the page
// serves at its route in the real host under the global <Routes @rendermode="InteractiveServer" />, with
// no per-page render mode and so no inherited-render-mode conflict at render. The bUnit specs render the
// component in isolation and drive the interactive action; this Testcontainers spec is the wiring proof
// that the real host renders the page (the static GET cannot open a circuit to drive the action).
public class ReplayToolEndToEndTests : IClassFixture<AdminConsoleAdmitFixture>
{
    private readonly AdminConsoleAdmitFixture _fixture;

    public ReplayToolEndToEndTests(AdminConsoleAdmitFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Authenticated_admin_request_to_replay_renders_the_rebuild_form()
    {
        using var client = _fixture.Factory.CreateClient();

        var response = await client.GetAsync("/replay");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Replay Tool");
        body.Should().Contain("order-throughput");
    }
}
