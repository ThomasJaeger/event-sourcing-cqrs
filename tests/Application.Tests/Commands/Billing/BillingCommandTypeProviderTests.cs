using EventSourcingCqrs.Application.Commands.Billing;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Billing;

public class BillingCommandTypeProviderTests
{
    [Fact]
    public void GetCommandTypes_returns_only_RefundPayment_in_v1()
    {
        var provider = new BillingCommandTypeProvider();

        // Exactly RefundPayment: CapturePayment is deliberately absent in v1
        // (the no-capture stance, F-0009-Q / ADR 0023). The order-sensitive
        // single-element assertion locks the deferral in.
        provider.GetCommandTypes().Should().Equal(typeof(RefundPayment));
    }
}
