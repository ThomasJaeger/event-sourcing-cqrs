using System.Net;
using System.Text.Json;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.AdminConsole;

// HTTP rendering must reach the event-store catalog, including metadata for unknown payload types.
// The existing admin fixture uses a separate migrated database and the real permission gate.
public sealed class EventCatalogEndToEndTests(AdminConsoleAdmitFixture fixture)
    : IClassFixture<AdminConsoleAdmitFixture>
{
    [Theory]
    [InlineData("/streams")]
    [InlineData("/correlations")]
    public async Task The_admin_page_offers_stored_identifiers_before_manual_entry(string path)
    {
        var aggregateId = Guid.NewGuid();
        var streamId = $"order:{aggregateId:N}";
        var correlationId = Guid.NewGuid();
        await SeedAsync(streamId, correlationId);
        using var client = fixture.Factory.CreateClient();

        using var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain(path == "/streams" ? streamId : correlationId.ToString());
        html.Should().NotContain("Unable to load");
    }

    private async Task SeedAsync(string streamId, Guid correlationId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO event_store.events
                (stream_id, stream_version, event_id, event_type, event_version, payload, metadata, occurred_utc)
            VALUES (@stream, 1, @event, 'UnknownCatalogFixtureEvent', 1, '{}', @metadata::jsonb, @occurred)
            """;
        command.Parameters.AddWithValue("stream", streamId);
        command.Parameters.AddWithValue("event", Guid.NewGuid());
        command.Parameters.AddWithValue("metadata", JsonSerializer.Serialize(new
        {
            correlation_id = correlationId,
            tenant_id = "00000000-0000-0000-0000-000000000001",
        }));
        command.Parameters.AddWithValue("occurred", DateTime.UtcNow);
        await command.ExecuteNonQueryAsync();
    }
}
