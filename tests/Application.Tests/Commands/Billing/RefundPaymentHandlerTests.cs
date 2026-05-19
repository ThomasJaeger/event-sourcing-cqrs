using EventSourcingCqrs.Application.Commands.Billing;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Billing;

public sealed class RefundPaymentHandlerTests
{
    [Fact]
    public async Task HandleAsync_refunds_a_captured_payment()
    {
        var fixture = new PaymentTestFixture();
        await fixture.SeedCapturedAsync();
        var handler = new RefundPaymentHandler(fixture.Repository, fixture.Accessor);

        await handler.HandleAsync(
            new RefundPayment(
                PaymentTestFixture.PaymentId,
                PaymentTestFixture.RefundReason),
            CancellationToken.None);

        var loaded = await fixture.LoadAsync();
        loaded!.Status.Should().Be(PaymentStatus.Refunded);
    }

    [Fact]
    public async Task HandleAsync_throws_AggregateNotFoundException_when_the_payment_does_not_exist()
    {
        var fixture = new PaymentTestFixture();
        var handler = new RefundPaymentHandler(fixture.Repository, fixture.Accessor);

        var act = () => handler.HandleAsync(
            new RefundPayment(
                PaymentTestFixture.UnknownId,
                PaymentTestFixture.RefundReason),
            CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AggregateNotFoundException>()).Which;
        thrown.AggregateId.Should().Be(PaymentTestFixture.UnknownId);
    }
}
