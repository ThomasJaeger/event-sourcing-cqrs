using System.Net;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.EventStoreProvider;

// RED for slice 3's Kurrent provider value, the KurrentDB analog of EventStoreProviderSwitchTests and
// of PLAN.md:453: switching the configured event store to KurrentDB is a configuration change, not a
// code change. The host under test is the Api composition every other command test boots, the command
// is the one DraftOrderTests dispatches, and the only difference is the provider key and the KurrentDB
// connection string behind it.
//
// It runs RED today by booting the host, which is honest RED, not a compile failure: the Api parser
// rejects EVENT_STORE_PROVIDER=Kurrent, so CreateClient throws at host construction with the same
// "Recognized values are Postgres and SqlServer" message the Workers parser facts capture. The GREEN
// slice teaches the parser the value and composes the Kurrent adapter, and the append lands.
//
// The assertion is scoped to the append. The Api host registers no hosted services, so nothing in this
// composition drains the outbox or catches a projection up; the event reaching KurrentDB is the whole
// of what the host does with a command, and waiting on a projection here would be waiting on a
// processor that does not run.
public sealed class KurrentProviderSwitchTests : IClassFixture<KurrentProviderApiFixture>
{
    private readonly KurrentProviderApiFixture _fixture;

    public KurrentProviderSwitchTests(KurrentProviderApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Selecting_the_Kurrent_provider_appends_the_command_event_to_KurrentDB()
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
