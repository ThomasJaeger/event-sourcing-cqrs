using System.Net;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.AdminConsole;

// Characterizes the wired admit-and-render path of the Event Store Browser page (Phase 12). Reuses the
// admit fixture, which boots the AdminConsole over a migrated Testcontainers Postgres with an Admin
// principal. An authenticated Admin GET of /streams must return 200 with the page rendered. This proves the
// page serves at its route in the real host under the global <Routes @rendermode="InteractiveServer" />,
// with no per-page render mode and so no inherited-render-mode conflict at render, and that it boots under
// the host's DI validation. The bUnit specs render the component in isolation and drive the interactive
// inspect; this Testcontainers spec is the wiring proof that the real host serves the page. A GET renders
// the baseline (pre-inspect) state, so it asserts the baseline markers, not an inspected outcome.
public class EventStoreBrowserEndToEndTests : IClassFixture<AdminConsoleAdmitFixture>
{
    private readonly AdminConsoleAdmitFixture _fixture;

    public EventStoreBrowserEndToEndTests(AdminConsoleAdmitFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Authenticated_admin_request_to_streams_renders_the_browser_form()
    {
        using var client = _fixture.Factory.CreateClient();

        var response = await client.GetAsync("/streams");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Event Store Browser");
        body.Should().Contain("Inspect stream");
    }
}
