using System.Net;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Commands.Sales;

public class DraftOrderTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public DraftOrderTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Posting_draft_order_appends_order_drafted_event()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        var response = await client.PostCommandAsync(
            "DraftOrder",
            new { orderId, customerId },
            idempotencyKey: Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var eventStore = _fixture.Factory.Services.GetRequiredService<IEventStore>();
        var envelopes = await eventStore.ReadStreamAsync(
            StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderId), fromVersion: 0);
        envelopes.Should().ContainSingle();
        envelopes[0].EventType.Should().Be("OrderDrafted");
    }

    [Fact]
    public async Task Posting_draft_order_twice_on_same_id_returns_409()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        var first = await client.PostCommandAsync(
            "DraftOrder",
            new { orderId, customerId },
            idempotencyKey: Guid.NewGuid().ToString());
        first.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var second = await client.PostCommandAsync(
            "DraftOrder",
            new { orderId, customerId },
            idempotencyKey: Guid.NewGuid().ToString());
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
