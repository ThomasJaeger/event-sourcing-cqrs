using System.Net;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Commands.Sales;

public class RemoveOrderLineTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public RemoveOrderLineTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Posting_remove_order_line_appends_order_line_removed_event()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();

        await OrderSetup.DraftWithLineAsync(client, orderId, lineId);

        var response = await client.PostCommandAsync(
            "RemoveOrderLine",
            new { orderId, lineId },
            idempotencyKey: Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var eventStore = _fixture.Factory.Services.GetRequiredService<IEventStore>();
        var envelopes = await eventStore.ReadStreamAsync(
            StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderId), fromVersion: 0);
        envelopes.Should().HaveCount(3);
        envelopes[2].EventType.Should().Be("OrderLineRemoved");
    }

    [Fact]
    public async Task Posting_remove_unknown_line_returns_422()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();

        await OrderSetup.DraftWithLineAsync(client, orderId, Guid.NewGuid());

        var response = await client.PostCommandAsync(
            "RemoveOrderLine",
            new { orderId, lineId = Guid.NewGuid() },
            idempotencyKey: Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Posting_remove_order_line_on_unknown_order_returns_404()
    {
        var client = _fixture.Factory.CreateClient();

        var response = await client.PostCommandAsync(
            "RemoveOrderLine",
            new { orderId = Guid.NewGuid(), lineId = Guid.NewGuid() },
            idempotencyKey: Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
