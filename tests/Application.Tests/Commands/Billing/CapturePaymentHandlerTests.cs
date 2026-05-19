using EventSourcingCqrs.Application.Commands.Billing;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Billing;

public sealed class CapturePaymentHandlerTests
{
    [Fact]
    public async Task HandleAsync_captures_an_authorized_payment()
    {
        var fixture = new PaymentTestFixture();
        await fixture.SeedAuthorizedAsync();
        var handler = new CapturePaymentHandler(fixture.Repository, fixture.Accessor);

        await handler.HandleAsync(
            new CapturePayment(PaymentTestFixture.PaymentId),
            CancellationToken.None);

        var loaded = await fixture.LoadAsync();
        loaded!.Status.Should().Be(PaymentStatus.Captured);
        loaded.CapturedAmount.Should().Be(PaymentTestFixture.Amount);
    }

    [Fact]
    public async Task HandleAsync_throws_AggregateNotFoundException_when_the_payment_does_not_exist()
    {
        var fixture = new PaymentTestFixture();
        var handler = new CapturePaymentHandler(fixture.Repository, fixture.Accessor);

        var act = () => handler.HandleAsync(
            new CapturePayment(PaymentTestFixture.UnknownId),
            CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AggregateNotFoundException>()).Which;
        thrown.AggregateId.Should().Be(PaymentTestFixture.UnknownId);
    }
}
