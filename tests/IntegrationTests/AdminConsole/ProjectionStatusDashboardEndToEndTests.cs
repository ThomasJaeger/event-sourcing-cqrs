using System.Net;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.AdminConsole;

// Characterizes the wired admit-and-render path of the Projection Status Dashboard (ADR 0040). Reuses
// the admit fixture, which boots the AdminConsole over a migrated Testcontainers Postgres with an Admin
// principal and both connection strings set to the migrated database. An authenticated Admin GET of
// /projections must return 200 with the projection-status table rendered, proving the lag reader's three
// focused ports resolve under the host's build validation (no read-model or event-store over-provisioning)
// and that the page renders end to end against real Postgres. This is the Testcontainers wiring proof to
// the bUnit render proof: the head read hits event_store.events and the checkpoint read hits the
// read-model schema, both provided by CreateMigratedDatabaseAsync.
public class ProjectionStatusDashboardEndToEndTests : IClassFixture<AdminConsoleAdmitFixture>
{
    private readonly AdminConsoleAdmitFixture _fixture;

    public ProjectionStatusDashboardEndToEndTests(AdminConsoleAdmitFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Authenticated_admin_request_to_projections_renders_the_projection_status_table()
    {
        using var client = _fixture.Factory.CreateClient();

        var response = await client.GetAsync("/projections");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Projection Status");
        // One row per registered projection; order-list is one of the eight names the roster exposes.
        body.Should().Contain("order-list");
    }
}
