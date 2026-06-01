using System.Net;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Commands.Billing;

public class RefundPaymentTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public RefundPaymentTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Posting_refund_payment_on_captured_payment_appends_payment_refunded_event()
    {
        var client = _fixture.Factory.CreateClient();
        var paymentId = Guid.NewGuid();
        var eventStore = _fixture.Factory.Services.GetRequiredService<IEventStore>();

        await PaymentSeed.CapturedAsync(eventStore, paymentId);

        var response = await client.PostCommandAsync(
            "RefundPayment",
            new { paymentId, reason = "customer dissatisfied" },
            idempotencyKey: Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var envelopes = await eventStore.ReadStreamAsync(
            StreamId.ForAggregate<Payment>(WellKnownTenants.Default, paymentId), fromVersion: 0);
        envelopes.Should().HaveCount(3);
        envelopes[2].EventType.Should().Be("PaymentRefunded");
    }

    [Fact]
    public async Task Posting_refund_payment_on_authorized_payment_returns_422()
    {
        var client = _fixture.Factory.CreateClient();
        var paymentId = Guid.NewGuid();
        var eventStore = _fixture.Factory.Services.GetRequiredService<IEventStore>();

        await PaymentSeed.AuthorizedAsync(eventStore, paymentId);

        var response = await client.PostCommandAsync(
            "RefundPayment",
            new { paymentId, reason = "any" },
            idempotencyKey: Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Posting_refund_payment_on_unknown_payment_returns_404()
    {
        var client = _fixture.Factory.CreateClient();

        var response = await client.PostCommandAsync(
            "RefundPayment",
            new { paymentId = Guid.NewGuid(), reason = "any" },
            idempotencyKey: Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
