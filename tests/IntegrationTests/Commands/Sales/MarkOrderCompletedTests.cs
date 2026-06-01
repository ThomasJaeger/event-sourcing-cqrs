using System.Net;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Commands.Sales;

public class MarkOrderCompletedTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public MarkOrderCompletedTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Posting_mark_order_completed_on_placed_order_appends_order_completed_event()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();

        await OrderSetup.PlaceOrderAsync(client, orderId);

        var response = await client.PostCommandAsync(
            "MarkOrderCompleted",
            new { orderId },
            idempotencyKey: Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var eventStore = _fixture.Factory.Services.GetRequiredService<IEventStore>();
        var envelopes = await eventStore.ReadStreamAsync(
            StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderId), fromVersion: 0);
        envelopes.Should().HaveCount(5);
        envelopes[4].EventType.Should().Be("OrderCompleted");
    }

    [Fact]
    public async Task Posting_mark_order_completed_on_drafted_order_returns_422()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();

        await OrderSetup.DraftAsync(client, orderId);

        var response = await client.PostCommandAsync(
            "MarkOrderCompleted",
            new { orderId },
            idempotencyKey: Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}
