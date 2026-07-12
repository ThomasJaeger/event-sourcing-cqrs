using System.Net;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.AdminConsole;

// Characterizes the wired admit-and-render path of the Correlation-ID Tracer page (Phase 12), the twin of
// EventStoreBrowserEndToEndTests. Reuses the admit fixture, which boots the AdminConsole over a migrated
// Testcontainers Postgres with an Admin principal. An authenticated Admin GET of /correlations must return
// 200 with the page rendered. This proves the page serves at its route in the real host under the global
// <Routes @rendermode="InteractiveServer" />, with no per-page render mode and so no inherited-render-mode
// conflict at render, and that the host's DI validation accepts the seam and the trace reader the page pulls
// in. The bUnit specs render the component in isolation and drive the interactive trace; this Testcontainers
// spec is the wiring proof that the real host serves the page. A GET renders the baseline (pre-trace) state,
// so it asserts the baseline markers, not a traced outcome.
//
// Green on write, and declared as such: the route was mapped and gated in the page commit, so the honest
// failure window closed there and no RED for it exists. This characterizes shipped behavior, the same
// provenance the Browser's e2e carried.
public class CorrelationIdTracerEndToEndTests : IClassFixture<AdminConsoleAdmitFixture>
{
    private readonly AdminConsoleAdmitFixture _fixture;

    public CorrelationIdTracerEndToEndTests(AdminConsoleAdmitFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Authenticated_admin_request_to_correlations_renders_the_tracer_form()
    {
        using var client = _fixture.Factory.CreateClient();

        var response = await client.GetAsync("/correlations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Correlation-ID Tracer");
        body.Should().Contain("Trace correlation");
    }
}
