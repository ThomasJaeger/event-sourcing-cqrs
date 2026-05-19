using EventSourcingCqrs.Application.Commands.Billing;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Billing;

public sealed class VoidPaymentHandlerTests
{
    [Fact]
    public async Task HandleAsync_voids_an_authorized_payment()
    {
        var fixture = new PaymentTestFixture();
        await fixture.SeedAuthorizedAsync();
        var handler = new VoidPaymentHandler(fixture.Repository, fixture.Accessor);

        await handler.HandleAsync(
            new VoidPayment(
                PaymentTestFixture.PaymentId,
                PaymentTestFixture.VoidReason),
            CancellationToken.None);

        var loaded = await fixture.LoadAsync();
        loaded!.Status.Should().Be(PaymentStatus.Voided);
    }

    [Fact]
    public async Task HandleAsync_throws_AggregateNotFoundException_when_the_payment_does_not_exist()
    {
        var fixture = new PaymentTestFixture();
        var handler = new VoidPaymentHandler(fixture.Repository, fixture.Accessor);

        var act = () => handler.HandleAsync(
            new VoidPayment(
                PaymentTestFixture.UnknownId,
                PaymentTestFixture.VoidReason),
            CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AggregateNotFoundException>()).Which;
        thrown.AggregateId.Should().Be(PaymentTestFixture.UnknownId);
    }
}
