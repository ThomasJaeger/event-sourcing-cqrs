using System.Net;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Commands.Sales;

public class CancelOrderTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public CancelOrderTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Posting_cancel_order_on_drafted_order_appends_order_cancelled_event()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();

        await OrderSetup.DraftAsync(client, orderId);

        var response = await client.PostCommandAsync(
            "CancelOrder",
            new { orderId, reason = "customer changed mind", issuedByUserId = Guid.NewGuid() },
            idempotencyKey: Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var eventStore = _fixture.Factory.Services.GetRequiredService<IEventStore>();
        var envelopes = await eventStore.ReadStreamAsync(
            StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderId), fromVersion: 0);
        envelopes.Should().HaveCount(2);
        envelopes[1].EventType.Should().Be("OrderCancelled");
    }

    [Fact]
    public async Task Posting_cancel_order_twice_returns_422_on_second_attempt()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();

        await OrderSetup.DraftAsync(client, orderId);
        await client.PostCommandAsync(
            "CancelOrder",
            new { orderId, reason = "first cancel", issuedByUserId = Guid.NewGuid() },
            idempotencyKey: Guid.NewGuid().ToString());

        var response = await client.PostCommandAsync(
            "CancelOrder",
            new { orderId, reason = "second cancel", issuedByUserId = Guid.NewGuid() },
            idempotencyKey: Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Posting_cancel_order_on_shipped_order_returns_422()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();

        await OrderSetup.ShipOrderAsync(client, orderId);

        var response = await client.PostCommandAsync(
            "CancelOrder",
            new { orderId, reason = "too late", issuedByUserId = Guid.NewGuid() },
            idempotencyKey: Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}
