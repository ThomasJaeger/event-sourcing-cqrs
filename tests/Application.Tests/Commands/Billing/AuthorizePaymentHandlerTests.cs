using EventSourcingCqrs.Application.Commands.Billing;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Billing;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Billing;

public sealed class AuthorizePaymentHandlerTests
{
    [Fact]
    public async Task HandleAsync_authorizes_a_new_payment_and_persists_it()
    {
        var fixture = new PaymentTestFixture();
        var handler = new AuthorizePaymentHandler(fixture.Repository, fixture.Accessor);

        await handler.HandleAsync(
            new AuthorizePayment(
                PaymentTestFixture.PaymentId,
                PaymentTestFixture.OrderId,
                PaymentTestFixture.Amount,
                PaymentTestFixture.PaymentMethodReference),
            CancellationToken.None);

        var loaded = await fixture.LoadAsync();
        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(PaymentTestFixture.PaymentId);
        loaded.OrderId.Should().Be(PaymentTestFixture.OrderId);
        loaded.AuthorizedAmount.Should().Be(PaymentTestFixture.Amount);
        loaded.Status.Should().Be(PaymentStatus.Authorized);
    }
}
