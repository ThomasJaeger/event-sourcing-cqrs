using System.Net;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.EventStoreProvider;

// PLAN.md:253, the Phase 2 done-when: switching the configured event store from PostgreSQL to SQL
// Server in a test run requires no domain-code changes. The host under test is the Api composition
// every other command test boots, the command is the one DraftOrderTests dispatches, and the only
// difference is the provider key and the connection string behind it.
//
// The assertion is scoped to the append. The Api host registers no hosted services, so nothing in
// this composition drains the outbox or catches a projection up; the event reaching the SQL Server
// events table is the whole of what the host does with a command, and waiting on a projection here
// would be waiting on a processor that does not run.
public sealed class EventStoreProviderSwitchTests : IClassFixture<SqlServerProviderApiFixture>
{
    private readonly SqlServerProviderApiFixture _fixture;

    public EventStoreProviderSwitchTests(SqlServerProviderApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Selecting_the_SqlServer_provider_appends_the_command_event_to_the_SqlServer_events_table()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        var response = await client.PostCommandAsync(
            "DraftOrder",
            new { orderId, customerId },
            idempotencyKey: Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var streamId = StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderId);
        var eventTypes = await _fixture.ReadEventTypesAsync(streamId.Value);
        eventTypes.Should().ContainSingle().Which.Should().Be("OrderDrafted");

        // The write moved, rather than merely landing. A dual write or a fallback to the PostgreSQL
        // adapter would satisfy the assertion above and fail this one.
        var postgresEventTypes = await _fixture.ReadPostgresEventTypesAsync(streamId.Value);
        postgresEventTypes.Should().BeEmpty();
    }
}
